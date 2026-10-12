using DataMigration.SettlApi.Models;
using TimeSeries.Cli.Runners;
using TimeSeries.Cli.Tests.Fixtures;

namespace TimeSeries.Cli.Tests.Runners;

public class PostRunnerTests : IDisposable
{
    private const string MeteringPoint = "571313100000000001";
    private const string OutputDir = "test-output";
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly XellentTimeSeriesSeeder _xellent = new();
    private readonly RecordingTimeSeriesClient _ts = new();
    private readonly RecordingMeteringPointClient _mp = new();
    private readonly RecordingCustomerClient _cust = new();

    public PostRunnerTests()
    {
        _xellent.SeedHourlyTimeSeries(MeteringPoint, Start, hours: 24);
    }

    public void Dispose() => _xellent.Dispose();

    private TimeSeriesRunner BuildRunner(Action<string>? log = null, string? outputDirectory = null) => new(
        connectionString: "ignored",
        outputBaseDirectory: outputDirectory ?? OutputDir,
        defaultTimeSeriesYears: 2,
        settlTimeSeriesClient: _ts,
        settlMeteringPointClient: _mp,
        settlCustomerClient: _cust,
        contextFactory: _xellent.CreateContext,
        log: log ?? (_ => { }));

    private static PostOptions OptionsFor(string mp, DateTime start, DateTime end, bool dryRun = false) => new()
    {
        MeteringPointId = mp,
        StartDate = start,
        EndDate = end,
        DeliveryCategory = "El-ekstern",
        Environment = "test",
        DryRun = dryRun,
    };

    [Fact]
    public async Task RunPostAsync_HappyPath_PostsPointsAndAddsExternallyInvoicedPeriod()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto> { new() { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } } };

        var rc = await BuildRunner().RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(0, rc);
        PostSnapshot.AssertMatchesGolden(PostSnapshot.From(_ts, _mp), "expected-posts-happy-path.json");
    }

    [Fact]
    public async Task RunPostAsync_ResolvesExactRevisionForPointsAndEip_IndependentOfWindowEnd()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var revision = new DateTimeOffset(2023, 6, 15, 12, 34, 56, TimeSpan.FromHours(2));
        var logs = new List<string>();
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses =
                [
                    new DeliveryStatusDto
                    {
                        InDelivery = true,
                        Period = new TimePeriodDto { Start = revision }
                    }
                ]
            },
            null);
        _mp.GetTimeSeriesResponse = _ =>
            [new TimeSeriesInfoDto { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } }];

        var unrelatedWindowEnd = new DateTime(2035, 11, 9, 0, 0, 0, DateTimeKind.Utc);
        var rc = await BuildRunner(logs.Add).RunPostAsync(OptionsFor(MeteringPoint, Start, unrelatedWindowEnd));

        Assert.Equal(0, rc);
        Assert.Single(_mp.GetDeliveryStatusesCalls);
        Assert.Equal(revision, Assert.Single(_ts.PostCalls).Payload.Revision);
        Assert.Equal(revision, Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods).Revision);
        Assert.Equal($"REVISION MP {MeteringPoint}: {revision:O}", Assert.Single(logs, l => l.StartsWith("REVISION MP ")));
    }

    [Fact]
    public async Task RunPostAsync_DeliveryBoundary_ExcludesEarlierPointsAndKeepsPointAtBoundary()
    {
        var mpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        var revision = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var boundary = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var logs = new List<string>();
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
                        Period = new TimePeriodDto { Start = boundary.AddYears(-1), End = revision.AddDays(-1) }
                    },
                    new DeliveryStatusDto
                    {
                        InDelivery = false,
                        Period = new TimePeriodDto { Start = boundary, End = revision }
                    }
                ]
            },
            null);
        _mp.GetTimeSeriesResponse = _ =>
            [new TimeSeriesInfoDto { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } }];

        var rc = await BuildRunner(logs.Add).RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(0, rc);
        var posted = Assert.Single(_ts.PostCalls).Payload.Points;
        Assert.Equal(12, posted.Count);
        Assert.Equal(boundary.UtcDateTime, posted[0].Time);
        Assert.Contains(
            $"DELIVERY FILTER MP {MeteringPoint}: boundary={boundary:O}, excluded=12, included=12",
            logs);
        Assert.Contains(
            $"RESULT MP {MeteringPoint}: points=12, excluded=12, boundary={boundary:O}",
            logs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunPostAsync_UnresolvedOrAmbiguousDeliveryBoundary_SafetySkips(bool multipleMatches)
    {
        var mpGuid = Guid.NewGuid();
        var revision = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var matchingEnd = multipleMatches ? revision : revision.AddDays(-1);
        var statuses = new List<DeliveryStatusDto>
        {
            new() { InDelivery = true, Period = new TimePeriodDto { Start = revision } },
            new()
            {
                InDelivery = false,
                Period = new TimePeriodDto { Start = revision.AddYears(-1), End = matchingEnd }
            }
        };
        if (multipleMatches)
        {
            statuses.Add(new DeliveryStatusDto
            {
                InDelivery = false,
                Period = new TimePeriodDto { Start = revision.AddYears(-2), End = revision }
            });
        }

        var logs = new List<string>();
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetDeliveryStatusesResponse = _ => (new GetDeliveryStatusesResponse { DeliveryStatuses = statuses }, null);

        var rc = await BuildRunner(logs.Add).RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(0, rc);
        Assert.Empty(_mp.GetTimeSeriesCalls);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Contains(
            logs,
            line => line.StartsWith($"SKIP MP {MeteringPoint}: ", StringComparison.Ordinal) &&
                    line.Contains(multipleMatches ? "ambiguous delivery boundary" : "delivery boundary unresolved"));
    }

    [Fact]
    public async Task RunPostAsync_AllPointsBeforeDeliveryBoundary_SafetySkipsWithExcludedCount()
    {
        var mpGuid = Guid.NewGuid();
        var revision = new DateTimeOffset(2024, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var boundary = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var logs = new List<string>();
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

        var rc = await BuildRunner(logs.Add).RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(0, rc);
        Assert.Empty(_mp.GetTimeSeriesCalls);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Contains(
            $"DELIVERY FILTER MP {MeteringPoint}: boundary={boundary:O}, excluded=24, included=0",
            logs);
        Assert.Contains($"SKIP MP {MeteringPoint}: no points on or after delivery boundary ({boundary:O})", logs);
    }

    [Theory]
    [InlineData("skip-delivery-boundary-unresolved.csv", false)]
    [InlineData("skip-no-points-after-boundary.csv", true)]
    public async Task RunPostAsync_BoundarySkip_WritesRowToItsOwnCategoryCsv(string expectedFile, bool boundaryMatches)
    {
        var mpGuid = new Guid("33333333-3333-3333-3333-333333333333");
        var revision = new DateTimeOffset(2024, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var boundary = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        // A false period that ends at the revision yields a usable boundary (every seeded point
        // then falls before it); one that ends elsewhere leaves the boundary unresolved.
        var falseEnd = boundaryMatches ? revision : revision.AddDays(-5);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "timeseries-skip-tests", Guid.NewGuid().ToString("N"));
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
                        Period = new TimePeriodDto { Start = boundary, End = falseEnd }
                    }
                ]
            },
            null);

        try
        {
            var options = OptionsFor(MeteringPoint, Start, End);
            options.CustomerNumber = "4711";
            Assert.Equal(0, await BuildRunner(outputDirectory: outputDirectory).RunPostAsync(options));

            var csv = File.ReadAllLines(Path.Combine(outputDirectory, expectedFile));
            Assert.Equal(2, csv.Length);
            Assert.StartsWith(
                "\"CustomerNumber\";\"MeteringPointIdentification\";\"Type\";\"SettlMeteringPointId\";\"Revision\";\"DeliveryBoundary\";\"Detail\"",
                csv[0]);
            Assert.Contains($"\"4711\";\"{MeteringPoint}\";\"\";\"{mpGuid}\"", csv[1]);
            Assert.Contains(revision.ToString("O"), csv[1]);

            // Categories that did not fire must leave no file behind — their absence is the signal.
            Assert.False(File.Exists(Path.Combine(outputDirectory, "skip-existing-settl-data.csv")));
            Assert.False(File.Exists(Path.Combine(outputDirectory, TimeSeriesRunner.AmbiguousRevisionCsvFileName)));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunPostAsync_ResetClearsStaleSkipCsvsFromEarlierRun()
    {
        var mpGuid = new Guid("44444444-4444-4444-4444-444444444444");
        var revision = new DateTimeOffset(2024, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "timeseries-skip-tests", Guid.NewGuid().ToString("N"));
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
                        Period = new TimePeriodDto { Start = revision.AddYears(-1), End = revision.AddDays(-5) }
                    }
                ]
            },
            null);

        try
        {
            var runner = BuildRunner(outputDirectory: outputDirectory);
            Assert.Equal(0, await runner.RunPostAsync(OptionsFor(MeteringPoint, Start, End)));

            var path = Path.Combine(outputDirectory, "skip-delivery-boundary-unresolved.csv");
            Assert.Equal(2, File.ReadAllLines(path).Length);

            // A clean second run must not inherit the first run's rows.
            _mp.GetDeliveryStatusesResponse = _ => (
                new GetDeliveryStatusesResponse
                {
                    DeliveryStatuses =
                    [
                        new DeliveryStatusDto { InDelivery = true, Period = new TimePeriodDto { Start = revision } }
                    ]
                },
                null);
            Assert.Equal(0, await runner.RunPostAsync(OptionsFor(MeteringPoint, Start, End)));

            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunPostAsync_InvalidMatchingBoundaryPeriod_SafetySkips(bool defaultStart)    {
        var mpGuid = Guid.NewGuid();
        var revision = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var invalidStart = defaultStart ? default : revision.AddHours(1);
        var logs = new List<string>();
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
                        Period = new TimePeriodDto { Start = invalidStart, End = revision }
                    }
                ]
            },
            null);

        var rc = await BuildRunner(logs.Add).RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(0, rc);
        Assert.Empty(_mp.GetTimeSeriesCalls);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Contains(
            logs,
            line => line.StartsWith($"SKIP MP {MeteringPoint}: invalid delivery boundary period", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPostAsync_BoundaryUsesSameInstantAndDanishDateForOverlapAndEip()
    {
        var mpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        var windowStart = new DateTime(2024, 5, 31, 0, 0, 0, DateTimeKind.Unspecified);
        var windowEnd = new DateTime(2024, 6, 2, 0, 0, 0, DateTimeKind.Unspecified);
        var boundary = new DateTimeOffset(2024, 5, 31, 22, 0, 0, TimeSpan.Zero);
        var revision = new DateTimeOffset(2024, 6, 5, 22, 0, 0, TimeSpan.Zero);
        _xellent.SeedHourlyTimeSeries(
            MeteringPoint,
            new DateTime(2024, 5, 31, 20, 0, 0, DateTimeKind.Utc),
            hours: 6);
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
                        Period = new TimePeriodDto
                        {
                            Start = boundary,
                            End = revision.ToOffset(TimeSpan.FromHours(2))
                        }
                    }
                ]
            },
            null);
        _mp.GetTimeSeriesResponse = _ =>
            [new TimeSeriesInfoDto { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } }];

        var options = OptionsFor(MeteringPoint, windowStart, windowEnd);
        options.SkipIfExistingPoints = true;
        var rc = await BuildRunner().RunPostAsync(options);

        Assert.Equal(0, rc);
        Assert.Equal(new DateOnly(2024, 6, 1), Assert.Single(_ts.GetPointsCalls).Start);
        var eip = Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods);
        Assert.Equal(new DateOnly(2024, 6, 1), eip.Period!.Start);
        Assert.Equal(boundary.UtcDateTime, Assert.Single(_ts.PostCalls).Payload.Points[0].Time);
    }

    [Fact]
    public async Task RunPostAsync_CanAccumulateAmbiguitiesAcrossOneSelectiveRun()
    {
        const string secondMp = "571313100000000002";
        var firstGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var secondGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "timeseries-ambiguity-tests", Guid.NewGuid().ToString("N"));
        _xellent.SeedHourlyTimeSeries(secondMp, Start, hours: 24);
        _mp.SearchByIdentificationResponse = id => new MeteringPointInfoDto
        {
            Id = id == MeteringPoint ? firstGuid : secondGuid,
            MeteringPointIdentification = id
        };
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses =
                [
                    new DeliveryStatusDto
                    {
                        InDelivery = true,
                        Period = new TimePeriodDto { Start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) }
                    },
                    new DeliveryStatusDto
                    {
                        InDelivery = true,
                        Period = new TimePeriodDto { Start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) }
                    }
                ]
            },
            null);

        try
        {
            var runner = BuildRunner(outputDirectory: outputDirectory);
            var firstOptions = OptionsFor(MeteringPoint, Start, End);
            firstOptions.CustomerNumber = "100";
            var secondOptions = OptionsFor(secondMp, Start, End);
            secondOptions.CustomerNumber = "200";
            secondOptions.ResetAmbiguityCsv = false;

            Assert.Equal(0, await runner.RunPostAsync(firstOptions));
            Assert.Equal(0, await runner.RunPostAsync(secondOptions));

            var csv = File.ReadAllLines(Path.Combine(outputDirectory, TimeSeriesRunner.AmbiguousRevisionCsvFileName));
            Assert.Equal(3, csv.Length);
            Assert.Contains("\"100\";\"571313100000000001\"", csv[1]);
            Assert.Contains("\"200\";\"571313100000000002\"", csv[2]);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunPostAsync_DryRun_LooksUpButPostsNothing()
    {
        var rc = await BuildRunner().RunPostAsync(OptionsFor(MeteringPoint, Start, End, dryRun: true));

        Assert.Equal(0, rc);
        Assert.Single(_mp.SearchByIdentificationCalls);
        Assert.Single(_mp.GetTimeSeriesCalls);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAsync_TimeSeriesPostFails_AbortsBeforeEipPost()
    {
        _ts.PostResponse = (_, _) => (false, "boom");

        var rc = await BuildRunner().RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(1, rc);
        Assert.Single(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAsync_SkipResolutionChange_SingleResolution_PostsAsNormal()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto> { new() { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } } };

        var opts = OptionsFor(MeteringPoint, Start, End);
        opts.SkipResolutionChange = true;
        var rc = await BuildRunner().RunPostAsync(opts);

        Assert.Equal(0, rc);
        Assert.Single(_ts.PostCalls);
        Assert.Single(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAsync_SkipIfExistingPoints_ExistingData_SkipsMpEntirely()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto> { new() { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } } };
        _ts.GetPointsResponse = (_, _, _) => (new SettlPointsSummaryDto { Count = 5, From = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(2024, 1, 1, 23, 0, 0, TimeSpan.Zero) }, null);

        var opts = OptionsFor(MeteringPoint, Start, End);
        opts.SkipIfExistingPoints = true;
        var rc = await BuildRunner().RunPostAsync(opts);

        Assert.Equal(0, rc);
        Assert.Single(_ts.GetPointsCalls);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAsync_SkipIfExistingPoints_NoExistingData_PostsAsNormal()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto> { new() { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } } };
        // Default GetPointsResponse returns Count=0 — POST should proceed.

        var opts = OptionsFor(MeteringPoint, Start, End);
        opts.SkipIfExistingPoints = true;
        var rc = await BuildRunner().RunPostAsync(opts);

        Assert.Equal(0, rc);
        Assert.Single(_ts.GetPointsCalls);
        Assert.Single(_ts.PostCalls);
        Assert.Single(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAsync_SkipIfExistingPoints_FlagOff_DoesNotCallPointsSummary()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        _mp.SearchByIdentificationResponse = _ => new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MeteringPoint };
        _mp.GetTimeSeriesResponse = _ => new List<TimeSeriesInfoDto> { new() { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } } };

        var rc = await BuildRunner().RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(0, rc);
        Assert.Empty(_ts.GetPointsCalls);
        Assert.Single(_ts.PostCalls);
    }

    [Fact]
    public async Task RunPostAsync_EipPostFails_ReturnsError()
    {
        _mp.AddEipResponse = (_, _) => (false, "eip rejected");

        var rc = await BuildRunner().RunPostAsync(OptionsFor(MeteringPoint, Start, End));

        Assert.Equal(1, rc);
        Assert.Single(_ts.PostCalls);
        Assert.Single(_mp.AddEipCalls);
    }
}
