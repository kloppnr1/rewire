using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataMigration.SettlApi.Models;

/// <summary>
/// Serializes DateOnly as midnight Danish time expressed as UTC.
/// Settl kræver Kind=Utc (Z-suffix) og validerer at time-of-day er 0
/// efter konvertering til dansk tid.
/// E.g. 2023-07-01 (CEST/UTC+2) → "2023-06-30T22:00:00Z"
///      2023-01-01 (CET/UTC+1)  → "2022-12-31T23:00:00Z"
///
/// <para>For Contract↔MP-link End-datoer fortolker Settl værdien som
/// exclusive end og viser Xellent-slutdato som "dagen før" — brug
/// <see cref="NullableDateOnlyPlusOneConverter"/> der lægger +1 dag
/// på inden konvertering.</para>
/// </summary>
public class DateOnlyUtcConverter : JsonConverter<DateOnly>
{
    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    /// <summary>
    /// Settl's "no end date" sentinel (≈ DateTime.MaxValue). The Settl UI renders this
    /// exact value as an empty/open end date — same marker Settl uses on its own
    /// contract lines. <see cref="DateOnly.MaxValue"/> (9999-12-31) maps to this on write;
    /// running it through the Danish-midnight conversion would overflow into year 10000.
    /// </summary>
    internal const string OpenEndedSentinel = "9999-12-31T23:59:58.999Z";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return TryReadDanishDate(reader.GetString(), out var date, out _) ? date : default;
    }

    internal static bool TryReadDanishDate(string? value, out DateOnly date, out bool openEnded)
    {
        date = default;
        openEnded = false;

        // Dates and timestamps without a zone already represent a Danish calendar date.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            && parsed.Kind == DateTimeKind.Unspecified)
        {
            date = DateOnly.FromDateTime(parsed);
            return true;
        }

        // Parsing an offset into DateTime can first convert it to the server's local time.
        // Preserve the original instant with DateTimeOffset before converting to Danish time.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return false;
        openEnded = instant.UtcDateTime is { Year: 9999, Month: 12, Day: 31 };
        date = openEnded
            ? DateOnly.MaxValue
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, DanishTimeZone).DateTime);
        return true;
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(FormatAsDanishMidnightUtc(value));
    }

    internal static string FormatAsDanishMidnightUtc(DateOnly value)
    {
        if (value == DateOnly.MaxValue)
            return OpenEndedSentinel;
        var danishMidnight = new DateTime(value.Year, value.Month, value.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(danishMidnight, DanishTimeZone);
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Skriver DateOnly +1 dag som Danish-midnight-UTC. Bruges på Contract↔MP-link End,
/// hvor Settl tolker datoen som exclusive end (Xellent 31-12-2027 → Settl 01-01-2028).
/// </summary>
public class NullableDateOnlyPlusOneConverter : JsonConverter<DateOnly?>
{
    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var str = reader.GetString();
        if (str == null) return null;
        if (DateOnlyUtcConverter.TryReadDanishDate(str, out var date, out var openEnded))
        {
            // Open-ended sentinel — bypass the exclusive-end -1 day shift (and the
            // timezone conversion that would overflow into year 10000).
            if (openEnded)
                return DateOnly.MaxValue;
            return date.AddDays(-1);
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }
        // Avoid AddDays(1) overflow for sentinel "no end" date (DateOnly.MaxValue).
        var shifted = value.Value >= DateOnly.MaxValue ? value.Value : value.Value.AddDays(1);
        writer.WriteStringValue(DateOnlyUtcConverter.FormatAsDanishMidnightUtc(shifted));
    }
}

public class NullableDateOnlyUtcConverter : JsonConverter<DateOnly?>
{
    private static readonly DateOnlyUtcConverter Inner = new();

    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return Inner.Read(ref reader, typeof(DateOnly), options);
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value == null)
            writer.WriteNullValue();
        else
            Inner.Write(writer, value.Value, options);
    }
}
