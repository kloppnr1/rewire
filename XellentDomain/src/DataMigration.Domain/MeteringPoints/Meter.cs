namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// A physical meter attached to a metering point. <see cref="ReadingInterval"/> is the time
/// between automatic readings; null when not applicable (e.g. installation-based meters).
/// <see cref="Decimals"/>, <see cref="DigitsOfPrecision"/> og <see cref="ReadingOccurrence"/>
/// er commodity-bestemte måleegenskaber sat af mapperen.
/// </summary>
public sealed record Meter(
    string Number,
    decimal MultiplicationFactor,
    TimeSpan? ReadingInterval)
{
    public int Decimals { get; init; }
    public int DigitsOfPrecision { get; init; }
    public MeterReadingOccurrence ReadingOccurrence { get; init; } = MeterReadingOccurrence.Hourly;

    /// <summary>
    /// DataHub sub type of the metering point the meter sits on — "Physical", "Virtual" or
    /// "Calculated", already translated from the Xellent code. Null when the source has no
    /// sub type, or on commodities where it is not extracted (water/heat).
    /// </summary>
    public string? MeteringPointSubType { get; init; }
}
