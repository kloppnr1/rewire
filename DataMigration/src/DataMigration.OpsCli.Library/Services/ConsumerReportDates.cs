namespace DataMigration.OpsCli.Services;

internal static class ConsumerReportDates
{
    private static readonly TimeZoneInfo DanishZone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    internal static string Format(DateTimeOffset date, bool end = false) => end && date.Year == 9999
        ? "(open)" : TimeZoneInfo.ConvertTime(date, DanishZone).ToString("O");
}
