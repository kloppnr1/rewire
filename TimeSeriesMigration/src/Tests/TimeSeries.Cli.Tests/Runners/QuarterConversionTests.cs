using DataMigration.SettlApi.Models;
using TimeSeries.Cli.Runners;
using TimeSeries.Cli.Services;
using TimeSeries.Cli.Tests.Fixtures;
using TimeSeries.Core.Models;

namespace TimeSeries.Cli.Tests.Runners;

public class QuarterConversionTests : IDisposable
{
    private const string Mp = "571313100000000097";
    private readonly XellentTimeSeriesSeeder _xellent = new();
    private readonly RecordingTimeSeriesClient _ts = new();
    private readonly RecordingMeteringPointClient _mp = new();
    private readonly List<string> _logs = [];
    private readonly string _output = Path.Combine(Path.GetTempPath(), "quarter-conversion-" + Guid.NewGuid());
    private static readonly DateTime StartUtc = new(2023, 12, 30, 23, 0, 0, DateTimeKind.Utc);

    private void Seed(decimal value = 0.001m)
    {
        _xellent.SeedHourlyTimeSeries(Mp, StartUtc, 48, value, companyId: "c1");
        _xellent.SeedReadingPeriod(Mp, "c1", new DateTime(2000, 1, 1), new DateTime(2023, 12, 31), 0);
        _xellent.SeedReadingPeriod(Mp, "c1", new DateTime(2024, 1, 1), new DateTime(1900, 1, 1), 1);
        _mp.GetTimeSeriesResponse = _ =>
        [
            new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 1, Unit = "Hour" } },
            new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 15, Unit = "Minutes" } }
        ];
    }

    private Task<int> Run(bool dryRun = false, bool skipExisting = false, bool skipResolution = false, DateTimeOffset? boundary = null) => new TimeSeriesRunner(
        "ignored", _output, 3, _ts, _mp, new RecordingCustomerClient(),
        contextFactory: _xellent.CreateContext, log: _logs.Add).RunPostAsync(new PostOptions
        {
            MeteringPointId = Mp, Environment = "test", DryRun = dryRun,
            SkipIfExistingPoints = skipExisting, SkipResolutionChange = skipResolution,
            ResolvedRevision = boundary.HasValue ? boundary.Value.AddDays(1) : null, ResolvedDeliveryBoundary = boundary,
            StartDate = new DateTime(2023, 12, 31), EndDate = new DateTime(2024, 1, 2)
        });

    [Fact]
    public async Task Posting_ConvertsOnlyDatedQuarterPeriod_AndLeavesSourceIntact()
    {
        Seed();
        Assert.Equal(0, await Run());
        Assert.Equal(new[] { 24, 96 }, _ts.PostCalls.Select(c => c.Payload.Points.Count));
        var quarters = _ts.PostCalls[1].Payload.Points;
        Assert.All(quarters, p => Assert.Equal(0.00025m, p.Quantity));
        Assert.Equal(StartUtc.AddHours(24), quarters[0].Time);
        Assert.Equal(StartUtc.AddHours(48).AddMinutes(-15), quarters[^1].Time);
        Assert.Equal(0.048m, _ts.PostCalls.SelectMany(p => p.Payload.Points).Sum(p => p.Quantity));
        using var ctx = _xellent.CreateContext();
        Assert.Equal(48, ctx.EmsTimeseriesValues.Count());
        Assert.All(ctx.EmsTimeseriesValues, p => Assert.Equal(0.001m, p.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidLaterPeriod_BlocksEntireMeteringPoint(bool dryRun)
    {
        Seed(0.0001m);
        Assert.Equal(1, await Run(dryRun));
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Contains(_logs, l => l.Contains($"Conversion failed for MP {Mp}") && l.Contains("0.0001"));
        Assert.Contains($"OUTGOING MP {Mp}: resolutions=Hour+Quarter, convertedHours=24, convertedQuarters=96", _logs);
    }

    [Theory]
    [InlineData(false, 0, "Hour+Quarter", 24)]
    [InlineData(true, 0, "Hour+Quarter", 24)]
    [InlineData(false, 36, "Quarter", 12)]
    [InlineData(true, 36, "Quarter", 12)]
    public async Task OutgoingPlan_ShowsConversionAfterDeliveryFiltering(bool dryRun, int boundaryHours, string resolutions, int convertedHours)
    {
        Seed();
        Assert.Equal(0, await Run(dryRun, boundary: new DateTimeOffset(StartUtc.AddHours(boundaryHours))));
        Assert.Contains($"OUTGOING MP {Mp}: resolutions={resolutions}, convertedHours={convertedHours}, convertedQuarters={convertedHours * 4}", _logs);
    }

    [Fact]
    public async Task OutgoingPlan_DoesNotMarkHourlyValuesWithoutQuarterReadingEvidence()
    {
        _xellent.SeedHourlyTimeSeries(Mp, StartUtc, 48, 0.001m, companyId: "c1");
        _mp.GetTimeSeriesResponse = _ => [new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 1, Unit = "Hour" } }];
        Assert.Equal(0, await Run(dryRun: true));
        Assert.Contains($"OUTGOING MP {Mp}: resolutions=Hour, convertedHours=0, convertedQuarters=0", _logs);
    }

    [Fact]
    public async Task MissingQuarterTarget_BlocksEarlierHourlyWritesToo()
    {
        Seed();
        _mp.GetTimeSeriesResponse = _ => [new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 1, Unit = "Hour" } }];
        Assert.Equal(1, await Run());
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CountOnly_UsesSamePeriodsCountsAndPrecisionValidation(bool invalid)
    {
        Seed(invalid ? 0.0001m : 0.001m);
        using var ctx = _xellent.CreateContext();
        var service = new TimeSeriesExtractionService(ctx, meteringPoints: [Mp]);
        var count = await service.CountTimeSeriesAsync(Mp, new DateTime(2023, 12, 31), new DateTime(2024, 1, 2), null);
        Assert.Equal(120, count.Segments.Sum(OutgoingTimeSeries.PointCount));
        Assert.Equal(new[] { "Hour", "Quarter" }, count.Segments.Select(OutgoingTimeSeries.Resolution));
        Assert.Equal(invalid, count.Segments.Any(s => OutgoingTimeSeries.Validate(s) != null));
        // Excluded bad values cannot block a later delivery window.
        var after = await service.CountTimeSeriesAsync(Mp, new DateTime(2023, 12, 31), new DateTime(2024, 1, 2), new DateTimeOffset(StartUtc.AddHours(48)));
        Assert.All(after.Segments, s => Assert.Null(OutgoingTimeSeries.Validate(s)));
    }

    [Fact]
    public async Task ExistingDataSkip_UsesQuarterTarget_AndDoesNotFailOnMissingHourTarget()
    {
        Seed();
        var target = Guid.NewGuid();
        _mp.GetTimeSeriesResponse = _ => [new() { Id = target, TimeResolution = new() { Length = 15, Unit = "Minutes" } }];
        _ts.GetPointsResponse = (_, _, _) => (new SettlPointsSummaryDto { Count = 96 }, null);
        Assert.Equal(0, await Run(skipExisting: true));
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.All(_ts.GetPointsCalls, c => Assert.Equal(target, c.TsGuid));
        Assert.Contains(_logs, l => l.Contains("existing Settl TS data"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolutionSkip_UsesOnlyValuesInsideDeliveryBoundary(bool dryRun)
    {
        Seed();
        Assert.Equal(0, await Run(dryRun, skipResolution: true, boundary: new DateTimeOffset(StartUtc.AddHours(24))));
        Assert.DoesNotContain(_logs, l => l.Contains("resolution change"));
        Assert.Contains(_logs, l => l.Contains("points=96"));
    }

    [Fact]
    public async Task SourceExport_RetainsBothHourlySegmentsWithoutOverwriting()
    {
        Seed();
        using var ctx = _xellent.CreateContext();
        var source = await new TimeSeriesExtractionService(ctx, meteringPoints: [Mp])
            .ExtractTimeSeriesAsync([], new DateTime(2023, 12, 31), new DateTime(2024, 1, 2));
        Directory.CreateDirectory(_output);
        Assert.Equal(2, await new JsonExportService().ExportTimeSeriesAsync(source, _output));
        var files = Directory.GetFiles(_output, "*.json");
        Assert.Equal(2, files.Length);
        var points = files.SelectMany(file => System.Text.Json.JsonSerializer.Deserialize<TimeSeriesFileDto>(File.ReadAllText(file))!.Points).ToList();
        Assert.Equal(48, points.Count);
        Assert.All(points, p => Assert.Equal(0.001m, p.Quantity));
    }

    public void Dispose()
    {
        _xellent.Dispose();
        if (Directory.Exists(_output)) Directory.Delete(_output, true);
    }

    [Fact]
    public async Task ExtractPreview_PairsAllSegments_AndMatchesPostedQuantitiesAndTimes()
    {
        Seed();
        var runner = new TimeSeriesRunner("ignored", _output, 3, _ts, _mp, new RecordingCustomerClient(),
            contextFactory: _xellent.CreateContext, log: _logs.Add);
        Assert.Equal(0, await runner.RunExtractAsync(new ExtractOptions
        {
            MeteringPointId = Mp, StartDate = new DateTime(2023, 12, 31), EndDate = new DateTime(2024, 1, 2),
            IncludeOutgoingPreview = true, DeliveryBoundary = new DateTimeOffset(StartUtc.AddHours(12))
        }));
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        var file = Assert.Single(Directory.GetFiles(_output, $"OutgoingPreview_{Mp}.json", SearchOption.AllDirectories));
        var preview = System.Text.Json.JsonSerializer.Deserialize<TimeSeriesPreviewFileDto>(File.ReadAllText(file))!;
        Assert.Null(preview.Error);
        Assert.Equal(48, preview.SourceCount);
        Assert.Equal(12, preview.ExcludedCount);
        Assert.Equal(108, preview.OutgoingCount);
        Assert.Equal(2, Directory.GetFiles(_output, "TimeSeries_*.json", SearchOption.AllDirectories).Length);

        Assert.Equal(0, await Run(boundary: new DateTimeOffset(StartUtc.AddHours(12))));
        Assert.Equal(_ts.PostCalls.SelectMany(c => c.Payload.Points).Select(p => (p.Time, p.Quantity)),
            preview.Points.SelectMany(p => p.Outgoing).Select(p => (p.Time.UtcDateTime, p.Quantity)));
    }
}
