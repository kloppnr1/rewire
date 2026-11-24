namespace DataMigration.Domain.TimeSeries;

/// <summary>
/// Granularity of <see cref="TimeSeriesPoint.Time"/> stamps in a
/// <see cref="MeteringPointTimeSeries"/>. <see cref="Hour"/> is the typical
/// production value; the others are reserved for non-hourly profiles.
/// </summary>
public enum TimeResolution
{
    None = 0,
    Quarter = 1,
    Hour = 2,
    Month = 3,
    Day = 4,
    Year = 5,
    Minute = 6,
}
