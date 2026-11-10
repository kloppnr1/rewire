namespace XellentInvestigationQueries;

/// <summary>
/// Danish calendar-day boundaries for the UTC timestamps in EMS_TIMESERIESVALUES.
/// Convert each local midnight separately so daylight-saving days retain their
/// actual 23- or 25-hour length.
/// </summary>
public static class DanishTimeWindows
{
    private static readonly TimeZoneInfo DanishZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    public static (DateTime FromUtc, DateTime ToUtc) ForDay(DateOnly day)
    {
        var localStart = day.ToDateTime(TimeOnly.MinValue);
        var localEnd = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return (
            TimeZoneInfo.ConvertTimeToUtc(localStart, DanishZone),
            TimeZoneInfo.ConvertTimeToUtc(localEnd, DanishZone));
    }

    /// <summary>
    /// Preserve the offset when displaying a UTC value. The repeated autumn
    /// 02:00 hour needs +02:00 or +01:00 to identify the actual observation.
    /// </summary>
    public static DateTimeOffset ToDanishTime(DateTime sourceUtc)
    {
        var utc = DateTime.SpecifyKind(sourceUtc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), DanishZone);
    }
}
