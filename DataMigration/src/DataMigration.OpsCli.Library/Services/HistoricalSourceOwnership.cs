using System.Text;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Data;

namespace DataMigration.OpsCli.Services;

internal sealed record HistoricalSourceClaim(string SourceAccount, string SettlCustomer, DateOnly Cos);

internal sealed record HistoricalSourceDecision(
    string SourceAccount,
    string Disposition,
    string AssignedCustomer,
    string CandidateCustomers,
    string CandidateCosDates,
    string CurrentMeteringPointCustomers = "",
    DateOnly? EarliestXellentContractEnd = null,
    DateOnly? LatestXellentContractEnd = null);

internal sealed record HistoricalSourceOwnershipResult(
    IReadOnlyList<HistoricalSourceDecision> Decisions,
    IReadOnlyList<SourceAccountOwnershipIssue> Issues);

/// <summary>
/// Adds historical merge-group members that the COS-1 MP lookup cannot see. Direct MP ownership
/// always wins. A historical-only source requires one merge-group claimant. The current Settl
/// customer of a historical MP is reported, but cannot prove who owned it years earlier.
/// </summary>
internal static class HistoricalSourceOwnership
{
    internal static IReadOnlyDictionary<string, string> FindXellentRepresentatives(
        IReadOnlyList<MigratedAccount> accounts,
        IReadOnlyDictionary<string, string> cvrByXellentAccount,
        string? env)
    {
        var representatives = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            var representative = CustomerNumberPolicy.AccountCandidates(account.SettlAccountNumber, env)
                .FirstOrDefault(candidate => account.SourceAccounts.Contains(candidate)
                    && cvrByXellentAccount.ContainsKey(candidate));
            if (representative is not null)
                representatives[account.SettlAccountNumber] = representative;
        }
        return representatives;
    }

    internal static HistoricalSourceOwnershipResult Assign(
        IReadOnlyList<MigratedAccount> accounts,
        IEnumerable<HistoricalSourceClaim> claims,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> meteringPointCustomersBySource,
        IReadOnlySet<string> directlyObservedSources,
        bool skipUnresolved,
        IReadOnlyDictionary<string, string>? cvrByXellentAccount = null,
        IReadOnlyDictionary<string, string>? representativeBySettlCustomer = null)
    {
        var direct = accounts.SelectMany(a => a.SourceAccounts)
            .ToHashSet(StringComparer.Ordinal);
        var byCustomer = accounts.ToDictionary(a => a.SettlAccountNumber, StringComparer.Ordinal);
        var decisions = new List<HistoricalSourceDecision>();
        var issues = new List<SourceAccountOwnershipIssue>();

        foreach (var group in claims.Where(c => !direct.Contains(c.SourceAccount)
                && !directlyObservedSources.Contains(c.SourceAccount))
            .GroupBy(c => c.SourceAccount, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var candidates = group.Select(c => c.SettlCustomer)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var dates = group.Select(c => c.Cos).Distinct().Order().ToArray();
            var mpCustomers = meteringPointCustomersBySource.GetValueOrDefault(group.Key) ?? [];
            var mpCustomerText = string.Join('|', mpCustomers.Order(StringComparer.Ordinal));
            var chosen = candidates.Length == 1 ? candidates[0] : null;
            var sameCvrResolution = false;
            if (chosen is null && cvrByXellentAccount is not null
                && representativeBySettlCustomer is not null
                && cvrByXellentAccount.TryGetValue(group.Key, out var sourceCvr)
                && !string.IsNullOrWhiteSpace(sourceCvr))
            {
                var representatives = candidates.Select(customer =>
                {
                    if (!representativeBySettlCustomer.TryGetValue(customer, out var account)
                        || !cvrByXellentAccount.TryGetValue(account, out var cvr)
                        || !string.Equals(cvr.Trim(), sourceCvr.Trim(), StringComparison.Ordinal)
                        || !long.TryParse(account, out var number))
                        return (Customer: customer, Account: account, Number: (long?)null);
                    return (Customer: customer, Account: account, Number: (long?)number);
                }).ToArray();
                if (representatives.All(r => r.Number.HasValue)
                    && representatives.Select(r => r.Account).Distinct(StringComparer.Ordinal).Count() == candidates.Length)
                {
                    chosen = representatives.MinBy(r => r.Number)!.Customer;
                    sameCvrResolution = true;
                }
            }
            if (chosen is not null && byCustomer.TryGetValue(chosen, out var owner))
            {
                owner.SourceAccounts.Add(group.Key);
                decisions.Add(new HistoricalSourceDecision(group.Key,
                    sameCvrResolution ? "AssignedSameCvrLowestXellentAccount" : "AssignedUniqueHistoricalGroup",
                    chosen, string.Join('|', candidates),
                    string.Join('|', dates.Select(d => d.ToString("yyyy-MM-dd"))), mpCustomerText));
            }
            else
            {
                var candidateText = string.Join('|', candidates);
                issues.Add(new SourceAccountOwnershipIssue(
                    "HistoricalSourceMultipleSettlCustomers",
                    group.Key, "", candidateText));
                decisions.Add(new HistoricalSourceDecision(group.Key,
                    skipUnresolved ? "SkippedAmbiguousHistoricalOwner" : "BlockedAmbiguousHistoricalOwner",
                    "", candidateText, string.Join('|', dates.Select(d => d.ToString("yyyy-MM-dd"))),
                    mpCustomerText));
            }
        }

        foreach (var account in accounts)
            account.SourceAccounts = [.. account.SourceAccounts.Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)];
        return new HistoricalSourceOwnershipResult(decisions, issues);
    }

    internal static string BuildCsv(IReadOnlyList<HistoricalSourceDecision> decisions)
    {
        var csv = new StringBuilder();
        csv.AppendLine("SourceAccount;Disposition;AssignedCustomer;CandidateCustomers;CandidateCosDates;CurrentMeteringPointCustomers;EarliestXellentContractEnd;LatestXellentContractEnd");
        foreach (var row in decisions.OrderBy(d => d.SourceAccount, StringComparer.Ordinal))
            csv.Append(row.SourceAccount).Append(';')
                .Append(row.Disposition).Append(';')
                .Append(row.AssignedCustomer).Append(';')
                .Append(row.CandidateCustomers).Append(';')
                .Append(row.CandidateCosDates).Append(';')
                .Append(row.CurrentMeteringPointCustomers).Append(';')
                .Append(row.EarliestXellentContractEnd?.ToString("yyyy-MM-dd")).Append(';')
                .AppendLine(row.LatestXellentContractEnd?.ToString("yyyy-MM-dd"));
        return csv.ToString();
    }

    internal static string ReportPath(string stateOutputPath)
    {
        var directory = Path.GetDirectoryName(stateOutputPath);
        var filename = $"{Path.GetFileNameWithoutExtension(stateOutputPath)}-historical-sources.csv";
        return string.IsNullOrEmpty(directory) ? filename : Path.Combine(directory, filename);
    }
}
