namespace DataMigration.XellentData.Enums;

/// <summary>
/// Codes used by EMS_TIMESERIESVALUES.QUALITYOFVALUE.
/// Only 0 (machine-approved reading) has been observed in production data so far.
/// Cast from raw int: <c>(QualityOfValue)entity.Qualityofvalue</c>.
/// </summary>
public enum QualityOfValue
{
    /// <summary>"Maskinelt indlæst, godkendt" — automated reading, approved.</summary>
    MachineApprovedReading = 0,
}
