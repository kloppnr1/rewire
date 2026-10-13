using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Tests.Fixtures;

namespace DataMigration.OpsCli.Tests.Runners;

public class DeliveryHistoryPrefixTests
{
    public static IEnumerable<object[]> IdentifiedPatterns()
    {
        string[] tails =
        [
            "", "false,ChangeOfSupplier,Na", "false,Na,ChangeOfSupplier",
            "false,Na,ChangeOfSupplier|true,ChangeOfSupplier,Na",
            "false,Na,ChangeOfSupplier|true,ChangeOfSupplier,Na|false,Na,ChangeOfSupplier",
            "false,Na,ChangeOfSupplier|true,ChangeOfSupplier,Na|false,Na,MoveOut",
            "false,Na,ChangeOfSupplier|true,MoveIn,Na",
            "false,Na,ChangeOfSupplier|true,MoveIn,Na|false,Na,ChangeOfSupplier",
            "false,Na,ChangeOfSupplier|true,MoveIn,Na|false,Na,MoveOut",
            "false,Na,ChangeOfSupplier|true,Na,ChangeOfSupplier",
            "false,Na,ChangeOfSupplier|true,Na,Unknown|false,Na,ChangeOfSupplier|true,ChangeOfSupplier,Na",
            "false,Na,MoveOut", "false,Na,MoveOut|true,ChangeOfSupplier,Na",
            "false,Na,MoveOut|true,MoveIn,Na",
            "false,Na,MoveOut|true,Na,ChangeOfSupplier|false,Na,ChangeOfSupplier",
            "false,Na,Na|true,Na,Na", "false,Unknown,Na", "false,Unknown,Na|true,MoveIn,Na"
        ];
        return tails.Select(t => new object[] { "false,Na,Na|true,ChangeOfSupplier,Na" + (t.Length == 0 ? "" : "|" + t) });
    }

    [Theory]
    [MemberData(nameof(IdentifiedPatterns))]
    public void Every_identified_pattern_matches_the_prefix(string pattern)
    {
        var result = DeliveryHistoryPrefix.Assess(History(pattern));
        Assert.True(result.Matches);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData("true,Na,Na|true,ChangeOfSupplier,Na")]
    [InlineData("false,MoveOut,Na|true,ChangeOfSupplier,Na")]
    [InlineData("false,Na,Na|true,MoveIn,Na")]
    [InlineData("false,Na,Na|true,ChangeOfSupplier,MoveOut")]
    [InlineData("true,Na,Na|false,Na,Na|true,ChangeOfSupplier,Na")]
    [InlineData("false,na,Na|true,ChangeOfSupplier,Na")]
    [InlineData("false,Na,Na")]
    [InlineData("")]
    public void Nonmatching_or_later_prefix_is_not_eligible(string pattern)
    {
        var result = DeliveryHistoryPrefix.Assess(History(pattern));
        Assert.False(result.Matches);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Orders_by_start_not_response_position()
    {
        var result = DeliveryHistoryPrefix.Assess(History("false,Na,Na|true,ChangeOfSupplier,Na|false,Na,MoveOut", reverse: true));
        Assert.True(result.Matches);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"deliveryStatuses":[{"period":{"start":null}}]}""")]
    [InlineData("""{"deliveryStatuses":[{"period":{"start":"2026-01-01"}},{"period":{"start":"2026-01-01"}}]}""")]
    public void Unreadable_or_ambiguous_history_is_unknown_not_a_nonmatch(string? json)
    {
        var result = DeliveryHistoryPrefix.Assess(json);
        Assert.Null(result.Matches);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Does_not_restrict_later_states_or_reasons()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(History("false,Na,Na|true,ChangeOfSupplier,Na|false,Unknown,Unknown"))!;
        json["deliveryStatuses"]![2]!["inDelivery"] = null;
        json["deliveryStatuses"]![2]!["startReason"] = null;
        Assert.True(DeliveryHistoryPrefix.Assess(json.ToJsonString()).Matches);
    }

    [Fact]
    public async Task Nonmatching_mp_still_reads_consumers_without_writes()
    {
        var id = Guid.NewGuid();
        var client = Client(id);
        client.DeliveryHistories[id] = (History("true,Na,Na|true,ChangeOfSupplier,Na"), null);
        using var csv = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], null, csv));
        Assert.Equal(2, client.GetByIdV4Calls);
        Assert.Equal(0, client.BlankCalls);
        Assert.Contains("\"No\"", csv.ToString());
    }

    [Fact]
    public async Task Failed_history_retains_warning_row_and_still_reads_consumers()
    {
        var id = Guid.NewGuid();
        var client = Client(id);
        client.DeliveryHistories[id] = (null, "Fixture failure");
        using var csv = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], null, csv));
        Assert.Contains(id.ToString(), csv.ToString());
        Assert.Contains("DeliveryPatternMatch", csv.ToString());
        Assert.Contains("Fixture failure", csv.ToString());
        Assert.Contains("Unknown", csv.ToString());
        Assert.Equal(2, client.GetByIdV4Calls);
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Limit_counts_duplicates_not_prefix_matches_and_stops_next_candidate()
    {
        var first = Guid.NewGuid();
        var client = Client(first);
        client.DeliveryHistories[first] = (History("true,Na,Na|true,ChangeOfSupplier,Na"), null);
        var second = Guid.NewGuid();
        client.MarketPoints.Add(new() { Id = second, InDelivery = false });
        client.ConsumerPoints[first] = new() { Id = first, Consumer = new() { Start = new DateTime(2026, 1, 1), CustomerType = "Person" } };
        client.ConsumerPoints[second] = new()
        {
            Id = second, Consumer = new()
            {
                Start = new DateTime(2026, 1, 1), CustomerType = "Person",
                FirstConsumerParty = new() { Name = new() { FullName = "Synthetic" } },
                SecondConsumerParty = new() { Name = new() { FullName = "Synthetic" } }
            }
        };
        client.MarketPoints.Add(new() { Id = Guid.NewGuid() });
        using var csv = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], 1, csv));
        Assert.Equal(2, client.DeliveryRequests.Count);
        Assert.Equal(2, client.ConsumerRequests.Count);
        Assert.Equal(second, client.ConsumerRequests[1].Id);
    }

    private static StubFixConsumer2DuplicateMeteringPointClient Client(Guid id)
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "fixture", InDelivery = false });
        return client;
    }

    private static string History(string pattern, bool reverse = false)
    {
        var entries = pattern.Length == 0 ? [] : pattern.Split('|').Select((token, i) =>
        {
            var parts = token.Split(',');
            return new
            {
                inDelivery = bool.Parse(parts[0]), startReason = parts[1], stopReason = parts[2],
                period = new { start = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i),
                    end = new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero).AddDays(i + 1) }
            };
        }).ToArray();
        return JsonSerializer.Serialize(new { deliveryStatuses = reverse ? entries.Reverse().ToArray() : entries });
    }
}
