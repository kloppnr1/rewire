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
        // Settl's MeterCharacteristics versions per metering point: start (UTC) and what holds from it.
        public Dictionary<Guid, List<(DateTime Start, MeterEntry Entry)>> Versions { get; } = new();
        public string? HistoryError { get; set; }
        public List<(Guid Id, DateTime At)> VersionReads { get; } = [];

        public Task<(Guid? Id, string? Error)> FindAsync(string identification, CancellationToken cancellationToken) =>
            Task.FromResult<(Guid?, string?)>((Ids.TryGetValue(identification, out var id) ? id : null, null));

        public Task<(List<DateTime>? Starts, string? Error)> MeterCharacteristicsStartsAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<(List<DateTime>?, string?)>(HistoryError != null ? (null, HistoryError) : (Starts[id], null));

        public Task<(MeterEntry? Entry, string? Error)> MeterEntryAtAsync(Guid id, DateTime at, CancellationToken cancellationToken)
        {
            lock (VersionReads)
                VersionReads.Add((id, at));
            var entry = Versions[id].Where(v => v.Start <= at).MaxBy(v => v.Start).Entry;
            return Task.FromResult<(MeterEntry?, string?)>((entry, null));
        }
    }

    private static readonly DateTime Created = new(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc);   // 2024-01-01 Danish
    private static readonly DateTime FromDataHub = new(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc); // 2026-09-01 Danish

    private static MeterEntry Entry(string occurrence, double? volume = 4200, string? ampere = "25", int meters = 1) =>
        new(volume, Enumerable.Range(0, meters).Select(_ => new MeterValues(occurrence, 6, 2, 1.0, ampere)).ToList());

    // A metering point in Settl as migrated: meter entries from create (2024-01-01 Danish) and DataHub
    // (2026-09-01), all QuarterHourly with one meter.
    private Guid InSettl(string mp, string occurrence = "QuarterHourly")
    {
        var id = Guid.NewGuid();
        _settl.Ids[mp] = id;
        _settl.Starts[id] = [FromDataHub, Created];
        _settl.Versions[id] = [(Created, Entry(occurrence)), (FromDataHub, Entry(occurrence, volume: 3900))];
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

    // Settl creates the series the meter rows name; no TimeSeries workbook is written.
    private void OnlyTheMetersFile() =>
        Assert.Equal([Path.GetFileName(MetersPath)], Directory.GetFiles(_dir, "*.xlsx").Select(Path.GetFileName));

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
    public async Task AnHourToQuarterSwitch_GetsHourlyFromSettlsEarliestEntry_AndQuarterFromChangeDate()
    {
        InSettl(Mp1);

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
        Assert.Equal("", meters[0]["MeterIdentification"]);  // the row names no meter
        OnlyTheMetersFile();
        Assert.DoesNotContain(_logs, l => l.StartsWith("TimeSeries", StringComparison.Ordinal));
        // The earliest entry and the one at ChangeDate (2025-12-09 Danish midnight is 2025-12-08T23:00Z) are read an hour into them.
        Assert.Equal([new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 12, 9, 0, 0, 0, DateTimeKind.Utc)],
            _settl.VersionReads.Select(v => v.At));
    }

    [Fact]
    public async Task EachRow_CarriesWhatSettlHoldsAtItsDate()
    {
        var id = InSettl(Mp1);
        _settl.Versions[id] =
        [
            (Created, Entry("QuarterHourly", volume: 4200, ampere: "25")),
            (new DateTime(2025, 6, 30, 22, 0, 0, DateTimeKind.Utc), Entry("QuarterHourly", volume: 3900, ampere: "35")),
        ];

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        var meters = Rows(MetersPath);
        Assert.Equal(("4200", "6", "2", "1", "25"),
            (meters[0]["EstimatedAnnualVolume"], meters[0]["DigitsOfPrecision"], meters[0]["Decimals"], meters[0]["MultiplicationFactor"], meters[0]["Ampere"]));
        Assert.Equal(("3900", "35"), (meters[1]["EstimatedAnnualVolume"], meters[1]["Ampere"]));
    }

    [Fact]
    public async Task NoVolumeOrAmpereInSettl_LeavesThoseCellsEmpty()
    {
        var id = InSettl(Mp1);
        _settl.Versions[id] = [(Created, Entry("QuarterHourly", volume: null, ampere: null))];

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        var meters = Rows(MetersPath);
        Assert.All(meters, m => Assert.Equal(("", "", "6"), (m["EstimatedAnnualVolume"], m["Ampere"], m["DigitsOfPrecision"])));
    }

    [Fact]
    public async Task SeveralMeters_LeaveTheMeterColumnsEmpty_AndSaySo()
    {
        var id = InSettl(Mp1);
        _settl.Versions[id] = [(Created, Entry("QuarterHourly", meters: 2))];

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        var meters = Rows(MetersPath);
        Assert.All(meters, m => Assert.Equal(("4200", "", "", "", ""),
            (m["EstimatedAnnualVolume"], m["DigitsOfPrecision"], m["Decimals"], m["MultiplicationFactor"], m["Ampere"])));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: 2 meters at 2024-01-01, meter columns left empty"));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: 2 meters at 2025-12-09, meter columns left empty"));
    }

    [Fact]
    public async Task TheFileKeepsSettlsTemplateHeader()
    {
        InSettl(Mp1);

        await Run(Switches((Mp1, "2025-12-09", "PT15M")));

        using var meters = new XLWorkbook(MetersPath);
        var sheet = meters.Worksheet("MeteringPointMeters");
        Assert.Equal(["Type", "MeteringPointMeter", "Version", "8", "Identification", "MeteringPointIdentification"],
            Enumerable.Range(1, 6).Select(c => sheet.Cell(1, c).GetString()));
        Assert.Equal(SwitchImportRunner.MetersColumns, sheet.Row(2).CellsUsed().Select(c => c.GetString()));
    }

    [Fact]
    public async Task AQuarterToHourSwitch_IsTheOtherWayRound()
    {
        InSettl(Mp1, occurrence: "Hourly");

        Assert.Equal(0, await Run(Switches((Mp1, "2025-12-09", "PT1H"))));

        var meters = Rows(MetersPath);
        Assert.Equal(("QuarterHourly", $"{Mp1}_PT15M"), (meters[0]["MeterReadingOccurrence"], meters[0]["MeasuredTimeSeriesName"]));
        Assert.Equal(("Hourly", $"{Mp1}_PT1H"), (meters[1]["MeterReadingOccurrence"], meters[1]["MeasuredTimeSeriesName"]));
        OnlyTheMetersFile();
    }

    [Theory]
    [InlineData("2024-01-01")]  // on Settl's earliest entry (2023-12-31T23:00Z is 2024-01-01 in Denmark)
    [InlineData("2023-09-18")]  // before it
    public async Task ASwitchOnOrBeforeSettlsEarliestEntry_IsLeftOut(string changeDate)
    {
        InSettl(Mp1);

        Assert.Equal(0, await Run(Switches((Mp1, changeDate, "PT15M"))));

        Assert.Empty(Rows(MetersPath));
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: left out: switch on or before Settl's earliest meter entry"));
    }

    [Fact]
    public async Task AnEarliestEntryThatIsAlreadyHourly_IsLeftOut()
    {
        InSettl(Mp1, occurrence: "Hourly");

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
    public async Task ASettlFailure_WritesNoFile()
    {
        InSettl(Mp1);
        _settl.HistoryError = "503";

        Assert.Equal(1, await Run(Switches((Mp1, "2025-12-09", "PT15M"))));

        Assert.False(File.Exists(MetersPath));
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
