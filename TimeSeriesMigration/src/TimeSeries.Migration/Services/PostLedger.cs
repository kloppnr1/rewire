using System.Text.Json;

namespace TimeSeries.Cli.Services;

/// <summary>
/// Every write to Settl, written down around the call, so nothing is sent twice: Settl keeps every
/// version of a value and has no delete (production 2026-10-22: MP 571313101700000478 got its values and
/// its externally invoiced period twice). One JSON-lines file per environment, next to the done marks;
/// each write gets a "starting" line just before the call and a "done" or "failed" line just after it.
/// Removing a write's lines by hand lets it be sent again. Dry runs read it and never write to it.
/// <para>It also holds the done marks: a "meteringPoint" line says the whole metering point is finished, either
/// posted by a live run or found already in Settl by the snapshot's done check. Batch runs leave those out.
/// The done marks used to live in done.jsonl; the first load that finds that file copies its marks in and
/// renames it to done.jsonl.moved.</para>
/// </summary>
public sealed class PostLedger
{
    public const string FileName = "ledger.jsonl";
    public const string OldDoneFileName = "done.jsonl";
    public const string Points = "points";
    public const string Period = "period";
    public const string MeteringPoint = "meteringPoint";
    public const string Starting = "starting";
    public const string Done = "done";
    public const string Failed = "failed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private List<Line>? _lines;
    private Dictionary<string, Line>? _doneMarks;

    public PostLedger(string directory)
    {
        _directory = directory;
        FilePath = Path.Combine(directory, FileName);
    }

    public string FilePath { get; }

    /// <summary>One write: what it went to, the period it covers (Danish dates, end exclusive), and how it went.
    /// A done mark has no time series; it carries the metering point's Settl ID, account, revision and the
    /// points posted or found in Settl, and its period is the window.</summary>
    public sealed record Line(string MeteringPoint, Guid? TimeSeries, string Kind, DateOnly From, DateOnly To,
        string Status, DateTimeOffset At, Guid? SettlId = null, string? Account = null, DateTimeOffset? Revision = null,
        int? Points = null);

    public enum Standing { Free, Done, Blocked }

    /// <summary>
    /// Whether this write may be sent. Done: the ledger already has it done, for this series and period.
    /// Blocked: it was started and never finished, or another write to the series that was done or never
    /// finished covers part of the period — Settl may hold it, so a person has to look. Free otherwise,
    /// also after a write Settl turned down.
    /// </summary>
    public (Standing Standing, string? Why) Check(string meteringPoint, Guid timeSeries, string kind, DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            var latest = Lines()
                .Where(l => l.MeteringPoint == meteringPoint && l.TimeSeries == timeSeries && l.Kind == kind)
                .GroupBy(l => (l.From, l.To))
                .Select(g => g.Last())
                .ToList();
            if (latest.FirstOrDefault(l => l.From == from && l.To == to) is { } same)
            {
                if (same.Status == Done)
                    return (Standing.Done, null);
                if (same.Status == Starting)
                    return (Standing.Blocked, $"{kind} {from:yyyy-MM-dd} → {to:yyyy-MM-dd} was started on {same.At:yyyy-MM-dd HH:mm} UTC and never finished");
            }
            if (latest.FirstOrDefault(l => (l.From, l.To) != (from, to) && l.Status != Failed && l.From < to && from < l.To) is { } overlap)
                return (Standing.Blocked, $"{kind} {overlap.From:yyyy-MM-dd} → {overlap.To:yyyy-MM-dd} ({overlap.Status} on {overlap.At:yyyy-MM-dd HH:mm} UTC) covers part of {from:yyyy-MM-dd} → {to:yyyy-MM-dd}");
            return (Standing.Free, null);
        }
    }

    public void Write(string meteringPoint, Guid timeSeries, string kind, DateOnly from, DateOnly to, string status) =>
        Append(new Line(meteringPoint, timeSeries, kind, from, to, status, DateTimeOffset.UtcNow));

    /// <summary>Marks the whole metering point done for this window.</summary>
    public void MarkDone(string meteringPoint, Guid settlId, string account, DateTimeOffset revision, DateOnly from, DateOnly to,
        int points) =>
        Append(new Line(meteringPoint, null, MeteringPoint, from, to, Done, DateTimeOffset.UtcNow, settlId, account, revision, points));

    public bool IsDone(string meteringPoint)
    {
        lock (_gate)
            return DoneByMeteringPoint().ContainsKey(meteringPoint);
    }

    /// <summary>Every done metering point, with its latest done mark.</summary>
    public List<Line> DoneMarks()
    {
        lock (_gate)
            return DoneByMeteringPoint().Values.ToList();
    }

    private Dictionary<string, Line> DoneByMeteringPoint()
    {
        var lines = Lines();
        if (_doneMarks == null)
        {
            _doneMarks = new Dictionary<string, Line>(StringComparer.Ordinal);
            foreach (var line in lines.Where(l => l.Kind == MeteringPoint && l.Status == Done))
                _doneMarks[line.MeteringPoint] = line;
        }
        return _doneMarks;
    }

    private void Append(Line line)
    {
        lock (_gate)
        {
            var lines = Lines();
            Directory.CreateDirectory(_directory);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(line, Json) + "\n");
            lines.Add(line);
            _doneMarks = null;
        }
    }

    private List<Line> Lines()
    {
        if (_lines == null)
        {
            _lines = [];
            Load();
        }
        MoveOldDoneMarks();
        return _lines;
    }

    // The done marks done.jsonl held before they moved here ({"key":<metering point>,"value":{…}}, a later
    // line for the same key wins). Marks already in the ledger are not copied again. The file is renamed, not
    // deleted: renaming it back and removing the copied lines undoes the move.
    private void MoveOldDoneMarks()
    {
        var oldPath = Path.Combine(_directory, OldDoneFileName);
        if (!File.Exists(oldPath))
            return;
        var marks = new Dictionary<string, OldDoneMark>(StringComparer.Ordinal);
        foreach (var text in File.ReadLines(oldPath))
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<OldDoneEntry>(text, Json) is { Key: { } key, Value: { } value })
                    marks[key] = value;
            }
            catch (JsonException) { }
        }
        var alreadyDone = _lines!.Where(l => l.Kind == MeteringPoint && l.Status == Done)
            .Select(l => l.MeteringPoint).ToHashSet(StringComparer.Ordinal);
        var copied = marks
            .Where(m => !alreadyDone.Contains(m.Key))
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => new Line(m.Key, null, MeteringPoint, m.Value.WindowStart, m.Value.WindowEnd, Done, m.Value.CheckedAt,
                m.Value.SettlId, m.Value.Account, m.Value.Revision, m.Value.ExistingPoints))
            .ToList();
        if (copied.Count > 0)
        {
            File.AppendAllText(FilePath, string.Concat(copied.Select(l => JsonSerializer.Serialize(l, Json) + "\n")));
            _lines!.AddRange(copied);
            _doneMarks = null;
        }
        File.Move(oldPath, oldPath + ".moved", overwrite: true);
    }

    private sealed record OldDoneEntry(string? Key, OldDoneMark? Value);

    private sealed record OldDoneMark(Guid SettlId, string Account, DateTimeOffset Revision, DateOnly WindowStart, DateOnly WindowEnd,
        int ExistingPoints, DateTimeOffset CheckedAt);

    private void Load()
    {
        if (File.Exists(FilePath))
        {
            foreach (var text in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                // A run killed mid-write can leave a torn last line; its write then has no end line either way.
                try
                {
                    if (JsonSerializer.Deserialize<Line>(text, Json) is { } line)
                        _lines!.Add(line);
                }
                catch (JsonException) { }
            }
        }
    }
}
