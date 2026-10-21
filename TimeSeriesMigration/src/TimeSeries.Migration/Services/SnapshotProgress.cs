using System.Text.Json;

namespace TimeSeries.Cli.Services;

/// <summary>
/// What a running <c>snapshot</c> says about itself in <c>progress.json</c>, next to the snapshot files, so
/// <c>progress</c> can show it from another session. <paramref name="Done"/>/<paramref name="Total"/> are
/// the current step inside the phase. <paramref name="Status"/> is Running, Completed or Stopped.
/// </summary>
public sealed record SnapshotProgressState(
    string Environment,
    int Phase,
    int PhaseCount,
    string PhaseName,
    string? Step,
    int Done,
    int Total,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Writes <c>progress.json</c> for a running snapshot. The snapshot's own work always wins: a step update is
/// written at most every <c>minInterval</c> (default 5 s), off the caller's thread, through a temp file and
/// an atomic move, and any failure is swallowed. A caller never waits for a write: an update that arrives
/// while a write is busy is picked up by that write before it finishes, so the file always ends on the
/// latest state.
/// </summary>
public sealed class SnapshotProgressWriter
{
    public const string FileName = "progress.json";
    public const int PhaseCount = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly TimeSpan _minInterval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _background;
    private readonly Action<string>? _debug;
    private readonly object _gate = new();
    private SnapshotProgressState _state;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private int _writing;
    private int _dirty;

    /// <param name="background">Write throttled step updates on the thread pool (the default). Tests pass
    /// false to write inline.</param>
    public SnapshotProgressWriter(string directory, string environment, TimeSpan? minInterval = null,
        Func<DateTimeOffset>? clock = null, bool background = true, Action<string>? debug = null)
    {
        _path = Path.Combine(directory, FileName);
        _minInterval = minInterval ?? TimeSpan.FromSeconds(5);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _background = background;
        _debug = debug;
        var now = _clock();
        _state = new SnapshotProgressState(environment, 0, PhaseCount, "Starting", null, 0, 0, "Running", now, now);
    }

    public string FilePath => _path;

    /// <summary>A new phase starts: written straight away.</summary>
    public void StartPhase(int phase, string name) =>
        Update(s => s with { Phase = phase, PhaseName = name, Step = null, Done = 0, Total = 0 }, force: true);

    /// <summary>Progress through a step, called often and from parallel work: written at most every interval.</summary>
    public void Report(string step, int done, int total) =>
        Update(s => s with { Step = step, Done = done, Total = total }, force: false);

    public void Complete() => Update(s => s with { Status = "Completed" }, force: true);

    /// <summary>The snapshot ended without completing (an error or an exception).</summary>
    public void Stop() => Update(s => s.Status == "Completed" ? s : s with { Status = "Stopped" }, force: true);

    private void Update(Func<SnapshotProgressState, SnapshotProgressState> change, bool force)
    {
        try
        {
            lock (_gate)
            {
                var now = _clock();
                _state = change(_state) with { UpdatedAt = now };
                if (!force && now - _lastWrite < _minInterval)
                    return;
                _lastWrite = now;
            }
            if (force || !_background)
                Write();
            else
                _ = Task.Run(Write);
        }
        catch (Exception ex)
        {
            Debug(ex);
        }
    }

    // Writes the newest state. Whoever holds the write flag keeps writing while updates arrive, so a
    // caller that finds it busy just marks the state dirty and returns.
    private void Write()
    {
        Volatile.Write(ref _dirty, 1);
        while (true)
        {
            if (Interlocked.Exchange(ref _writing, 1) == 1)
                return;
            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    SnapshotProgressState state;
                    lock (_gate)
                        state = _state;
                    WriteFile(state);
                }
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
            if (Volatile.Read(ref _dirty) == 0)
                return;
        }
    }

    private void WriteFile(SnapshotProgressState state)
    {
        try
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug(ex);
        }
    }

    private void Debug(Exception ex)
    {
        try { _debug?.Invoke($"progress.json not written: {ex.Message}"); }
        catch { /* progress must never break the snapshot */ }
    }
}

/// <summary>
/// The states of the last <c>post --all</c> run (dry or live), in <c>states.json</c> next to the snapshot
/// files: the count per reason under each state. <paramref name="Status"/> is Running, Completed or Stopped;
/// <paramref name="Checked"/>/<paramref name="Total"/> is how far a running one is.
/// </summary>
public sealed record PostRunStates(
    string Environment,
    bool DryRun,
    string Status,
    int Checked,
    int Total,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> States)
{
    public const string FileName = "states.json";

    /// <summary>The order the states are always shown in.</summary>
    public static readonly string[] Order = ["Done", "Ready", "Skipped", "Excluded", "Blocked", "Failed"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int TotalOf(string state) => States.TryGetValue(state, out var reasons) ? reasons.Values.Sum() : 0;

    /// <summary>"done 440 · ready 166 · skipped 1,556 · excluded 0 · blocked 61 · failed 0"</summary>
    public string Line() => FormatLine(TotalOf);

    public static string FormatLine(Func<string, int> totalOf) =>
        string.Join(" · ", Order.Select(s => $"{s.ToLowerInvariant()} {totalOf(s):N0}"));

    /// <summary>Through a temp file and an atomic move; a failure is swallowed, the run always wins.</summary>
    public void TryWrite(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static PostRunStates? TryRead(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<PostRunStates>(stream, Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>What <c>progress</c> shows: the running snapshot's own state plus the done marks so far, and the
/// states of the last <c>post --all</c> run.</summary>
public sealed record SnapshotProgressReport(
    string Directory,
    bool SnapshotExists,
    SnapshotProgressState? State,
    int DoneMarks,
    int? MigrationTotal,
    DateTimeOffset? MigrationTotalAt,
    DateTimeOffset? LastWrite,
    PostRunStates? LastRun = null);

/// <summary>
/// Reads a snapshot folder while a snapshot may be writing to it: files opened with
/// <see cref="FileShare.ReadWrite"/>, a busy file retried, a torn last line skipped. Never writes.
/// </summary>
public static class SnapshotProgressReader
{
    private const string DoneFile = "done.jsonl";
    private const string DoneSummaryFile = "done-summary.jsonl";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SnapshotProgressReport Read(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
            return new SnapshotProgressReport(directory, false, null, 0, null, null, null);

        var state = ReadState(Path.Combine(directory, SnapshotProgressWriter.FileName));
        var doneMarks = CountKeys(Path.Combine(directory, DoneFile));
        var (total, totalAt) = ReadDoneSummary(Path.Combine(directory, DoneSummaryFile));

        DateTimeOffset? lastWrite = null;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, SnapshotProgressWriter.FileName, StringComparison.OrdinalIgnoreCase))
                continue;
            DateTimeOffset written = File.GetLastWriteTimeUtc(file);
            if (lastWrite == null || written > lastWrite)
                lastWrite = written;
        }

        return new SnapshotProgressReport(directory, true, state, doneMarks, total, totalAt, lastWrite,
            PostRunStates.TryRead(directory));
    }

    private static SnapshotProgressState? ReadState(string path)
    {
        var text = ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try { return JsonSerializer.Deserialize<SnapshotProgressState>(text, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Distinct keys in a snapshot JSON-lines file; a later line for the same key is the same key.</summary>
    internal static int CountKeys(string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ReadLines(path))
        {
            var key = KeyOf(line);
            if (key != null)
                keys.Add(key);
        }
        return keys.Count;
    }

    private static (int? Total, DateTimeOffset? At) ReadDoneSummary(string path)
    {
        (int? Total, DateTimeOffset? At) result = (null, null);
        foreach (var line in ReadLines(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("value", out var value))
                    continue;
                int? total = value.TryGetProperty("total", out var t) && t.TryGetInt32(out var ti) ? ti : null;
                DateTimeOffset? at = value.TryGetProperty("checkedAt", out var c) && c.TryGetDateTimeOffset(out var ct) ? ct : null;
                result = (total, at);
            }
            catch (JsonException) { /* torn line */ }
        }
        return result;
    }

    private static string? KeyOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                ? key.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null; // a run writing right now can leave a torn last line
        }
    }

    private static List<string> ReadLines(string path)
    {
        var text = ReadAllText(path);
        if (string.IsNullOrEmpty(text))
            return [];
        return text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private static string? ReadAllText(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}

/// <summary>The lines <c>progress</c> prints. Pure, so the layout can be tested.</summary>
public static class SnapshotProgressRenderer
{
    public static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<string> Render(SnapshotProgressReport report, string environment, DateTimeOffset now,
        int? refreshSeconds = null)
    {
        var lines = new List<string> { $"Snapshot {environment} - {report.Directory}" };
        if (!report.SnapshotExists)
        {
            lines.Add("No snapshot folder yet — has snapshot been started for this environment?");
            lines.Add(Footer(now, refreshSeconds));
            return lines;
        }

        var state = report.State;
        lines.Add("Phase        " + (state == null
            ? "unknown (no progress.json: the snapshot started before progress reporting, or has not begun)"
            : $"{state.Phase}/{state.PhaseCount}  {state.PhaseName}{StatusSuffix(state)}"));

        lines.Add("Progress     " + (state is { Total: > 0 }
            ? $"{state.Done:N0} / {state.Total:N0}  {state.Step}  ({Percent(state.Done, state.Total)})"
            : "-"));

        lines.Add($"Done so far  {report.DoneMarks:N0} metering points");

        lines.Add("Remaining    " + (report.MigrationTotal is { } total
            ? $"{Math.Max(0, total - report.DoneMarks):N0}  (of {total:N0} to migrate, total from {report.MigrationTotalAt?.ToLocalTime():yyyy-MM-dd HH:mm})"
            : "n/a until the done check has finished once"));

        lines.AddRange(RenderLastRun(report.LastRun));

        if (report.LastWrite is { } last)
        {
            var age = now - last;
            var running = state == null || state.Status == "Running";
            lines.Add($"Last write   {last.ToLocalTime():HH:mm:ss}  ({Age(age)} ago)" +
                      (running && age > StalledAfter ? "  STALLED?" : ""));
        }
        else
        {
            lines.Add("Last write   -");
        }

        lines.Add(Footer(now, refreshSeconds));
        return lines;
    }

    // The last post --all run's states, the same lines its own summary ends with.
    private static IEnumerable<string> RenderLastRun(PostRunStates? run)
    {
        if (run == null)
        {
            yield return "Last check   none yet: run post --all --dry-run";
            yield break;
        }
        var kind = run.DryRun ? "post --all --dry-run" : "post --all";
        yield return $"Last check   {kind}, " + run.Status switch
        {
            "Running" => $"running: {run.Checked:N0} / {run.Total:N0} checked ({Percent(run.Checked, run.Total)})",
            "Completed" => $"completed {run.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
            _ => $"stopped before completing, {run.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
        };
        yield return $"States       {run.Line()}";
        foreach (var name in PostRunStates.Order.Where(s => run.TotalOf(s) > 0))
        {
            yield return $"  {name}: {run.TotalOf(name):N0}";
            foreach (var (reason, count) in run.States[name].OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal))
                yield return $"    {count,8:N0}  {reason}";
        }
    }

    private static string StatusSuffix(SnapshotProgressState state) => state.Status switch
    {
        "Completed" => "  (completed)",
        "Stopped" => "  (stopped before completing)",
        _ => ""
    };

    private static string Percent(int done, int total) => total <= 0 ? "-" : $"{done * 100.0 / total:0.0}%";

    private static string Age(TimeSpan age) => age.TotalSeconds < 0 ? "0 s"
        : age.TotalMinutes < 1 ? $"{(int)age.TotalSeconds} s"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min"
        : $"{(int)age.TotalHours} h {age.Minutes} min";

    private static string Footer(DateTimeOffset now, int? refreshSeconds) =>
        $"Updated      {now.ToLocalTime():HH:mm:ss}" +
        (refreshSeconds is { } s ? $" · refresh {s} s · Ctrl+C to stop" : "");
}

/// <summary>
/// <c>progress</c>: shows a (running) snapshot's progress once, or with <c>watch</c> repaints the same lines
/// every interval until cancelled. Read-only.
/// </summary>
public static class SnapshotProgressCommand
{
    public static async Task<int> RunAsync(string directory, string environment, bool watch, int refreshSeconds,
        CancellationToken cancellationToken)
    {
        if (!watch)
        {
            foreach (var line in SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(directory), environment, DateTimeOffset.UtcNow))
                Console.WriteLine(line);
            return 0;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, refreshSeconds));
        var repaint = !Console.IsOutputRedirected;
        var top = repaint ? Console.CursorTop : 0;
        var previousCount = 0;
        if (repaint)
            TrySetCursorVisible(false);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var lines = SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(directory), environment,
                    DateTimeOffset.UtcNow, (int)interval.TotalSeconds);
                if (repaint)
                {
                    var width = Math.Max(20, SafeWindowWidth() - 1);
                    Console.SetCursorPosition(0, top);
                    for (var i = 0; i < Math.Max(lines.Count, previousCount); i++)
                    {
                        var text = i < lines.Count ? lines[i] : "";
                        Console.WriteLine(text.Length > width ? text[..width] : text.PadRight(width));
                    }
                    // The console scrolled to fit the report: the block now starts higher up.
                    top = Math.Max(0, Console.CursorTop - Math.Max(lines.Count, previousCount));
                    previousCount = lines.Count;
                }
                else
                {
                    foreach (var line in lines)
                        Console.WriteLine(line);
                    Console.WriteLine();
                }

                try { await Task.Delay(interval, cancellationToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            if (repaint)
                TrySetCursorVisible(true);
        }
        return 0;
    }

    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth; }
        catch (IOException) { return 120; }
    }

    private static void TrySetCursorVisible(bool visible)
    {
        try { Console.CursorVisible = visible; }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
    }
}
