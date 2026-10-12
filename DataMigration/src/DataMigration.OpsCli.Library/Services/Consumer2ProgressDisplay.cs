using System.Diagnostics;

namespace DataMigration.OpsCli.Services;

internal sealed class Consumer2ProgressDisplay(TextWriter writer, TimeSpan interval) : IDisposable
{
    private readonly Stopwatch _phaseElapsed = Stopwatch.StartNew();
    private readonly Stopwatch _sinceUpdate = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly bool _redraw = ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected;
    private int _lineLength;

    public void Update(string message, bool force = false)
    {
        lock (_gate)
        {
            if (!force && _sinceUpdate.Elapsed < interval) return;
            message += $"   Elapsed: {_phaseElapsed.Elapsed:hh\\:mm\\:ss}";
            if (_redraw)
            {
                writer.Write("\r" + message.PadRight(_lineLength));
                _lineLength = message.Length;
            }
            else writer.WriteLine(message);
            writer.Flush();
            _sinceUpdate.Restart();
        }
    }

    public void StartPhase()
    {
        lock (_gate) _phaseElapsed.Restart();
    }

    public void Message(string message)
    {
        lock (_gate)
        {
            Dispose();
            writer.WriteLine(message);
            writer.Flush();
        }
    }

    public async Task<T> ReadAsync<T>(Func<Task<T>> read, string operation, Func<string> status, CancellationToken ct)
    {
        var elapsed = Stopwatch.StartNew();
        var pending = read();
        while (!pending.IsCompleted)
        {
            using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Keep the refresh deadline across consecutive requests, even when
            // each individual read finishes in less than five seconds.
            TimeSpan remaining;
            lock (_gate) remaining = interval - _sinceUpdate.Elapsed;
            // Also refresh when this batch crosses the waiting threshold, even if
            // the shared refresh deadline landed just before that threshold.
            var untilWaiting = interval - elapsed.Elapsed;
            if (untilWaiting > TimeSpan.Zero && remaining > untilWaiting) remaining = untilWaiting;
            var tick = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, tickCancellation.Token);
            if (await Task.WhenAny(pending, tick) == pending)
            {
                await tickCancellation.CancelAsync();
                break;
            }
            ct.ThrowIfCancellationRequested();
            Update(elapsed.Elapsed >= interval
                ? $"{status()}   Waiting for Settl response ({operation})... {elapsed.Elapsed.TotalSeconds:0} seconds"
                : status(), force: true);
        }
        return await pending;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_lineLength == 0) return;
            writer.WriteLine();
            _lineLength = 0;
        }
    }
}
