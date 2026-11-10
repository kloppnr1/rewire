using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Locks the per-customer outcome stream and the two failure modes that made an interrupted
/// run impossible to resume: the report only ever carried counters, and a stale selection
/// silently migrated nothing while reporting success.
/// </summary>
public class CustomerOutcomeTests : IClassFixture<PpaProfileFixture>
{
    private const string WaterProfile = "vand-sample";
    private const string ElectricityProfile = "ppa-customers";

    private readonly PpaProfileFixture _ppa;

    public CustomerOutcomeTests(PpaProfileFixture ppa) => _ppa = ppa;

    [Fact]
    public void Emits_one_outcome_per_customer()
    {
        var run = _ppa.Runner;

        Assert.NotEmpty(run.Outcomes);
        Assert.Equal(run.Report.CustomersExtracted, run.Outcomes.Count);

        // Every outcome names a distinct representative — the journal keys on this.
        Assert.Equal(
            run.Outcomes.Count,
            run.Outcomes.Select(o => o.RepresentativeAccount).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Outcome_kinds_add_up_to_the_report_counters()
    {
        var run = _ppa.Runner;

        Assert.Equal(
            run.Report.CustomersCreated,
            run.Outcomes.Count(o => o.Kind == CustomerOutcomeKind.Created));
        Assert.Equal(
            run.Report.CustomersExisting,
            run.Outcomes.Count(o => o.Kind == CustomerOutcomeKind.Existing));
    }

    [Fact]
    public void Failing_customer_is_reported_before_the_run_stops()
    {
        // Online water dry-run errors on not-pre-populated MPs. Without ContinueOnError the
        // runner returns mid-loop — the customer that caused it must still reach the journal,
        // otherwise a resume would retry every account except the one that actually broke.
        using var run = new ProfileTestRunner(WaterProfile, offline: false, continueOnError: false);

        Assert.Equal(1, run.ExitCode);
        Assert.NotEmpty(run.Outcomes);

        var last = run.Outcomes[^1];
        Assert.Equal(CustomerOutcomeKind.Failed, last.Kind);
        Assert.NotEmpty(last.Errors);
    }

    [Fact]
    public void Aborted_run_still_gets_its_summary_finalized()
    {
        // The stop-at-first-error path used to skip MigrationSummaryWriter.WriteAndFinalize,
        // so the one run you most need to understand came back without collector output.
        using var run = new ProfileTestRunner(WaterProfile, offline: false, continueOnError: false);

        Assert.Equal(1, run.ExitCode);
        Assert.NotEmpty(run.Report.Log);
        Assert.Contains(run.Report.Log, l => l.Contains("Migration stopped at customer"));
    }

    [Fact]
    public void Continue_on_error_reports_every_customer()
    {
        using var run = new ProfileTestRunner(WaterProfile, offline: false, continueOnError: true);

        Assert.Equal(run.Report.CustomersExtracted, run.Outcomes.Count);
    }

    [Fact]
    public void Stale_selection_fails_instead_of_migrating_nothing()
    {
        // A resume list that no longer matches the extract must not read as a completed run.
        using var run = new ProfileTestRunner(
            ElectricityProfile, offline: true, selectedAccounts: ["999999991", "999999992"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Empty(run.Outcomes);
        Assert.Contains(run.Report.Errors, e => e.Contains("forældet"));
        Assert.Equal(2, run.Report.MissingSelectedAccounts.Count);
    }

    [Fact]
    public void Partially_stale_selection_runs_the_rest_and_names_the_missing()
    {
        // Drift on some accounts is expected — Xellent keeps moving — so it must not abort the
        // resume, but the caller has to be able to see which ones fell out.
        var known = _ppa.Runner.Outcomes[0].RepresentativeAccount;

        using var run = new ProfileTestRunner(
            ElectricityProfile, offline: true, selectedAccounts: [known, "999999991"]);

        Assert.Equal(0, run.ExitCode);
        Assert.Single(run.Outcomes);
        Assert.Equal(known, run.Outcomes[0].RepresentativeAccount);
        Assert.Equal(["999999991"], run.Report.MissingSelectedAccounts);
    }
}
