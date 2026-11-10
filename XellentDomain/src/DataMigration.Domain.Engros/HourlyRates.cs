namespace DataMigration.Domain.Engros;

/// <summary>
/// 24 rates per day, one for each clock hour. Index 0 = hour 00–01,
/// index 23 = hour 23–24. For components with a flat rate the same value lives in all
/// 24 slots — use <see cref="IsFlat"/> to detect.
/// </summary>
public sealed record HourlyRates(decimal[] PerHour)
{
    /// <summary>The rate for the given clock-hour (0–23).</summary>
    public decimal AtHour(int hour) => PerHour[hour];

    /// <summary>True when all 24 hourly rates are identical.</summary>
    public bool IsFlat() => PerHour.Distinct().Count() == 1;

    /// <summary>Constructs a <see cref="HourlyRates"/> with the same rate for all 24 hours.</summary>
    public static HourlyRates Flat(decimal rate) => new(Enumerable.Repeat(rate, 24).ToArray());
}
