using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using XellentInvestigationQueries;

// Translation check only. No connection is opened and no source data is read.
var options = new DbContextOptionsBuilder<XellentDbContext>()
    .UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True",
        sql => sql.UseCompatibilityLevel(110))
    .Options;
using var db = new XellentDbContext(options);
var day = new DateOnly(2025, 1, 1);
var start = day.ToDateTime(TimeOnly.MinValue);
var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue);

var queries = new Dictionary<string, IQueryable>
{
    ["installation supply"] = InvestigationQueries.FindInstallationSupplyParts(
        db, "demo", "INSTALLATION", MigrationConstants.DeliveryCategoryWater, day),
    ["heat supply"] = InvestigationQueries.FindInstallationSupplyParts(
        db, "demo", "INSTALLATION", MigrationConstants.DeliveryCategoryHeating, day),
    ["posted invoice headers"] = InvestigationQueries.FindPostedInvoices(
        db, "demo", "ACCOUNT", start, end),
    ["meter events"] = InvestigationQueries.FindMeterEvents(
        db, "demo", "COMPANY", MigrationConstants.DeliveryCategoryWater,
        "INSTALLATION", start, end),
    ["current point values"] = InvestigationQueries.FindCurrentPointValues(
        db, "demo", "POINT", "COMPANY", MigrationConstants.DeliveryCategoryElectricity,
        1, start, end),
    ["part changes"] = InvestigationQueries.FindPartChanges(db, "demo", "CONTRACT"),
    ["postal DAR candidates"] = InvestigationQueries.FindPostalDarAccounts(
        db, "demo", "DAR"),
    ["installation DAR candidates"] = InvestigationQueries.FindInstallationDarAccounts(
        db, "demo", "DAR", MigrationConstants.DeliveryCategoryHeating),
    ["delpoint DAR candidates"] = InvestigationQueries.FindDelpointDarAccounts(
        db, "demo", "DAR", MigrationConstants.DeliveryCategoryElectricity),
    ["contact DAR candidates"] = InvestigationQueries.FindPointContactDarAccounts(
        db, "demo", "DAR", MigrationConstants.DeliveryCategoryElectricity),
    ["reading resolutions"] = InvestigationQueries.FindReadingResolutionHistory(
        db, "demo", "POINT", "COMPANY", MigrationConstants.DeliveryCategoryElectricity),
    ["delpoint meters"] = InvestigationQueries.FindDelpointMeters(
        db, "demo", "POINT", "COMPANY", MigrationConstants.DeliveryCategoryElectricity,
        start, end)
};

foreach (var (name, query) in queries)
{
    var sql = query.ToQueryString();
    if (string.IsNullOrWhiteSpace(sql) || !sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"No SQL generated for {name}");
    Console.WriteLine($"{name}: SQL generated");
}

var dstDays = new[]
{
    (Day: new DateOnly(2025, 3, 30), From: new DateTime(2025, 3, 29, 23, 0, 0, DateTimeKind.Utc),
        To: new DateTime(2025, 3, 30, 22, 0, 0, DateTimeKind.Utc), Hours: 23),
    (Day: new DateOnly(2025, 10, 26), From: new DateTime(2025, 10, 25, 22, 0, 0, DateTimeKind.Utc),
        To: new DateTime(2025, 10, 26, 23, 0, 0, DateTimeKind.Utc), Hours: 25)
};

foreach (var example in dstDays)
{
    var window = DanishTimeWindows.ForDay(example.Day);
    if (window.FromUtc != example.From || window.ToUtc != example.To
        || (window.ToUtc - window.FromUtc).TotalHours != example.Hours)
        throw new InvalidOperationException($"Wrong Danish UTC window for {example.Day}");
    Console.WriteLine($"{example.Day}: {window.FromUtc:O} to {window.ToUtc:O} ({example.Hours} hours)");
}

var firstAutumnHour = DanishTimeWindows.ToDanishTime(
    new DateTime(2025, 10, 26, 0, 30, 0, DateTimeKind.Utc));
var secondAutumnHour = DanishTimeWindows.ToDanishTime(
    new DateTime(2025, 10, 26, 1, 30, 0, DateTimeKind.Utc));
if (firstAutumnHour.Hour != 2 || secondAutumnHour.Hour != 2
    || firstAutumnHour.Offset != TimeSpan.FromHours(2)
    || secondAutumnHour.Offset != TimeSpan.FromHours(1))
    throw new InvalidOperationException("Repeated Danish autumn hour lost its offset");
Console.WriteLine($"Repeated autumn hour: {firstAutumnHour:yyyy-MM-dd HH:mm zzz} / "
    + $"{secondAutumnHour:yyyy-MM-dd HH:mm zzz}");
