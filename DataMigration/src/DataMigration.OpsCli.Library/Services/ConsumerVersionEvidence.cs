using System.Globalization;
using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal sealed record ConsumerVersion(MeteringPointV4Dto? Point, DateTime? RequestedDate, string Status)
{
    public DateTime? Start => Point?.Consumer?.Start ?? RequestedDate;
    public DateTime? End => Point?.Consumer?.End;
    /// <summary>Whether consumer 2 has a national id on this version, from Settl's separate national-id read.
    /// Null when it was not read (only read when <see cref="FixConsumer2DuplicateRunner.NeedsNationalIdCheck"/>).
    /// The value itself is never kept.</summary>
    public bool? SecondHasNationalId { get; init; }
    /// <summary>Whether Settl holds consumer 2's national id as an empty string rather than null. Null when not read.</summary>
    public bool? SecondNationalIdEmpty { get; init; }
    /// <summary>Consumer 2 has a name but an empty national id: DataHub rejects that update (BRS-015). Report only.</summary>
    public bool NameWithEmptyNationalId => SecondNationalIdEmpty == true && Point is { } point
        && Status == "As returned" && FixConsumer2DuplicateRunner.HasSecondName(point);
    public string? Reason => Point is null ? null : FixConsumer2DuplicateRunner.Consumer2Reason(Point, SecondHasNationalId);
    public bool Duplicate => Reason is not null;
}

internal sealed record ConsumerVersionEvidence(List<ConsumerVersion> Versions, List<string> Errors)
{
    internal static async Task<ConsumerVersionEvidence> ReadAsync(ISettlMeteringPointClient client, Guid id,
        Action versionRead, CancellationToken ct)
    {
        var versions = new List<ConsumerVersion>();
        var errors = new List<string>();
        var (current, currentError) = await client.GetByIdV4Async(id, null, ct);
        versionRead();
        if (current is null) errors.Add("Current consumer read failed: " + currentError);
        else
        {
            if (currentError is not null) errors.Add("Current consumer read warning: " + currentError);
            Add(current, null);
        }
        var (history, historyError) = await client.GetMarketHistoryAsync(id, ct);
        if (history?.ConsumerDates is null || historyError is not null)
            errors.Add("Consumer history missing or failed: " + historyError);
        else
        {
            var dates = history.ConsumerDates.Select(d => d == default ? d : ConsumerDeliveryEvidence.Utc(d).UtcDateTime).Distinct().Order().ToArray();
            foreach (var date in dates)
            {
                ct.ThrowIfCancellationRequested();
                if (date == default) { errors.Add("Consumer history contains an invalid start."); continue; }
                if (versions.Any(v => v.Point?.Consumer?.Start is { } existing && ConsumerDeliveryEvidence.Utc(existing).UtcDateTime == date)) continue;
                var (point, error) = await client.GetByIdV4Async(id, date, ct);
                versionRead();
                if (point is null || error is not null)
                {
                    var message = "Dated consumer read failed: " + error;
                    errors.Add(message);
                    versions.Add(new(null, date, message));
                }
                else if (point.Consumer?.Start is not { } returnedStart || ConsumerDeliveryEvidence.Utc(returnedStart).UtcDateTime != date)
                {
                    const string message = "Dated read did not return the requested Consumer start.";
                    errors.Add(message);
                    versions.Add(new(null, date, message));
                }
                else Add(point, date);
            }
            if (current?.Consumer?.Start is { } start && !dates.Contains(ConsumerDeliveryEvidence.Utc(start).UtcDateTime))
                errors.Add("Current Consumer start is absent from ConsumerDates.");
        }
        // Settl's metering-point read never carries national ids; read them separately, but only for the
        // versions where the answer decides selection or the empty-id flag.
        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            if (version.Point is not { } candidate || version.Status != "As returned"
                || !FixConsumer2DuplicateRunner.NeedsNationalIdCheck(candidate) && !FixConsumer2DuplicateRunner.HasSecondName(candidate)) continue;
            ct.ThrowIfCancellationRequested();
            var date = ConsumerDeliveryEvidence.Utc(candidate.Consumer!.Start!.Value).UtcDateTime;
            var (ids, idError) = await client.GetConsumerNationalIdsAsync(id, date, ct);
            versionRead();
            if (ids is null || idError is not null)
            {
                const string message = "Consumer national id read failed.";
                errors.Add(message + (idError is null ? "" : " " + idError));
                versions[i] = version with { Status = message };
            }
            else versions[i] = version with
            {
                SecondHasNationalId = !string.IsNullOrWhiteSpace(ids.SecondConsumerPartyNationalIdentificationNumber),
                SecondNationalIdEmpty = ids.SecondConsumerPartyNationalIdentificationNumber is { } second && string.IsNullOrWhiteSpace(second)
            };
        }
        var ordered = versions.OrderBy(v => v.Start ?? DateTime.MaxValue).ToList();
        for (var i = 0; i + 1 < ordered.Count; i++)
            if (ordered[i].Point?.Consumer is not null && ordered[i].Status == "As returned"
                && (ordered[i].End is null || ordered[i].End > ordered[i + 1].Start))
            {
                const string message = "Consumer versions have overlapping or inconsistent boundaries.";
                errors.Add(message);
                ordered[i] = ordered[i] with { Status = message };
            }
        return new(ordered, errors);

        void Add(MeteringPointV4Dto point, DateTime? requested)
        {
            var consumer = point.Consumer;
            var status = point.Id != id ? "Consumer read returned a different Settl ID."
                : consumer?.Start is null || consumer.Start == default(DateTime) ? "Consumer start missing or invalid."
                : consumer.End <= consumer.Start ? "Consumer end must be later than its start." : "As returned";
            if (status != "As returned") errors.Add(status);
            versions.Add(new(point.Id == id ? point : null, requested, status));
        }
    }
}

internal sealed record DeliveryOverlap(string Start, string End, string State, string StartReason,
    string StopReason, string OverlapStart, string OverlapEnd, string Status);

internal sealed record ConsumerDeliveryEvidence(List<DeliveryPeriod> Periods, List<string> Errors)
{
    internal static ConsumerDeliveryEvidence Parse(string? json, string? error)
    {
        var periods = new List<DeliveryPeriod>();
        var errors = new List<string>();
        if (json is null || error is not null) return new(periods, ["Delivery history missing or failed: " + error]);
        try
        {
            using var document = JsonDocument.Parse(json);
            var entries = Field(document.RootElement, "deliveryStatuses");
            if (entries.ValueKind != JsonValueKind.Array) return new(periods, ["Delivery history is not an array."]);
            foreach (var entry in entries.EnumerateArray())
            {
                var period = Field(entry, "period");
                var start = Field(period, "start");
                var end = Field(period, "end");
                var validStart = TryDate(start, out var startDate);
                DateTimeOffset? endDate = null;
                var validEnd = end.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
                if (!validEnd && TryDate(end, out var parsedEnd)) { validEnd = true; endDate = parsedEnd; }
                var status = !validStart || !validEnd || endDate <= startDate
                    ? "Delivery period dates missing or invalid." : "As returned";
                var state = Field(entry, "inDelivery");
                if (state.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    status = "Delivery state missing or unknown.";
                if (status != "As returned") errors.Add(status);
                periods.Add(new(validStart ? startDate : null, endDate, Value(start), Value(end), Value(state),
                    Value(Field(entry, "startReason")), Value(Field(entry, "stopReason")), status));
            }
        }
        catch (JsonException ex) { errors.Add("Invalid delivery history JSON: " + ex.Message); }
        var ordered = periods.OrderBy(p => p.Start ?? DateTimeOffset.MaxValue).ToList();
        for (var i = 0; i + 1 < ordered.Count; i++)
            if (ordered[i].Start is not null && ordered[i + 1].Start is not null
                && (ordered[i].End ?? DateTimeOffset.MaxValue) > ordered[i + 1].Start)
                errors.Add("Delivery periods overlap; interpretation is incomplete.");
        return new(ordered, errors);
    }

    internal IEnumerable<DeliveryOverlap> For(ConsumerVersion version)
    {
        if (version.Status != "As returned" || version.Start is null)
        {
            yield return Empty("Consumer boundaries unavailable or inconsistent; overlap not determined.");
            yield break;
        }
        var start = Utc(version.Start.Value);
        var end = version.End is { } value ? Utc(value) : DateTimeOffset.MaxValue;
        var found = false;
        foreach (var period in Periods)
        {
            if (period.Start is null || period.Status == "Delivery period dates missing or invalid.")
            {
                yield return new(period.RawStart, period.RawEnd, period.State, period.StartReason, period.StopReason,
                    "", "", period.Status);
                continue;
            }
            var overlapStart = period.Start > start ? period.Start.Value : start;
            var deliveryEnd = period.End ?? DateTimeOffset.MaxValue;
            var overlapEnd = deliveryEnd < end ? deliveryEnd : end;
            if (overlapStart >= overlapEnd) continue;
            found = true;
            yield return new(period.RawStart, period.RawEnd, period.State, period.StartReason, period.StopReason,
                overlapStart.ToString("O"), overlapEnd == DateTimeOffset.MaxValue ? "(open)" : overlapEnd.ToString("O"), period.Status == "As returned" ? "Overlapping period" : period.Status);
        }
        if (Errors.Count > 0) yield return Empty(string.Join(" | ", Errors.Distinct()));
        else if (!found) yield return Empty("No overlapping delivery period");
    }

    private static DeliveryOverlap Empty(string status) => new("", "", "", "", "", "", "", status);
    internal static DateTimeOffset Utc(DateTime value) => new(value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static bool TryDate(JsonElement value, out DateTimeOffset date) => DateTimeOffset.TryParse(
        Value(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) && date != default;
    private static JsonElement Field(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    private static string Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "(missing)", JsonValueKind.Null => "null",
        JsonValueKind.String => value.GetString() ?? "", _ => value.GetRawText()
    };
}

internal sealed record DeliveryPeriod(DateTimeOffset? Start, DateTimeOffset? End, string RawStart, string RawEnd,
    string State, string StartReason, string StopReason, string Status);
