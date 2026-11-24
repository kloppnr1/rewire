namespace DataMigration.SettlApi.Models;

// === Water metering point models (commodities/tenant-api/v1 — camelCase, flat structure) ===

public class CreateWaterMeteringPointRequest
{
    public Guid TransactionId { get; set; } = Guid.NewGuid();
    public string CountryCode { get; set; } = "DK";
    public WaterMeteringPointIdentifier? Identifier { get; set; }
    public WaterInstallationAddress? InstallationAddress { get; set; }
    public List<WaterHistoricalMeter> HistoricalMeters { get; set; } = [];
}

public class WaterMeteringPointIdentifier
{
    public string? Identification { get; set; }
    public string IdentificationKind { get; set; } = "AssignedByDistributor";
}

public class WaterInstallationAddress
{
    public WaterAddressIdentifier? AddressIdentifier { get; set; }
    public string? BuildingNumber { get; set; }
    public string? City { get; set; }
    public string CountryCode { get; set; } = "DK";
    public string? MunicipalityCode { get; set; }
    public string? PostalCode { get; set; }
    public string? StreetCode { get; set; }
    public string? StreetName { get; set; }
}

public class WaterAddressIdentifier
{
    public string? Identification { get; set; }
    public string IdentificationKind { get; set; } = "Dar";
}

public class WaterHistoricalMeter
{
    public Guid MeterId { get; set; } = Guid.NewGuid();
    public string? MeterIdentifier { get; set; }
    public string MeterMethod { get; set; } = "Automatic";
    public string MeterReadingOccurrence { get; set; } = "Hourly";
    public int DigitsOfPrecision { get; set; } = 1;
    public int Decimals { get; set; } = 1;
    public DateTime Start { get; set; }
}

/// <summary>
/// Utility for normalizing dates to DK midnight in UTC.
/// Winter (CET, UTC+1): 23:00:00Z — Summer (CEST, UTC+2): 22:00:00Z
/// </summary>
public static class DkMidnightNormalizer
{
    private static readonly TimeZoneInfo DkTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    /// <summary>
    /// Takes any DateTime and returns midnight local DK time expressed in UTC.
    /// </summary>
    public static DateTimeOffset NormalizeToMidnightUtc(DateTime date)
    {
        // Get midnight in DK for the given date
        var dkMidnight = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var utcOffset = DkTimeZone.GetUtcOffset(dkMidnight);
        return new DateTimeOffset(dkMidnight, utcOffset).ToUniversalTime();
    }

    /// <summary>
    /// Takes a DateOnly and returns midnight local DK time expressed in UTC.
    /// </summary>
    public static DateTimeOffset NormalizeToMidnightUtc(DateOnly date) =>
        NormalizeToMidnightUtc(date.ToDateTime(TimeOnly.MinValue));

    /// <summary>
    /// Takes a DateTimeOffset and returns midnight local DK time expressed in UTC (uses date portion only).
    /// </summary>
    public static DateTimeOffset NormalizeToMidnightUtc(DateTimeOffset date) =>
        NormalizeToMidnightUtc(date.DateTime);
}
