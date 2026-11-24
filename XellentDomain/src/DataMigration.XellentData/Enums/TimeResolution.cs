namespace DataMigration.XellentData.Enums;

/// <summary>
/// Codes used by EMS_TIMESERIES.TIMERESOLUTION (and EXU_TIMEREADINGSPERIOD.SERIESRESOLUTION
/// in part — note that table only uses 0 = Hourly, 1 = Quarterly).
/// Cast from raw int: <c>(TimeResolution)entity.Timeresolution</c>.
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
