using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Kørslen skal melde hvad den laver mens den laver det. Faserne er det eneste en viser har at
/// gå efter, og de tre måder en kørsel kan gå på — fuld, kun målepunkter, offline — går hver sin
/// vej gennem koden. Derfor holdes rækkefølgen fast her, path for path: ingen fase der bliver
/// stående når kørslen er ovre, og ingen fase der lover arbejde den vej aldrig laver.
/// </summary>
public class PhaseReportingTests
{
    private const string WaterProfile = "vand-sample";
    private const string ElectricityProfile = "pilot-production";

    [Fact]
    public void A_live_run_names_every_step_it_takes()
    {
        using var run = new ProfileTestRunner(WaterProfile, dryRun: false);

        Assert.Equal(
            [
                MigrationPhases.PreloadingSettlCache,
                MigrationPhases.ResolvingContracts,
                MigrationPhases.MigratingCustomers,
                null,
            ],
            run.Phases);
    }

    [Fact]
    public void A_dry_run_creates_no_metering_points_and_says_so_by_leaving_the_phase_out()
    {
        using var run = new ProfileTestRunner(WaterProfile);

        Assert.DoesNotContain(MigrationPhases.CreatingMeteringPoints, run.Phases);
        Assert.Contains(MigrationPhases.MigratingCustomers, run.Phases);
    }

    [Fact]
    public void An_offline_run_only_claims_the_one_thing_it_does()
    {
        // Offline rører hverken Settls øjebliksbillede eller kontrakt-id'erne, så de to faser
        // ville være løgn. Tilbage står kunde-løkken, der bygger sine kald mod attrapper.
        using var run = new ProfileTestRunner(WaterProfile, offline: true);

        Assert.Equal([MigrationPhases.MigratingCustomers, null], run.Phases);
    }

    [Fact]
    public void A_metering_points_run_never_claims_to_migrate_customers()
    {
        // Den vej har ingen kunde-løkke. Meldte den alligevel kunde-fasen ind, ville viseren stå
        // og vente på en tæller der aldrig kommer — det er præcis den fejl der var før.
        using var run = new ProfileTestRunner(WaterProfile, dryRun: false, meteringPointsOnly: true);

        Assert.DoesNotContain(MigrationPhases.MigratingCustomers, run.Phases);
        Assert.Equal(MigrationPhases.PreloadingSettlCache, run.Phases[0]);
        Assert.Null(run.Phases[^1]);
    }

    [Fact]
    public void No_run_leaves_a_phase_hanging()
    {
        using var live = new ProfileTestRunner(WaterProfile, dryRun: false);
        using var dry = new ProfileTestRunner(WaterProfile);
        using var offline = new ProfileTestRunner(WaterProfile, offline: true);
        using var mpOnly = new ProfileTestRunner(WaterProfile, dryRun: false, meteringPointsOnly: true);

        foreach (var run in new[] { live, dry, offline, mpOnly })
            Assert.Null(run.Phases[^1]);
    }

    [Fact]
    public void The_metering_point_phase_counts_metering_points_not_accounts()
    {
        // El-målepunkterne oprettes før den første kunde, og de er hverken lige så mange som
        // kontiene eller fordelt jævnt på dem. Tælleren skal derfor måles mod målepunkterne —
        // ellers viser søjlen et tal der aldrig når sit mål.
        using var run = new ProfileTestRunner(ElectricityProfile, dryRun: false, meteringPointsOnly: true);

        Assert.Contains(MigrationPhases.CreatingMeteringPoints, run.Phases);

        var mpProgress = run.Progress
            .Where(p => p.Phase == MigrationPhases.CreatingMeteringPoints)
            .ToList();

        Assert.NotEmpty(mpProgress);
        var total = mpProgress[0].Total;

        // Nævneren er el-målepunkterne i udtrækket — dem fasen opretter. Vand og varme lægges ind
        // udefra og tælles ikke med, og konti tælles slet ikke her.
        var electricityMps = run.Report.MeteringPoints.Count(mp => mp.Grid != null);
        Assert.True(electricityMps > 0, "Profilen skal have el-målepunkter, ellers tester det her ingenting.");
        Assert.Equal(electricityMps, total);
        Assert.Equal(0, mpProgress[0].Done);
        Assert.Equal(total, mpProgress[^1].Done);
    }

    [Fact]
    public void The_metering_point_phase_moves_while_it_works()
    {
        // Fasen melder efter hvert bundt, ikke kun til sidst. Meldingerne skal derfor komme flere
        // gange, stige hele vejen og lande præcis på fasens eget mål — en søjle der springer fra 0
        // til alt fortæller intet mens kørslen står på.
        using var run = new ProfileTestRunner(ElectricityProfile, dryRun: false, meteringPointsOnly: true);

        var mpProgress = run.Progress
            .Where(p => p.Phase == MigrationPhases.CreatingMeteringPoints)
            .ToList();

        Assert.True(mpProgress.Count > 1,
            $"Fasen skal melde mere end én gang, meldte {mpProgress.Count}.");

        // Nævneren er den samme hele vejen — den kommer fra fasen selv, ikke fra kørslens egen
        // optælling, så den kan ikke skifte undervejs.
        var total = mpProgress[0].Total;
        Assert.All(mpProgress, p => Assert.Equal(total, p.Total));

        for (var i = 1; i < mpProgress.Count; i++)
            Assert.True(mpProgress[i].Done >= mpProgress[i - 1].Done,
                $"Fremdriften gik baglæns: {mpProgress[i - 1].Done} → {mpProgress[i].Done}.");

        Assert.Equal(0, mpProgress[0].Done);
        Assert.Equal(total, mpProgress[^1].Done);
        Assert.True(total > 0, "Fasen skal have noget at lave, ellers tester det her ingenting.");
    }

    [Theory]
    [InlineData(true, false)]   // dry-run
    [InlineData(false, true)]   // offline
    public void A_run_that_skips_the_metering_point_phase_reports_neither_it_nor_its_progress(
        bool dryRun, bool offline)
    {
        // De veje opretter ingen målepunkter overhovedet. Så må de heller ikke melde fasen ind —
        // og slet ikke en fremdrift under den.
        using var run = new ProfileTestRunner(ElectricityProfile, dryRun: dryRun, offline: offline);

        Assert.DoesNotContain(MigrationPhases.CreatingMeteringPoints, run.Phases);
        Assert.DoesNotContain(run.Progress, p => p.Phase == MigrationPhases.CreatingMeteringPoints);
    }

    [Fact]
    public void The_customer_phase_counts_customers_from_zero()
    {
        // Tælleren nulstilles sammen med fasenavnet. Uden det ville en viser læse målepunkts-
        // fasens tal og kunde-fasens tal som én stigende række.
        using var run = new ProfileTestRunner(WaterProfile, offline: true);

        var customerProgress = run.Progress
            .Where(p => p.Phase == MigrationPhases.MigratingCustomers)
            .ToList();

        Assert.NotEmpty(customerProgress);
        Assert.Equal(0, customerProgress[0].Done);
        Assert.All(customerProgress, p => Assert.Equal(run.Report.CustomersExtracted, p.Total));
        Assert.Equal(run.Outcomes.Count, customerProgress[^1].Done);
    }
}
