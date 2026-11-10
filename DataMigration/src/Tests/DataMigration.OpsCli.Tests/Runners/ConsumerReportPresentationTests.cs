using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class ConsumerReportPresentationTests
{
    [Theory]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T01:00:00.0000000+01:00")]
    [InlineData("2026-06-30T22:00:00Z", "2026-07-01T00:00:00.0000000+02:00")]
    [InlineData("2024-10-23T22:00:00Z", "2024-10-24T00:00:00.0000000+02:00")]
    [InlineData("2026-03-29T00:30:00Z", "2026-03-29T01:30:00.0000000+01:00")]
    [InlineData("2026-03-29T01:30:00Z", "2026-03-29T03:30:00.0000000+02:00")]
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T02:30:00.0000000+02:00")]
    [InlineData("2026-10-25T01:30:00Z", "2026-10-25T02:30:00.0000000+01:00")]
    public void Danish_dates_apply_seasonal_offsets(string utc, string expected) =>
        Assert.Equal(expected, ConsumerReportDates.Format(DateTimeOffset.Parse(utc)));

    [Fact]
    public void Sentinel_end_is_open_and_does_not_overflow_conversion() =>
        Assert.Equal("(open)", ConsumerReportDates.Format(DateTimeOffset.MaxValue, true));

    [Fact]
    public async Task Tied_first_starts_do_not_guess_which_period_to_suppress()
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id });
        client.ConsumerPoints[id] = new()
        {
            Id = id, Consumer = new()
            {
                Start = new DateTime(2026, 1, 1), CustomerType = "Person",
                FirstConsumerParty = new() { Name = new() { FullName = "Synthetic" } },
                SecondConsumerParty = new() { Name = new() { FullName = "Synthetic" } }
            }
        };
        client.ConsumerHistories[id] = (new() { ConsumerDates = [new DateTime(2026, 1, 1)] }, null);
        client.DeliveryHistories[id] = ("""
            {"deliveryStatuses":[
              {"inDelivery":false,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"Na","stopReason":"Na"},
              {"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"ChangeOfSupplier","stopReason":"Na"}]}
            """, null);
        using var csv = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }.RunAsync("featuretest", [], null, csv));
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv.ToString()));
        parser.SetDelimiters(";");
        parser.ReadFields();
        var parsed = new List<string[]>();
        while (!parser.EndOfData) parsed.Add(parser.ReadFields()!);
        var rows = parsed.ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => { Assert.Equal("Unknown", row[13]); Assert.Equal("Yes", row[14]); Assert.Contains("tied", row[16]); });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Only_first_chronological_period_is_suppressed_and_first_only_matches_remain(bool laterDuplicate, bool firstInDelivery)
    {
        var id = Guid.NewGuid();
        var january = new DateTime(2026, 1, 1);
        var february = new DateTime(2026, 2, 1);
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id });
        client.ConsumerPoints[id] = Point(february, null, laterDuplicate);
        client.DatedConsumerPoints[(id, january)] = Point(january, february, !laterDuplicate);
        client.ConsumerHistories[id] = (new() { ConsumerDates = [january, february] }, null);
        client.DeliveryHistories[id] = (JsonSerializer.Serialize(new { deliveryStatuses = new[]
        {
            new { inDelivery = false, startReason = "Na", stopReason = "MoveOut", period = new { start = "2026-03-01T00:00:00Z", end = (string?)null } },
            new { inDelivery = true, startReason = "ChangeOfSupplier", stopReason = "Na", period = new { start = "2026-02-01T00:00:00Z", end = (string?)"2026-03-01T00:00:00Z" } },
            new { inDelivery = firstInDelivery, startReason = "Na", stopReason = "Na", period = new { start = "2026-01-01T00:00:00Z", end = (string?)"2026-02-01T00:00:00Z" } }
        } }), null);
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }.RunAsync("featuretest", [], null, csv));
        var rows = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal("No", rows[1][13]);
        Assert.Equal(laterDuplicate ? "No" : "Yes", rows[1][14]);
        Assert.Equal(laterDuplicate ? "Yes" : "No", rows[2][13]);
        Assert.Equal(laterDuplicate ? "Yes" : "No", rows[3][13]);
        Assert.Equal("false", rows[3][5]);
        Assert.Contains("duplicates: 1; read errors: 0", progress.ToString());
        Assert.Equal("2026-01-01T01:00:00.0000000+01:00", rows[1][3]);
        Assert.Equal(rows[1][3], rows[1][11]);
        Assert.Equal("(open)", rows[3][4]);

        MeteringPointV4Dto Point(DateTime start, DateTime? end, bool duplicate) => new()
        {
            Id = id, Consumer = new()
            {
                Start = start, End = end, CustomerType = "Person",
                FirstConsumerParty = new() { Name = new() { FullName = "Synthetic" } },
                SecondConsumerParty = new() { Name = new() { FullName = duplicate ? "Synthetic" : "Different" } }
            }
        };
    }
}
