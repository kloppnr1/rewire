using System.Text.Json;
using TimeSeries.Cli.Services;

namespace TimeSeries.Cli.Tests.Runners;

public class SnapshotProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snapshot-progress-" + Guid.NewGuid());
    private DateTimeOffset _now = new(2026, 10, 15, 6, 0, 0, TimeSpan.Zero);

    public SnapshotProgressTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SnapshotProgressWriter Writer() =>
        new(_dir, "featuretest", TimeSpan.FromSeconds(5), () => _now, background: false);

    private SnapshotProgressState? ReadState() =>
        SnapshotProgressReader.Read(_dir).State;

    [Fact]
    public void Phase_start_is_written_at_once()
    {
        var writer = Writer();
        writer.StartPhase(2, "Settl reads per metering point");

        var state = ReadState();
        Assert.NotNull(state);
        Assert.Equal(2, state!.Phase);
        Assert.Equal(4, state.PhaseCount);
        Assert.Equal("Running", state.Status);
        Assert.False(File.Exists(writer.FilePath + ".tmp"));
    }

    [Fact]
    public void Step_updates_are_throttled_to_the_interval()
    {
        var writer = Writer();
        writer.StartPhase(2, "Settl reads per metering point");

        _now = _now.AddSeconds(1);
        writer.Report("Settl reads", 100, 1000);
        Assert.Equal(0, ReadState()!.Done);

        _now = _now.AddSeconds(5);
        writer.Report("Settl reads", 600, 1000);
        var state = ReadState()!;
        Assert.Equal(600, state.Done);
        Assert.Equal(1000, state.Total);
        Assert.Equal("Settl reads", state.Step);
    }

    [Fact]
    public void Complete_survives_stop_and_stop_marks_an_unfinished_run()
    {
        var finished = Writer();
        finished.StartPhase(4, "Xellent counts");
        finished.Complete();
        finished.Stop();
        Assert.Equal("Completed", ReadState()!.Status);

        var aborted = Writer();
        aborted.StartPhase(1, "Settl list, Xellent contracts, payers");
        aborted.Stop();
        Assert.Equal("Stopped", ReadState()!.Status);
    }

    [Fact]
    public void A_failing_write_never_throws()
    {
        var missing = Path.Combine(_dir, "does-not-exist");
        var messages = new List<string>();
        var writer = new SnapshotProgressWriter(missing, "featuretest", TimeSpan.Zero, () => _now, background: false, debug: messages.Add);

        writer.StartPhase(1, "x");
        writer.Report("x", 1, 2);
        writer.Complete();
        writer.Stop();

        Assert.NotEmpty(messages);
    }

    [Fact]
    public void Reader_counts_distinct_done_keys_and_skips_a_torn_last_line()
    {
        File.WriteAllText(Path.Combine(_dir, "done.jsonl"),
            Line("571", new { count = 1 }) + Line("572", new { count = 2 }) + Line("571", new { count = 3 }) +
            "{\"key\":\"573\",\"val");

        var report = SnapshotProgressReader.Read(_dir);

        Assert.Equal(2, report.DoneMarks);
    }

    [Fact]
    public void Reader_takes_the_total_from_the_last_done_summary()
    {
        File.WriteAllText(Path.Combine(_dir, "done-summary.jsonl"),
            Line("last", new { total = 500, done = 10, checkedAt = "2026-10-01T10:00:00+00:00" }) +
            Line("last", new { total = 661, done = 440, checkedAt = "2026-10-14T10:00:00+00:00" }));
        File.WriteAllText(Path.Combine(_dir, "done.jsonl"), Line("571", new { count = 1 }));

        var report = SnapshotProgressReader.Read(_dir);
        var lines = SnapshotProgressRenderer.Render(report, "featuretest", _now);

        Assert.Equal(661, report.MigrationTotal);
        Assert.Contains(lines, l => l.StartsWith("Remaining") && l.Contains((660).ToString("N0")));
    }

    [Fact]
    public void Reader_works_while_a_writer_holds_the_file_open()
    {
        var path = Path.Combine(_dir, "done.jsonl");
        File.WriteAllText(path, Line("571", new { count = 1 }));
        using var open = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        Assert.Equal(1, SnapshotProgressReader.Read(_dir).DoneMarks);
    }

    [Fact]
    public void Missing_folder_and_missing_progress_file_render_without_failing()
    {
        var none = SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(Path.Combine(_dir, "nope")), "production", _now);
        Assert.Contains(none, l => l.StartsWith("No snapshot folder"));

        var noState = SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(_dir), "production", _now, 10);
        Assert.Contains(noState, l => l.StartsWith("Phase") && l.Contains("unknown"));
        Assert.Contains(noState, l => l.StartsWith("Remaining") && l.Contains("n/a"));
        Assert.Contains(noState, l => l.Contains("refresh 10 s"));
    }

    [Fact]
    public void Renders_phase_progress_and_stalled_hint()
    {
        var writer = Writer();
        writer.StartPhase(3, "Done check (asking Settl)");
        _now = _now.AddSeconds(10);
        writer.Report("Done check, asking Settl", 250, 1000);

        var report = SnapshotProgressReader.Read(_dir) with { LastWrite = _now };
        var fresh = SnapshotProgressRenderer.Render(report, "featuretest", _now);
        Assert.Contains(fresh, l => l.StartsWith("Phase") && l.Contains("3/4") && l.Contains("Done check"));
        Assert.Contains(fresh, l => l.StartsWith("Progress") && l.Contains((25.0).ToString("0.0") + "%"));
        Assert.DoesNotContain(fresh, l => l.Contains("STALLED"));

        var stale = SnapshotProgressRenderer.Render(report, "featuretest", _now.AddMinutes(6));
        Assert.Contains(stale, l => l.Contains("STALLED?"));
    }

    [Fact]
    public void There_is_no_set_aside_section()
    {
        var writer = Writer();
        writer.StartPhase(2, "Settl reads per metering point");

        var lines = SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(_dir), "featuretest", _now);

        Assert.DoesNotContain(lines, l => l.StartsWith("Set aside"));
        Assert.All(lines, l => Assert.True(l.All(c => c < 128 || char.IsLetterOrDigit(c)), l));
    }

    [Fact]
    public void The_last_post_all_runs_states_are_shown_like_its_own_summary()
    {
        var run = new PostRunStates("featuretest", DryRun: true, "Completed", 10, 10, _now, _now,
            new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                ["Ready"] = new Dictionary<string, int> { ["would be posted"] = 4 },
                ["Blocked"] = new Dictionary<string, int> { ["missing Hour series in Settl"] = 5, ["window end differs from Settl delivery date"] = 1 },
            });
        run.TryWrite(_dir);

        var lines = SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(_dir), "featuretest", _now).ToList();

        var header = lines.FindIndex(l => l.StartsWith("Last check   post --all --dry-run, completed", StringComparison.Ordinal));
        Assert.True(header >= 0, string.Join("\n", lines));
        Assert.Equal("States       done 0 · ready 4 · skipped 0 · excluded 0 · blocked 6 · failed 0", lines[header + 1]);
        Assert.Equal("  Ready: 4", lines[header + 2]);
        Assert.Equal("  Blocked: 6", lines[header + 4]);
        Assert.EndsWith("missing Hour series in Settl", lines[header + 5]);
    }

    [Fact]
    public void Without_a_post_all_run_progress_says_how_to_get_the_states()
    {
        var lines = SnapshotProgressRenderer.Render(SnapshotProgressReader.Read(_dir), "featuretest", _now);

        Assert.Contains("Last check   none yet: run post --all --dry-run", lines);
    }

    private static string Line(string key, object value) =>
        JsonSerializer.Serialize(new { key, value }) + "\n";
}
