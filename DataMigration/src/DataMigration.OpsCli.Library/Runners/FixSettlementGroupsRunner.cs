using System.Text;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class FixSettlementGroupsRunOptions
{
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public string DeliveryCategory { get; set; } = "El-ekstern";
    public DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }
    public Dictionary<string, string> SettlementGroupMap { get; set; } = [];
    public bool DryRun { get; set; }
    /// <summary>
    /// When set, the runner writes a semicolon-separated CSV with one row per (customer, group)
    /// pair (Outcome = WouldAdd / AlreadyMember / NotMigrated). UTF-8 with BOM for Excel.
    /// CSV is always complete — Limit/Offset only restrict the Settl-API mutation phase.
    /// </summary>
    public string? OutputCsvPath { get; set; }

    /// <summary>
    /// Maximum number of WouldAdd-pairs to actually post to Settl. Null = unlimited.
    /// Window is applied across the globally-sorted (GroupName → Account) WouldAdd set,
    /// so re-running with the same input + same Limit/Offset hits exactly the same pairs.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Number of WouldAdd-pairs to skip before applying Limit. Default 0.
    /// Pairs outside [Offset, Offset+Limit) are still listed in the CSV but not mutated.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Explicit selection of (GroupName, Account) WouldAdd pairs to mutate. Mutually
    /// exclusive with Limit/Offset — set one or the other, not both. Pairs not present
    /// in the resolved WouldAdd set count as Failed with a "not in WouldAdd set" error.
    /// Match: GroupName OrdinalIgnoreCase, Account Ordinal.
    /// </summary>
    public IReadOnlyList<(string GroupName, string Account)>? SelectedPairs { get; set; }

    /// <summary>Target Settl environment ("production" padder til 6 cifre; alle andre er no-op). See <see cref="CustomerNumberPolicy"/>.</summary>
    public string? Env { get; set; }
}

internal sealed record FixSettlementGroupsRow(
    string GroupName,
    string Outcome,
    string Account,
    string BillingPlanNum,
    string Reason);

/// <summary>
/// Adds already-migrated customers to existing Settl Settlement Groups based on the
/// profile's <c>Mappings.SettlementGroup</c>-mapping (BillingPlanNum → GroupName).
/// Mirrors <c>SettlClient.AssignSettlementGroupsAsync</c> but stand-alone for re-runs
/// when SettlementGroup-membership drifted from the desired state.
///
/// <para>Verdo SettlementGroups are keyed by <c>CustomerId</c>, not by
/// <c>MeteringPointId</c> — Settl rejects MP-based membership in practice. Each
/// representative customer (CVR+Fingerprint merge winner) is added once per group
/// regardless of how many MPs the customer has under that billing plan.</para>
///
/// <para>Does NOT create groups in Settl — only adds customers to groups that already
/// exist (and fails if a target group is missing).</para>
/// </summary>
public class FixSettlementGroupsRunner
{
    private const string CountryCode = "DK";
    private readonly IXellentDomainLoader _loader;
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlSettlementGroupClient _settlementGroupClient;
    private readonly Action<string> _log;

    public FixSettlementGroupsRunner(
        IXellentDomainLoader loader,
        ISettlCustomerClient customerClient,
        ISettlSettlementGroupClient settlementGroupClient,
        Action<string>? log = null)
    {
        _loader = loader;
        _customerClient = customerClient;
        _settlementGroupClient = settlementGroupClient;
        _log = log ?? Console.Error.WriteLine;
    }

    public async Task<int> RunAsync(FixSettlementGroupsRunOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Accounts.Length == 0)
        {
            _log("ERROR: --account or --profile required");
            return 1;
        }
        if (options.SettlementGroupMap.Count == 0)
        {
            _log("ERROR: profile.Mappings.SettlementGroup is empty — nothing to assign");
            return 1;
        }
        if (options.Limit is < 1)
        {
            _log("ERROR: --limit must be >= 1");
            return 1;
        }
        if (options.Offset < 0)
        {
            _log("ERROR: --offset must be >= 0");
            return 1;
        }
        if (options.SelectedPairs is { Count: > 0 } && (options.Limit.HasValue || options.Offset > 0))
        {
            _log("ERROR: SelectedPairs cannot be combined with Limit/Offset — pick one selection mode");
            return 1;
        }

        // Upfront validation: resolve target group names → Guids. Fail before Xellent load.
        var targetGroupNames = options.SettlementGroupMap.Values
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groupIdByName = new System.Collections.Concurrent.ConcurrentDictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var missingGroups = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            targetGroupNames,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (groupName, ct) =>
            {
                var (group, _) = await _settlementGroupClient.FindByNameAsync(CountryCode, groupName, ct);
                if (group != null && group.IsActive)
                    groupIdByName[groupName] = group.Id;
                else
                    missingGroups.Add(groupName);
            });

        if (!missingGroups.IsEmpty)
        {
            _log($"ERROR: {missingGroups.Count} target group(s) not found as active in Settl:");
            foreach (var name in missingGroups.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                _log($"  MISSING: '{name}'");

            var (allGroups, listAllError) = await _settlementGroupClient.ListAllAsync(CountryCode, cancellationToken);
            if (allGroups != null)
            {
                var activeGroups = allGroups.Where(g => g.IsActive).ToList();
                _log($"Available active groups in Settl ({activeGroups.Count}):");
                foreach (var g in activeGroups.OrderBy(g => g.GroupName, StringComparer.OrdinalIgnoreCase))
                    _log($"  {g.GroupName}");
            }
            else
            {
                _log($"  (could not list available groups: {listAllError})");
            }
            return 1;
        }

        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: MapDeliveryCategoryToCommodity(options.DeliveryCategory),
            LegacyTg4Merge: options.LegacyTg4Merge);
        var loadResult = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);

        // Resolve (groupName → set of representative customer Accountnums) by walking each
        // customer's contracts and mapping BillingPlanCode → GroupName via profile mapping.
        // The XellentDomainMapper has already merged non-rep accounts into their representatives,
        // so customer.AccountNumber is always the representative's number — Settl-lookup-safe.
        var accountsByGroup = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var billingPlansByAccountGroup = new Dictionary<(string Account, string GroupName), string>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["no-billing-plan"] = 0,
            ["unmapped-billing-plan"] = 0,
        };

        foreach (var customer in loadResult.Customers)
        {
            foreach (var contract in customer.Contracts)
            {
                var planCode = contract.Billing?.PlanCode;
                if (string.IsNullOrEmpty(planCode)) { skipped["no-billing-plan"]++; continue; }
                if (!options.SettlementGroupMap.TryGetValue(planCode, out var groupName))
                {
                    skipped["unmapped-billing-plan"]++;
                    continue;
                }
                if (!accountsByGroup.TryGetValue(groupName, out var set))
                    accountsByGroup[groupName] = set = new HashSet<string>(StringComparer.Ordinal);
                if (set.Add(customer.AccountNumber))
                    billingPlansByAccountGroup[(customer.AccountNumber, groupName)] = planCode;
            }
        }

        _log($"Resolved {accountsByGroup.Sum(g => g.Value.Count)} (account, group) pair(s) across {accountsByGroup.Count} group(s) " +
             $"(skipped: {string.Join(", ", skipped.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}"))})");

        // Per-pair CSV rows.
        var csvRows = options.OutputCsvPath != null
            ? new System.Collections.Concurrent.ConcurrentBag<FixSettlementGroupsRow>()
            : null;

        var added = 0;
        var alreadyMember = 0;
        var notMigrated = 0;
        var failed = 0;
        const int MaxParallelism = 16;

        // Phase 1: resolve outcomes per group (build full CSV) and collect WouldAdd pairs.
        // Mutation is deferred to Phase 2 so Limit/Offset can be applied globally.
        var wouldAddByGroup = new Dictionary<string, List<(string Account, Guid CustomerId)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (groupName, accounts) in accountsByGroup)
        {
            var groupId = groupIdByName[groupName];

            var (existingMemberIds, listError) = await _settlementGroupClient.ListCustomerIdsAsync(groupId, cancellationToken);
            if (existingMemberIds == null)
            {
                _log($"  FAILED '{groupName}': could not list existing customer-members ({listError}) — {accounts.Count} account(s) skipped");
                failed += accounts.Count;
                continue;
            }
            _log($"  Group '{groupName}': {existingMemberIds.Count} existing customer-member(s) — checking {accounts.Count} account(s) ({MaxParallelism}-way parallel)...");

            var toAddBag = new System.Collections.Concurrent.ConcurrentBag<(string Account, Guid CustomerId)>();
            var groupAlreadyMember = 0;
            var groupNotMigrated = 0;
            var processed = 0;
            await Parallel.ForEachAsync(
                accounts,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
                async (account, ct) =>
                {
                    var billingPlan = billingPlansByAccountGroup.GetValueOrDefault((account, groupName), "");
                    var settlCustomerNumber = CustomerNumberPolicy.PadForEnv(account, options.Env, out var wasPadded);
                    if (wasPadded)
                        _log($"  WARNING: CustomerNumber {account} padded to {settlCustomerNumber} for env");
                    var (customerInfo, _) = await _customerClient.SearchByCustomerNumberAsync(settlCustomerNumber, CountryCode, ct);
                    if (customerInfo == null)
                    {
                        Interlocked.Increment(ref groupNotMigrated);
                        _log($"  SKIP {account}: not migrated to Settl yet (no customer-record found)");
                        csvRows?.Add(new FixSettlementGroupsRow(groupName, "NotMigrated", account, billingPlan, ""));
                    }
                    else if (existingMemberIds.Contains(customerInfo.Id))
                    {
                        Interlocked.Increment(ref groupAlreadyMember);
                        csvRows?.Add(new FixSettlementGroupsRow(groupName, "AlreadyMember", account, billingPlan, ""));
                    }
                    else
                    {
                        toAddBag.Add((account, customerInfo.Id));
                        csvRows?.Add(new FixSettlementGroupsRow(groupName, "WouldAdd", account, billingPlan, ""));
                    }

                    var done = Interlocked.Increment(ref processed);
                    if (done % 200 == 0)
                        _log($"    progress: {done}/{accounts.Count} (toAdd={toAddBag.Count}, alreadyMember={groupAlreadyMember}, notMigrated={groupNotMigrated})");
                });

            alreadyMember += groupAlreadyMember;
            notMigrated += groupNotMigrated;
            if (toAddBag.Count > 0)
                wouldAddByGroup[groupName] = toAddBag.ToList();
        }

        // Phase 2: apply Limit/Offset or SelectedPairs to the globally-sorted WouldAdd set,
        // then mutate (or count as dry-run) per group. Sort order matches the CSV — GroupName →
        // Account — so the same Limit/Offset always picks the same pairs across re-runs.
        var allWouldAdd = wouldAddByGroup
            .SelectMany(kv => kv.Value.Select(v => (GroupName: kv.Key, v.Account, v.CustomerId)))
            .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Account, StringComparer.Ordinal)
            .ToList();

        List<(string GroupName, string Account, Guid CustomerId)> slicedPairs;
        int skippedByWindow;
        if (options.SelectedPairs is { Count: > 0 } selectedPairs)
        {
            var selectedSet = new HashSet<(string GroupName, string Account)>(
                selectedPairs.Select(p => (p.GroupName, p.Account)),
                new SelectedPairComparer());

            slicedPairs = allWouldAdd
                .Where(x => selectedSet.Contains((x.GroupName, x.Account)))
                .ToList();
            skippedByWindow = 0;

            var matched = new HashSet<(string GroupName, string Account)>(
                slicedPairs.Select(x => (x.GroupName, x.Account)),
                new SelectedPairComparer());
            var unknownSelections = selectedPairs.Where(p => !matched.Contains((p.GroupName, p.Account))).ToList();
            foreach (var p in unknownSelections)
            {
                _log($"  FAILED: selected pair '{p.GroupName}' / '{p.Account}' not in WouldAdd set");
                failed++;
            }

            _log($"--- Selection: {slicedPairs.Count}/{selectedPairs.Count} pair(s) matched WouldAdd; " +
                 $"{unknownSelections.Count} unknown ---");
        }
        else
        {
            slicedPairs = allWouldAdd
                .Skip(options.Offset)
                .Take(options.Limit ?? int.MaxValue)
                .ToList();
            skippedByWindow = allWouldAdd.Count - slicedPairs.Count;

            if (options.Limit.HasValue || options.Offset > 0)
            {
                _log($"--- Window: offset={options.Offset} limit={(options.Limit?.ToString() ?? "unlimited")} → " +
                     $"applying to {slicedPairs.Count}/{allWouldAdd.Count} WouldAdd pair(s); {skippedByWindow} outside window ---");
            }
        }

        foreach (var byGroup in slicedPairs.GroupBy(p => p.GroupName, StringComparer.OrdinalIgnoreCase))
        {
            var groupName = byGroup.Key;
            var groupId = groupIdByName[groupName];
            var toAdd = byGroup.Select(p => p.CustomerId).ToList();

            if (options.DryRun)
            {
                _log($"  DRY-RUN '{groupName}' [{groupId}]: would add {toAdd.Count} customer(s)");
                added += toAdd.Count;
                continue;
            }

            var (success, addError) = await _settlementGroupClient.AddCustomersAsync(
                groupId,
                new AddSettlementGroupCustomersRequest
                {
                    Customers = toAdd.Select(id => new SettlementGroupCustomerEntry
                    {
                        CustomerId = id,
                        IncludeCustomerSubsidiaries = false
                    }).ToList(),
                },
                cancellationToken);

            if (!success)
            {
                _log($"  FAILED '{groupName}': {addError} — {toAdd.Count} customer(s) not added");
                failed += toAdd.Count;
                continue;
            }

            added += toAdd.Count;
            _log($"  Group '{groupName}': added {toAdd.Count} customer(s)");
        }

        _log("--- Summary ---");
        _log($"  Added:           {added}{(options.DryRun ? " (dry-run — no API calls made)" : "")}");
        _log($"  Already member:  {alreadyMember}");
        _log($"  Not migrated:    {notMigrated}");
        _log($"  Failed:          {failed}");
        if (skippedByWindow > 0)
            _log($"  Outside window:  {skippedByWindow} (WouldAdd pair(s) skipped by --limit/--offset; still in CSV)");

        if (csvRows != null && options.OutputCsvPath != null)
        {
            var rowCount = WriteCsv(csvRows, options.OutputCsvPath);
            _log($"Wrote {rowCount} rows to {options.OutputCsvPath}");
        }

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Writes one row per processed (account, group) as semicolon-separated UTF-8 CSV (with BOM
    /// so dansk Excel opens it cleanly). Stable sort: GroupName → Outcome → Account.
    /// </summary>
    private static int WriteCsv(
        System.Collections.Concurrent.ConcurrentBag<FixSettlementGroupsRow> rows,
        string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sorted = rows
            .OrderBy(r => r.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Outcome, StringComparer.Ordinal)
            .ThenBy(r => r.Account, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Account;BillingPlanNum;GroupName;Outcome;Reason");
        foreach (var r in sorted)
        {
            sb.Append(Csv(r.Account)).Append(';')
              .Append(Csv(r.BillingPlanNum)).Append(';')
              .Append(Csv(r.GroupName)).Append(';')
              .Append(Csv(r.Outcome)).Append(';')
              .Append(Csv(r.Reason))
              .Append('\n');
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return sorted.Count;
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny([';', '"', '\n', '\r']) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static Commodity MapDeliveryCategoryToCommodity(string deliveryCategory) => deliveryCategory switch
    {
        MigrationConstants.DeliveryCategoryElectricity => Commodity.Electricity,
        MigrationConstants.DeliveryCategoryWater => Commodity.Water,
        MigrationConstants.DeliveryCategoryHeating => Commodity.Heat,
        MigrationConstants.DeliveryCategoryCharge => Commodity.EvCharging,
        _ => Commodity.Electricity
    };

    private sealed class SelectedPairComparer : IEqualityComparer<(string GroupName, string Account)>
    {
        public bool Equals((string GroupName, string Account) x, (string GroupName, string Account) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.GroupName, y.GroupName)
            && StringComparer.Ordinal.Equals(x.Account, y.Account);

        public int GetHashCode((string GroupName, string Account) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GroupName),
                StringComparer.Ordinal.GetHashCode(obj.Account));
    }
}
