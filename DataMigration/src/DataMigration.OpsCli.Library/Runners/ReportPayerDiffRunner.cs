using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Runners;

public class ReportPayerDiffRunOptions
{
    public string ProfileName { get; set; } = "";
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }
    public bool ShowAll { get; set; }

    /// <summary>
    /// Default true: én CSV-række pr. payer (MeteringPointIds for netop den payer).
    /// Sæt false for at samle alle payers i én række pr. account
    /// (MeteringPointIdsByPayer som "{payer}=[mp,mp] | {payer2}=[mp]").
    /// </summary>
    public bool ExplodePayers { get; set; } = true;
}

/// <summary>
/// Read-only Xellent report: viser hvad der reelt migreres til Settl under
/// nuværende algoritmer (fingerprint @ 0.7.6, sekventiel payer-fan-out)
/// sammenholdt med pre-0.7.6 fingerprint-formlen
/// (Name|AddName|Street|Zip|City|Email|EAN). Default: én række pr. payer
/// (med payerens e-faktura-EAN + dens MP'er); --no-explode-payers samler i
/// stedet alle payers i én række pr. account.
/// </summary>
public class ReportPayerDiffRunner
{
    private static readonly string[] CsvHeader =
    [
        "AccountNumber", "CustomerName", "Role", "RepresentativeAccount",
        "PayerIdentifiers", "ElectronicNumber", "MeteringPointIdsByPayer", "Status", "Action"
    ];

    private readonly string _connectionString;
    // Bygger sit eget Xellent-lag udenom XellentDomainLoader, så listen skal med her.
    private readonly IReadOnlyList<string> _ignoredGroups;

    public ReportPayerDiffRunner(string connectionString, IReadOnlyList<string>? ignoredGroups = null)
    {
        _connectionString = connectionString;
        _ignoredGroups = ignoredGroups ?? [];
    }

    public async Task<int> RunAsync(
        ReportPayerDiffRunOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (options.Accounts.Length == 0)
        {
            Console.Error.WriteLine("ERROR: profile leverede ingen accounts");
            return 1;
        }

        var contextOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(_connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(contextOptions);

        var warnings = new WarningCollector();
        var refDateOnly = DateOnly.FromDateTime(options.ReferenceDate);
        var mapperOptions = new MapperOptions(
            ReferenceDate: refDateOnly,
            Commodity: Commodity.Electricity,
            LegacyTg4Merge: options.LegacyTg4Merge,
            IgnoredGroupNumbers: IgnoredGroupsLoader.ToSet(_ignoredGroups));
        var mapper = new XellentDomainMapper(context, warnings, mapperOptions, options.DataAreaId);

        Console.Error.WriteLine($"=== report-payer-diff: profile='{options.ProfileName}' accounts={options.Accounts.Length} ===");

        var newResolution = await AccountRepresentativeResolver.ResolveAsync(
            context, options.Accounts, options.DataAreaId, options.LegacyTg4Merge, refDateOnly, cancellationToken,
            ignoredGroupNumbers: IgnoredGroupsLoader.ToSet(_ignoredGroups));

        IReadOnlyDictionary<string, string> oldRepByAccount;
        if (options.LegacyTg4Merge)
        {
            Console.Error.WriteLine("  Profile uses LegacyTg4Merge — fingerprint not in play; skipping OLD-fingerprint diff");
            oldRepByAccount = newResolution.RepresentativeByAccount;
        }
        else
        {
            oldRepByAccount = await LegacyFingerprintResolver.ResolveAsync(
                context, options.Accounts, options.DataAreaId, refDateOnly, cancellationToken);
        }

        var loadResult = await mapper.LoadAsync(options.Accounts, cancellationToken);
        var payersByRep = loadResult.Customers.ToDictionary(
            c => c.AccountNumber,
            c => c.Payers,
            StringComparer.Ordinal);

        // rep account → (ContractNum → [MeteringPointId]). Members are already folded into
        // the representative in loadResult, so this holds every MP that hangs under the rep.
        // Join key to Payer.ContractNumbers (payer fan-out is keyed per unique contract-EAN).
        var mappingService = new SettlMappingService(loadResult, warnings, options.ReferenceDate, MigrationConstants.DeliveryCategoryElectricity);
        var accountToMps = await mappingService.ExtractAccountMeteringPointMappingAsync(options.Accounts);
        var mpsByContractByRep = accountToMps.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .GroupBy(t => t.ContractNum, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.MeteringPointId).Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal),
            StringComparer.Ordinal);

        var nameByAccount = await context.CustTables
            .Where(c => c.Dataareaid == options.DataAreaId && options.Accounts.Contains(c.Accountnum))
            .Select(c => new { c.Accountnum, c.Name })
            .ToDictionaryAsync(c => c.Accountnum, c => c.Name ?? "", StringComparer.Ordinal, cancellationToken);

        int repCount = 0, memberCount = 0, newlyMergedCount = 0, fanOutCount = 0;
        int promotedCount = 0, repChangedCount = 0, oldRepCount = 0;
        var prelim = new List<(string acc, string name, string oldRep, string newRep, bool isRep, string payerIds, string eans, string mpByPayer, List<(string payerId, string ean, string mpIds)> breakdown, string status, bool isInteresting)>();

        // For a rep's payers, correlate each payer's MPs via Payer.ContractNumbers → ContractNum.
        // Returns one entry per payer: (payerId, e-invoice EAN, "mp,mp,..."). Empty when the
        // rep has no payers/MPs. Drives both the exploded one-row-per-payer output and the
        // collapsed "{payerId}=[mp,mp] | {payerId2}=[mp]" single-cell format.
        List<(string payerId, string ean, string mpIds)> BuildPayerBreakdown(string repAccount, IReadOnlyList<Payer> ps)
        {
            var result = new List<(string, string, string)>();
            mpsByContractByRep.TryGetValue(repAccount, out var mpsByContract);
            foreach (var p in ps)
            {
                var mpIds = mpsByContract is null
                    ? new List<string>()
                    : p.ContractNumbers
                        .SelectMany(cn => mpsByContract.TryGetValue(cn, out var ids) ? ids : Enumerable.Empty<string>())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                result.Add((p.Identifier, p.BuyerInvoicing?.ElectronicNumber ?? "", string.Join(",", mpIds)));
            }
            return result;
        }

        static string FormatMpByPayer(List<(string payerId, string ean, string mpIds)> breakdown) =>
            string.Join(" | ", breakdown.Select(s => $"{s.payerId}=[{s.mpIds}]"));

        foreach (var acc in options.Accounts.OrderBy(a => a, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = nameByAccount.GetValueOrDefault(acc, "");
            newResolution.RepresentativeByAccount.TryGetValue(acc, out var newRep);
            newRep ??= acc;
            oldRepByAccount.TryGetValue(acc, out var oldRep);
            oldRep ??= acc;
            var isNewRep = string.Equals(newRep, acc, StringComparison.Ordinal);
            var wasOldRep = string.Equals(oldRep, acc, StringComparison.Ordinal);
            if (wasOldRep) oldRepCount++;

            string payerIds;
            string eans;
            string mpByPayer;
            var breakdown = new List<(string payerId, string ean, string mpIds)>();
            bool hasFanOut;
            if (isNewRep)
            {
                repCount++;
                if (payersByRep.TryGetValue(acc, out var ps) && ps.Count > 0)
                {
                    breakdown = BuildPayerBreakdown(acc, ps);
                    payerIds = string.Join(",", ps.Select(p => p.Identifier));
                    eans = string.Join(",", breakdown.Select(b => b.ean));
                    mpByPayer = FormatMpByPayer(breakdown);
                    hasFanOut = ps.Count > 1;
                    if (hasFanOut) fanOutCount++;
                }
                else
                {
                    payerIds = "";
                    eans = "";
                    mpByPayer = "";
                    hasFanOut = false;
                }
            }
            else
            {
                memberCount++;
                payerIds = "";
                eans = "";
                mpByPayer = "";
                hasFanOut = false;
            }

            string status;
            bool isNewlyMerged = wasOldRep && !isNewRep;
            bool isPromoted = !wasOldRep && isNewRep;
            bool isRepChanged = !wasOldRep && !isNewRep && !string.Equals(oldRep, newRep, StringComparison.Ordinal);
            if (isNewlyMerged) { status = "NEWLY_MERGED"; newlyMergedCount++; }
            else if (isPromoted) { status = "PROMOTED_TO_REP"; promotedCount++; }
            else if (isRepChanged) { status = "REP_CHANGED"; repChangedCount++; }
            else status = "UNCHANGED";

            // "Interesting" = anything that requires operator action
            var interesting = hasFanOut || isNewlyMerged || isPromoted || isRepChanged;
            prelim.Add((acc, name, oldRep, newRep, isNewRep, payerIds, eans, mpByPayer, breakdown, status, interesting));
        }

        // Map newRep → list of "affected" accounts under it (NEWLY_MERGED + REP_CHANGED).
        // Drives ABSORB-suffix on rep rows. Also REP_CHANGED rows reference oldRep separately.
        var affectedByNewRep = prelim
            .Where(r => r.status == "NEWLY_MERGED" || r.status == "REP_CHANGED")
            .GroupBy(r => r.newRep, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => r.acc).OrderBy(a => a, AccountnumComparer.Instance).ToList(),
                StringComparer.Ordinal);

        var repsAffected = affectedByNewRep.Keys.ToHashSet(StringComparer.Ordinal);

        string ActionFor(string acc, string oldRep, string newRep, bool isRep, string status, string payerIds)
        {
            // Fan-out fragment used for reps that need payer-renumbering.
            // "{acc}" → "{acc}-1" + new {acc}-2..N.
            string FanOutFragment() =>
                $"RENAME_DEFAULT_PAYER:{acc}->{acc}-1+CREATE_PAYERS:{string.Join(",", payerIds.Split(',').Skip(1))}+RELINK_MPS_BY_CONTRACT_EAN";

            var hasFanOut = !string.IsNullOrEmpty(payerIds) && payerIds.Contains(',');

            return status switch
            {
                "NEWLY_MERGED"    => $"DELETE_CUSTOMER+MOVE_PAYERS+MOVE_MPS_TO:{newRep}",
                "PROMOTED_TO_REP" => hasFanOut
                                       ? $"CREATE_CUSTOMER_WITH_PAYERS:{payerIds}+RECEIVE_MPS_FROM:{oldRep}+RELINK_MPS_BY_CONTRACT_EAN"
                                       : $"CREATE_CUSTOMER+RECEIVE_PAYERS_AND_MPS_FROM:{oldRep}",
                "REP_CHANGED"     => $"RELINK_MPS_FROM:{oldRep}_TO:{newRep}",
                _ when isRep      => CombineRepActions(acc, hasFanOut, FanOutFragment),
                _                 => $"KEEP_LINKED_TO:{newRep}"
            };
        }

        string CombineRepActions(string acc, bool hasFanOut, Func<string> fanOutFragment)
        {
            var parts = new List<string>();
            if (affectedByNewRep.TryGetValue(acc, out var absorbed))
                parts.Add($"ABSORB:{string.Join(",", absorbed)}");
            if (hasFanOut)
                parts.Add(fanOutFragment());
            if (parts.Count == 0) return "KEEP_AS_IS";
            return string.Join("+", parts);
        }

        var rows = prelim.Select(r =>
        {
            var role = r.isRep ? "Representative" : "Member";
            var action = ActionFor(r.acc, r.oldRep, r.newRep, r.isRep, r.status, r.payerIds);
            return (r.acc, r.name, role, r.newRep, r.payerIds, r.eans, r.mpByPayer, r.breakdown,
                    r.isRep, r.status, action, r.isInteresting);
        }).ToList();

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Summary ===");
        Console.Error.WriteLine($"  Total accounts i profil:                 {options.Accounts.Length}");
        Console.Error.WriteLine($"  OLD representatives (pre-0.7.6 fp):      {oldRepCount}");
        Console.Error.WriteLine($"  NEW representatives (migreres til Settl):{repCount}");
        Console.Error.WriteLine($"  Forskel (OLD - NEW):                     {oldRepCount - repCount}");
        Console.Error.WriteLine($"  NEWLY_MERGED (mister Settl-customer):    {newlyMergedCount}");
        Console.Error.WriteLine($"  PROMOTED_TO_REP (ny Settl-customer):     {promotedCount}");
        Console.Error.WriteLine($"  REP_CHANGED (relink af MPs):             {repChangedCount}");
        Console.Error.WriteLine($"  Members (under en rep):                  {memberCount}");
        Console.Error.WriteLine($"  Representatives med payer-fan-out:       {fanOutCount}");
        Console.Error.WriteLine();

        await output.WriteLineAsync(string.Join(";", CsvHeader.Select(CsvQuote)));

        // Sort: group by representative-account, rep-row first inside the group,
        // then NEWLY_MERGED members, then UNCHANGED members; alphabetical accountnum
        // as tiebreaker. Produces a "rep + its accounts" cluster the reader can scan.
        var ordered = rows
            .OrderBy(r => r.newRep, StringComparer.Ordinal)
            .ThenBy(r => r.isRep ? 0 : 1)
            .ThenBy(r => r.status == "NEWLY_MERGED" ? 0 : 1)
            .ThenBy(r => r.acc, StringComparer.Ordinal);

        // In exploded mode, MeteringPointIdsByPayer holds just that payer's MP list (bare
        // "mp,mp"); in collapsed mode it holds the full "{payer}=[mp] | …" single cell.
        string Line(string acc, string name, string role, string newRep, string payerCol, string eanCol, string mpCol, string status, string action) =>
            string.Join(";",
                CsvQuote(acc), CsvQuote(name), CsvQuote(role),
                CsvQuote(newRep), CsvQuote(payerCol), CsvQuote(eanCol), CsvQuote(mpCol),
                CsvQuote(status), CsvQuote(action));

        var rowsWritten = 0;
        foreach (var r in ordered)
        {
            var emit = options.ShowAll
                || r.isInteresting
                || repsAffected.Contains(r.newRep);
            if (!emit) continue;

            if (options.ExplodePayers && r.breakdown.Count > 0)
            {
                foreach (var (payerId, ean, mpIds) in r.breakdown)
                {
                    await output.WriteLineAsync(
                        Line(r.acc, r.name, r.role, r.newRep, payerId, ean, mpIds, r.status, r.action));
                    rowsWritten++;
                }
            }
            else
            {
                await output.WriteLineAsync(
                    Line(r.acc, r.name, r.role, r.newRep, r.payerIds, r.eans, r.mpByPayer, r.status, r.action));
                rowsWritten++;
            }
        }
        Console.Error.WriteLine($"  Rows written: {rowsWritten}");
        return 0;
    }

    private static string CsvQuote(string? value)
    {
        var s = value ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
