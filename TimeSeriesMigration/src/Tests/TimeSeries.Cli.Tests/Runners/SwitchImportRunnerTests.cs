using ClosedXML.Excel;
using TimeSeries.Cli.Runners;

namespace TimeSeries.Cli.Tests.Runners;

public sealed class SwitchImportRunnerTests : IDisposable
{
    private const string Mp1 = "571313100000000001";
    private const string Mp2 = "571313100000000002";
    private static readonly DateOnly Today = new(2026, 11, 10);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "switch-import-" + Guid.NewGuid().ToString("N"));
    private readonly FakeReader _settl = new();
    private readonly List<string> _logs = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeReader : ISwitchImportSettlReader
    {
        public Dictionary<string, Guid> Ids { get; } = new();
        public Dictionary<Guid, List<DateTime>> Starts { get; } = new();
        public Dictionary<Guid, List<string>> Occurrences { get; } = new();
        public Dictionary<Guid, List<string>> Series { get; } = new();
        public string? HistoryError { get; set; }
        public List<(Guid Id, DateTime At)> VersionReads { get; } = [];

        public Task<(Guid? Id, string? Error)> FindAsync(string identification, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid?, string?)>((Ids.TryGetValue(identification, out var id) ? id : null, null));

        public Task<(List<DateTime>? Starts, string? Error)> MeterCharacteristicsStartsAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<(List<DateTime>?, string?)>(HistoryError != null ? (null, HistoryError) : (Starts[id], null));

        public Task<(List<string>? Occurrences, string? Error)> MeterReadingOccurrencesAtAsync(Guid id, DateTime at, CancellationToken cancellationToken)
        {
            lock (VersionReads)
                VersionReads.Add((id, at));
            return Task.FromResult<(List<string>?, string?)>((Occurrences[id], null));
        }

        public Task<(List<string>? Resolutions, string? Error)> TimeSeriesResolutionsAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<(List<string>?, string?)>((Series[id], null));
    }

    // A metering point in Settl as migrated: meter entries from create (2024-01-01 Danish) and DataHub
    // (2026-09-01), all QuarterHourly, and only the 15-minute series.
    private Guid InSettl(string mp, string[]? series = null, string occurrence = "QuarterHourly")
    {
        var id = Guid.NewGuid();
        _settl.Ids[mp] = id;
        _settl.Starts[id] = [new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc), new DateTime(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc)];
        _settl.Occurrences[id] = [occurrence];
        _settl.Series[id] = [.. series ?? ["Quarter"]];
        return id;
    }

    private static string Message(string mp, string readingOccurrence) =>
        "<ns0:DK_NotifyMasterDataMeteringPoint xmlns:ns0=\"un:unece:260:data:EEM-DK_NotifyMasterDataMeteringPoint:v3\">" +
        $"<ns0:PayloadMasterDataMPEvent><ns0:MeteringPointDomainLocation><ns0:Identification schemeAgencyIdentifier=\"9\">{mp}</ns0:Identification></ns0:MeteringPointDomainLocation>" +
        $"<ns0:DetailMeteringPointCharacteristic><ns0:MeterReadingOccurrence>{readingOccurrence}</ns0:MeterReadingOccurrence></ns0:DetailMeteringPointCharacteristic>" +
        "</ns0:PayloadMasterDataMPEvent></ns0:DK_NotifyMasterDataMeteringPoint>";

    // resolution-switches-<env>.csv as post --all writes it.
    private string Switches(params (string Mp, string ChangeDate, string Occurrence)[] rows)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "resolution-switches-test.csv");
        var text = "MeteringPointId;ChangeDate;MissingSeries;XellentChangeDate;SameDate;PreviousDataHubMessage;DataHubMessage\r\n" +
                   string.Concat(rows.Select(r => $"{r.Mp};{r.ChangeDate};Hour;{r.ChangeDate};yes;{Message(r.Mp, "PT1H")};{Message(r.Mp, r.Occurrence)}\r\n"));
        File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
        return path;
    }

    private Task<int> Run(string input) =>
        new SwitchImportRunner(_settl, _logs.Add).RunAsync("test", input, _dir, parallel: 4, Today);

    private string MetersPath => Path.Combine(_dir, "MeteringPointMeters_V8_test_2026-11-10.xlsx");
    private string SeriesPath => Path.Combine(_dir, "TimeSeries_V1_test_2026-11-10.xlsx");

    private static List<Dictionary<string, string>> Rows(string path)
    {
        using var book = new XLWorkbook(path);
        var sheet = book.Worksheets.First();
        var header = sheet.Row(2).CellsUsed().ToDictionary(c => c.Address.ColumnNumber, c => c.GetString());
        // Dates as yyyy-MM-dd, so a day/month mix-up shows.
        static string Text(IXLCell cell) => cell.DataType == XLDataType.DateTime
            ? cell.GetDateTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : cell.GetString();
        return sheet.RowsUsed().Where(r => r.RowNumber() > 2)
            .Select(r => header.ToDictionary(h => h.Value, h => Text(r.Cell(h.Key))))
            .ToList();
    }

    [Fact]
    public async Task AnHourToQuarterSwitch_GetsHourlyFromSettlsEarliestEntry_QuarterFromChangeDate_AndAPt1hSeries()
    {
        var id = InSettl(Mp1);

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        var meters = Rows(MetersPath);
        Assert.Equal(2, meters.Count);
        Assert.Equal(("Update", "False", Mp1, "2024-01-01", "DK", "Hourly", $"{Mp1}_PT1H"),
            (meters[0]["Action"], meters[0]["OverwriteFuture"], meters[0]["MeteringPointIdentification"], meters[0]["Date"],
             meters[0]["CountryCode"], meters[0]["MeterReadingOccurrence"], meters[0]["MeasuredTimeSeriesName"]));
        Assert.Equal(("2025-12-09", "QuarterHourly", $"{Mp1}_PT15M"),
            (meters[1]["Date"], meters[1]["MeterReadingOccurrence"], meters[1]["MeasuredTimeSeriesName"]));
        // A real date shown year first, so Settl's month-first parsing cannot swap or reject it.
        using (var book = new XLWorkbook(MetersPath))
        {
            var date = book.Worksheets.First().Cell(4, Array.IndexOf(SwitchImportRunner.MetersColumns, "Date") + 1);
            Assert.Equal(XLDataType.DateTime, date.DataType);
            Assert.Equal("2025-12-09", date.GetFormattedString());
        }
        Assert.Equal("", meters[0]["MeterIdentification"]);  // only the columns that change are filled

        var series = Assert.Single(Rows(SeriesPath));
        Assert.Equal(("Create", $"{Mp1}_PT1H", "DK", "Measured Time Series - PT1H", "Active", "Quantity", "DKK", "kWh", "Hour", "1", "False", Mp1),
            (series["Action"], series["Name"], series["CountryCode"], series["Description"], series["MeasurementType"], series["Type"],
             series["Currency"], series["QuantityUnit"], series["TimeResolutionUnit"], series["TimeResolutionLength"],
             series["InvertSignOnMarketUpdates"], series["MarketIdentification"]));
        // The earliest entry is read an hour into it.
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), Assert.Single(_settl.VersionReads).At);
    }

    [Fact]
    public async Task TheFilesKeepSettlsTemplateHeader()
    {
        InSettl(Mp1);

        await Run(Switches((Mp1, "2025-12-09", "PT15M")));

        using (var meters = new XLWorkbook(MetersPath))
        {
            var sheet = meters.Worksheet("MeteringPointMeters");
            Assert.Equal(["Type", "MeteringPointMeter", "Version", "8", "Identification", "MeteringPointIdentification"],
                Enumerable.Range(1, 6).Select(c => sheet.Cell(1, c).GetString()));
            Assert.Equal(SwitchImportRunner.MetersColumns, sheet.Row(2).CellsUsed().Select(c => c.GetString()));
        }
        using var series = new XLWorkbook(SeriesPath);
        var tsSheet = series.Worksheet("TimeSeries");
        Assert.Equal(["Type", "TimeSeries", "Version", "1"], Enumerable.Range(1, 4).Select(c => tsSheet.Cell(1, c).GetString()));
        Assert.Equal(SwitchImportRunner.TimeSeriesColumns, tsSheet.Row(2).CellsUsed().Select(c => c.GetString()));
    }

    [Fact]
    public async Task AQuarterToHourSwitch_IsTheOtherWayRound()
    {
        InSettl(Mp1, series: ["Hour"], occurrence: "Hourly");

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT1H"))));

        var meters = Rows(MetersPath);
        Assert.Equal(("QuarterHourly", $"{Mp1}_PT15M"), (meters[0]["MeterReadingOccurrence"], meters[0]["MeasuredTimeSeriesName"]));
        Assert.Equal(("Hourly", $"{Mp1}_PT1H"), (meters[1]["MeterReadingOccurrence"], meters[1]["MeasuredTimeSeriesName"]));
        var series = Assert.Single(Rows(SeriesPath));
        Assert.Equal(($"{Mp1}_PT15M", "Measured Time Series - PT15M", "Minutes", "15"),
            (series["Name"], series["Description"], series["TimeResolutionUnit"], series["TimeResolutionLength"]));
    }

    [Fact]
    public async Task ASeriesSettlHasAlready_IsNotCreated()
    {
        InSettl(Mp1, series: ["Hour", "Quarter"]);

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        Assert.Equal(2, Rows(MetersPath).Count);
        Assert.Empty(Rows(SeriesPath));
    }

    [Theory]
    [InlineData("2024-01-01")]  // on Settl's earliest entry (2023-12-31T23:00Z is 2024-01-01 in Denmark)
    [InlineData("2023-09-18")]  // before it
    public async Task ASwitchOnOrBeforeSettlsEarliestEntry_IsLeftOut(string changeDate)
    {
        InSettl(Mp1);

        Assert.Equal(0, await Run(Switches((Mp1, changeDate, "PT15M"))));

        Assert.Empty(Rows(MetersPath));
        Assert.Empty(Rows(SeriesPath));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: left out: switch on or before Settl's earliest meter entry"));
    }

    [Fact]
    public async Task AnEarliestEntryThatIsAlreadyHourly_IsLeftOut()
    {
        InSettl(Mp1, series: ["Hour", "Quarter"], occurrence: "Hourly");

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        Assert.Empty(Rows(MetersPath));
        Assert.Contains(_logs, l => l.Contains("already Hourly"));
    }

    [Fact]
    public async Task SeveralSwitches_OrNotInSettl_AreLeftOut()
    {
        InSettl(Mp1);

        Assert.Equal(0, await Run(Switches((Mp1, "2025-04-01", "PT15M"), (Mp1, "2025-12-09", "PT1H"), (Mp2, "2025-12-09", "PT15M"))));

        Assert.Empty(Rows(MetersPath));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: left out: more than one switch, by hand"));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp2}: left out: not found in Settl"));
    }

    [Fact]
    public async Task ASettlFailure_WritesNoFiles()
    {
        InSettl(Mp1);
        _settl.HistoryError = "503";

        Assert.Equal(1, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        Assert.False(File.Exists(MetersPath));
        Assert.False(File.Exists(SeriesPath));
    }

    [Fact]
    public async Task AMissingInputFile_Fails()
    {
        Assert.Equal(1, await Run(Path.Combine(_dir, "nope.csv")));
        Assert.Contains(_logs, l => l.Contains("Run post --all --env test --dry-run first"));
    }

    [Fact]
    public void AQuotedFieldWithASemicolon_IsReadWhole()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "quoted.csv");
        var xml = Message(Mp1, "PT15M").Replace("</ns0:PayloadMasterDataMPEvent>", "<ns0:Note>A &amp; B</ns0:Note></ns0:PayloadMasterDataMPEvent>");
        File.WriteAllText(path, "MeteringPointId;ChangeDate;MissingSeries;XellentChangeDate;SameDate;PreviousDataHubMessage;DataHubMessage\r\n" +
                                $"{Mp1};2025-12-09;Hour;2025-12-09;yes;;\"{xml.Replace("\"", "\"\"")}\"\r\n");

        var (rows, error) = SwitchImportRunner.ReadSwitches(path);

        Assert.Null(error);
        Assert.Equal(new SwitchImportRunner.SwitchRow(Mp1, new DateOnly(2025, 12, 9), "PT15M"), Assert.Single(rows));
    }
}
