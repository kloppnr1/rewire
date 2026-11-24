namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Field-path tagging for a specific historical billing/payment config version. The multi-version
/// reconcile scan tags a leaf that belongs to a historical version as
/// <c>"BillingConfiguration@2023-05-01.JointInvoice"</c>; the current/latest version keeps the bare
/// <c>"BillingConfiguration."</c> prefix (so single-version MPs are byte-identical to the old scan).
/// Apply parses the tag back to PUT that version's own Start in place. One type owns the format so the
/// emit (scan) and parse (apply) sides never drift.
/// </summary>
internal static class MpConfigVersionTag
{
    private static readonly string[] ConfigNames = ["BillingConfiguration", "PaymentConfiguration"];

    /// <summary>
    /// The field prefix for a historical version, e.g. <c>("BillingConfiguration", 2023-05-01)</c> →
    /// <c>"BillingConfiguration@2023-05-01."</c>. The leaf name is appended by the caller (the same way
    /// the bare <c>"BillingConfiguration."</c> prefix is used for the current version).
    /// </summary>
    public static string Prefix(string configName, DateOnly versionStart)
        => $"{configName}@{versionStart:yyyy-MM-dd}.";

    /// <summary>
    /// Splits a version-tagged field into its version Start and the bare field path
    /// (<c>"BillingConfiguration@2023-05-01.JointInvoice"</c> → <c>(2023-05-01, "BillingConfiguration.JointInvoice")</c>).
    /// Untagged fields — the current/latest version, or any non-config field — return
    /// <c>(null, field)</c> unchanged.
    /// </summary>
    public static (DateOnly? Version, string Field) Split(string field)
    {
        foreach (var cfg in ConfigNames)
        {
            var at = cfg + "@";
            if (!field.StartsWith(at, StringComparison.Ordinal)) continue;
            var dot = field.IndexOf('.', at.Length);
            if (dot > 0 && DateOnly.TryParseExact(field[at.Length..dot], "yyyy-MM-dd", out var d))
                return (d, cfg + "." + field[(dot + 1)..]);
        }
        return (null, field);
    }
}
