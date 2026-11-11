using ClosedXML.Excel;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

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

        /// <summary>First delivery start reason per metering point; MoveIn (not part of the migration) when not set.</summary>
        public Dictionary<Guid, DeliveryStartReason> FirstStart { get; } = new();
        public string? DeliveryError { get; set; }

        public Task<(IReadOnlyList<DeliveryStatusDto>? Statuses, string? Error)> DeliveryStatusesAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<(IReadOnlyList<DeliveryStatusDto>?, string?)>(DeliveryError != null
                ? (null, DeliveryError)
                : ([new DeliveryStatusDto
                {
                    InDelivery = true,
                    Period = new TimePeriodDto { Start = new DateTimeOffset(Delivery), End = DateTimeOffset.MaxValue },
                    StartReason = FirstStart.GetValueOrDefault(id, DeliveryStartReason.MoveIn),
                }], null));
    }

    private sealed class FakeOrigin : IMigrationOriginReader
    {
        public HashSet<string> WithAccount { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, DateOnly>? Asked { get; private set; }
        public string? Error { get; set; }

        public Task<(IReadOnlySet<string>? Migrated, string? Error)> WithAccountAtCosMinusOneAsync(
            IReadOnlyDictionary<string, DateOnly> cosByMeteringPoint, CancellationToken cancellationToken)
        {
            Asked = cosByMeteringPoint;
            return Task.FromResult<(IReadOnlySet<string>?, string?)>(Error != null
                ? (null, Error)
                : (cosByMeteringPoint.Keys.Where(WithAccount.Contains).ToHashSet(StringComparer.Ordinal), null));
        }
    }

    private readonly FakeOrigin _origin = new();

    private static MeterVersion Version(string occurrence, double? volume = null, int meters = 1) =>
        new(volume, Enumerable.Range(0, meters).Select(_ => new MeterVersionMeter(occurrence, 6, 2, 1.0, "25")).ToList());

    private void InSettl(string mp, params (DateTime Start, MeterVersion Version)[] versions) =>
        InSettl(mp, inDelivery: true, child: false, Delivery, versions);

    private Guid InSettl(string mp, bool inDelivery, bool child, DateTime? deliveryStart, params (DateTime Start, MeterVersion Version)[] versions)
    {
        var id = Guid.NewGuid();
        _settl.List.Add(new MissingEavCandidate(mp, id, inDelivery, child, deliveryStart));
        _settl.Versions[id] = versions.ToList();
        return id;
    }

    private Guid IdOf(string mp) => _settl.List.Single(c => c.Identification == mp).Id;

    private string FilePath => Path.Combine(_dir, "missing-eav.xlsx");

    private Task<int> Run(params string[] only) =>
        new ExportMissingEavExcelRunner(_settl, _origin, _logs.Add).RunAsync(new ExportMissingEavExcelRunOptions
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
        Assert.All(rows, r => Assert.Equal(("Update", "False", Mp1, "DK", "2000"),
            (r["Action"], r["OverwriteFuture"], r["MeteringPointIdentification"], r["CountryCode"], r["EstimatedAnnualVolume"])));
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
    public async Task PartOfTheMigration_IsLeftOut_ChangeOfSupplierWithoutXellentAccountAndMoveIn_AreNot()
    {
        InSettl(Mp1, (Created, Version("Hourly")));   // ChangeOfSupplier, Xellent account at COS-1: migrated
        InSettl(Mp2, (Created, Version("Hourly")));   // ChangeOfSupplier, no Xellent account at COS-1
        InSettl(Mp3, (Created, Version("Hourly")));   // MoveIn
        _settl.FirstStart[IdOf(Mp1)] = DeliveryStartReason.ChangeOfSupplier;
        _settl.FirstStart[IdOf(Mp2)] = DeliveryStartReason.ChangeOfSupplier;
        _origin.WithAccount.Add(Mp1);
        _origin.WithAccount.Add(Mp2 + "0");

        Assert.Equal(0, await Run());

        Assert.Equal([Mp2, Mp3], Rows().Select(r => r["MeteringPointIdentification"]));
        // Only the ChangeOfSupplier points go to Xellent, each with its first delivery date (2025-04-01 Danish).
        Assert.Equal(new Dictionary<string, DateOnly> { [Mp1] = new(2025, 4, 1), [Mp2] = new(2025, 4, 1) }, _origin.Asked);
        Assert.Contains(_logs, l => l.Trim() == $"1  {ExportMissingEavExcelRunner.Migrated}");
        Assert.DoesNotContain(_logs, l => l.Contains($"MP {Mp1}:"));

        // The decisions file says why each one is in or out.
        Assert.Equal(
        [
            "MeteringPoint;FirstDeliveryStart;FirstStartReason;XellentAccountAtCosMinusOne;Result",
            $"{Mp1};2025-04-01;ChangeOfSupplier;yes;{ExportMissingEavExcelRunner.Migrated}",
            $"{Mp2};2025-04-01;ChangeOfSupplier;no;row(s) written: 1",
            $"{Mp3};2025-04-01;MoveIn;;row(s) written: 1",
        ], File.ReadAllLines(ExportMissingEavExcelRunner.DecisionsPath(FilePath)));
    }

    [Fact]
    public async Task AnUnknownFirstDeliveryStart_IsLeftOutByHand()
    {
        InSettl(Mp1, (Created, Version("Hourly")));
        InSettl(Mp2, (Created, Version("Hourly")));
        _settl.FirstStart[IdOf(Mp1)] = DeliveryStartReason.Unknown;

        Assert.Equal(0, await Run());

        Assert.Equal([Mp2], Rows().Select(r => r["MeteringPointIdentification"]));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: left out: first delivery start is UnknownDeliveryStartReason, by hand"));
    }

    [Fact]
    public async Task AXellentReadFailure_WritesNoFile()
    {
        InSettl(Mp1, (Created, Version("Hourly")));
        _settl.FirstStart[IdOf(Mp1)] = DeliveryStartReason.ChangeOfSupplier;
        _origin.Error = "network-related";

        Assert.Equal(1, await Run());

        Assert.False(File.Exists(FilePath));
        Assert.Contains(_logs, l => l.Contains("ERROR: Xellent read failed: network-related"));
    }

    [Fact]
    public async Task ADeliveryStatusFailure_WritesNoFile_AndDoesNotReadXellent()
    {
        InSettl(Mp1, (Created, Version("Hourly")));
        _settl.DeliveryError = "500";

        Assert.Equal(1, await Run());

        Assert.False(File.Exists(FilePath));
        Assert.Null(_origin.Asked);
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
