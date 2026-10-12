namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// Curated Xellent accounts backing the 6 Plan B fix-* scenarios. Each entry lives in
/// AXDB50_TEST at the documented 6-digit Accountnum and is provisioned into Settl
/// featuretest at the same <c>CustomerNumber</c> (featuretest enforces a 6-digit
/// numeric format — no VRFY- string prefix is possible).
///
/// <para>Scenario 4 (<c>XellentCleared</c>) and scenario 5 (<c>AlreadyCorrect</c>) are
/// runtime-mutated during a test's Arrange phase and reuse the happy-path account.</para>
///
/// <para><b>Cleanup:</b> Settl featuretest does not support customer / metering-point
/// delete (B-prereq-1). Tests accept customer-leak and rely on the small, curated list
/// here so a sweep utility can target these accountnums specifically. Payers and
/// contract-associations can be deleted and are torn down per test class.</para>
///
/// <para>All accounts validated against AXDB50_TEST on 2026-04-15.</para>
/// </summary>
public static class FixTestAccounts
{
    /// <summary>
    /// Fiktiv Personm — 5 MPs, single active El-ekstern contract 10483451
    /// (enddate 1900-01-01). Matches <c>Profile448928Tests</c> SQLite regression.
    /// </summary>
    public const string HappyPath = "448928";

    /// <summary>
    /// Fiktiv Personn — single El-ekstern contract 10519988 with enddate 2025-04-14,
    /// i.e. one day before the default <c>includeEndedCutoff = today.AddYears(-1)</c>
    /// at 2026-04-15. Fix-* must skip + count <c>MpsNotInXellent</c>.
    /// </summary>
    public const string InactivePastCutoff = "468595";

    /// <summary>
    /// Hybel Midt A/S — member of the Hybel/CVR 37387207 group. Representative
    /// (alphabetically-first Accountnum) is <see cref="Representative"/>. Fix-* must
    /// resolve to 372328's MPs, not 372537's, when both are in the input set.
    /// </summary>
    public const string NonRepresentative = "372537";

    /// <summary>
    /// Hybel Midt A/S — representative of the Hybel/CVR 37387207 group (lowest Accountnum).
    /// Provisioned alongside <see cref="NonRepresentative"/> so tests can assert that
    /// <c>AccountRepresentativeResolver</c> dedupes a rep+non-rep input to this Settl customer.
    /// 372328's contract ended 2023-07-12 — <see cref="ProvisioningFixture"/> provisions it
    /// with a historical reference date.
    /// </summary>
    public const string Representative = "372328";

    /// <summary>
    /// Joint-invoice child — contract 10288038 active with
    /// <c>Referencetomaincontract = 10065029</c>, 1 active MP. Expected
    /// <c>JointInvoiceNumber = "10065029"</c> after fix-joint.
    /// </summary>
    public const string JointInvoice = "100029";

    /// <summary>
    /// Fiktiv Persono — active El-ekstern contract 10489795, EAV reading ~8959 kWh/yr
    /// (readingdate 2025-05-31), one MP (571313113161269318, DK Vest, grid 131).
    /// Standalone customer (no CVR group). Replaced 100034 (Fiktiv Personj) on 2026-04-23
    /// after provisioning of 100034 failed in featuretest.
    /// </summary>
    public const string AnnualConsumption = "452830";

    /// <summary>All curated accounts. Used by the sweep utility to target cleanup.</summary>
    public static IReadOnlyList<string> All => new[]
    {
        HappyPath,
        InactivePastCutoff,
        NonRepresentative,
        Representative,
        JointInvoice,
        AnnualConsumption,
    };
}
