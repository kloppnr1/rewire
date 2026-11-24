using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class Consumer2ProgressDisplayTests
{
    [Fact]
    public async Task New_batch_at_refresh_deadline_shows_elapsed_without_zero_second_wait()
    {
        using var writer = new RefreshWriter();
        using var progress = new Consumer2ProgressDisplay(writer, TimeSpan.FromMilliseconds(100));
        progress.Update("initial", true);
        await Task.Delay(120);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = progress.ReadAsync(() => release.Task, "new batch", () => "checking", default);
        try
        {
            await writer.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Elapsed:", writer.ToString());
            Assert.DoesNotContain("Waiting for Settl", writer.ToString());
        }
        finally { release.TrySetResult(1); await read; }
    }

    [Fact]
    public async Task Batch_crossing_wait_threshold_refreshes_after_an_earlier_shared_deadline()
    {
        using var writer = new RefreshWriter();
        using var progress = new Consumer2ProgressDisplay(writer, TimeSpan.FromMilliseconds(100));
        progress.Update("initial", true);
        await Task.Delay(120);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = progress.ReadAsync(() => release.Task, "batch", () => "checking", default);
        try
        {
            await writer.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(read.IsCompleted);
            Assert.Contains("Waiting for Settl response (batch)", writer.ToString());
        }
        finally { release.TrySetResult(1); await read; }
    }

    private sealed class RefreshWriter : StringWriter
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Refreshed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains("Waiting for Settl") == true) Waiting.TrySetResult();
            if (value?.Contains("checking") == true) Refreshed.TrySetResult();
        }
    }
}
