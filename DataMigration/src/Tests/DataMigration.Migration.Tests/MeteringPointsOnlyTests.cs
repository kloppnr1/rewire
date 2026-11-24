using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// En målepunkts-kørsel stopper efter målepunkts-fasen: ingen kunder, ingen betalere, ingen
/// ejer-kunder. Målepunkterne er den dyre og skrøbelige del af en all-active-kørsel, og det
/// her gør det muligt at tage dem alene og lade resten følge efter bagefter.
/// </summary>
public class MeteringPointsOnlyTests
{
    private const string Profile = "vand-sample";

    [Fact]
    public void Rejects_a_run_that_cannot_write()
    {
        // Målepunkts-fasen springes helt over i dry-run og offline, så kombinationen ville ikke
        // lave noget. Afvist frem for at køre og melde nul oprettede.
        using var run = new ProfileTestRunner(Profile, offline: true, meteringPointsOnly: true);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(run.Report.Errors, e => e.Contains("--mp-only"));
        Assert.Equal(0, run.Report.CustomersExtracted);
    }

    [Fact]
    public void Stops_after_the_metering_points_without_creating_anyone()
    {
        using var mpOnly = new ProfileTestRunner(Profile, dryRun: false, meteringPointsOnly: true);
        using var full = new ProfileTestRunner(Profile, dryRun: false);

        // Kørslen gik godt — den er halvfærdig med vilje, ikke fejlet.
        Assert.Equal(0, mpOnly.ExitCode);

        // Ingen kunde nåede fiktivvejbgd. Outcomes fyldes af OnCustomerCompleted, som kun kaldes derinde.
        Assert.Empty(mpOnly.Outcomes);
        Assert.Equal(0, mpOnly.Report.CustomersCreated);
        Assert.Equal(0, mpOnly.Report.PayersCreated);

        // Ejer-kunder oprettes FØR målepunkterne, så uden det her ville "kun målepunkter" være
        // usandt — kørslen ville have skrevet rigtige kunder i Settl.
        Assert.Empty(mpOnly.Report.OwnerCustomers);

        // Til sammenligning: den fulde kørsel over samme data laver netop de ting.
        Assert.NotEmpty(full.Outcomes);
        Assert.True(full.Report.CustomersCreated > 0);
    }

    [Fact]
    public void The_report_is_finished_even_though_the_run_stopped_early()
    {
        using var run = new ProfileTestRunner(Profile, dryRun: false, meteringPointsOnly: true);

        // Springer udgangen opsummeringen over, mister rapporten advarsler, valideringsfund og
        // collector-fejl — netop det man skal bruge for at vurdere om målepunkterne gik godt.
        Assert.NotEmpty(run.Report.Log);
        Assert.Contains(run.Report.Log, l => l.Contains("Migration Summary"));
    }

    [Fact]
    public void The_scope_is_reported_before_the_run_stops()
    {
        // Genoptagelsen udleder de udestående konti af det scope udtrækket melder ind, minus de
        // kunder fiktivvejbgd nåede. Meldes scopet ikke ind før udgangen, bliver de udestående tomme,
        // og en genoptagelse gør stille ingenting. Rækkefølgen håndhæves ingen andre steder.
        MigrationReport? atExtraction = null;
        using var run = new ProfileTestRunner(
            Profile,
            offline: true,
            onExtractionComplete: r => atExtraction = r);

        Assert.NotNull(atExtraction);
        Assert.True(
            atExtraction!.CustomersExtracted > 0,
            "Udtrækket skal have meldt sit scope ind før kørslen kan stoppe — ellers har en "
          + "genoptagelse ingen konti at køre.");
    }
}
