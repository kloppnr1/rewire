namespace DataMigration.Domain.TimeSeries;

/// <summary>
/// Quality flag for a measured <see cref="TimeSeriesPoint"/>. Only
/// <see cref="MachineApprovedReading"/> has been observed in production data so far;
/// add new members when other source values surface.
/// </summary>
public enum QualityOfValue
{
    /// <summary>Maskinelt indlæst, godkendt — automated reading, approved.</summary>
    MachineApprovedReading = 0,
}
