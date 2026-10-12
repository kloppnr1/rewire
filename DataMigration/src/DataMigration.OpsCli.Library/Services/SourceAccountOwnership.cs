using System.Text;
using DataMigration.OpsCli.Data;

namespace DataMigration.OpsCli.Services;

internal sealed record SourceAccountOwnershipIssue(
    string Reason,
    string SourceAccount,
    string MeteringPoint,
    string SettlCustomers,
    DateOnly? CosDate = null);

internal sealed record SourceAccountOwnershipResult(
    int MappedMeteringPoints,
    int MappedSourceAccounts,
    int CosResolvedSourceAccounts,
    IReadOnlyList<SourceAccountOwnershipIssue> Issues);

/// <summary>
/// Assigns invoice source accounts through Settl's metering-point/customer links and Xellent's
/// COS-pinned metering-point/account links. No merge-group reconstruction or first-claim rule.
/// </summary>
internal static class SourceAccountOwnership
{
    internal static SourceAccountOwnershipResult Assign(
        IReadOnlyList<MigratedAccount> accounts,
        IReadOnlyDictionary<string, string> settlCustomerByMp,
        IReadOnlyCollection<(string MpId, string Account)> xellentPairs,
        IReadOnlyDictionary<string, DateOnly> settlCosByCustomer,
        IReadOnlyDictionary<string, DateOnly> xellentCosByMp,
        bool skipUnresolvedSourceAccounts = false)
    {
        var issues = new List<SourceAccountOwnershipIssue>();
        var xellentAccountsByMp = xellentPairs
            .GroupBy(p => p.MpId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.Account.Trim())
                    .Where(a => a.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

        var customersBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var meteringPointsBySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (mp, customer) in settlCustomerByMp.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!xellentAccountsByMp.TryGetValue(mp, out var sources) || sources.Length == 0)
            {
                issues.Add(new SourceAccountOwnershipIssue("NoXellentAccountAtCos", "", mp, customer));
                continue;
            }

            if (sources.Length > 1)
            {
                issues.Add(new SourceAccountOwnershipIssue(
                    "MultipleXellentAccountsAtCos", string.Join('|', sources), mp, customer));
                continue;
            }

            var source = sources[0];
            if (!customersBySource.TryGetValue(source, out var customers))
                customersBySource[source] = customers = new HashSet<string>(StringComparer.Ordinal);
            customers.Add(customer);
            if (!meteringPointsBySource.TryGetValue(source, out var mps))
                meteringPointsBySource[source] = mps = new HashSet<string>(StringComparer.Ordinal);
            mps.Add(mp);
        }

        var ownerBySource = new Dictionary<string, string>(StringComparer.Ordinal);
        var cosResolved = 0;
        foreach (var (source, customers) in customersBySource.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (customers.Count == 1)
            {
                ownerBySource[source] = customers.Single();
                continue;
            }

            // The Xellent account's COS is the earliest COS-1-matched contract end among its MPs,
            // consistent with the account COS used by the reconcile pipeline. Match it against the
            // Settl customer's own earliest MP COS. Never break a tie by MP count or input order.
            var mps = meteringPointsBySource[source];
            var xellentDates = mps.Select(mp => xellentCosByMp.GetValueOrDefault(mp))
                .Where(d => d != default).ToArray();
            var matches = xellentDates.Length == mps.Count
                ? customers.Where(c => settlCosByCustomer.TryGetValue(c, out var cos)
                    && cos == xellentDates.Min()).ToArray()
                : [];
            if (matches.Length == 1)
            {
                ownerBySource[source] = matches[0];
                cosResolved++;
                continue;
            }
            issues.Add(new SourceAccountOwnershipIssue(
                "NoUniqueCosMatchedSettlCustomerForSourceAccount",
                source,
                string.Join('|', mps.Order(StringComparer.Ordinal)),
                string.Join('|', customers.Order(StringComparer.Ordinal))));
        }

        var knownCustomers = accounts.Select(a => a.SettlAccountNumber).ToHashSet(StringComparer.Ordinal);
        foreach (var customer in customersBySource.Values.SelectMany(c => c).Distinct(StringComparer.Ordinal))
        {
            if (!knownCustomers.Contains(customer))
                issues.Add(new SourceAccountOwnershipIssue("SettlCustomerMissingFromState", "", "", customer));
        }

        if (issues.All(i => i.Reason == "NoXellentAccountAtCos"
            || (skipUnresolvedSourceAccounts
                && i.Reason == "NoUniqueCosMatchedSettlCustomerForSourceAccount")))
        {
            var sourcesByCustomer = ownerBySource
                .Select(kv => (Source: kv.Key, Customer: kv.Value))
                .GroupBy(x => x.Customer, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Source).Order(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
            foreach (var account in accounts)
                account.SourceAccounts = sourcesByCustomer.GetValueOrDefault(account.SettlAccountNumber) ?? [];
        }

        return new SourceAccountOwnershipResult(
            settlCustomerByMp.Count - issues.Count(i => i.Reason is "NoXellentAccountAtCos" or "MultipleXellentAccountsAtCos"),
            customersBySource.Count,
            cosResolved,
            issues);
    }

    internal static string BuildIssueCsv(IReadOnlyList<SourceAccountOwnershipIssue> issues)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Reason;SourceAccount;MeteringPoint;SettlCustomers;CosDate");
        foreach (var issue in issues
            .OrderBy(i => i.Reason, StringComparer.Ordinal)
            .ThenBy(i => i.SourceAccount, StringComparer.Ordinal)
            .ThenBy(i => i.MeteringPoint, StringComparer.Ordinal))
        {
            csv.Append(Csv(issue.Reason)).Append(';')
                .Append(Csv(issue.SourceAccount)).Append(';')
                .Append(Csv(issue.MeteringPoint)).Append(';')
                .Append(Csv(issue.SettlCustomers)).Append(';')
                .AppendLine(issue.CosDate?.ToString("yyyy-MM-dd") ?? "");
        }
        return csv.ToString();
    }

    internal static string IssueReportPath(string stateOutputPath)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-ownership-issues.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }

    private static string Csv(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
}
