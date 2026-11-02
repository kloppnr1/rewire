using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using DataMigration.OpsCli.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Tests.Runners;

public class GenerateStateFromSettlCustomerLookupTests
{
    [Fact]
    public void Skipping_unknown_settl_owner_excludes_every_point_sharing_its_xellent_account()
    {
        var cos = new DateOnly(2026, 6, 11);
        var unknown = new SettlMpWithCos("UNKNOWN", Guid.NewGuid(), cos, new MeteringPointInfoDto());
        var shared = new SettlMpWithCos("SHARED", Guid.NewGuid(), cos, new MeteringPointInfoDto());
        var safe = new SettlMpWithCos("SAFE", Guid.NewGuid(), cos, new MeteringPointInfoDto());

        var result = GenerateStateFromSettlRunner.OmitUnresolvedMeteringPoints(
            [unknown, shared, safe], new HashSet<string> { "UNKNOWN" },
            [("UNKNOWN", "417016"), ("SHARED", "417016"), ("SAFE", "503281")]);

        Assert.Equal("SAFE", Assert.Single(result.Kept).Identification);
        Assert.Equal("417016", Assert.Single(result.ExcludedSources));
        Assert.Equal(2, result.OmittedCount);
        Assert.Equal("SharesXellentAccountWithUnresolvedMeteringPoint",
            Assert.Single(result.SharedSourceIssues).Reason);
    }

    [Fact]
    public void Excluded_source_is_not_reintroduced_by_a_representative_group()
    {
        var state = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "503281" }] };
        var evidence = new GenerateStateOfflineCache.OwnershipEvidence(
            "SAFE", "503281", "503280", "503281", new DateOnly(2026, 6, 11),
            ["503280", "417016"]);

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(
            state, [evidence], "production", ["417016"]);

        Assert.Empty(result.Issues);
        Assert.Equal(["503280"], Assert.Single(state.Accounts).SourceAccounts);
    }

    [Fact]
    public void Skipping_conflicting_claims_removes_the_shared_source_from_both_customers()
    {
        var state = new MigrationStateDocument { Accounts =
        [
            new MigratedAccount { SettlAccountNumber = "503281" },
            new MigratedAccount { SettlAccountNumber = "417016" }
        ] };
        var evidence = new[]
        {
            new GenerateStateOfflineCache.OwnershipEvidence("MP1", "503281", "503281", "503281",
                new DateOnly(2026, 6, 11), ["503281"]),
            new GenerateStateOfflineCache.OwnershipEvidence("MP2", "417016", "503281", "417016",
                new DateOnly(2026, 6, 11), ["503281"])
        };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(
            state, evidence, "production", skipUnresolvedSources: true);

        Assert.Equal("XellentSourceClaimedByMultipleCustomers", Assert.Single(result.Issues).Reason);
        Assert.All(state.Accounts, a => Assert.Empty(a.SourceAccounts));
    }

    [Fact]
    public void Skipping_a_mismatched_claim_also_removes_an_earlier_claim_to_the_same_source()
    {
        var state = new MigrationStateDocument { Accounts =
        [
            new MigratedAccount { SettlAccountNumber = "503281" },
            new MigratedAccount { SettlAccountNumber = "417016" }
        ] };
        var evidence = new[]
        {
            new GenerateStateOfflineCache.OwnershipEvidence("MP1", "503281", "503281", "503281",
                new DateOnly(2026, 6, 11), ["503281"]),
            new GenerateStateOfflineCache.OwnershipEvidence("MP2", "417016", "503281", "503281",
                new DateOnly(2026, 6, 11), ["503281"])
        };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(
            state, evidence, "production", skipUnresolvedSources: true);

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(result.Issues).Reason);
        Assert.All(state.Accounts, a => Assert.Empty(a.SourceAccounts));
    }

    private static readonly GenerateStateOfflineCache.OwnershipEvidence SameCvrRow = new(
        "MP1", "403522", "447135", "446977", new DateOnly(2026, 6, 11), ["446977", "447135", "447136"]);

    private static MigrationStateDocument StateWith(string customer) => new()
        { Accounts = [new MigratedAccount { SettlAccountNumber = customer }] };

    [Fact]
    public void Same_cvr_customer_is_accepted_for_the_source_account_alone_and_audited()
    {
        var state = StateWith("403522");

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state, [SameCvrRow], "production",
            cvrByAccount: new Dictionary<string, string> { ["447135"] = " 36665030 ", ["403522"] = "36665030" });

        Assert.Empty(result.Issues);
        Assert.Equal(["447135"], Assert.Single(state.Accounts).SourceAccounts);
        Assert.Equal(new SameCvrAcceptance("447135", "446977", "MP1", "403522", new DateOnly(2026, 6, 11)),
            Assert.Single(result.SameCvrAccepted!));
    }

    [Theory]
    [InlineData("36665030", "87654321")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("11111111", "11111111")]
    public void Different_blank_or_placeholder_cvr_is_still_rejected(string sourceCvr, string customerCvr)
    {
        var state = StateWith("403522");

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state, [SameCvrRow], "production",
            skipUnresolvedSources: true,
            cvrByAccount: new Dictionary<string, string> { ["447135"] = sourceCvr, ["403522"] = customerCvr });

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(result.Issues).Reason);
        Assert.Empty(Assert.Single(state.Accounts).SourceAccounts);
        Assert.Empty(result.SameCvrAccepted!);
    }

    [Fact]
    public void Settl_customer_that_is_no_xellent_account_is_still_rejected()
    {
        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(StateWith("403522"), [SameCvrRow],
            "production", skipUnresolvedSources: true,
            cvrByAccount: new Dictionary<string, string> { ["447135"] = "36665030" });

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void Padded_settl_customer_finds_its_short_xellent_account()
    {
        var row = SameCvrRow with { SettlCustomerNumber = "941589" };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(StateWith("941589"), [row], "production",
            cvrByAccount: new Dictionary<string, string> { ["447135"] = "36665030", ["41589"] = "36665030" });

        Assert.Empty(result.Issues);
        Assert.Single(result.SameCvrAccepted!);
    }

    [Fact]
    public void Real_six_digit_account_wins_over_a_short_account_with_the_same_cvr()
    {
        var row = SameCvrRow with { SettlCustomerNumber = "941589" };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(StateWith("941589"), [row], "production",
            skipUnresolvedSources: true, cvrByAccount: new Dictionary<string, string>
                { ["447135"] = "36665030", ["941589"] = "87654321", ["41589"] = "36665030" });

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public void Same_cvr_claim_lost_to_a_conflict_is_not_counted_as_accepted()
    {
        var state = new MigrationStateDocument { Accounts =
        [
            new MigratedAccount { SettlAccountNumber = "403522" },
            new MigratedAccount { SettlAccountNumber = "447135" }
        ] };
        var direct = SameCvrRow with { MeteringPoint = "MP2", SettlCustomerNumber = "447135" };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state, [SameCvrRow, direct], "production",
            skipUnresolvedSources: true,
            cvrByAccount: new Dictionary<string, string> { ["447135"] = "36665030", ["403522"] = "36665030" });

        Assert.Equal("XellentSourceClaimedByMultipleCustomers", Assert.Single(result.Issues).Reason);
        Assert.Empty(result.SameCvrAccepted!);
        Assert.All(state.Accounts, a => Assert.Empty(a.SourceAccounts));
    }

    [Fact]
    public void Without_saved_cvrs_the_same_cvr_rule_does_not_run()
    {
        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(StateWith("403522"), [SameCvrRow],
            "production", skipUnresolvedSources: true);

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(result.Issues).Reason);
        Assert.Null(result.SameCvrAccepted);
    }

    [Fact]
    public void Same_cvr_report_sits_beside_the_state_file_and_lists_each_point()
    {
        var csv = SourceAccountOwnership.BuildSameCvrCsv(
            [new SameCvrAcceptance("447135", "446977", "MP1", "403522", new DateOnly(2026, 6, 11))]);

        Assert.Equal(["SourceAccount;Representative;MeteringPoint;SettlCustomer;CosDate",
            "447135;446977;MP1;403522;2026-06-11"], csv.Trim().Split(Environment.NewLine));
        Assert.Equal(Path.Combine("out", "state-same-cvr-accepted.csv"),
            SourceAccountOwnership.SameCvrReportPath(Path.Combine("out", "state.json")));
    }

    [Fact]
    public void Settl_only_point_is_audited_before_payer_lookup_but_for_origin_remains_in_scope()
    {
        var cos = new DateOnly(2026, 6, 11);
        var settlOnly = new SettlMpWithCos("SETTL-ONLY", Guid.NewGuid(), cos,
            new MeteringPointInfoDto());
        var forOrigin = new SettlMpWithCos("FOR-ORIGIN", Guid.NewGuid(), cos,
            new MeteringPointInfoDto());

        var (included, audit) = GenerateStateFromSettlRunner.FilterForXellentOrigin(
            [settlOnly, forOrigin], new HashSet<string> { forOrigin.Identification }, []);

        Assert.Equal(forOrigin.Id, Assert.Single(included).Id);
        var excluded = Assert.Single(audit);
        Assert.Equal(settlOnly.Identification, excluded.MeteringPoint);
        Assert.Equal(cos, excluded.SelectedCos);
        Assert.Equal("NoForXellentOrigin", excluded.Disposition);
    }

    [Fact]
    public async Task Cos_driven_lookup_keeps_historical_customer_even_with_no_invoices()
    {
        var mp = Guid.NewGuid();
        var historicalCustomer = Guid.NewGuid();
        var currentCustomer = Guid.NewGuid();
        var cos = new DateOnly(2026, 5, 1);
        var payer = new DatedPayerClient(mp, cos, historicalCustomer, currentCustomer,
            "278435-1");
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer);
        var mps = new[] { new SettlMpWithCos("571313115200285419", mp, cos, new MeteringPointInfoDto()) };
        var customerNumbers = new Dictionary<Guid, string>
        {
            [historicalCustomer] = "278435", [currentCustomer] = "000006"
        };
        var atCos = await runner.ResolveCustomerByMeteringPointAsync(
            mps, customerNumbers, CancellationToken.None);
        Assert.Equal("278435", atCos[mp]);
        Assert.DoesNotContain("000006", atCos.Values);
        Assert.Equal(cos.ToDateTime(TimeOnly.MinValue), Assert.Single(payer.SearchDates));

        // The COS-driven population filter runs before the --no-invoices early return.
        var cosByCustomer = CosStatePlanner.MinCosByCustomer(mps.Select(m => (atCos[m.Id], m.Cos)));
        Assert.True(cosByCustomer.ContainsKey("278435"));
        Assert.False(cosByCustomer.ContainsKey("000006"));

        payer.SearchDates.Clear();
        var current = await runner.ResolveCustomerByMeteringPointAsync(
            mps, customerNumbers, CancellationToken.None, useCosDate: false);
        Assert.Equal("000006", current[mp]);
        Assert.Null(Assert.Single(payer.SearchDates));
    }

    [Fact]
    public async Task Five_digit_payer_identifier_uses_six_digit_parent_customer_number()
    {
        var mp = Guid.NewGuid();
        var cos = new DateOnly(2026, 5, 1);
        var customer = Guid.NewGuid();
        var payer = new DatedPayerClient(mp, cos, customer, Guid.NewGuid(), "10010");
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer);
        var issues = new List<SourceAccountOwnershipIssue>();
        var result = await runner.ResolveCustomerByMeteringPointAsync(
            [new SettlMpWithCos("MP", mp, cos, new MeteringPointInfoDto())],
            new Dictionary<Guid, string> { [customer] = "910010" }, CancellationToken.None, issues);
        Assert.Equal("910010", result[mp]);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Missing_parent_customer_number_is_reported_and_not_assigned()
    {
        var mp = Guid.NewGuid();
        var cos = new DateOnly(2026, 5, 1);
        var payer = new DatedPayerClient(mp, cos, Guid.NewGuid(), Guid.NewGuid(), "438765-1");
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer);
        var issues = new List<SourceAccountOwnershipIssue>();
        var result = await runner.ResolveCustomerByMeteringPointAsync(
            [new SettlMpWithCos("MP", mp, cos, new MeteringPointInfoDto())],
            new Dictionary<Guid, string>(), CancellationToken.None, issues);
        Assert.Empty(result);
        Assert.Equal("MissingOrInvalidSettlCustomerNumber", Assert.Single(issues).Reason);
        Assert.Equal("MP", issues[0].MeteringPoint);
    }

    [Fact]
    public async Task No_payer_at_cos_uses_todays_customer_when_it_matches_the_xellent_account()
    {
        var (mp, payer, runner) = NoPayerAtCos();
        var customerByMp = await runner.ResolveCustomerByMeteringPointAsync(
            [mp], CurrentCustomer, CancellationToken.None);
        Assert.Empty(customerByMp);

        var resolved = await runner.ResolveMissingCustomersTodayAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None);

        Assert.Equal("MP", Assert.Single(resolved));
        Assert.Equal("278435", customerByMp[mp.Id]);
        Assert.Equal([mp.Cos.ToDateTime(TimeOnly.MinValue), null], payer.SearchDates);

        var state = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "278435" }] };
        var ownership = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state,
            [new GenerateStateOfflineCache.OwnershipEvidence("MP", "278435", "278435", "278435",
                mp.Cos, ["278435"])], "production");
        Assert.Empty(ownership.Issues);
        Assert.Equal(["278435"], Assert.Single(state.Accounts).SourceAccounts);
    }

    [Fact]
    public async Task No_payer_at_cos_rejects_todays_customer_when_it_does_not_match_the_xellent_account()
    {
        var (mp, _, runner) = NoPayerAtCos();
        var customerByMp = new Dictionary<Guid, string>();
        await runner.ResolveMissingCustomersTodayAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None);
        Assert.Equal("278435", customerByMp[mp.Id]);

        var state = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "278435" }] };
        var ownership = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state,
            [new GenerateStateOfflineCache.OwnershipEvidence("MP", "278435", "503281", "503281",
                mp.Cos, ["503281"])], "production", skipUnresolvedSources: true);

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(ownership.Issues).Reason);
        Assert.Empty(Assert.Single(state.Accounts).SourceAccounts);
    }

    [Fact]
    public void Rejected_today_point_does_not_keep_a_customer_or_move_its_cos_window()
    {
        var onlyToday = Guid.NewGuid();
        var also = Guid.NewGuid();
        var own = Guid.NewGuid();
        var mps = new[]
        {
            new SettlMpWithCos("ONLY-TODAY", onlyToday, new DateOnly(2026, 3, 1), new MeteringPointInfoDto()),
            new SettlMpWithCos("ALSO-TODAY", also, new DateOnly(2026, 3, 1), new MeteringPointInfoDto()),
            new SettlMpWithCos("OWN", own, new DateOnly(2026, 8, 1), new MeteringPointInfoDto())
        };
        var customers = new Dictionary<Guid, string>
        {
            [onlyToday] = "100001", [also] = "100002", [own] = "100002"
        };

        var cos = GenerateStateFromSettlRunner.MinCosWithoutRejectedTodayPoints(mps, customers,
            new HashSet<string> { "ONLY-TODAY", "ALSO-TODAY" });

        Assert.False(cos.ContainsKey("100001"));
        Assert.Equal(new DateOnly(2026, 8, 1), cos["100002"]);
    }

    [Fact]
    public async Task Metering_point_resolved_at_cos_is_not_looked_up_again()
    {
        var (mp, payer, runner) = NoPayerAtCos();
        var customerByMp = new Dictionary<Guid, string> { [mp.Id] = "503281" };

        var resolved = await runner.ResolveMissingCustomersTodayAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None);

        Assert.Empty(resolved);
        Assert.Empty(payer.SearchDates);
        Assert.Equal("503281", customerByMp[mp.Id]);
    }

    [Fact]
    public async Task Unusable_link_at_cos_is_not_replaced_by_a_today_lookup()
    {
        var (mp, payer, runner) = NoPayerAtCos();
        var issue = new SourceAccountOwnershipIssue(
            "MissingOrInvalidSettlCustomerNumber", "", "MP", Guid.NewGuid().ToString());

        var resolved = await runner.ResolveMissingCustomersTodayAsync(
            [mp], CurrentCustomer, [], [issue], offline: false, CancellationToken.None);

        Assert.Empty(resolved);
        Assert.Empty(payer.SearchDates);
    }

    [Fact]
    public async Task Offline_run_skips_the_today_lookup_without_throwing()
    {
        var (mp, payer, runner) = NoPayerAtCos();
        var customerByMp = new Dictionary<Guid, string>();

        var resolved = await runner.ResolveMissingCustomersTodayAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: true, CancellationToken.None);

        Assert.Empty(resolved);
        Assert.Empty(customerByMp);
        Assert.Empty(payer.SearchDates);
    }

    [Fact]
    public async Task Unknown_customer_today_stays_unresolved_and_is_reported()
    {
        var (mp, _, runner) = NoPayerAtCos();
        var customerByMp = new Dictionary<Guid, string>();
        var issues = new List<SourceAccountOwnershipIssue>();

        var resolved = await runner.ResolveMissingCustomersTodayAsync(
            [mp], new Dictionary<Guid, string>(), customerByMp, issues, offline: false,
            CancellationToken.None);

        Assert.Empty(resolved);
        Assert.Empty(customerByMp);
        Assert.Equal("MissingOrInvalidSettlCustomerNumber", Assert.Single(issues).Reason);
    }

    [Fact]
    public async Task No_payer_at_cos_resolves_through_the_customer_link_at_cos()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var linked = Guid.NewGuid();
        var links = new StubCustomerLinks((mp.Id, linked));
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer, customerLinkLookup: links);
        var customerByMp = await runner.ResolveCustomerByMeteringPointAsync(
            [mp], CurrentCustomer, CancellationToken.None);
        var customers = new Dictionary<Guid, string> { [linked] = "278435" };

        var resolved = await runner.ResolveMissingCustomersByLinkAsync(
            [mp], customers, customerByMp, [], offline: false, CancellationToken.None);
        var today = await runner.ResolveMissingCustomersTodayAsync(
            [mp], customers, customerByMp, [], offline: false, CancellationToken.None);

        Assert.Equal("MP", Assert.Single(resolved));
        Assert.Equal("278435", customerByMp[mp.Id]);
        Assert.Equal(mp.Cos.ToDateTime(TimeOnly.MinValue), Assert.Single(links.Dates));
        Assert.Empty(today);
        Assert.Equal([mp.Cos.ToDateTime(TimeOnly.MinValue)], payer.SearchDates);

        var state = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "278435" }] };
        var ownership = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state,
            [new GenerateStateOfflineCache.OwnershipEvidence("MP", "278435", "278435", "278435",
                mp.Cos, ["278435"])], "production");
        Assert.Empty(ownership.Issues);
        Assert.Equal(["278435"], Assert.Single(state.Accounts).SourceAccounts);
    }

    [Fact]
    public async Task Customer_link_with_a_mismatching_customer_is_still_rejected()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var linked = Guid.NewGuid();
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer,
            customerLinkLookup: new StubCustomerLinks((mp.Id, linked)));
        var customerByMp = new Dictionary<Guid, string>();
        var resolved = await runner.ResolveMissingCustomersByLinkAsync(
            [mp], new Dictionary<Guid, string> { [linked] = "278435" }, customerByMp, [],
            offline: false, CancellationToken.None);
        Assert.Equal("278435", customerByMp[mp.Id]);

        var state = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "278435" }] };
        var ownership = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(state,
            [new GenerateStateOfflineCache.OwnershipEvidence("MP", "278435", "503281", "503281",
                mp.Cos, ["503281"])], "production", skipUnresolvedSources: true);

        var issue = Assert.Single(ownership.Issues);
        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", issue.Reason);
        Assert.Contains(issue.MeteringPoint, resolved);
        Assert.Empty(Assert.Single(state.Accounts).SourceAccounts);
    }

    [Fact]
    public async Task No_customer_link_at_cos_leaves_the_point_for_the_today_lookup()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer,
            customerLinkLookup: new StubCustomerLinks());
        var customerByMp = new Dictionary<Guid, string>();

        Assert.Empty(await runner.ResolveMissingCustomersByLinkAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None));

        Assert.Empty(customerByMp);
        Assert.Equal("MP", Assert.Single(await runner.ResolveMissingCustomersTodayAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None)));
    }

    [Fact]
    public async Task Metering_point_resolved_through_the_payer_is_not_looked_up_by_link()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var links = new StubCustomerLinks((mp.Id, TodayCustomer));
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer, customerLinkLookup: links);
        var customerByMp = new Dictionary<Guid, string> { [mp.Id] = "503281" };

        Assert.Empty(await runner.ResolveMissingCustomersByLinkAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None));

        Assert.Empty(links.Dates);
        Assert.Equal("503281", customerByMp[mp.Id]);
    }

    [Fact]
    public async Task Two_linked_customers_at_cos_are_reported_and_not_assigned()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer,
            customerLinkLookup: new StubCustomerLinks((mp.Id, first), (mp.Id, second)));
        var customerByMp = new Dictionary<Guid, string>();
        var issues = new List<SourceAccountOwnershipIssue>();

        Assert.Empty(await runner.ResolveMissingCustomersByLinkAsync(
            [mp], new Dictionary<Guid, string> { [first] = "100001", [second] = "100002" },
            customerByMp, issues, offline: false, CancellationToken.None));

        Assert.Empty(customerByMp);
        Assert.Equal("MultipleSettlCustomersForMeteringPoint", Assert.Single(issues).Reason);
    }

    [Fact]
    public async Task Linked_customer_without_a_six_digit_number_is_reported_and_not_assigned()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer,
            customerLinkLookup: new StubCustomerLinks((mp.Id, Guid.NewGuid())));
        var customerByMp = new Dictionary<Guid, string>();
        var issues = new List<SourceAccountOwnershipIssue>();

        Assert.Empty(await runner.ResolveMissingCustomersByLinkAsync(
            [mp], new Dictionary<Guid, string>(), customerByMp, issues, offline: false,
            CancellationToken.None));

        Assert.Empty(customerByMp);
        Assert.Equal("MissingOrInvalidSettlCustomerNumber", Assert.Single(issues).Reason);
    }

    [Fact]
    public async Task Failed_customer_link_lookup_assigns_nothing()
    {
        var (mp, payer, _) = NoPayerAtCos();
        var links = new StubCustomerLinks((mp.Id, TodayCustomer)) { Error = "HTTP 500" };
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer, customerLinkLookup: links);
        var customerByMp = new Dictionary<Guid, string>();

        Assert.Empty(await runner.ResolveMissingCustomersByLinkAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: false, CancellationToken.None));

        Assert.Empty(customerByMp);
    }

    [Fact]
    public async Task Offline_run_skips_the_customer_link_lookup_without_throwing()
    {
        var (mp, _, runner) = NoPayerAtCos();
        var customerByMp = new Dictionary<Guid, string>();

        var resolved = await runner.ResolveMissingCustomersByLinkAsync(
            [mp], CurrentCustomer, customerByMp, [], offline: true, CancellationToken.None);

        Assert.Empty(resolved);
        Assert.Empty(customerByMp);
    }

    [Fact]
    public async Task Customer_link_lookup_posts_the_relation_date_and_reads_settls_answer()
    {
        var (mp, customer) = (Guid.NewGuid(), Guid.NewGuid());
        var handler = new RecordingHandler($$"""
            [{"Customer":{"Id":"{{customer}}","CustomerNumber":"278435"},
              "MeteringPoints":[{"Id":"{{Guid.NewGuid()}}","MeteringPointId":"{{mp}}",
                "Start":"2021-12-31T23:00:00Z","End":"9999-12-31T23:59:58.999Z"}]}]
            """);
        var lookup = new SettlCustomerLinkLookup(new HttpClient(handler), new StubAuth(),
            Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid" }),
            NullLogger<SettlCustomerLinkLookup>.Instance);

        var (items, error) = await lookup.FindCustomersForMeteringPointsAsync(
            [mp], new DateTime(2026, 3, 1), CancellationToken.None);

        Assert.Null(error);
        var entry = Assert.Single(items!);
        Assert.Equal(customer, entry.Customer!.Id);
        Assert.Equal(mp, Assert.Single(entry.MeteringPoints!).MeteringPointId);
        Assert.Equal("https://offline.invalid/internal-billing/api/v2/customers/CustomerInfosForMeteringPoints",
            handler.Url);
        Assert.Equal($$"""{"RelationDate":"2026-03-01T00:00:00","MeteringPointIds":["{{mp}}"]}""", handler.Body);
    }

    private sealed class StubCustomerLinks(params (Guid Mp, Guid Customer)[] links) : ISettlCustomerLinkLookup
    {
        public string? Error { get; init; }
        public List<DateTime> Dates { get; } = [];

        public Task<(List<CustomerLinkEntry>? Items, string? Error)> FindCustomersForMeteringPointsAsync(
            IReadOnlyList<Guid> meteringPointIds, DateTime relationDate, CancellationToken cancellationToken)
        {
            Dates.Add(relationDate);
            if (Error is not null)
                return Task.FromResult<(List<CustomerLinkEntry>?, string?)>((null, Error));
            List<CustomerLinkEntry> items = [.. links.Where(l => meteringPointIds.Contains(l.Mp))
                .Select(l => new CustomerLinkEntry
                {
                    Customer = new CustomerLinkCustomer { Id = l.Customer },
                    MeteringPoints = [new CustomerLinkMeteringPoint { MeteringPointId = l.Mp }]
                })];
            return Task.FromResult<(List<CustomerLinkEntry>?, string?)>((items, null));
        }
    }

    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private sealed class StubAuth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
        public void InvalidateToken() { }
    }

    private static readonly Guid TodayCustomer = Guid.NewGuid();
    private static readonly Dictionary<Guid, string> CurrentCustomer = new() { [TodayCustomer] = "278435" };

    private static (SettlMpWithCos Mp, DatedPayerClient Payer, GenerateStateFromSettlRunner Runner) NoPayerAtCos()
    {
        var id = Guid.NewGuid();
        var cos = new DateOnly(2026, 3, 1);
        var payer = new DatedPayerClient(id, cos, Guid.NewGuid(), TodayCustomer, noLinkAtCos: true);
        return (new SettlMpWithCos("MP", id, cos, new MeteringPointInfoDto()), payer,
            new GenerateStateFromSettlRunner(null!, payerClient: payer));
    }

    private sealed class DatedPayerClient(
        Guid mp, DateOnly cos, Guid historicalCustomer, Guid currentCustomer,
        string historicalIdentifier = "278435", bool noLinkAtCos = false) : ISettlPayerClient
    {
        public List<DateTime?> SearchDates { get; } = [];

        public Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(
            IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken ct = default)
        {
            SearchDates.Add(searchDate);
            if (noLinkAtCos && searchDate is not null)
                return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>(([], null));
            var customer = searchDate == cos.ToDateTime(TimeOnly.MinValue)
                ? historicalCustomer : currentCustomer;
            return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>((
                [new PayerForMeteringPointsDto
                {
                    Payer = new PayerResponse { CustomerId = customer, IsPrimaryCustomerPayer = true,
                        PayerInformation = new PayerInformationDto { PayerIdentifier =
                            customer == historicalCustomer ? historicalIdentifier : "000006" } },
                    MeteringPoints = [new PayerMeteringPointDto { MeteringPointId = mp }]
                }], null));
        }

        public Task<(bool Success, string? Error)> CreateAsync(CreatePayersRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(string payerIdentification, string countryCode = "DK", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(Guid payerId, DateTime? effectiveDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> UpdateRawAsync(Guid payerId, string updateRequestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> UpdatePayerBillingAsync(Guid payerId, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> billingPatches, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> DeleteAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(Guid customerId, string countryCode = "DK", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Json, string? Error)> GetRawByIdAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
