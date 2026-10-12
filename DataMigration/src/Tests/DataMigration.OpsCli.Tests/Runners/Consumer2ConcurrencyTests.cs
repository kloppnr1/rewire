using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class Consumer2ConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Read_only_runs_use_eight_reads_and_show_progress_while_the_batch_is_pending(bool report)
    {
        var (client, ids) = Population(10);
        var active = 0;
        var maximum = 0;
        var started = 0;
        var eightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeConsumerRead = async ct =>
        {
            var current = Interlocked.Increment(ref active);
            int old;
            do { old = Volatile.Read(ref maximum); }
            while (current > old && Interlocked.CompareExchange(ref maximum, current, old) != old);
            if (Interlocked.Increment(ref started) == 8) eightStarted.TrySetResult();
            try { await release.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref active); }
        };
        using var output = new StringWriter();
        using var progress = new WaitingWriter();
        var logs = new List<string>();
        var run = report
            ? new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress, ProgressInterval = TimeSpan.FromMilliseconds(20) }
                .RunAsync("featuretest", [], null, output)
            : new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress, ProgressInterval = TimeSpan.FromMilliseconds(20) }
                .RunAsync(new() { DryRun = true });
        try
        {
            await eightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, Volatile.Read(ref started));
            await progress.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(8, maximum);
        Assert.Equal(10, client.GetByIdV4Calls);
        Assert.Equal(0, client.BlankCalls);
        Assert.Contains("10 / 10", progress.ToString());
        if (report)
        {
            var rows = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            Assert.Equal(10, rows.Length);
            for (var i = 0; i < ids.Length; i++) Assert.Contains(ids[i].ToString(), rows[i]);
        }
        else
        {
            var matches = logs.Where(line => line.Contains("would blank", StringComparison.Ordinal)).ToArray();
            Assert.Equal(10, matches.Length);
            for (var i = 0; i < ids.Length; i++) Assert.Contains(ids[i].ToString(), matches[i]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_stops_all_eight_pending_reads_without_starting_the_next_batch(bool report)
    {
        var (client, _) = Population(10);
        var started = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeConsumerRead = async ct =>
        {
            if (Interlocked.Increment(ref started) == 8) ready.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        using var cancellation = new CancellationTokenSource();
        using var output = new StringWriter();
        using var progress = new StringWriter();
        var run = report
            ? new ReportConsumer2DuplicateRunner(client) { ProgressOutput = progress }.RunAsync("featuretest", [], null, output, cancellation.Token)
            : new FixConsumer2DuplicateRunner(client, _ => { }) { ProgressOutput = progress }.RunAsync(new() { DryRun = true }, cancellation.Token);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(8, started);
        Assert.Equal(0, client.BlankCalls);
    }

    [Fact]
    public async Task Live_cleanup_does_not_prefetch_reads_and_still_verifies_each_write()
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids)
        {
            client.Reads.Enqueue(Point(id));
            var cleared = Point(id);
            cleared.Consumer!.SecondConsumerParty!.Name = new() { FullName = "" };
            client.Reads.Enqueue(cleared);
        }
        var started = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.BeforeConsumerRead = ct =>
        {
            if (Interlocked.Increment(ref started) != 1) return Task.CompletedTask;
            ready.TrySetResult();
            return release.Task.WaitAsync(ct);
        };
        using var progress = new StringWriter();
        var run = new FixConsumer2DuplicateRunner(client, _ => { }) { ProgressOutput = progress }
            .RunAsync(new() { MeteringPointIds = ids.Select(id => id.ToString()).ToArray() });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, started);
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(4, client.GetByIdV4Calls);
        Assert.Equal(2, client.BlankCalls);
    }

    [Fact]
    public async Task Cleanup_dry_run_limit_does_not_read_or_report_extra_matches()
    {
        var (client, _) = Population(10);
        var logs = new List<string>();
        using var progress = new StringWriter();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress }
            .RunAsync(new() { DryRun = true, Limit = 1 }));
        Assert.Equal(1, client.GetByIdV4Calls);
        Assert.Single(logs, line => line.Contains("would blank", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("remaining points were not checked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_read_in_full_cleanup_dry_run_is_counted_and_reported()
    {
        var (client, ids) = Population(8);
        client.ConsumerPoints[ids[0]] = null;
        var logs = new List<string>();
        using var progress = new StringWriter();
        Assert.Equal(1, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress }.RunAsync(new() { DryRun = true }));
        Assert.Contains(logs, line => line.Contains("Read errors: 1", StringComparison.Ordinal));
        Assert.Equal(8, client.GetByIdV4Calls);
        Assert.Equal(0, client.BlankCalls);
    }

    private static (StubFixConsumer2DuplicateMeteringPointClient Client, Guid[] Ids) Population(int count)
    {
        var client = new StubFixConsumer2DuplicateMeteringPointClient();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            client.MarketPoints.Add(new() { Id = id, MeteringPointIdentification = id.ToString(), InDelivery = true });
            client.ConsumerPoints.Add(id, Point(id));
        }
        return (client, ids);
    }

    private static MeteringPointV4Dto Point(Guid id) => new()
    {
        Id = id,
        Identification = new() { MeteringPointIdentification = id.ToString() },
        Consumer = new()
        {
            CustomerType = "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Synthetic" } },
            SecondConsumerParty = new() { Name = new() { FullName = "Synthetic" } }
        }
    };

    private sealed class WaitingWriter : StringWriter
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("Waiting for Settl", StringComparison.Ordinal) == true) Waiting.TrySetResult();
        }
    }
}
