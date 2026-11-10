namespace DataMigration.Domain.TimeSeries;

/// <summary>
/// A single measured value in a <see cref="MeteringPointTimeSeries"/>.
/// <see cref="Time"/> is the start of the interval (e.g. 14:00 for the 14–15 hour at hourly
/// resolution) in UTC.
/// </summary>
public sealed record TimeSeriesPoint(
    DateTime Time,
    decimal Value,
    QualityOfValue Quality);
