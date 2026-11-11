using ClosedXML.Excel;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Runners;

public sealed class ExportMissingEavExcelRunnerTests : IDisposable
{
    private const string Mp1 = "571313100000000001";
    private const string Mp2 = "571313100000000002";
    private const string Mp3 = "571313100000000003";
    private static readonly DateOnly Today = new(2026, 11, 11);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "missing-eav-" + Guid.NewGuid().ToString("N"));
    private readonly FakeReader _settl = new();
    private readonly List<string> _logs = [];

    private static readonly DateTime Created = new(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc);  // 2024-01-01 Danish
    private static readonly DateTime Delivery = new(2025, 3, 31, 22, 0, 0, DateTimeKind.Utc);  // 2025-04-01 Danish
    private static readonly DateTime Switch = new(2025, 12, 8, 23, 0, 0, DateTimeKind.Utc);    // 2025-12-09 Danish
    private static readonly DateTime Future = new(2027, 1, 31, 23, 0, 0, DateTimeKind.Utc);    // 2027-02-01 Danish

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeReader : IMissingEavSettlReader
    {
        public List<MissingEavCandidate> List { get; } = [];
        public Dictionary<Guid, List<(DateTime Start, MeterVersion Version)>> Versions { get; } = new();
        public string? HistoryError { get; set; }

        public Task<(List<MissingEavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(List<MissingEavCandidate>?, string?)>((List, null));

        public Task<(MissingEavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken) =>
            Task.FromResult<(MissingEavCandidate?, string?)>((List.FirstOrDefault(c => c.Identification == identification), null));

        public Task<(List<DateTime>? Starts, string? Error)> VersionStartsAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<(List<DateTime>?, string?)>(HistoryError != null
                ? (null, HistoryError)
                : (Versions[id].Select(v => v.Start).Reverse().ToList(), null));

        public Task<(MeterVersion? Version, string? Error)> VersionAtAsync(Guid id, DateTime at, CancellationToken cancellationToken) =>
            Task.FromResult<(MeterVersion?, string?)>((Versions[id].Where(v => v.Start <= at).MaxBy(v => v.Start).Version, null));
    }

    private static MeterVersion Version(string occurrence, double? volume = null, int meters = 1) =>
        new(volume, Enumerable.Range(0, meters).Select(_ => new MeterVersionMeter(occurrence, 6, 2, 1.0, "25")).ToList());

    private void InSettl(string mp, params (DateTime Start, MeterVersion Version)[] versions) =>
        InSettl(mp, inDelivery: true, child: false, Delivery, versions);

    private void InSettl(string mp, bool inDelivery, bool child, DateTime? deliveryStart, params (DateTime Start, MeterVersion Version)[] versions)
    {
        var id = Guid.NewGuid();
        _settl.List.Add(new MissingEavCandidate(mp, id, inDelivery, child, deliveryStart));
        _settl.Versions[id] = versions.ToList();
    }

    private string FilePath => Path.Combine(_dir, "missing-eav.xlsx");

    private Task<int> Run(params string[] only) =>
        new ExportMissingEavExcelRunner(_settl, _logs.Add).RunAsync(new ExportMissingEavExcelRunOptions
        {
            OutputPath = FilePath,
            MeteringPoints = only,
            Parallel = 4,
            Today = Today,
        });

    private List<Dictionary<string, string>> Rows()
    {
        using var book = new XLWorkbook(FilePath);
        var sheet = book.Worksheets.First();
        Assert.Equal(["Type", "MeteringPointMeter", "Version", "8", "Identification", "MeteringPointIdentification"],
            Enumerable.Range(1, 6).Select(c => sheet.Cell(1, c).GetString()));
        var header = sheet.Row(2).CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString());
        static string Text(IXLCell cell) => cell.DataType == XLDataType.DateTime
            ? cell.GetDateTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : cell.GetString();
        return sheet.RowsUsed().Where(r => r.RowNumber() > 2)
            .Select(r => header.ToDictionary(h => h.Value, h => Text(r.Cell(h.Key))))
            .ToList();
    }

    [Fact]
    public async Task EachVersionWithoutVolume_FromTheOneInForceAtDeliveryStart_GetsAFullRow()
    {
        InSettl(Mp1,
            (Created, Version("Hourly")),
            (Switch, Version("QuarterHourly")),
            (Future, Version("QuarterHourly")));

        Assert.Equal(0, await Run());

        var rows = Rows();
        Assert.Equal(["2024-01-01", "2025-12-09", "2027-02-01"], rows.Select(r => r["Date"]));
        Assert.All(rows, r => Assert.Equal(("Update", "False", Mp1, "DK", "2000", "kWh"),
            (r["Action"], r["OverwriteFuture"], r["MeteringPointIdentification"], r["CountryCode"], r["EstimatedAnnualVolume"], r["QuantityUnit"])));
        Assert.Equal(["Hourly", "QuarterHourly", "QuarterHourly"], rows.Select(r => r["MeterReadingOccurrence"]));
        Assert.Equal([$"{Mp1}_PT1H", $"{Mp1}_PT15M", $"{Mp1}_PT15M"], rows.Select(r => r["MeasuredTimeSeriesName"]));
        // The row replaces the version, so the meter's values ride along.
        Assert.All(rows, r => Assert.Equal(("6", "2", "1", "25"),
            (r["DigitsOfPrecision"], r["Decimals"], r["MultiplicationFactor"], r["Ampere"])));
    }

    [Fact]
    public async Task VersionsBeforeTheOneInForceAtDeliveryStart_AndVersionsWithAVolume_GetNoRow()
    {
        InSettl(Mp1,
            (new DateTime(2022, 12, 31, 23, 0, 0, DateTimeKind.Utc), Version("Hourly")),
            (Created, Version("Hourly", volume: 4200)),
            (Switch, Version("QuarterHourly")));

        Assert.Equal(0, await Run());

        Assert.Equal(["2025-12-09"], Rows().Select(r => r["Date"]));
    }

    [Fact]
    public async Task ChildrenNotInDeliveryAndAlreadySet_AreLeftOut()
    {
        InSettl(Mp1, inDelivery: false, child: false, null, (Created, Version("Hourly")));
        InSettl(Mp2, inDelivery: true, child: true, Delivery, (Created, Version("Hourly")));
        InSettl(Mp3, (Created, Version("Hourly", volume: 3000)));

        Assert.Equal(0, await Run());

        Assert.Empty(Rows());
        Assert.Contains(_logs, l => l.Trim() == "1  already has an EstimatedAnnualVolume");
    }

    [Fact]
    public async Task WithoutDeliveryPeriod_ScopeStartsAtTheVersionInForceToday()
    {
        InSettl(Mp1, inDelivery: true, child: false, null,
            (Created, Version("Hourly")),
            (Switch, Version("QuarterHourly")));

        Assert.Equal(0, await Run());

        Assert.Equal(["2025-12-09"], Rows().Select(r => r["Date"]));
    }

    [Fact]
    public async Task AVersionWithSeveralMeters_LeavesTheWholeMeteringPointOut()
    {
        InSettl(Mp1,
            (Created, Version("Hourly")),
            (Switch, Version("QuarterHourly", meters: 2)));
        InSettl(Mp2, (Created, Version("Hourly")));

        Assert.Equal(0, await Run());

        Assert.Equal([Mp2], Rows().Select(r => r["MeteringPointIdentification"]));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: left out: meter entry at 2025-12-09 has 2 meters"));
    }

    [Fact]
    public async Task MeteringPoints_ReadsJustThose()
    {
        InSettl(Mp1, (Created, Version("Hourly")));
        InSettl(Mp2, (Created, Version("Hourly")));

        Assert.Equal(0, await Run(Mp2));

        Assert.Equal([Mp2], Rows().Select(r => r["MeteringPointIdentification"]));
    }

    [Fact]
    public async Task ASettlReadFailure_WritesNoFile()
    {
        InSettl(Mp1, (Created, Version("Hourly")));
        _settl.HistoryError = "500";

        Assert.Equal(1, await Run());

        Assert.False(File.Exists(FilePath));
    }
}
