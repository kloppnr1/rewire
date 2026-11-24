using System.Globalization;
using System.Text;
using System.Text.Json;
using TimeSeries.Cli.Services;

namespace TimeSeries.Cli.Runners;

/// <summary>What check-posted-series reads from Settl: a metering point's time series list as Settl returns it.</summary>
public interface IPostedSeriesReader
{
    Task<(string? Json, string? Error)> SeriesJsonAsync(Guid meteringPoint, CancellationToken cancellationToken);
}

/// <summary>
/// check-posted-series: read-only. For every points write the ledger has as done, finds the series it went to in the
/// metering point's time series list and says what it is. post picks a series by resolution only, and Settl's list
/// holds profile series too: production 571313103103408103 (2026-11-23) had its Hour and Quarter values posted to
/// profile_…_PT1H and profile_…_PT15M. Measured: named &lt;MP&gt;_PT1H or &lt;MP&gt;_PT15M, or a field that says
/// Measured. Profile: named profile_…, or a field that says Profile. The raw lists go to a JSON-lines file, so the
/// field that tells them apart can be read off.
/// </summary>
public sealed class PostedSeriesCheckRunner(IPostedSeriesReader settl, Action<string> log)
{
    public const string Measured = "measured";
    public const string Profile = "profile";
    public const string NotListed = "not listed";
    public const string Unknown = "unknown";

    internal sealed record Finding(PostLedger.Line Write, Guid SettlId, string Kind, string Name, string Fields);

    public async Task<int> RunAsync(string environment, string ledgerDirectory, string outputPath,
        IReadOnlyCollection<string>? only, int parallel, CancellationToken cancellationToken = default)
    {
        var ledger = new PostLedger(ledgerDirectory);
        var settlIds = ledger.DoneMarks()
            .Where(m => m.SettlId is { } id && id != Guid.Empty)
            .ToDictionary(m => m.MeteringPoint, m => m.SettlId!.Value, StringComparer.Ordinal);
        var writes = ledger.PostedPoints()
            .Where(w => only == null || only.Contains(w.MeteringPoint))
            .GroupBy(w => (w.MeteringPoint, w.TimeSeries, w.From, w.To)).Select(g => g.Last())
            .ToList();
        var byMeteringPoint = writes.GroupBy(w => w.MeteringPoint, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        log($"Check posted series ({environment}): {writes.Count} posted points write(s) for {byMeteringPoint.Count} metering point(s) in {ledger.FilePath}");

        var findings = new List<Finding>[byMeteringPoint.Count];
        var raws = new string?[byMeteringPoint.Count];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var done = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        await Task.WhenAll(byMeteringPoint.Select(async (group, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                findings[i] = [];
                if (!settlIds.TryGetValue(group.Key, out var settlId))
                {
                    errors.Add($"MP {group.Key}: no Settl id in the ledger's done mark");
                    return;
                }
                var (json, error) = await settl.SeriesJsonAsync(settlId, cancellationToken);
                if (error != null || json == null)
                {
                    errors.Add($"MP {group.Key}: time series list failed: {error}");
                    return;
                }
                raws[i] = json;
                var listed = ReadList(json);
                foreach (var write in group)
                {
                    var series = listed.FirstOrDefault(s => s.Id == write.TimeSeries);
                    findings[i].Add(series == null
                        ? new Finding(write, settlId, NotListed, "", "")
                        : new Finding(write, settlId, Classify(group.Key, series), series.Name, series.Fields));
                }
                if (Interlocked.Increment(ref done) % 250 == 0)
                    log($"  {done}/{byMeteringPoint.Count} metering points checked");
            }
            finally
            {
                gate.Release();
            }
        }));

        var all = findings.Where(f => f != null).SelectMany(f => f).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WriteCsv(outputPath, all);
        var rawPath = Path.ChangeExtension(outputPath, null) + "-raw.jsonl";
        File.WriteAllText(rawPath, string.Concat(byMeteringPoint.Select((g, i) => raws[i] == null ? "" :
            JsonSerializer.Serialize(new { meteringPoint = g.Key, timeSeries = JsonDocument.Parse(raws[i]!).RootElement }) + "\n")));

        foreach (var f in all.Where(f => f.Kind != Measured))
            log($"  MP {f.Write.MeteringPoint}: {f.Write.From:yyyy-MM-dd} → {f.Write.To:yyyy-MM-dd} went to {f.Write.TimeSeries} — {f.Kind}{(f.Name.Length > 0 ? $" ({f.Name})" : "")}");
        foreach (var e in errors.Order(StringComparer.Ordinal))
            log($"  ERROR {e}");
        log("");
        foreach (var g in all.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()))
            log($"  {g.Count(),6}  {g.Key} ({g.Select(f => f.Write.MeteringPoint).Distinct().Count()} metering point(s))");
        log($"Posted series: {all.Count} write(s) → {Path.GetFullPath(outputPath)}");
        log($"Raw time series lists → {Path.GetFullPath(rawPath)}");
        return errors.IsEmpty ? 0 : 1;
    }

    internal sealed record ListedSeries(Guid Id, string Name, string Fields);

    // Every element's id, its name (or description) and all its plain fields, whatever Settl calls them.
    internal static List<ListedSeries> ReadList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList()
            : doc.RootElement.TryGetProperty("items", out var arr) || doc.RootElement.TryGetProperty("Items", out arr) ? arr.EnumerateArray().ToList() : [];
        var result = new List<ListedSeries>();
        foreach (var item in items.Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var fields = item.EnumerateObject()
                .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            if (!fields.TryGetValue("id", out var idText) || !Guid.TryParse(idText, out var id))
                continue;
            var name = fields.TryGetValue("name", out var n) ? n : fields.TryGetValue("description", out var d) ? d : "";
            result.Add(new ListedSeries(id, name,
                string.Join(", ", fields.Where(f => !f.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Select(f => $"{f.Key}={f.Value}"))));
        }
        return result;
    }

    internal static string Classify(string meteringPoint, ListedSeries series)
    {
        if (series.Name.StartsWith("profile_", StringComparison.OrdinalIgnoreCase)
            || series.Fields.Contains("Profile", StringComparison.OrdinalIgnoreCase))
            return Profile;
        if (series.Name == $"{meteringPoint}_PT1H" || series.Name == $"{meteringPoint}_PT15M"
            || series.Fields.Contains("Measured", StringComparison.OrdinalIgnoreCase))
            return Measured;
        return Unknown;
    }

    private static void WriteCsv(string path, IEnumerable<Finding> findings)
    {
        static string F(string s) => s.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var text = new StringBuilder("MeteringPointId;SettlId;TimeSeriesId;From;To;PostedAt;Kind;Name;Fields\r\n");
        foreach (var f in findings.OrderBy(f => f.Write.MeteringPoint, StringComparer.Ordinal).ThenBy(f => f.Write.From))
            text.Append(string.Join(";", f.Write.MeteringPoint, f.SettlId, f.Write.TimeSeries, f.Write.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                f.Write.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f.Write.At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                f.Kind, F(f.Name), F(f.Fields))).Append("\r\n");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
    }
}

/// <summary>check-posted-series' reader over the real client.</summary>
public sealed class SettlPostedSeriesReader(DataMigration.SettlApi.Clients.SettlTimeSeriesMeteringPointClient client) : IPostedSeriesReader
{
    public Task<(string? Json, string? Error)> SeriesJsonAsync(Guid meteringPoint, CancellationToken cancellationToken) =>
        client.GetTimeSeriesRawAsync(meteringPoint, cancellationToken);
}
