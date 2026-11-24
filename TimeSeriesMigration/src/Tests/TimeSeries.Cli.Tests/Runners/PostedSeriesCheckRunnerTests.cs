using TimeSeries.Cli.Runners;
using TimeSeries.Cli.Services;

namespace TimeSeries.Cli.Tests.Runners;

public sealed class PostedSeriesCheckRunnerTests : IDisposable
{
    private const string Mp1 = "571313103103408103";
    private const string Mp2 = "571313104200157246";
    private static readonly Guid Settl1 = Guid.NewGuid(), Settl2 = Guid.NewGuid();
    private static readonly Guid Profile1H = Guid.NewGuid(), Measured1H = Guid.NewGuid(), Measured2H = Guid.NewGuid();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "posted-series-" + Guid.NewGuid().ToString("N"));
    private readonly FakeReader _settl = new();
    private readonly List<string> _logs = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeReader : IPostedSeriesReader
    {
        public Dictionary<Guid, string> Json { get; } = new();
        public string? Error { get; set; }

        public Task<(string? Json, string? Error)> SeriesJsonAsync(Guid meteringPoint, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, string?)>(Error != null ? (null, Error) : (Json[meteringPoint], null));
    }

    // The ledger as post leaves it: the points writes and the done mark with the Settl id.
    private string Ledger()
    {
        var ledgerDir = Path.Combine(_dir, "snapshot");
        var ledger = new PostLedger(ledgerDir);
        var (from, to) = (new DateOnly(2023, 1, 1), new DateOnly(2023, 9, 18));
        ledger.Write(Mp1, Profile1H, PostLedger.Points, from, to, PostLedger.Starting);
        ledger.Write(Mp1, Profile1H, PostLedger.Points, from, to, PostLedger.Done);
        ledger.Write(Mp1, Profile1H, PostLedger.Period, from, to, PostLedger.Done);
        ledger.MarkDone(Mp1, Settl1, "447149", DateTimeOffset.UtcNow, from, to, 6239);
        ledger.Write(Mp2, Measured2H, PostLedger.Points, from, to, PostLedger.Done);
        ledger.MarkDone(Mp2, Settl2, "1", DateTimeOffset.UtcNow, from, to, 6239);
        return ledgerDir;
    }

    private string Out => Path.Combine(_dir, "posted-series-test.csv");

    private Task<int> Run(string ledgerDir, IReadOnlyCollection<string>? only = null) =>
        new PostedSeriesCheckRunner(_settl, _logs.Add).RunAsync("test", ledgerDir, Out, only, parallel: 2);

    [Fact]
    public async Task APointsWriteToAProfileSeries_IsFlagged_AndTheRawListIsKept()
    {
        var ledgerDir = Ledger();
        _settl.Json[Settl1] = $$$"""
            [{"id":"{{{Profile1H}}}","name":"profile_{{{Mp1}}}_PT1H","description":"Profile Time Series - PT1H","timeResolution":{"length":1,"unit":"Hour"}},
             {"id":"{{{Measured1H}}}","name":"{{{Mp1}}}_PT1H","description":"Measured Time Series - PT1H"}]
            """;
        _settl.Json[Settl2] = $$$"""[{"id":"{{{Measured2H}}}","name":"{{{Mp2}}}_PT1H"}]""";

        Assert.Equal(0, await Run(ledgerDir));

        var rows = File.ReadAllLines(Out).Skip(1).ToList();
        Assert.Equal(2, rows.Count);   // the period write and the done mark are not points writes
        Assert.Contains($"{Mp1};{Settl1};{Profile1H};2023-01-01;2023-09-18;", rows[0]);
        Assert.Contains($";profile;profile_{Mp1}_PT1H;", rows[0]);
        Assert.Contains($";measured;{Mp2}_PT1H;", rows[1]);
        Assert.Contains(_logs, l => l.Contains($"MP {Mp1}: 2023-01-01 → 2023-09-18 went to {Profile1H} — profile (profile_{Mp1}_PT1H)"));
        Assert.Contains(_logs, l => l.Contains("1  profile (1 metering point(s))"));
        var raw = File.ReadAllLines(Path.Combine(_dir, "posted-series-test-raw.jsonl"));
        Assert.Equal(2, raw.Length);
        Assert.Contains("Profile Time Series - PT1H", raw[0]);
    }

    [Fact]
    public async Task ASeriesTheListDoesNotHave_IsNotListed_AndNamelessOnesAreUnknown()
    {
        var ledgerDir = Ledger();
        _settl.Json[Settl1] = """[]""";
        _settl.Json[Settl2] = $$$"""[{"id":"{{{Measured2H}}}","timeResolution":{"length":1,"unit":"Hour"}}]""";

        Assert.Equal(0, await Run(ledgerDir));

        var rows = File.ReadAllLines(Out).Skip(1).ToList();
        Assert.Contains(";not listed;", rows[0]);
        Assert.Contains(";unknown;", rows[1]);
    }

    [Fact]
    public async Task Only_LimitsTheCheck_AndAFailedReadFails()
    {
        var ledgerDir = Ledger();
        _settl.Error = "503";

        Assert.Equal(1, await Run(ledgerDir, only: [Mp2]));

        Assert.Contains(_logs, l => l.Contains("1 posted points write(s) for 1 metering point(s)"));
        Assert.Contains(_logs, l => l.Contains($"ERROR MP {Mp2}: time series list failed: 503"));
    }
}
