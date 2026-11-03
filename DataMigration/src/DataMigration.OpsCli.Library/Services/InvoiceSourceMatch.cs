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
    /// on unrelated accounts, never matches. Nor does a source account that is a Settl customer
    /// itself: it was not merged into anyone, so a shared CVR gives no other customer a claim on it.
    /// The Settl number as written is tried before its unpadded forms, so a real six-digit account
    /// is never mistaken for a padded shorter one.
    /// </summary>
    internal static bool SameCvr(string customerNumber, string source,
        IReadOnlyDictionary<string, string> cvrByAccount, IReadOnlySet<string> settlCustomers, string? env)
    {
        if (settlCustomers.Contains(CustomerNumberPolicy.PadForEnv(source, env, out _))) return false;
        if (!cvrByAccount.TryGetValue(source, out var sourceCvr)) return false;
        var cvr = sourceCvr.Trim();
        if (cvr.Length == 0 || cvr.All(c => c == cvr[0])) return false;
        var account = CustomerNumberPolicy.AccountCandidates(customerNumber, env)
            .FirstOrDefault(cvrByAccount.ContainsKey);
        return account is not null && cvrByAccount[account].Trim() == cvr;
    }

    /// <summary>
    /// True when <paramref name="account"/> is a Settl customer under its own number, and that
    /// number is not <paramref name="claimingCustomer"/>. Such an account was not merged into the
    /// claiming customer at migration, so today's representative group gives that customer no
    /// claim on it. An account number that cannot be padded is never a Settl customer.
    /// </summary>
    internal static bool IsOtherSettlCustomer(string account, string claimingCustomer,
        IReadOnlySet<string> settlCustomers, string? env)
    {
        string own;
        try { own = CustomerNumberPolicy.PadForEnv(account, env, out _); }
        catch (InvalidOperationException) { return false; }
        return own != claimingCustomer && settlCustomers.Contains(own);
    }

    internal static string[] ApprovedSources(Kind kind, string source,
        string representative, IReadOnlyDictionary<string, string[]> membersByRepresentative) => kind switch
    {
        Kind.Direct => [source],
        Kind.Representative => membersByRepresentative.GetValueOrDefault(representative) ?? [source],
        _ => []
    };
}
