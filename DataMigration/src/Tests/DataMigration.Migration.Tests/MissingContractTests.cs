using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;

namespace DataMigration.Migration.Tests;

/// <summary>
/// What happens to an account whose metering points need a contract Settl does not have. It is
/// created like everybody else — customer, payer, metering points, billing and the customer/payer
/// links — with only the contract link left out, and it comes out Failed with an error naming the
/// contract, so a retry brings it back for the link once the contract is mapped or created.
/// The Failed booking is the safeguard: the original incident was such an account booked as
/// finished, so nothing ever brought it back. Only that account is booked as failed; the rest of
/// the batch runs on.
/// </summary>
public class MissingContractTests : IClassFixture<PpaProfileFixture>
{
    private const string Profile = "ppa-customers";

    /// <summary>One of the profile's mapped contracts. Only the accounts in that group need it,
    /// which is what makes the blast radius visible in the same run.</summary>
    private const string MissingContract = "E_PPA_JN_Data";

    private readonly PpaProfileFixture _ppa;

    public MissingContractTests(PpaProfileFixture ppa) => _ppa = ppa;

    [Fact]
    public void The_account_that_needs_it_is_created_and_still_comes_out_failed()
    {
        var blocked = AccountsNeeding(MissingContract);
        Assert.NotEmpty(blocked);

        using var run = RunWithSettlMissing(MissingContract);

        foreach (var account in blocked)
        {
            var outcome = Assert.Single(run.Outcomes, o => o.RepresentativeAccount == account);
            Assert.Equal(CustomerOutcomeKind.Failed, outcome.Kind);

            // The migrate-loop opens with this line per customer. Its presence is the whole point:
            // the account went through customer, payer and metering point like everybody else.
            Assert.Contains(run.Report.Log, l => l.StartsWith($"--- Customer {account} "));
        }

        // Failed in the outcomes, failed for the exit code — the run must not read as clean.
        Assert.Equal(1, run.ExitCode);
    }

    [Fact]
    public void The_customer_is_counted_as_created_not_as_failed()
    {
        // The counters count entities, the outcomes count accounts — the same convention as an
        // account whose customer was created and a link then refused. The customer WAS created.
        var blocked = AccountsNeeding(MissingContract);

        using var run = RunWithSettlMissing(MissingContract);

        Assert.Equal(0, run.Report.CustomersFailed);
        Assert.Equal(run.Report.CustomersExtracted, run.Report.CustomersCreated);
    }

    [Fact]
    public void Only_the_account_that_needs_it_is_booked_as_failed()
    {
        var blocked = AccountsNeeding(MissingContract);

        using var run = RunWithSettlMissing(MissingContract);

        // Everybody else went through clean — one wrong product name costs only the accounts that
        // need it, and it costs them the booking, not the migration.
        var created = run.Outcomes.Where(o => o.Kind == CustomerOutcomeKind.Created).ToList();
        var failed = run.Outcomes.Where(o => o.Kind == CustomerOutcomeKind.Failed).ToList();
        Assert.NotEmpty(created);
        Assert.DoesNotContain(created, o => blocked.Contains(o.RepresentativeAccount));
        Assert.Equal(run.Report.CustomersExtracted - blocked.Count, created.Count);
        Assert.Equal(blocked.Count, failed.Count);
        Assert.All(failed, o => Assert.Contains(o.RepresentativeAccount, blocked));
    }

    [Fact]
    public void The_error_names_the_contract_and_says_the_account_was_created()
    {
        var account = AccountsNeeding(MissingContract).First();

        using var run = RunWithSettlMissing(MissingContract);

        var outcome = Assert.Single(run.Outcomes, o => o.RepresentativeAccount == account);
        var error = Assert.Single(outcome.Errors);
        Assert.Contains(account, error);
        Assert.Contains("created without its contract link", error);
        // The dashboard groups failures on this exact phrase — it must survive any rewording.
        Assert.Contains($"contract '{MissingContract}' does not exist in Settl", error);
        Assert.Contains(error, run.Report.Errors);
    }

    [Fact]
    public void A_missing_contract_does_not_stop_a_run_without_force()
    {
        // The old refusal never stopped a run, whatever --force said, and neither does this: the
        // error is booked on the account, not raised as a Settl error. Every account gets its
        // outcome, including the ones sorted after the failed one.
        var blocked = AccountsNeeding(MissingContract);
        Assert.NotEmpty(blocked);

        using var run = RunWithSettlMissing(MissingContract, continueOnError: false);

        Assert.DoesNotContain(run.Report.Log, l => l.StartsWith("=== Migration stopped at customer"));
        Assert.Equal(run.Report.CustomersExtracted, run.Outcomes.Count);
    }

    [Fact]
    public void A_contract_Settl_has_blocks_nobody()
    {
        // Same run, same client, only the one name put back. Without this the tests above would
        // still pass if the run failed every account regardless of what Settl answered.
        var allNames = ContractNamesByAccount().Values.SelectMany(n => n).Distinct().ToArray();

        using var run = new ProfileTestRunner(Profile, contractClient: new ListContractClient(allNames));

        Assert.Equal(0, run.Report.CustomersFailed);
        Assert.DoesNotContain(run.Outcomes, o => o.Kind == CustomerOutcomeKind.Failed);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void A_metering_points_run_creates_them_anyway_and_books_nobody_as_failed()
    {
        // A metering-points-only run never links a contract to anything — the linking happens in
        // the customer loop, which this run stops before. So there is nothing to book as failed.
        var blocked = AccountsNeeding(MissingContract);
        Assert.NotEmpty(blocked);

        using var run = RunWithSettlMissing(MissingContract, meteringPointsOnly: true);

        Assert.Equal(0, run.Report.CustomersFailed);
        Assert.Empty(run.Outcomes);
        Assert.DoesNotContain(run.Report.Log, l => l.Contains("created without the contract link"));
        Assert.DoesNotContain(run.Report.Errors, e => e.Contains(MissingContract));

        // The metering points are made, the affected account's included — that is the whole run.
        Assert.True(run.Report.MeteringPointsCreated > 0);
        foreach (var account in blocked)
            Assert.Contains(run.Report.MeteringPointAccountOutcomes, o => o.Account == account);
    }

    [Fact]
    public void A_metering_points_run_still_says_the_contract_is_missing()
    {
        // Said once, as a warning, so the completion run's Failed booking is not the first anybody
        // hears of it. It must not read as an error: nothing failed in THIS run.
        var account = AccountsNeeding(MissingContract).First();

        using var run = RunWithSettlMissing(MissingContract, meteringPointsOnly: true);

        Assert.Contains(run.Report.CollectorWarnings,
            w => w.Contains(account) && w.Contains(MissingContract));
        Assert.Contains(run.Report.Log,
            l => l.StartsWith("  WARN: ") && l.Contains(account) && l.Contains(MissingContract));
        Assert.DoesNotContain(run.Report.Log,
            l => l.StartsWith("  ERROR: ") && l.Contains(account) && l.Contains(MissingContract));
    }

    [Fact]
    public void A_metering_points_run_reports_no_error_at_all_about_the_contract()
    {
        // Two messages about one fact is the noise being cleaned up. The per-account warning above
        // already names the accounts and says what is owed; a run-level ERROR on top read as
        // breakage in a run where nothing broke.
        using var run = RunWithSettlMissing(MissingContract, meteringPointsOnly: true);

        Assert.DoesNotContain(run.Report.CollectorErrors, e => e.Contains(MissingContract));
        Assert.Equal(0, run.Report.CollectorErrorCount);
    }

    [Fact]
    public void A_full_run_still_reports_the_missing_contract_as_an_error()
    {
        // The other half of the same rule: a run that DOES link contracts keeps the error.
        using var run = RunWithSettlMissing(MissingContract);

        Assert.Contains(run.Report.CollectorErrors,
            e => e.Contains($"Contract '{MissingContract}' not found"));
    }

    [Fact]
    public void The_missing_contract_error_does_not_end_in_a_bare_colon()
    {
        // Settl answered with its contract list and no error string, so there is no reason to
        // print. "Contract 'X' not found:" with nothing after it reads as a message that lost its
        // second half.
        using var run = RunWithSettlMissing(MissingContract);

        var error = Assert.Single(run.Report.CollectorErrors, e => e.Contains(MissingContract));
        Assert.Equal($"Contract '{MissingContract}' not found in Settl", error);
    }

    [Fact]
    public void Capture_mode_books_nobody()
    {
        // Reconcile writes nothing, and an account whose contract is missing is exactly one it
        // must still be able to show.
        using var run = RunWithSettlMissing(MissingContract, capture: true);

        Assert.Equal(0, run.Report.CustomersFailed);
        Assert.DoesNotContain(run.Outcomes, o => o.Kind == CustomerOutcomeKind.Failed);
        Assert.Equal(run.Report.CustomersExtracted, run.CapturePlans.Count);
    }

    /// <summary>
    /// A run where Settl answers with every needed contract except one — the shape of the
    /// production case, where a single fallback product name had no contract behind it.
    /// </summary>
    private ProfileTestRunner RunWithSettlMissing(
        string contractName, bool meteringPointsOnly = false, bool capture = false, bool continueOnError = false)
    {
        var available = ContractNamesByAccount().Values
            .SelectMany(names => names)
            .Distinct(StringComparer.Ordinal)
            .Where(n => n != contractName)
            .ToArray();

        return new ProfileTestRunner(
            Profile,
            // A metering-points run only exists in a live run — dry-run skips the phase entirely.
            dryRun: !meteringPointsOnly,
            meteringPointsOnly: meteringPointsOnly,
            continueOnError: continueOnError,
            contractClient: new ListContractClient(available),
            capture: capture);
    }

    private List<string> AccountsNeeding(string contractName) =>
        [.. ContractNamesByAccount()
            .Where(kv => kv.Value.Contains(contractName, StringComparer.Ordinal))
            .Select(kv => kv.Key)];

    /// <summary>
    /// Which contract name each account's metering points resolve to, read off the shared run
    /// where Settl was never asked — so the test data follows the profile instead of repeating it.
    /// </summary>
    private Dictionary<string, List<string>> ContractNamesByAccount()
    {
        var report = _ppa.Runner.Report;
        return report.AccountMeteringPoints.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .Where(report.MpContractNames.ContainsKey)
                .Select(mp => report.MpContractNames[mp])
                .Distinct(StringComparer.Ordinal)
                .ToList());
    }
}
