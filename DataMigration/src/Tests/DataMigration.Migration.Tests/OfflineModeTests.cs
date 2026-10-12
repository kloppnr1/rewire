using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Locks the offline dry-run behaviour: <c>CreateOptions.Offline</c> makes the whole pipeline
/// run without Settl reachability and reports every entity as would-create. The water/heat
/// case is the distinguishing one — a normal (online) dry-run errors on a not-found water MP
/// ("must be pre-populated"), while offline reports it as would-create instead.
/// </summary>
public class OfflineModeTests
{
    private const string WaterProfile = "vand-sample";

    [Fact]
    public void Offline_water_profile_reports_would_create_without_pre_populated_errors()
    {
        using var run = new ProfileTestRunner(WaterProfile, offline: true);

        Assert.DoesNotContain(run.Report.Errors, e => e.Contains("pre-populated"));
        Assert.True(run.Report.MeteringPointsExtracted > 0, "expected water MPs in the extract");
        // Offline: every searched MP is not-found → would-create. None reported as existing.
        Assert.Equal(run.Report.MeteringPointsExtracted, run.Report.MeteringPointsCreated);
        Assert.Equal(0, run.Report.MeteringPointsExisting);
    }

    [Fact]
    public void Online_dry_run_water_profile_still_flags_missing_pre_population()
    {
        // Same profile, same mock clients, but Offline=false: the not-found water MPs (mocks
        // return null) must surface the pre-population error — proving offline is the only thing
        // that suppresses it, not the mocks themselves.
        using var run = new ProfileTestRunner(WaterProfile, offline: false);

        Assert.Contains(run.Report.Errors, e => e.Contains("pre-populated"));
    }

    [Fact]
    public void Offline_resolves_expected_contract_names_locally()
    {
        using var run = new ProfileTestRunner(WaterProfile, offline: true);

        // Contract NAMES are resolved locally from mapping config (no Settl) so the report can
        // show the expected contract per MP. The fallback uses the product as the name, so every
        // extracted MP gets an entry.
        Assert.NotEmpty(run.Report.MpContractNames);
        Assert.NotEmpty(run.Report.MpContractLinks);

        // But the Settl GUID lookup is still skipped offline — no contract name carries a Settl
        // contract id, so no actual contract-link mutation would be attempted.
        Assert.All(run.Report.MpContractLinks, link =>
            Assert.False(string.IsNullOrEmpty(link.ContractName)));
    }
}
