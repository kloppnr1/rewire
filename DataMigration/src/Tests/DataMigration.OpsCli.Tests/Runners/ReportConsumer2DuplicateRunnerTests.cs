using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class ReportConsumer2DuplicateRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task Report_includes_all_delivery_states_without_writes(bool? inDelivery)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "571313100000000002", InDelivery = inDelivery });
        client.Reads.Enqueue(Duplicate(id));
        using var output = new StringWriter();
        var result = await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [], null, output);
        Assert.Equal(0, result);
        Assert.Equal(0, client.BlankCalls);
        Assert.Contains("DeliveryPatternMatch", output.ToString());
        Assert.DoesNotContain("Environment", output.ToString());
        Assert.Contains("Test; \"Name\"", output.ToString().Replace("\"\"", "\""));
        Assert.DoesNotContain("1234567890", output.ToString());
    }

    [Fact]
    public async Task Report_uses_exact_cleanup_detection_and_keeps_business_points_out()
    {
        var id = Guid.NewGuid();
        var mp = Duplicate(id);
        mp.Consumer!.CustomerType = "Company";
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(mp);
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [id.ToString()], null, output));
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Failed_consumer_read_marks_report_incomplete()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        using var output = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(1, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", [Guid.NewGuid().ToString()], null, output));
        Assert.Contains("Report incomplete", progress.ToString());
        Assert.Contains("Checked: 1 / 1", progress.ToString());
        Assert.Contains("read errors: 2", progress.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Scoped_identification_and_guid_keep_identification(bool useGuid)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "571313100000000002", InDelivery = false });
        client.Reads.Enqueue(Duplicate(id));
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("production-hev",
            [useGuid ? id.ToString() : "571313100000000002"], null, output));
        Assert.Contains("DeliveryPatternMatch", output.ToString());
        Assert.DoesNotContain("production-hev", output.ToString());
        Assert.Equal(0, client.BlankCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_mismatched_market_summary_does_not_hide_duplicate(bool mismatched)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        if (mismatched) client.MarketPoints.Add(new() { Id = Guid.NewGuid(), MeteringPointIdentification = "571313100000000002", InDelivery = false });
        client.Reads.Enqueue(Duplicate(id));
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [id.ToString()], null, output));
        Assert.Contains("ConsumerVersionCount", output.ToString());
        Assert.Contains("DeliveryStartDk", output.ToString());
    }

    [Fact]
    public async Task Matching_limit_stops_before_reading_another_candidate()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) client.MarketPoints.Add(new() { Id = id });
        client.Reads.Enqueue(Duplicate(ids[0]));
        using var output = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", [], 1, output));
        Assert.Contains("Stopped at --limit 1", progress.ToString());
        Assert.Contains("Checked: 1 / 2", progress.ToString());
        Assert.Equal(1, client.GetByIdV4Calls);
        Assert.Equal(3, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Differing_or_missing_names_are_excluded(bool missing)
    {
        var id = Guid.NewGuid();
        var mp = Duplicate(id);
        mp.Consumer!.SecondConsumerParty!.Name = missing ? null : new() { FullName = "Other" };
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.Reads.Enqueue(mp);
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [id.ToString()], null, output));
        Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData("market-list page")]
    [InlineData("consumer read")]
    [InlineData("metering-point lookup")]
    public async Task Progress_is_visible_while_a_Settl_request_is_still_pending(string operation)
    {
        var id = Guid.NewGuid();
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = "571313100000000002", InDelivery = true });
        client.Reads.Enqueue(Duplicate(id));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Wait(CancellationToken ct) => release.Task.WaitAsync(ct);
        string[] scope = [];
        switch (operation)
        {
            case "market-list page": client.BeforeMarketRead = Wait; break;
            case "consumer read": client.BeforeConsumerRead = Wait; break;
            case "metering-point lookup": client.BeforeLookup = Wait; scope = ["571313100000000002"]; break;
        }
        using var output = new StringWriter();
        using var progress = new WaitingProgressWriter();
        var run = new ReportConsumer2DuplicateRunner(client)
        {
            ProgressOutput = progress,
            ProgressInterval = TimeSpan.FromMilliseconds(20),
            OutputPath = "consumer2.csv"
        }.RunAsync("featuretest", scope, null, output);
        try
        {
            var waiting = await progress.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            Assert.Contains(operation, waiting);
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Contains("Environment: featuretest", progress.ToString());
        Assert.Contains("Checked: 1 / 1", progress.ToString());
        Assert.Contains("duplicates: 1; read errors: 0", progress.ToString());
        Assert.Contains("Report: consumer2.csv", progress.ToString());
        Assert.DoesNotContain("Waiting for Settl", output.ToString());
        Assert.DoesNotContain("Checking consumers", output.ToString());
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Cancellation_stops_a_pending_read_and_its_progress_loop()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient
        {
            BeforeConsumerRead = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct)
        };
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        using var progress = new WaitingProgressWriter();
        var run = new ReportConsumer2DuplicateRunner(client)
        {
            ProgressOutput = progress,
            ProgressInterval = TimeSpan.FromMilliseconds(20)
        }.RunAsync("featuretest", [Guid.NewGuid().ToString()], null, output, cancellation.Token);
        try
        {
            await progress.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.DoesNotContain("Complete", progress.ToString());
        Assert.Equal(0, client.BlankCalls);
    }

    private sealed class WaitingProgressWriter : StringWriter
    {
        public TaskCompletionSource<string> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("Waiting for Settl response", StringComparison.Ordinal) == true) Waiting.TrySetResult(value);
        }
    }

    private static MeteringPointV4Dto Duplicate(Guid id) => new()
    {
        Id = id, Identification = new() { MeteringPointIdentification = "571313100000000002" },
        Consumer = new()
        {
            Start = new DateTime(2026, 1, 1),
            CustomerType = "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Test; \"Name\"" } },
            SecondConsumerParty = new() { Name = new() { FullName = " Test; \"Name\" " } }
        }
    };
}
