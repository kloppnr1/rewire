using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;

namespace DataMigration.Migration.Tests;

/// <summary>
/// All-active er én Settl-kunde per Xellent-konto. Scopet og den slåede-fra sammenlægning
/// hører sammen — kommer de fra hinanden, giver samme scope forskelligt resultat alt efter
/// om kørslen startes fra kommandolinjen eller fra dashboardets batch-vej.
/// </summary>
public class AllActiveScopeTests
{
    private static CreateOptions BuildOptions(params string[] accounts) =>
        CreateOptionsFactory.Build(
            new ProfileOptions
            {
                Name = "test",
                Accounts = accounts,
                Extraction = new ExtractionOptions { DeliveryCategory = "El" },
            },
            env: "featuretest",
            referenceDate: new DateTime(2026, 1, 1),
            exemption: new BillingExemptionConfig(null, null, null, 1),
            baseIgnoredGroups: []);

    [Fact]
    public void All_active_scope_never_merges_accounts()
    {
        var options = CreateOptionsFactory.ApplyAllActiveScope(BuildOptions());

        Assert.True(options.LoadEntireDataArea);
        Assert.True(options.DisableRepresentativeMerge);
    }

    [Fact]
    public void All_active_scope_drops_the_profiles_account_list()
    {
        // Med profil er det stadig scopet der afgør det: kontolisten ryger ud, og
        // sammenlægningen bliver ikke slået til igen af profilen.
        var options = CreateOptionsFactory.ApplyAllActiveScope(BuildOptions("100001", "100002"));

        Assert.Empty(options.Accounts);
        Assert.True(options.LoadEntireDataArea);
        Assert.True(options.DisableRepresentativeMerge);
    }

    [Fact]
    public void Profile_runs_without_all_active_still_merge()
    {
        var options = BuildOptions("100001", "100002");

        Assert.False(options.LoadEntireDataArea);
        Assert.False(options.DisableRepresentativeMerge);
        Assert.Equal(["100001", "100002"], options.Accounts);
    }
}
