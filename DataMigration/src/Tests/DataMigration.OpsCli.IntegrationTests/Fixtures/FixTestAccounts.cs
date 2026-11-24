namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// Account numbers the integration tests use. Only <see cref="HappyPath"/> has an anonymised Xellent
/// snapshot (in DataMigration.Migration.Tests' seed data); the reconcile flow tests migrate it into
/// the fake Settl tenant. The others name the shapes the original live tests relied on, and are
/// kept as identifiers for tests that seed synthetic rows under them.
/// </summary>
public static class FixTestAccounts
{
    /// <summary>Private customer with one active El-ekstern contract and five metering points.</summary>
    public const string HappyPath = "448928";

    /// <summary>Single El-ekstern contract that ended before the one-year include cutoff.</summary>
    public const string InactivePastCutoff = "468595";

    /// <summary>Member of a CVR merge group; its representative is <see cref="Representative"/>.</summary>
    public const string NonRepresentative = "372537";

    /// <summary>Representative (lowest account number) of the CVR merge group.</summary>
    public const string Representative = "372328";

    /// <summary>Joint-invoice child: its contract references a main contract.</summary>
    public const string JointInvoice = "100029";

    /// <summary>Standalone customer with one metering point and an annual-consumption reading.</summary>
    public const string AnnualConsumption = "452830";

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
