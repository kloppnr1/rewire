using DataMigration.SettlApi.Models;
using TimeSeries.Cli.Runners;
using TimeSeries.Cli.Tests.Fixtures;

namespace TimeSeries.Cli.Tests.Runners;

/// <summary>
/// Dækker MP'er hvor Xellent har flere EMS_TIMESERIES-rows med forskellig Timeresolution
/// (typisk COMPANYID-skift Hour → Quarter ved arsskifte). Runner skal:
///   - opdage segmenterne via extraction-laget
///   - match per segment til eksisterende Settl-TS via (Length, Unit)
///   - fejle fail-fast når et segment ikke matcher en eksisterende Settl-TS
///     (begge TS forventes oprettet i Settl på forhånd)
///   - POSTe points + tilføje EIP per segment med korrekte periode-grænser
/// </summary>
public class ResolutionSegmentTests : IDisposable
{
    private const string MeteringPoint = "571313100000000099";
    private const string OutputDir = "test-output-segments";

    // Hour-segment: 2023-12-01 → 2024-01-01 (744 timer = 31 dage)
    // Quarter-segment: 2024-01-01 → 2024-01-02 (96 kvarter = 1 dag)
    private static readonly DateTime HourStart = new(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime QuarterStart = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowStart = HourStart;
    private static readonly DateTime WindowEnd = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const int HourCount = 744;
    private const int QuarterCount = 96;

    private readonly XellentTimeSeriesSeeder _xellent = new();
    private readonly RecordingTimeSeriesClient _ts = new();
    private readonly RecordingMeteringPointClient _mp = new();
    private readonly RecordingCustomerClient _cust = new();

    public ResolutionSegmentTests()
    {
        _xellent.SeedHourlyTimeSeries(MeteringPoint, HourStart, hours: HourCount);
        _xellent.SeedQuarterTimeSeries(MeteringPoint, QuarterStart, quarters: QuarterCount);
    }

    public void Dispose() => _xellent.Dispose();

    private TimeSeriesRunner BuildRunner() => new(
        connectionString: "ignored",
        outputBaseDirectory: OutputDir,
        defaultTimeSeriesYears: 2,
        settlTimeSeriesClient: _ts,
        settlMeteringPointClient: _mp,
        settlCustomerClient: _cust,
        contextFactory: _xellent.CreateContext,
        log: _ => { });

    private static PostOptions Opts(bool dryRun = false) => new()
    {
        MeteringPointId = MeteringPoint,
        StartDate = WindowStart,
        EndDate = WindowEnd,
        DeliveryCategory = "El-ekstern",
        Environment = "test",
        DryRun = dryRun,
    };

    [Fact]
    public async Task TwoSegments_WithMissingQuarterTs_FailsFast()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var hourTsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        // Kun Hour-TS findes i Settl — Quarter-segmentet matcher intet.
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto>
        {
            new() { Id = hourTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } }
        };

        var rc = await BuildRunner().RunPostAsync(Opts());

        // Fail-fast: ingen TS oprettes, og kørslen afbrydes.
        Assert.NotEqual(0, rc);
    }

    [Fact]
    public async Task TwoSegments_DryRun_DoesNotMutateAnything()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var hourTsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var quarterTsGuid = new Guid("33333333-3333-3333-3333-333333333333");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto>
        {
            new() { Id = hourTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new() { Id = quarterTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }
        };

        var rc = await BuildRunner().RunPostAsync(Opts(dryRun: true));

        Assert.Equal(0, rc);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task TwoSegments_WithSkipResolutionChange_SkipsMpEntirely()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var hourTsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var quarterTsGuid = new Guid("33333333-3333-3333-3333-333333333333");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto>
        {
            new() { Id = hourTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new() { Id = quarterTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }
        };

        var logs = new List<string>();
        var runner = new TimeSeriesRunner(
            connectionString: "ignored",
            outputBaseDirectory: OutputDir,
            defaultTimeSeriesYears: 2,
            settlTimeSeriesClient: _ts,
            settlMeteringPointClient: _mp,
            settlCustomerClient: _cust,
            contextFactory: _xellent.CreateContext,
            log: logs.Add);

        var opts = Opts();
        opts.SkipResolutionChange = true;
        var rc = await runner.RunPostAsync(opts);

        Assert.Equal(0, rc);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Contains(logs, l => l.StartsWith($"SKIP MP {MeteringPoint}: resolution change"));
    }

    [Fact]
    public async Task TwoSegments_WithBothTsInSettl_PostsBothSegments()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var hourTsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var quarterTsGuid = new Guid("33333333-3333-3333-3333-333333333333");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto>
        {
            new() { Id = hourTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new() { Id = quarterTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }
        };

        var rc = await BuildRunner().RunPostAsync(Opts());

        Assert.Equal(0, rc);
        Assert.Equal(2, _ts.PostCalls.Count);
        Assert.Equal(HourCount, _ts.PostCalls.Single(c => c.TsGuid == hourTsGuid).Payload.Points.Count);
        Assert.Equal(QuarterCount, _ts.PostCalls.Single(c => c.TsGuid == quarterTsGuid).Payload.Points.Count);
        Assert.Equal(2, _mp.AddEipCalls.Count);
    }

    [Fact]
    public async Task ReadingPeriods_DecideTheSplit_AndCutTheHourSeriesWhereQuarterReadingStarts()
    {
        // Xellent's reading periods say hourly up to 2023-12-31 and quarter-hourly from 2024-01-01.
        // A second hourly series runs on past the switch; its points after it must not be posted.
        const string mp = "571313100000000098";
        var mpGuid = Guid.NewGuid();
        var hourTsGuid = Guid.NewGuid();
        var quarterTsGuid = Guid.NewGuid();
        _xellent.SeedHourlyTimeSeries(mp, new DateTime(2023, 12, 30, 23, 0, 0, DateTimeKind.Utc), hours: 48, companyId: "c1");
        _xellent.SeedQuarterTimeSeries(mp, new DateTime(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "c1");
        _xellent.SeedReadingPeriod(mp, "c1", new DateTime(2000, 1, 1), new DateTime(2023, 12, 31), seriesResolution: 0);
        _xellent.SeedReadingPeriod(mp, "c1", new DateTime(2024, 1, 1), new DateTime(1900, 1, 1), seriesResolution: 1);
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = mp };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto>
        {
            new() { Id = hourTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new() { Id = quarterTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }
        };

        var options = Opts();
        options.MeteringPointId = mp;
        options.StartDate = new DateTime(2023, 12, 31);
        options.EndDate = new DateTime(2024, 1, 2);
        var rc = await BuildRunner().RunPostAsync(options);

        Assert.Equal(0, rc);
        // Hourly points only before 2024-01-01 00:00 Danish time (= 2023-12-31 23:00 UTC).
        var hourPoints = _ts.PostCalls.Single(c => c.TsGuid == hourTsGuid).Payload.Points;
        Assert.Equal(24, hourPoints.Count);
        Assert.True(hourPoints.All(p => p.Time < new DateTime(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(96, _ts.PostCalls.Single(c => c.TsGuid == quarterTsGuid).Payload.Points.Count);
        var periods = _mp.AddEipCalls.SelectMany(c => c.Request.Periods).ToList();
        var hourEip = Assert.Single(periods, p => p.TimeSeriesId == hourTsGuid);
        var quarterEip = Assert.Single(periods, p => p.TimeSeriesId == quarterTsGuid);
        Assert.Equal((new DateOnly(2023, 12, 31), new DateOnly(2024, 1, 1)), (hourEip.Period!.Start, hourEip.Period.End));
        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2)), (quarterEip.Period!.Start, quarterEip.Period.End));
    }

    [Fact]
    public async Task TwoResolutionsUnderOneAgreement_SplitTheEipPeriodWhereTheQuarterSeriesStarts()
    {
        // Both seeded series belong to the same company, so they share one open agreement. The
        // agreement cannot separate them; the Quarter series' first point (2024-01-01) does.
        var mpGuid = Guid.NewGuid();
        var hourTsGuid = Guid.NewGuid();
        var quarterTsGuid = Guid.NewGuid();
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto>
        {
            new() { Id = hourTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new() { Id = quarterTsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }
        };

        var rc = await BuildRunner().RunPostAsync(Opts());

        Assert.Equal(0, rc);
        var hourEip = Assert.Single(_mp.AddEipCalls.SelectMany(c => c.Request.Periods), p => p.TimeSeriesId == hourTsGuid);
        var quarterEip = Assert.Single(_mp.AddEipCalls.SelectMany(c => c.Request.Periods), p => p.TimeSeriesId == quarterTsGuid);
        Assert.Equal((new DateOnly(2023, 12, 1), new DateOnly(2024, 1, 1)), (hourEip.Period!.Start, hourEip.Period.End));
        Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 2)), (quarterEip.Period!.Start, quarterEip.Period.End));
    }

    [Fact]
    public async Task DeliveryBoundary_RemovesEmptyLeadingSegment_AndPostsOnlySurvivingResolution()
    {
        var mpGuid = Guid.NewGuid();
        var quarterTsGuid = Guid.NewGuid();
        var boundary = new DateTimeOffset(QuarterStart);
        var revision = boundary.AddDays(1);
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses =
                [
                    new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto { Start = revision } },
                    new DeliveryStatusDto
                    {
                        InDelivery = false,
                        Period = new TimePeriodDto { Start = boundary, End = revision }
                    }
                ]
            },
            null);
        _mp.GetTimeSeriesResponse = _ =>
        [
            new TimeSeriesInfoDto
            {
                Id = quarterTsGuid,
                TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" }
            }
        ];

        var rc = await BuildRunner().RunPostAsync(Opts());

        Assert.Equal(0, rc);
        var post = Assert.Single(_ts.PostCalls);
        Assert.Equal(quarterTsGuid, post.TsGuid);
        Assert.Equal(QuarterCount, post.Payload.Points.Count);
        Assert.Equal(boundary.UtcDateTime, post.Payload.Points[0].Time);
        var eip = Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods);
        Assert.Equal(new DateOnly(2024, 1, 1), eip.Period!.Start);
    }
}
