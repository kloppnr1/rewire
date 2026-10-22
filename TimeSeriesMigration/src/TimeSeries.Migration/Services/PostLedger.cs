using System.Text.Json;

namespace TimeSeries.Cli.Services;

/// <summary>
/// Every write to Settl, written down around the call, so nothing is sent twice: Settl keeps every
/// version of a value and has no delete (production 2026-10-22: MP 571313101700000478 got its values and
/// its externally invoiced period twice). One JSON-lines file per environment, next to the done marks;
/// each write gets a "starting" line just before the call and a "done" or "failed" line just after it.
/// Removing a write's lines by hand lets it be sent again. Dry runs read it and never write to it.
/// </summary>
public sealed class PostLedger
{
    public const string FileName = "ledger.jsonl";
    public const string Points = "points";
    public const string Period = "period";
    public const string Starting = "starting";
    public const string Done = "done";
    public const string Failed = "failed";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly string _directory;
    private List<Line>? _lines;

    public PostLedger(string directory)
    {
        _directory = directory;
        FilePath = Path.Combine(directory, FileName);
    }

    public string FilePath { get; }

    /// <summary>One write: what it went to, the period it covers (Danish dates, end exclusive), and how it went.</summary>
    public sealed record Line(string MeteringPoint, Guid TimeSeries, string Kind, DateOnly From, DateOnly To,
        string Status, DateTimeOffset At);

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

    public void Write(string meteringPoint, Guid timeSeries, string kind, DateOnly from, DateOnly to, string status)
    {
        var line = new Line(meteringPoint, timeSeries, kind, from, to, status, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            File.AppendAllText(FilePath, JsonSerializer.Serialize(line, Json) + "\n");
            Lines().Add(line);
        }
    }

    private List<Line> Lines()
    {
        if (_lines != null)
            return _lines;
        _lines = [];
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
                        _lines.Add(line);
                }
                catch (JsonException) { }
            }
        }
        return _lines;
    }
}
