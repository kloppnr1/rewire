using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SourceAccountOwnershipTests
{
    [Fact]
    public void Multiple_mps_for_one_source_account_assign_it_once_to_the_settl_customer()
    {
        var customer = Account("900100");
        var result = Assign(
            [customer],
            new Dictionary<string, string> { ["mp1"] = "900100", ["mp2"] = "900100" },
            [("mp1", "100"), ("mp2", "100")]);

        Assert.Empty(result.Issues);
        Assert.Equal(["100"], customer.SourceAccounts);
        Assert.Equal(2, result.MappedMeteringPoints);
        Assert.Equal(1, result.MappedSourceAccounts);
    }

    [Fact]
    public void Merged_source_accounts_follow_their_mps_to_one_settl_customer()
    {
        var customer = Account("900100");
        var result = Assign(
            [customer],
            new Dictionary<string, string> { ["mp1"] = "900100", ["mp2"] = "900100" },
            [("mp1", "100"), ("mp2", "200")]);

        Assert.Empty(result.Issues);
        Assert.Equal(["100", "200"], customer.SourceAccounts);
    }

    [Fact]
    public void Source_account_linked_to_two_settl_customers_is_reported_not_assigned()
    {
        var first = Account("900100");
        var second = Account("900200");
        var result = Assign(
            [first, second],
            new Dictionary<string, string> { ["mp1"] = "900100", ["mp2"] = "900200" },
            [("mp1", "100"), ("mp2", "100")]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("NoUniqueCosMatchedSettlCustomerForSourceAccount", issue.Reason);
        Assert.Equal("100", issue.SourceAccount);
        Assert.Equal("mp1|mp2", issue.MeteringPoint);
        Assert.Equal("900100|900200", issue.SettlCustomers);
        Assert.Empty(first.SourceAccounts);
        Assert.Empty(second.SourceAccounts);
    }

    [Fact]
    public void Missing_xellent_mp_link_is_reported_not_assigned()
    {
        var customer = Account("900100");
        var result = Assign(
            [customer],
            new Dictionary<string, string> { ["mp1"] = "900100" },
            []);

        Assert.Equal("NoXellentAccountAtCos", Assert.Single(result.Issues).Reason);
        Assert.Empty(customer.SourceAccounts);
    }

    [Fact]
    public void Missing_xellent_mp_is_skipped_without_discarding_valid_source_accounts()
    {
        var customer = Account("900100");
        var result = Assign(
            [customer],
            new Dictionary<string, string> { ["missing"] = "900100", ["mapped"] = "900100" },
            [("mapped", "100")]);

        Assert.Equal("NoXellentAccountAtCos", Assert.Single(result.Issues).Reason);
        Assert.Equal(["100"], customer.SourceAccounts);
    }

    [Fact]
    public void Shared_source_account_goes_to_unique_customer_matching_xellent_cos()
    {
        var first = Account("900100");
        var second = Account("900200");
        var result = Assign(
            [first, second],
            new Dictionary<string, string> { ["mp1"] = "900100", ["mp2"] = "900200" },
            [("mp1", "100"), ("mp2", "100")],
            new Dictionary<string, DateOnly>
            {
                ["900100"] = new(2026, 5, 1),
                ["900200"] = new(2026, 9, 1)
            },
            new Dictionary<string, DateOnly>
            {
                ["mp1"] = new(2026, 5, 1),
                ["mp2"] = new(2026, 9, 1)
            });

        Assert.Empty(result.Issues);
        Assert.Equal(["100"], first.SourceAccounts);
        Assert.Empty(second.SourceAccounts);
        Assert.Equal(1, result.CosResolvedSourceAccounts);
    }

    [Fact]
    public void Shared_source_account_with_cos_tie_remains_blocking()
    {
        var result = Assign(
            [Account("900100"), Account("900200")],
            new Dictionary<string, string> { ["mp1"] = "900100", ["mp2"] = "900200" },
            [("mp1", "100"), ("mp2", "100")],
            new Dictionary<string, DateOnly>
            {
                ["900100"] = new(2026, 9, 1),
                ["900200"] = new(2026, 9, 1)
            },
            new Dictionary<string, DateOnly>
            {
                ["mp1"] = new(2026, 9, 1),
                ["mp2"] = new(2026, 9, 1)
            });

        Assert.Equal("NoUniqueCosMatchedSettlCustomerForSourceAccount", Assert.Single(result.Issues).Reason);
        Assert.Equal(0, result.CosResolvedSourceAccounts);
    }

    [Fact]
    public void Opt_in_skip_omits_unresolved_source_but_keeps_uncontested_sources()
    {
        var first = Account("900100");
        var second = Account("900200");
        var result = Assign(
            [first, second],
            new Dictionary<string, string>
            {
                ["mp1"] = "900100", ["mp2"] = "900200", ["mp3"] = "900100"
            },
            [("mp1", "100"), ("mp2", "100"), ("mp3", "200")],
            new Dictionary<string, DateOnly>
            {
                ["900100"] = new(2026, 9, 1), ["900200"] = new(2026, 9, 1)
            },
            new Dictionary<string, DateOnly>
            {
                ["mp1"] = new(2026, 9, 1), ["mp2"] = new(2026, 9, 1),
                ["mp3"] = new(2026, 9, 1)
            },
            skipUnresolvedSourceAccounts: true);

        Assert.Equal("NoUniqueCosMatchedSettlCustomerForSourceAccount", Assert.Single(result.Issues).Reason);
        Assert.Equal(["200"], first.SourceAccounts);
        Assert.Empty(second.SourceAccounts);
    }

    [Fact]
    public void Multiple_xellent_accounts_for_one_mp_are_reported_not_assigned()
    {
        var customer = Account("900100");
        var result = Assign(
            [customer],
            new Dictionary<string, string> { ["mp1"] = "900100" },
            [("mp1", "100"), ("mp1", "200")]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal("MultipleXellentAccountsAtCos", issue.Reason);
        Assert.Equal("100|200", issue.SourceAccount);
        Assert.Empty(customer.SourceAccounts);
    }

    [Fact]
    public void Issue_report_is_stable_and_names_the_problem()
    {
        var csv = SourceAccountOwnership.BuildIssueCsv(
            [new SourceAccountOwnershipIssue("NoXellentAccountAtCos", "", "mp1", "900100")]);

        Assert.Equal(
            "Reason;SourceAccount;MeteringPoint;SettlCustomers;CosDate" + Environment.NewLine +
            "NoXellentAccountAtCos;;mp1;900100;" + Environment.NewLine,
            csv);
        Assert.Equal("state-invoices-ownership-issues.csv",
            SourceAccountOwnership.IssueReportPath("state-invoices.json"));
    }

    private static MigratedAccount Account(string number) => new() { SettlAccountNumber = number };

    private static SourceAccountOwnershipResult Assign(
        IReadOnlyList<MigratedAccount> accounts,
        IReadOnlyDictionary<string, string> settlCustomerByMp,
        IReadOnlyCollection<(string MpId, string Account)> xellentPairs,
        IReadOnlyDictionary<string, DateOnly>? settlCosByCustomer = null,
        IReadOnlyDictionary<string, DateOnly>? xellentCosByMp = null,
        bool skipUnresolvedSourceAccounts = false) =>
        SourceAccountOwnership.Assign(accounts, settlCustomerByMp, xellentPairs,
            settlCosByCustomer ?? new Dictionary<string, DateOnly>(),
            xellentCosByMp ?? new Dictionary<string, DateOnly>(), skipUnresolvedSourceAccounts);
}
