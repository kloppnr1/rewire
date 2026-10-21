using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class ConsumerCleanupSelectionTests
{
    [Theory]
    // The report shows every Yes version; the fix only writes the one in effect today.
    [InlineData("duplicate", 1, 1)]
    [InlineData("future-selected", 1, 0)]
    [InlineData("latest-clean", 0, 0)]
    [InlineData("first-only", 0, 0)]
    [InlineData("repeated-version", 1, 1)]
    [InlineData("inactive", 1, 1)]
    [InlineData("nonmatching-pattern", 1, 1)]
    public async Task Preview_targets_only_the_current_report_yes_version(string scenario, int expected, int expectedPreviews)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var start = new DateTime(2026, 1, 1);
        var point = Point(id, start, true);
        client.ConsumerPoints[id] = point;
        if (scenario == "first-only")
            client.DeliveryHistories[id] = ("""{"deliveryStatuses":[{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"Na","stopReason":"Na"}]}""", null);
        if (scenario == "inactive") client.DeliveryHistories[id] = (StubFixConsumer2DuplicateMeteringPointClient.PrefixHistory.Replace("true", "false"), null);
        if (scenario == "nonmatching-pattern") client.DeliveryHistories[id] = (StubFixConsumer2DuplicateMeteringPointClient.PrefixHistory.Replace("ChangeOfSupplier", "MoveIn"), null);
        if (scenario == "repeated-version")
            client.DeliveryHistories[id] = ("""{"deliveryStatuses":[{"inDelivery":false,"period":{"start":"2025-12-01T00:00:00Z","end":"2026-01-01T00:00:00Z"},"startReason":"Na","stopReason":"Na"},{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":"2026-06-01T00:00:00Z"},"startReason":"MoveIn","stopReason":"Na"},{"inDelivery":false,"period":{"start":"2026-06-01T00:00:00Z","end":null},"startReason":"Na","stopReason":"MoveOut"}]}""", null);
        if (scenario == "future-selected")
        {
            var future = new DateTime(2026, 12, 1);
            point.Consumer!.End = future;
            client.DatedConsumerPoints[(id, future)] = Point(id, future, true);
            client.ConsumerHistories[id] = (new() { ConsumerDates = [start, future] }, null);
        }
        if (scenario == "latest-clean")
        {
            var newer = new DateTime(2026, 6, 1);
            point.Consumer!.End = newer;
            client.DatedConsumerPoints[(id, start)] = point;
            client.ConsumerPoints[id] = Point(id, newer, false);
            client.ConsumerHistories[id] = (new() { ConsumerDates = [start, newer] }, null);
        }
        using var csv = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }.RunAsync("featuretest", [id.ToString()], null, csv));
        var reportDates = csv.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(row => row.Split(';').Select(cell => cell.Trim('"')).ToArray()).Where(row => row[13] == "Yes")
            .Select(row => row[11]).Distinct().ToArray();
        var logs = new List<string>();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress }
            .RunAsync(new() { MeteringPointIds = [id.ToString()], DryRun = true }));
        var previews = logs.Where(line => line.Contains("would blank")).ToArray();
        Assert.Equal(expected, reportDates.Length);
        Assert.Equal(expectedPreviews, previews.Length);
        Assert.Contains($"  Selected but not current (left alone): {expected - expectedPreviews}", logs);
        foreach (var date in reportDates.Take(expectedPreviews)) Assert.Single(previews, line => line.Contains(date));
        Assert.Equal(0, client.BlankCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_history_in_full_scan_is_reported_and_never_written(bool dryRun)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id });
        client.ConsumerPoints[id] = Point(id, new(2026, 1, 1), true);
        client.ConsumerHistories[id] = (null, "Unavailable");
        var logs = new List<string>();
        Assert.Equal(1, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = TextWriter.Null }.RunAsync(new() { DryRun = dryRun }));
        Assert.Contains(logs, line => line.Contains("Unavailable"));
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Execution_updates_each_selected_dated_version_once()
    {
        var id = Guid.NewGuid();
        var start = new DateTime(2026, 1, 1);
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(Point(id, start, true));
        client.Reads.Enqueue(Point(id, start, true));
        client.Reads.Enqueue(Point(id, start, false));
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, _ => { }) { ProgressOutput = TextWriter.Null }
            .RunAsync(new() { MeteringPointIds = [id.ToString(), id.ToString()] }));
        Assert.Equal((id, start), Assert.Single(client.CleanedVersions));
    }

    private static MeteringPointV4Dto Point(Guid id, DateTime start, bool duplicate) => new()
    {
        Id = id, Consumer = new() { Start = start, CustomerType = "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Fixture" } },
            SecondConsumerParty = new() { Name = new() { FullName = duplicate ? "Fixture" : "" } } }
    };
}
