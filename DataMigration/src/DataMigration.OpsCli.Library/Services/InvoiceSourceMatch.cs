using DataMigration.Migration.Services;

namespace DataMigration.OpsCli.Services;

/// <summary>Compares the COS-pinned Xellent account with Settl's linked customer number.</summary>
internal static class InvoiceSourceMatch
{
    internal enum Kind { None, Direct, Representative, SameCvr }

    internal static Kind Match(string customerNumber, string source, string representative, string? env)
    {
        if (CustomerNumberPolicy.PadForEnv(source, env, out _) == customerNumber) return Kind.Direct;
        return CustomerNumberPolicy.PadForEnv(representative, env, out _) == customerNumber
            ? Kind.Representative : Kind.None;
    }

    /// <summary>
    /// Accounts merged at migration under an older rule can sit under a Settl customer today's
    /// resolver no longer groups them with. True when that customer number is a Xellent account
    /// with the source account's CVR. A blank CVR, or the placeholder 11111111 that Xellent holds
    /// on unrelated accounts, never matches. The Settl number as written is tried before its
    /// unpadded forms, so a real six-digit account is never mistaken for a padded shorter one.
    /// </summary>
    internal static bool SameCvr(string customerNumber, string source,
        IReadOnlyDictionary<string, string> cvrByAccount, string? env)
    {
        if (!cvrByAccount.TryGetValue(source, out var sourceCvr)) return false;
        var cvr = sourceCvr.Trim();
        if (cvr.Length == 0 || cvr.All(c => c == cvr[0])) return false;
        var account = CustomerNumberPolicy.AccountCandidates(customerNumber, env)
            .FirstOrDefault(cvrByAccount.ContainsKey);
        return account is not null && cvrByAccount[account].Trim() == cvr;
    }

    internal static string[] ApprovedSources(Kind kind, string source,
        string representative, IReadOnlyDictionary<string, string[]> membersByRepresentative) => kind switch
    {
        Kind.Direct => [source],
        Kind.Representative => membersByRepresentative.GetValueOrDefault(representative) ?? [source],
        _ => []
    };
}
