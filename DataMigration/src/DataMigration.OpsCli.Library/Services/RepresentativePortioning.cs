using DataMigration.Domain.Xellent;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Keeps <c>AccountRepresentativeResolver</c> inside SQL Server's query-plan budget when a caller
/// hands it a whole-population account list.
///
/// <para><b>Why this exists.</b> The resolver runs a four-table left join plus a five-table inner
/// join with the account list expanded inline — Xellent runs at compatibility level 110 (SQL Server
/// 2012), so EF cannot use <c>OPENJSON</c> and writes every account as a literal in the <c>IN</c>
/// list. The statement therefore grows with the account count until the planner gives up with
/// "the query processor ran out of internal resources and could not produce a query plan". That is
/// a plan-compilation failure, not a timeout: it happens in seconds and no batch size on any other
/// query prevents it.</para>
///
/// <para><b>Cut on CVR classes, never on raw accounts.</b> Both of the resolver's union edge keys
/// are CVR-prefixed and union is only ever applied inside one edge bucket, so a merge group can
/// never span two CVR classes. Splitting a group across two calls would turn one Settl customer
/// into two and silently change the representative for the rest. The packing rule itself is
/// <see cref="CosStatePlanner.BuildCvrPortions"/> — the same one <c>generate-state-from-settl</c>
/// uses, deliberately not a second copy that could drift from it.</para>
///
/// <para>Measured ceiling, which callers are guarded and which are not, and the rules for writing
/// the next guard: <c>docs/representative-resolver-scale-limit.md</c>. Water and heating
/// populations will reach the same size.</para>
/// </summary>
public static class RepresentativePortioning
{
    /// <summary>
    /// Largest account list handed to the resolver in one call.
    ///
    /// <para>Measured against production (AXDB50, compat 110) with the resolver's two statements and
    /// a literal <c>IN</c> list: 30.000 accounts still produced a plan, 35.000 did not (the
    /// five-table legal-contact join is the one that gives up first). The 2026-08-31
    /// <c>generate-state-from-settl</c> run bracketed the same ceiling from the other side — 13.343
    /// planned, at least 41.600 failed — and chose 15000. Kept at 15000 here so both callers share
    /// one number, comfortably below the lowest observed failure with room for the join fan-out to
    /// vary with the data.</para>
    ///
    /// <para>A soft cap: a single CVR class larger than this is still resolved whole, because
    /// splitting one would change the answer.</para>
    /// </summary>
    public const int PortionSize = 15000;

    /// <summary>
    /// Combines the per-portion resolutions into the one a single call would have produced.
    ///
    /// <para>Exact, not approximate: the portions partition the account list and no merge group
    /// spans two of them, so every per-account map has disjoint keys and every count is additive.
    /// <c>Accounts</c> is re-sorted the same way the resolver sorts its own result, so a portioned
    /// run and a single-call run produce the same list in the same order.</para>
    /// </summary>
    public static AccountRepresentativeResolution Merge(
        IReadOnlyList<AccountRepresentativeResolution> parts)
    {
        if (parts.Count == 1) return parts[0];

        var nameByAccount = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var representativeByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        var fingerprintByAccount = new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal);
        var accounts = new List<string>();
        var mergedCount = 0;

        foreach (var part in parts)
        {
            mergedCount += part.MergedCount;
            accounts.AddRange(part.Accounts);
            foreach (var (key, value) in part.NameByAccount) nameByAccount[key] = value;
            foreach (var (key, value) in part.RepresentativeByAccount) representativeByAccount[key] = value;
            foreach (var (key, value) in part.FingerprintByAccount) fingerprintByAccount[key] = value;
        }

        return new AccountRepresentativeResolution(
            [.. accounts.OrderBy(a => a, StringComparer.Ordinal)],
            mergedCount,
            nameByAccount,
            representativeByAccount,
            fingerprintByAccount);
    }
}
