namespace DataMigration.Migration.Configuration;

/// <summary>
/// Key normalisation shared by every lookup keyed on a Xellent contract template. Xellent spells the
/// same template both with an underscore (<c>V_Privat</c>) and with a space (<c>V Privat</c>) — both
/// spellings are live in production on the same product — so neither may miss.
///
/// <para>Lives here rather than in OpsCli because both tables that key on a contract template need
/// it: the create path's template+product contract map (this assembly) and OpsCli's
/// <c>settlement-config.json</c> template+product segment table, which calls straight into this
/// through <c>SettlementConfigKeys.NormalizeTemplate</c>. One implementation, so the two tables
/// cannot disagree about which spellings are the same template.</para>
/// </summary>
public static class ContractTemplateKey
{
    public static string Normalize(string template) => template.Replace(' ', '_');
}

/// <summary>
/// How a product key in a template+product table matches a Xellent <c>Productnum</c>: an exact name,
/// a <c>Spot*</c> prefix, or <c>*</c> for any product. Matching is case-insensitive throughout — the
/// tables are hand-typed while Xellent's product and template codes are not normalised, and a run
/// where an account keeps its contract only because somebody typed a JSON key the same way as
/// Xellent did is not a run anybody can reason about.
///
/// <para><see cref="SpecificityRank"/> exists so the ordering is decided once, at load time: exact
/// beats prefix beats catch-all, whatever order the file happens to be written in.</para>
/// </summary>
public static class ProductPattern
{
    /// <summary>Any product.</summary>
    public static bool IsCatchAll(string pattern) => pattern == "*";

    /// <summary>Prefix form (<c>Spot*</c>), which matches every product starting with the stem.</summary>
    public static bool IsPrefix(string pattern) => !IsCatchAll(pattern) && pattern.EndsWith('*');

    public static bool Matches(string pattern, string productNum) =>
        IsCatchAll(pattern)
        || (IsPrefix(pattern) && productNum.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase))
        || string.Equals(pattern, productNum, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lower sorts first: 0 exact, 1 prefix, 2 catch-all.</summary>
    public static int SpecificityRank(string pattern) => IsCatchAll(pattern) ? 2 : IsPrefix(pattern) ? 1 : 0;
}
