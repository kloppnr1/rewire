using System.Globalization;
using System.Text.Json;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

/// <summary>Groups the actual delivery-history response without applying duplicate or delivery-state filters.</summary>
public sealed class ReportDeliveryPatternsRunner(ISettlMeteringPointClient client)
{
    public TextWriter ProgressOutput { get; init; } = Console.Error;
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<int> RunAsync(string environment, bool includeChildren, TextWriter patterns,
        TextWriter examples, TextWriter sources, CancellationToken ct = default)
    {
        using var progress = new Consumer2ProgressDisplay(ProgressOutput, ProgressInterval);
        progress.Message($"Delivery patterns: {environment}; all delivery states; up to 8 concurrent reads.");
        var points = new List<MeteringPointInfoDto>();
        var pages = 0;
        string Loading() => $"Loading MPs: pages {pages:N0}; loaded {points.Count:N0}";
        progress.Update(Loading(), true);
        for (var skip = 0; ; skip += 500)
        {
            var offset = skip;
            var (items, _, error) = await progress.ReadAsync(() => client.FindMeteringPointsAsync(500, offset, false, ct),
                "MP list", Loading, ct);
            if (error is not null) throw new InvalidOperationException($"MP list failed at {skip}: {error}");
            points.AddRange(items);
            pages++;
            progress.Update(Loading(), true);
            if (items.Count < 500) break;
        }
        var selected = points.Where(p => includeChildren || p.ParentMeteringPoint?.Id is null || p.ParentMeteringPoint.Id == Guid.Empty)
            .DistinctBy(p => p.Id).OrderBy(p => p.MeteringPointIdentification, StringComparer.Ordinal).ThenBy(p => p.Id).ToList();
        progress.Message($"Selected {selected.Count:N0} MPs; no duplicate-name or InDelivery filter.");
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var completed = 0;
        var errors = 0;
        string Status() => $"Histories: {Volatile.Read(ref completed):N0} / {selected.Count:N0}; patterns: {groups.Count:N0}; read/parse errors: {errors:N0}";
        progress.Update(Status(), true);
        for (var offset = 0; offset < selected.Count; offset += 8)
        {
            ct.ThrowIfCancellationRequested();
            var batch = selected.Skip(offset).Take(8).ToArray();
            var responses = await progress.ReadAsync(() => Task.WhenAll(batch.Select(async point =>
            {
                var (json, error) = await client.GetDeliveryStatusesRawAsync(point.Id, ct);
                Interlocked.Increment(ref completed);
                return (Point: point, Json: json, Error: error, CapturedAt: DateTimeOffset.UtcNow);
            })), "delivery histories (up to 8 concurrent)", Status, ct);
            foreach (var response in responses)
            {
                var history = Interpret(response.Json, response.Error);
                if (history.Failed) errors++;
                if (!groups.TryGetValue(history.Key, out var group))
                    groups.Add(history.Key, group = new Group(history.Pattern));
                group.Count++;
                if (group.Examples.Count < 3)
                    group.Examples.Add(new(response.Point, response.Json, history, response.CapturedAt));
            }
            progress.Update(Status());
        }
        progress.Update(Status(), true);
        progress.Message($"History reads complete: {completed:N0} / {selected.Count:N0}. Exporting {groups.Count:N0} patterns and examples...");
        await Row(patterns, ["PatternId", "DeliveryPattern", "MPCount", "ExampleMPs"]);
        await Row(examples, ["PatternId", "MeteringPointIdentification", "SettlId", "Environment", "MPInDelivery",
            "CurrentOrNextStartUtc", "CurrentOrNextEndUtc", "CompletedStartUtc", "CompletedEndUtc", "PeriodIndex",
            "PeriodStartAsReturned", "PeriodEndAsReturned", "PeriodInDelivery", "StartReason", "StopReason", "EvidenceStatus", "SourceLine"]);
        var sourceLine = 0;
        var number = 0;
        foreach (var group in groups.Values.OrderBy(g => g.Pattern, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var patternId = $"P{++number:D4}";
            await Row(patterns, [patternId, group.Pattern, group.Count.ToString(CultureInfo.InvariantCulture),
                string.Join(" | ", group.Examples.Select(e => string.IsNullOrWhiteSpace(e.Point.MeteringPointIdentification)
                    ? $"(missing identification; Settl ID {e.Point.Id})" : e.Point.MeteringPointIdentification))]);
            foreach (var example in group.Examples)
            {
                sourceLine++;
                await sources.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    PatternId = patternId, example.Point.Id, example.Point.MeteringPointIdentification,
                    Environment = environment, CapturedAt = example.CapturedAt,
                    Endpoint = $"/api/v3/meteringPoints/{example.Point.Id}/deliveryStatuses",
                    ResponseJson = example.Json, EvidenceStatus = example.History.Status
                }));
                var entries = example.History.Entries.Count == 0 ? new[] { default(JsonElement) } : example.History.Entries.ToArray();
                for (var index = 0; index < entries.Length; index++)
                {
                    var entry = entries[index];
                    var period = Field(entry, "period");
                    await Row(examples, [patternId, string.IsNullOrWhiteSpace(example.Point.MeteringPointIdentification)
                            ? "(missing)" : example.Point.MeteringPointIdentification, example.Point.Id.ToString(), environment,
                        example.Point.InDelivery?.ToString().ToLowerInvariant() ?? "(missing)",
                        Date(example.Point.CurrentOrNextDeliveryPeriod?.Start), Date(example.Point.CurrentOrNextDeliveryPeriod?.End),
                        Date(example.Point.MostRecentCompletedDeliveryPeriod?.Start), Date(example.Point.MostRecentCompletedDeliveryPeriod?.End),
                        example.History.Entries.Count == 0 ? "" : (index + 1).ToString(CultureInfo.InvariantCulture),
                        Value(Field(period, "start")), Value(Field(period, "end")), Value(Field(entry, "inDelivery"), true),
                        Value(Field(entry, "startReason")), Value(Field(entry, "stopReason")), example.History.Status,
                        sourceLine.ToString(CultureInfo.InvariantCulture)]);
                }
            }
        }
        progress.Update(Status(), true);
        progress.Message($"Exported {groups.Count:N0} patterns covering {selected.Count:N0} MPs, with up to 3 examples per pattern. No Settl writes.");
        return errors == 0 ? 0 : 1;
    }

    internal static History Interpret(string? json, string? error = null)
    {
        if (error is not null || json is null) return new("read-failed", "Read failed", [], error ?? "Empty response", true);
        try
        {
            using var document = JsonDocument.Parse(json);
            var statuses = Field(document.RootElement, "deliveryStatuses");
            if (statuses.ValueKind != JsonValueKind.Array)
                return new("history-" + statuses.ValueKind, "History " + statuses.ValueKind, [], "History is not an array", false);
            var entries = statuses.EnumerateArray().Select((entry, index) => (Entry: entry.Clone(), Index: index))
                .OrderBy(item => StartTicks(item.Entry)).ThenBy(item => item.Index).Select(item => item.Entry).ToList();
            if (entries.Count == 0) return new("empty-history", "Empty history", [], "Empty history", false);
            var tokens = entries.Select(entry => new[] { Key(Field(entry, "inDelivery")), Key(Field(entry, "startReason")), Key(Field(entry, "stopReason")) }).ToArray();
            var pattern = string.Join(" -> ", entries.Select(entry => $"{Value(Field(entry, "inDelivery"), true)} ({Value(Field(entry, "startReason"))} -> {Value(Field(entry, "stopReason"))})"));
            var ambiguous = entries.Any(entry => StartTicks(entry) == long.MaxValue);
            return new(JsonSerializer.Serialize(tokens),
                pattern, entries, ambiguous ? "Some period starts are missing/invalid; those entries follow dated entries in source order" : "As returned; ordered by period start", false);
        }
        catch (JsonException ex) { return new("invalid-json", "Invalid response", [], ex.Message, true); }
    }

    private static JsonElement Field(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    private static long StartTicks(JsonElement entry) => DateTimeOffset.TryParse(Value(Field(Field(entry, "period"), "start")),
        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date.UtcTicks : long.MaxValue;
    private static string Key(JsonElement value) => value.ValueKind + ":" + (value.ValueKind == JsonValueKind.Undefined ? "" : value.GetRawText());
    private static string Value(JsonElement value, bool preserveType = false) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "(missing)", JsonValueKind.Null => "null",
        JsonValueKind.String when !preserveType => value.GetString() ?? "", _ => value.GetRawText()
    };
    private static string Date(DateTimeOffset? value) => value is null || value == default(DateTimeOffset) ? "(missing)" : value.Value.ToUniversalTime().ToString("O");
    private static Task Row(TextWriter writer, string?[] values) => writer.WriteLineAsync(string.Join(';', values.Select(v => "\"" + (v ?? "(missing)").Replace("\"", "\"\"") + "\"")));
    internal sealed record History(string Key, string Pattern, List<JsonElement> Entries, string Status, bool Failed);
    private sealed record Example(MeteringPointInfoDto Point, string? Json, History History, DateTimeOffset CapturedAt);
    private sealed class Group(string pattern)
    {
        public string Pattern { get; } = pattern;
        public int Count { get; set; }
        public List<Example> Examples { get; } = [];
    }
}
