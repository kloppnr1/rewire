using System.Globalization;
using System.Text.Json;

namespace DataMigration.OpsCli.Services;

/// <summary>Checks only the oldest two delivery entries, without restricting later states/reasons.</summary>
internal sealed record DeliveryHistoryPrefix(bool? Matches, string? Warning)
{
    internal static DeliveryHistoryPrefix Assess(string? json, string? error = null)
    {
        if (json is null || error is not null)
            return Unknown("Delivery history read failed: " + (error ?? "Empty response"));
        try
        {
            using var document = JsonDocument.Parse(json);
            var statuses = Field(document.RootElement, "deliveryStatuses");
            if (statuses.ValueKind != JsonValueKind.Array) return Unknown("Delivery history is not an array.");
            var entries = new List<(DateTimeOffset Start, JsonElement Entry)>();
            foreach (var entry in statuses.EnumerateArray())
            {
                var start = Field(Field(entry, "period"), "start");
                if (start.ValueKind != JsonValueKind.String
                    || !DateTimeOffset.TryParse(start.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var date) || date == default)
                    return Unknown("Delivery history contains a missing or invalid start; oldest periods cannot be established.");
                entries.Add((date, entry));
            }
            if (entries.Count < 2) return new(false, null);
            entries.Sort((a, b) => a.Start.CompareTo(b.Start));
            if (entries[0].Start == entries[1].Start
                || entries.Count > 2 && entries[1].Start == entries[2].Start)
                return Unknown("Delivery history has tied starts among the oldest periods.");
            var checks = new[]
            {
                State(entries[0].Entry, false), Reason(entries[0].Entry, "startReason", "Na"),
                Reason(entries[0].Entry, "stopReason", "Na"),
                State(entries[1].Entry, true), Reason(entries[1].Entry, "startReason", "ChangeOfSupplier"),
                Reason(entries[1].Entry, "stopReason", "Na")
            };
            if (checks.Any(value => value == false)) return new(false, null);
            return checks.Any(value => value is null)
                ? Unknown("Delivery history prefix has missing or unreadable states/reasons.")
                : new(true, null);
        }
        catch (JsonException ex) { return Unknown("Invalid delivery history JSON: " + ex.Message); }
    }

    private static DeliveryHistoryPrefix Unknown(string warning) => new(null, warning);
    private static bool? State(JsonElement entry, bool expected)
    {
        var value = Field(entry, "inDelivery");
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() == expected : null;
    }
    private static bool? Reason(JsonElement entry, string name, string expected)
    {
        var value = Field(entry, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() == expected : null;
    }
    private static JsonElement Field(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
}
