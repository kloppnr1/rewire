using System.Text;
using DataMigration.Core.Models;
using DataMigration.Migration.Runners;
using DataMigration.OpsCli.Reporting;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Bygger den flade rækkeliste + de shard'ede detaljedata som den virtualiserede rapport
/// renderer i browseren. Rækkefølge og indhold svarer 1:1 til det træ generatoren
/// tidligere skrev som DOM — forskellen er at rækkerne nu er data, så klienten kun
/// materialiserer de synlige.
/// </summary>
internal sealed class ReportTreeBuilder
{
    private readonly OpsMigrationReport _report;
    private readonly Dictionary<string, string>? _groupOwners;

    private readonly Dictionary<string, CreateCustomerDto> _customerByAccount;
    private readonly Dictionary<Guid, List<CreatePayerDto>> _payersByCustomerId;
    private readonly Dictionary<string, MeteringPointDto> _mpByIdentification;
    private readonly Dictionary<Guid, CreateCustomerDto> _customerById;

    public ReportTreeBuilder(OpsMigrationReport report, Dictionary<string, string>? groupOwners)
    {
        _report = report;
        _groupOwners = groupOwners;

        _customerByAccount = new Dictionary<string, CreateCustomerDto>(StringComparer.Ordinal);
        foreach (var c in report.Customers)
        {
            if (c.CustomerNumber is { } num)
                _customerByAccount[num] = c;
        }

        _customerById = report.Customers.ToDictionary(c => c.Id);

        _payersByCustomerId = [];
        foreach (var p in report.Payers)
        {
            if (!_payersByCustomerId.TryGetValue(p.CustomerId, out var list))
                _payersByCustomerId[p.CustomerId] = list = [];
            list.Add(p);
        }

        _mpByIdentification = new Dictionary<string, MeteringPointDto>(StringComparer.Ordinal);
        foreach (var m in report.MeteringPoints)
        {
            if (m.Identification?.MeteringPointIdentification is { } id)
                _mpByIdentification[id] = m;
        }
    }

    public ReportPayload.TreeIndex Index { get; } = new();
    public List<ReportPayload.Shard> Shards { get; } = [];

    public void Build()
    {
        var byGroup = _report.AccountGroupings
            .GroupBy(g => g.GroupNum ?? "(ikke grupperet)")
            .ToList();

        var blockIndex = 0;

        foreach (var grp in byGroup)
        {
            var groupMpCount = grp.Sum(c =>
                _report.AccountMeteringPoints.GetValueOrDefault(c.RepresentativeAccount, []).Count);

            string groupLabel;
            if (grp.Key == "(ikke grupperet)")
                groupLabel = "(ikke grupperet)";
            else if (_groupOwners != null && _groupOwners.TryGetValue(grp.Key, out var extOwnerAcct))
                groupLabel = $"{grp.Key} (ejer: {extOwnerAcct})";
            else
                groupLabel = grp.Key;

            var headerRowIdx = Index.Rows.Count;
            Index.Rows.Add(new ReportPayload.Row
            {
                K = ReportPayload.KindOwner,
                D = 0,
                L = groupLabel,
                N = groupMpCount,
            });

            foreach (var group in grp)
            {
                var block = blockIndex++;
                var shardIdx = block / ReportPayload.CustomersPerShard;
                var search = new StringBuilder();

                Index.BlockAccount.Add(group.RepresentativeAccount);
                Index.BlockShard.Add(shardIdx);

                var mps = _report.AccountMeteringPoints.GetValueOrDefault(group.RepresentativeAccount, []);

                if (group.MemberAccounts.Count == 1)
                {
                    var acct = group.RepresentativeAccount;
                    var name = group.AccountNames.GetValueOrDefault(acct, "");
                    Index.Rows.Add(new ReportPayload.Row
                    {
                        K = ReportPayload.KindAccount,
                        D = 1,
                        A = acct,
                        Nm = string.IsNullOrEmpty(name) ? null : name,
                        Cv = string.IsNullOrEmpty(group.Cvr) ? null : group.Cvr,
                        N = mps.Count > 0 ? mps.Count : null,
                        B = block,
                    });
                    Append(search, acct, name, group.Cvr);
                    AddMpRows(mps, block, contractDepth: 2, search);
                }
                else
                {
                    Index.Rows.Add(new ReportPayload.Row
                    {
                        K = ReportPayload.KindMergeHeader,
                        D = 1,
                        N = group.MemberAccounts.Count,
                        Cv = string.IsNullOrEmpty(group.Cvr) ? null : group.Cvr,
                        B = block,
                    });
                    Append(search, group.Cvr);

                    foreach (var account in group.MemberAccounts)
                    {
                        var name = group.AccountNames.GetValueOrDefault(account, "");
                        var acctMps = _report.AccountMeteringPoints.GetValueOrDefault(account, []);
                        Append(search, account, name);

                        if (account == group.RepresentativeAccount)
                        {
                            Index.Rows.Add(new ReportPayload.Row
                            {
                                K = ReportPayload.KindRepresentative,
                                D = 2,
                                A = account,
                                Nm = string.IsNullOrEmpty(name) ? null : name,
                                N = acctMps.Count > 0 ? acctMps.Count : null,
                                B = block,
                            });
                            AddMpRows(acctMps, block, contractDepth: 3, search);
                        }
                        else
                        {
                            Index.Rows.Add(new ReportPayload.Row
                            {
                                K = ReportPayload.KindMergedAway,
                                D = 2,
                                A = account,
                                Nm = string.IsNullOrEmpty(name) ? null : name,
                                B = block,
                            });
                        }
                    }
                }

                Index.BlockSearch.Add(search.ToString().ToLowerInvariant());
                AddToShard(shardIdx, group);
            }

            Index.Groups.Add([headerRowIdx, Index.Rows.Count - headerRowIdx - 1]);
        }
    }

    /// <summary>
    /// Kontrakt-overskrift efterfulgt af dens målepunkter — samme gruppering og sortering
    /// som den tidligere RenderMpsByContract.
    /// </summary>
    private void AddMpRows(List<string> mpIds, int block, int contractDepth, StringBuilder search)
    {
        var byContract = mpIds
            .GroupBy(mp => _report.MpContractNames.GetValueOrDefault(mp, "(ingen kontrakt)"))
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var contractGroup in byContract)
        {
            Index.Rows.Add(new ReportPayload.Row
            {
                K = ReportPayload.KindContract,
                D = contractDepth,
                L = contractGroup.Key,
                N = contractGroup.Count(),
                B = block,
            });
            Append(search, contractGroup.Key);

            foreach (var mp in contractGroup)
            {
                Index.Rows.Add(new ReportPayload.Row
                {
                    K = ReportPayload.KindMeteringPoint,
                    D = contractDepth + 1,
                    G = mp,
                    J = _report.JoinedMeteringPoints.Contains(mp) ? 1 : null,
                    B = block,
                });
                Append(search, mp);
            }
        }
    }

    private void AddToShard(int shardIdx, AccountGrouping group)
    {
        while (Shards.Count <= shardIdx)
            Shards.Add(new ReportPayload.Shard());
        var shard = Shards[shardIdx];

        var acct = group.RepresentativeAccount;

        // Kunden. Kun repræsentanten findes i Settl — de øvrige gruppemedlemmer er
        // smeltet ind i den (repræsentationsprincippet), så alle konto-rækker i blokken
        // slår op på repræsentanten.
        if (_customerByAccount.TryGetValue(acct, out var customer))
        {
            _report.UnknownIdentities.TryGetValue(acct, out var unknown);
            shard.C[acct] = ReportPayload.SlimCustomer(customer, ResolveOwnerLabel(customer), unknown);

            if (_payersByCustomerId.TryGetValue(customer.Id, out var payers))
                shard.P[acct] = payers.Select(ReportPayload.SlimPayer).ToList();
        }

        // Målepunkter aggregeres på tværs af alle gruppens medlemskonti — samme som
        // MP-fanen viste før.
        var allMps = new List<string>();
        foreach (var member in group.MemberAccounts)
        {
            if (_report.AccountMeteringPoints.TryGetValue(member, out var memberMps))
                allMps.AddRange(memberMps);
        }
        shard.Am[acct] = allMps;

        foreach (var mp in allMps)
        {
            if (!shard.M.ContainsKey(mp) && _mpByIdentification.TryGetValue(mp, out var mpDto))
            {
                shard.M[mp] = ReportPayload.SlimMeteringPoint(
                    mpDto, _report.MpBillingOverrides.GetValueOrDefault(mp));
            }
            if (_report.MpContractNames.TryGetValue(mp, out var contractName))
                shard.Cn.TryAdd(mp, contractName);
            if (_report.JoinedMeteringPoints.Contains(mp) && !shard.J.Contains(mp))
                shard.J.Add(mp);
        }
    }

    private string? ResolveOwnerLabel(CreateCustomerDto c)
    {
        if (c.Owner?.OwnerId is not { } ownerId) return null;
        if (_customerById.TryGetValue(ownerId, out var ownerCustomer))
            return $"{ownerCustomer.CustomerNumber} {ownerCustomer.Address?.PostalAddress?.Name1?.FullName}".Trim();
        return _report.ExternalOwners.TryGetValue(ownerId, out var external)
            ? $"{external} (ekstern)"
            : null;
    }

    private static void Append(StringBuilder sb, params string?[] values)
    {
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(v);
        }
    }
}
