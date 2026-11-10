using DataMigration.OpsCli.Data;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class HistoricalSourceOwnershipTests
{
    [Fact]
    public void Unique_historical_member_is_added_without_replacing_direct_owner()
    {
        var first = Account("A", "100");
        var second = Account("B", "200");

        var result = HistoricalSourceOwnership.Assign([first, second],
        [
            new("100", "B", new DateOnly(2026, 9, 1)),
            new("300", "A", new DateOnly(2026, 3, 1))
        ], EmptyMpCustomers(), new HashSet<string>(StringComparer.Ordinal) { "100", "200" },
            skipUnresolved: false);

        Assert.Empty(result.Issues);
        Assert.Equal(["100", "300"], first.SourceAccounts);
        Assert.Equal(["200"], second.SourceAccounts);
        Assert.Equal("AssignedUniqueHistoricalGroup", Assert.Single(result.Decisions).Disposition);
        Assert.Equal("A", result.Decisions[0].AssignedCustomer);
    }

    [Fact]
    public void Competing_historical_claims_are_reported_and_never_assigned()
    {
        var first = Account("A", "100");
        var second = Account("B", "200");
        var claims = new[]
        {
            new HistoricalSourceClaim("300", "A", new DateOnly(2026, 3, 1)),
            new HistoricalSourceClaim("300", "B", new DateOnly(2026, 9, 1))
        };

        var result = HistoricalSourceOwnership.Assign([first, second], claims,
            EmptyMpCustomers(), new HashSet<string>(StringComparer.Ordinal) { "100", "200" },
            skipUnresolved: true);

        Assert.Equal("HistoricalSourceMultipleSettlCustomers", Assert.Single(result.Issues).Reason);
        Assert.Equal("SkippedAmbiguousHistoricalOwner", Assert.Single(result.Decisions).Disposition);
        Assert.Equal("A|B", result.Decisions[0].CandidateCustomers);
        Assert.DoesNotContain("300", first.SourceAccounts);
        Assert.DoesNotContain("300", second.SourceAccounts);
    }

    [Fact]
    public void Same_cvr_claims_use_lowest_original_xellent_account_not_padded_settl_number()
    {
        var first = Account("101254", "101254");
        var second = Account("952461", "52461");
        var claims = new[]
        {
            new HistoricalSourceClaim("101253", "101254", new DateOnly(2026, 5, 1)),
            new HistoricalSourceClaim("101253", "952461", new DateOnly(2026, 6, 1))
        };
        var cvrByAccount = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["101253"] = "36665030", ["101254"] = "36665030", ["52461"] = "36665030"
        };
        var representatives = HistoricalSourceOwnership.FindXellentRepresentatives(
            [first, second], cvrByAccount, "production");
        Assert.Equal("52461", representatives["952461"]);

        var result = HistoricalSourceOwnership.Assign([first, second], claims,
            EmptyMpCustomers(), new HashSet<string>(StringComparer.Ordinal) { "101254", "52461" },
            skipUnresolved: true, cvrByAccount, representatives);

        Assert.Empty(result.Issues);
        Assert.Equal("AssignedSameCvrLowestXellentAccount", Assert.Single(result.Decisions).Disposition);
        Assert.Equal("952461", result.Decisions[0].AssignedCustomer);
        Assert.DoesNotContain("101253", first.SourceAccounts);
        Assert.Equal(1, second.SourceAccounts.Count(s => s == "101253"));
    }

    [Fact]
    public void Different_or_unknown_cvr_claims_remain_unresolved()
    {
        var representatives = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A"] = "100", ["B"] = "200"
        };
        foreach (var candidateCvr in new[] { "87654321", "" })
        {
            var first = Account("A", "100");
            var second = Account("B", "200");
            var cvrByAccount = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["300"] = "36665030", ["100"] = "36665030", ["200"] = candidateCvr
            };

            var result = HistoricalSourceOwnership.Assign([first, second],
                [new("300", "A", new DateOnly(2026, 3, 1)),
                 new("300", "B", new DateOnly(2026, 9, 1))],
                EmptyMpCustomers(), new HashSet<string>(StringComparer.Ordinal) { "100", "200" },
                skipUnresolved: true, cvrByAccount, representatives);

            Assert.Equal("SkippedAmbiguousHistoricalOwner", Assert.Single(result.Decisions).Disposition);
            Assert.Single(result.Issues);
            Assert.DoesNotContain("300", first.SourceAccounts);
            Assert.DoesNotContain("300", second.SourceAccounts);
        }
    }

    [Fact]
    public void Csv_includes_contract_dates_for_auditing_older_members()
    {
        var rows = new[]
        {
            new HistoricalSourceDecision("404892", "AssignedUniqueHistoricalGroup", "352639",
                "352639", "2026-03-01", "", new DateOnly(2022, 1, 25), new DateOnly(2024, 4, 11))
        };

        var csv = HistoricalSourceOwnership.BuildCsv(rows);

        Assert.Contains("CurrentMeteringPointCustomers", csv);
        Assert.Contains("404892;AssignedUniqueHistoricalGroup;352639;352639;2026-03-01;;2022-01-25;2024-04-11", csv);
    }

    [Fact]
    public void Current_mp_customer_cannot_resolve_competing_historical_claims()
    {
        var first = Account("A", "100");
        var second = Account("B", "200");
        var claims = new[]
        {
            new HistoricalSourceClaim("300", "A", new DateOnly(2026, 3, 1)),
            new HistoricalSourceClaim("300", "B", new DateOnly(2026, 9, 1))
        };
        var mpCustomers = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["300"] = ["B"]
        };

        var result = HistoricalSourceOwnership.Assign([first, second], claims, mpCustomers,
            new HashSet<string>(StringComparer.Ordinal) { "100", "200" }, false);

        Assert.Equal("HistoricalSourceMultipleSettlCustomers", Assert.Single(result.Issues).Reason);
        Assert.DoesNotContain("300", first.SourceAccounts);
        Assert.DoesNotContain("300", second.SourceAccounts);
        Assert.Equal("B", Assert.Single(result.Decisions).CurrentMeteringPointCustomers);
    }

    [Fact]
    public void Later_mp_customer_does_not_veto_unique_historical_claim()
    {
        var first = Account("A", "100");
        var second = Account("B", "200");
        var mpCustomers = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["300"] = ["B"]
        };

        var result = HistoricalSourceOwnership.Assign([first, second],
            [new("300", "A", new DateOnly(2026, 3, 1))], mpCustomers,
            new HashSet<string>(StringComparer.Ordinal) { "100", "200" }, true);

        Assert.Empty(result.Issues);
        Assert.Contains("300", first.SourceAccounts);
        Assert.DoesNotContain("300", second.SourceAccounts);
        Assert.Equal("B", Assert.Single(result.Decisions).CurrentMeteringPointCustomers);
    }

    [Fact]
    public void Directly_observed_but_unresolved_source_cannot_return_as_historical()
    {
        var first = Account("A", "100");

        var result = HistoricalSourceOwnership.Assign([first],
            [new("300", "A", new DateOnly(2026, 3, 1))], EmptyMpCustomers(),
            new HashSet<string>(StringComparer.Ordinal) { "100", "300" }, true);

        Assert.Empty(result.Decisions);
        Assert.DoesNotContain("300", first.SourceAccounts);
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>> EmptyMpCustomers() =>
        new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

    private static MigratedAccount Account(string number, params string[] sources) => new()
    {
        SettlAccountNumber = number,
        SourceAccounts = [.. sources]
    };
}
