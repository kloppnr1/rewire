using DataMigration.SettlApi.Models;
using System.Text.Json;
using TimeSeries.Cli.Runners;
using TimeSeries.Cli.Tests.Fixtures;

namespace TimeSeries.Cli.Tests.Runners;

// Batch mode takes its metering points from Settl's list and finds each one's account in Xellent:
// whoever held it the day before delivery starts. The Settl customer is never looked up.
public class PostAllRunnerTests : IDisposable
{
    // One folder per test: live runs mark posted metering points done in it, and a shared folder would
    // carry those marks into the next test.
    private readonly string OutputDir = Path.Combine("test-output-batch", Guid.NewGuid().ToString("N"));
    private const string Account = "313809";
    private const string MpId1 = "571313100000000001";
    private const string MpId2 = "571313100000000002";

    private readonly XellentTimeSeriesSeeder _xellent = new();
    private readonly RecordingTimeSeriesClient _ts = new();
    private readonly RecordingMeteringPointClient _mp = new();
    private readonly RecordingCustomerClient _cust = new();
    private readonly RecordingPayerClient _payers = new();

    public void Dispose()
    {
        _xellent.Dispose();
        if (Directory.Exists(OutputDir))
            Directory.Delete(OutputDir, recursive: true);
    }

    private TimeSeriesRunner BuildRunner(string? outputDirectory = null, Action<string>? log = null) => new(
        connectionString: "ignored",
        outputBaseDirectory: outputDirectory ?? OutputDir,
        defaultTimeSeriesYears: 2,
        settlTimeSeriesClient: _ts,
        settlMeteringPointClient: _mp,
        settlCustomerClient: _cust,
        contextFactory: _xellent.CreateContext,
        log: log ?? (_ => { }),
        settlPayerClient: _payers);

    private static readonly TimeZoneInfo Danish =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    private static DateTimeOffset DanishMidnight(DateTime danishDate) => new(
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(danishDate.Date, DateTimeKind.Unspecified), Danish), TimeSpan.Zero);

    // Batch mode takes the window end from the single InDelivery=true delivery status. Tests that
    // mean "supply starts on this Danish date" state it here.
    private void InDeliveryFrom(DateTime danishDate)
    {
        var start = DanishMidnight(danishDate);
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses = [new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = start } }]
            },
            null);
    }

    // Settl lists these metering points as in delivery.
    private void InSettl(params (string Identification, Guid Id)[] meteringPoints) =>
        _mp.MeteringPoints = meteringPoints
            .Select(m => new MeteringPointInfoDto { Id = m.Id, MeteringPointIdentification = m.Identification, InDelivery = true })
            .ToList();

    private static List<TimeSeriesInfoDto> HourTs(Guid tsGuid) =>
        [new TimeSeriesInfoDto { Id = tsGuid, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } }];

    /// <summary>Settl has both series for every metering point, so nothing is missing.</summary>
    private void BothSeriesInSettl() =>
        _mp.GetTimeSeriesResponse = _ =>
        [
            new TimeSeriesInfoDto { Id = Guid.NewGuid(), TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new TimeSeriesInfoDto { Id = Guid.NewGuid(), TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } },
        ];

    [Fact]
    public async Task MissingReadingsScan_ChecksHistoricalWindowWithoutPostingOrChangingDoneMarks()
    {
        var end = new DateTime(2025, 6, 15);
        var heldSince = end.AddMonths(-2);
        var mpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        InSettl((MpId1, mpGuid));
        InDeliveryFrom(end);
        _xellent.SeedContract(Account, MpId1, partStart: heldSince, migratedOn: end);
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);
        var donePath = Path.Combine(OutputDir, "snapshot", "test", "done.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(donePath)!);
        const string priorDone = "preserve prior done evidence";
        File.WriteAllText(donePath, priorDone);
        var skipPath = Path.Combine(OutputDir, "skip-not-migrated.csv");
        File.WriteAllText(skipPath, "preserve prior run");

        var result = await BuildRunner().ScanMissingReadingsAsync("test");

        Assert.Equal(0, result.ExitCode);
        var entry = Assert.Single(result.Entries);
        Assert.Equal("Missing", entry.Status);
        Assert.Equal(Account, entry.Account);
        Assert.Equal(DateOnly.FromDateTime(heldSince), entry.WindowStart);
        Assert.Equal(DateOnly.FromDateTime(end), entry.WindowEnd);
        var query = Assert.Single(_ts.GetPointsCalls);
        Assert.Equal(entry.WindowStart, query.Start);
        Assert.Equal(entry.WindowEnd, query.End);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Equal(priorDone, File.ReadAllText(donePath));
        Assert.Equal("preserve prior run", File.ReadAllText(skipPath));
    }

    [Fact]
    public async Task MissingReadingsScan_FindsReadingsInSecondSeriesAndStopsAtFirstPositiveChunk()
    {
        var end = new DateTime(2025, 6, 15);
        var hour = Guid.NewGuid();
        var quarter = Guid.NewGuid();
        InSettl((MpId1, Guid.NewGuid()));
        InDeliveryFrom(end);
        _xellent.SeedContract(Account, MpId1, migratedOn: end);
        _mp.GetTimeSeriesResponse = _ => [
            new() { Id = hour, TimeResolution = new() { Length = 1, Unit = "Hour" } },
            new() { Id = quarter, TimeResolution = new() { Length = 15, Unit = "Minutes" } }];
        _ts.GetPointsResponse = (id, _, _) => (new() { Count = id == quarter ? 1 : 0 }, null);

        var result = await BuildRunner().ScanMissingReadingsAsync("test");

        Assert.Equal("Present", Assert.Single(result.Entries).Status);
        Assert.Single(_ts.GetPointsCalls, c => c.TsGuid == quarter);
        Assert.All(_ts.GetPointsCalls, c => Assert.True(c.End <= c.Start.AddYears(1)));
        Assert.Empty(_ts.PostCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingReadingsScan_DoesNotCallFailedOrAbsentSummaryMissing(bool failed)
    {
        var end = new DateTime(2025, 6, 15);
        InSettl((MpId1, Guid.NewGuid()));
        InDeliveryFrom(end);
        _xellent.SeedContract(Account, MpId1, migratedOn: end);
        _ts.GetPointsResponse = (_, _, _) => (null, failed ? "Read failed" : null);

        var result = await BuildRunner().ScanMissingReadingsAsync("test");

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Unknown", Assert.Single(result.Entries).Status);
        Assert.NotNull(result.Entries[0].Detail);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task MissingReadingsScan_StopsOnConnectionFailure()
    {
        var end = new DateTime(2025, 6, 15);
        InSettl((MpId1, Guid.NewGuid()));
        InDeliveryFrom(end);
        _xellent.SeedContract(Account, MpId1, migratedOn: end);
        _ts.GetPointsResponse = (_, _, _) => (null, "No such host is known");

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildRunner().ScanMissingReadingsAsync("test"));

        Assert.Single(_ts.GetPointsCalls);
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task MissingReadingsScan_KeepsAmbiguousOwnershipUnknown()
    {
        var end = new DateTime(2025, 6, 15);
        InSettl((MpId1, Guid.NewGuid()));
        InDeliveryFrom(end);
        _xellent.SeedContract(Account, MpId1, migratedOn: end);
        _xellent.SeedContract("other-account", MpId1, migratedOn: end);

        var result = await BuildRunner().ScanMissingReadingsAsync("test");

        Assert.Equal("Unknown", Assert.Single(result.Entries).Status);
        Assert.Contains("account holders", result.Entries[0].Detail);
        Assert.Empty(_ts.GetPointsCalls);
    }

    [Fact]
    public async Task RunPostAllAsync_DerivesWindowFromResolvedRevisionInDanishTime()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var revision = new DateTimeOffset(2023, 6, 14, 22, 0, 0, TimeSpan.Zero);
        var expectedEnd = new DateTime(2023, 6, 15);
        var expectedStart = expectedEnd.AddYears(-3);
        var logs = new List<string>();

        _xellent.SeedHourlyTimeSeries(MpId1, expectedStart, hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: new DateTime(2023, 6, 15));
        InSettl((MpId1, mpGuid));
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses = [new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = revision } }]
            },
            null);
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Contains($"  MP {MpId1}: end=2023-06-15  start=2020-06-15", logs);
        PostSnapshot.AssertMatchesGolden(PostSnapshot.From(_ts, _mp), "expected-posts-batch-window.json");
    }

    [Fact]
    public async Task RunPostAllAsync_UsesExplicitWindowOverrideInsteadOfRevision()
    {
        var mpGuid = Guid.NewGuid();
        var overrideEnd = new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        _xellent.SeedHourlyTimeSeries(MpId1, overrideEnd.AddYears(-3), hours: 24);
        _xellent.SeedContract(Account, MpId1);
        InSettl((MpId1, mpGuid));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false,
            continueOnError: false,
            windowEndDateOverride: overrideEnd);

        Assert.Equal(0, rc);
        Assert.Single(_ts.PostCalls);
        Assert.Single(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAllAsync_NeverLooksUpTheSettlCustomer()
    {
        // A merged (non-representative) account is not a customer in Settl, but its MP is.
        var mpGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, mpGuid));
        _cust.SearchByCustomerNumberResponse = _ => null;

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Empty(_cust.SearchByCustomerNumberCalls);
        Assert.Equal(mpGuid, Assert.Single(_mp.AddEipCalls).MpGuid);
    }

    // Children are never in scope: a child known from the Xellent relation table is left out.
    [Fact]
    public async Task RunPostAllAsync_NeverPostsChildMeteringPoints()
    {
        var parentGuid = Guid.NewGuid();
        var childGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        InDeliveryFrom(supplyStart);

        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract(Account, MpId2, migratedOn: supplyStart);
        _xellent.SeedChildRelation(MpId1, MpId2);
        InSettl((MpId1, parentGuid), (MpId2, childGuid));
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Single(_mp.AddEipCalls);
        Assert.Equal(parentGuid, _mp.AddEipCalls[0].MpGuid);
        Assert.DoesNotContain(childGuid, _mp.GetDeliveryStatusesCalls);
        Assert.Contains(logs, l => l.EndsWith("1 children skipped.", StringComparison.Ordinal));
    }

    // Children are never in scope: a child Settl relates to its parent is left out too.
    [Fact]
    public async Task RunPostAllAsync_NeverPostsSettlChildMeteringPoints()
    {
        var parentGuid = Guid.NewGuid();
        var childGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _mp.MeteringPoints =
        [
            new MeteringPointInfoDto { Id = parentGuid, MeteringPointIdentification = MpId1, InDelivery = true },
            new MeteringPointInfoDto { Id = childGuid, MeteringPointIdentification = MpId2, InDelivery = true,
                ParentMeteringPoint = new ResLinkDto { Id = parentGuid } },
        ];
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Equal(parentGuid, Assert.Single(_mp.AddEipCalls).MpGuid);
        Assert.DoesNotContain(childGuid, _mp.GetDeliveryStatusesCalls);
        Assert.Contains(logs, l => l.EndsWith("1 children skipped.", StringComparison.Ordinal));
    }

    // Settl bug (2026-09): a migrated metering point can be shown as not in delivery everywhere — the list,
    // the delivery-status history (one open InDelivery=false period) and the metering point itself — with
    // only the current delivery status's start telling when delivery began (featuretest MP
    // 571313103103321037: InDelivery=false from 2023-12-31T23:00Z, reason Na).
    private void NotInDeliveryInSettl(Guid mpGuid, DateTime? currentDeliveryStart)
    {
        _mp.MeteringPoints = [new MeteringPointInfoDto { Id = mpGuid, MeteringPointIdentification = MpId1, InDelivery = false }];
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses = [new DeliveryStatusDto { InDelivery = false, Period = new TimePeriodDto { Start = DanishMidnight(new DateTime(2020, 1, 1)) } }]
            },
            null);
        _mp.GetByIdV4Response = _ => new MeteringPointV4Dto
        {
            DeliveryStatus = currentDeliveryStart is { } start
                ? new MeteringPointV4DeliveryStatusDto { Start = DanishMidnight(start).UtcDateTime, InDelivery = false, StartReason = "Na" }
                : null
        };
    }

    [Fact]
    public async Task RunPostAllAsync_NotInDeliveryButMigrated_IsPostedFromTheCurrentDeliveryStatus()
    {
        var mpGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2024, 1, 1);
        NotInDeliveryInSettl(mpGuid, supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24);
        _xellent.SeedContract(Account, MpId1, partStart: new DateTime(2022, 1, 1), migratedOn: supplyStart);
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.True(rc == 0, string.Join(Environment.NewLine, logs.Where(l => l.Contains("ERROR"))));
        Assert.Contains($"  MP {MpId1}: end=2024-01-01  start=2022-01-01", logs);
        Assert.Equal(mpGuid, Assert.Single(_mp.AddEipCalls).MpGuid);
    }

    [Fact]
    public async Task Snapshot_SeedThenOfflineRun_PostsTheSameWithoutReadingSettl()
    {
        var outputDirectory = Path.Combine(OutputDir, Guid.NewGuid().ToString("N"));
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());

        try
        {
            Assert.Equal(0, await BuildRunner(outputDirectory).RunSeedSnapshotAsync("test"));
            var (lists, statuses, current, series) =
                (_mp.FindMeteringPointsCalls, _mp.GetDeliveryStatusesCalls.Count, _mp.GetByIdV4Calls.Count, _mp.GetTimeSeriesCalls.Count);
            Assert.True(lists > 0 && statuses == 1 && current == 1 && series == 1);

            var logs = new List<string>();
            var rc = await BuildRunner(outputDirectory, logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false, offline: true);

            Assert.Equal(0, rc);
            Assert.Single(_ts.PostCalls);
            Assert.Single(_mp.AddEipCalls);
            // Nothing was read from Settl a second time.
            Assert.Equal((lists, statuses, current, series),
                (_mp.FindMeteringPointsCalls, _mp.GetDeliveryStatusesCalls.Count, _mp.GetByIdV4Calls.Count, _mp.GetTimeSeriesCalls.Count));
            Assert.Contains(logs, l => l.StartsWith("Snapshot: ", StringComparison.Ordinal) && l.EndsWith(" 0 read live.", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Done_SeedMarksWhatSettlAlreadyHolds_AndRunsLeaveItOut()
    {
        // MP1's series already has points in Settl, MP2's has none.
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, migratedOn: supplyStart);
        var (mp1Guid, mp2Guid, ts1, ts2) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InSettl((MpId1, mp1Guid), (MpId2, mp2Guid));
        _mp.GetTimeSeriesResponse = id => HourTs(id == mp1Guid ? ts1 : ts2);
        _ts.GetPointsResponse = (ts, _, _) => ts == ts1
            ? (new SettlPointsSummaryDto { Count = 24 }, null)
            : (new SettlPointsSummaryDto { Count = 0 }, null);
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test"));
        var doneLines = DoneMarkLines();
        Assert.Single(doneLines);
        Assert.Contains(MpId1, doneLines[0]);
        // The done check writes no skip files, and stops at MP1's first year with points: one call for
        // MP1, every year for MP2.
        Assert.Empty(Directory.GetFiles(OutputDir, "*.csv"));
        Assert.Single(_ts.GetPointsCalls, c => c.TsGuid == ts1);
        var summaryCalls = _ts.GetPointsCalls.Count;

        // A second seed reads the answers from the snapshot.
        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test"));
        Assert.Equal(summaryCalls, _ts.GetPointsCalls.Count);

        // A live run posts only MP2; MP1 is left out from the start. The post marks MP2 done.
        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));
        Assert.Equal(mp2Guid, Assert.Single(_mp.AddEipCalls).MpGuid);
        Assert.Contains(logs, l => l.StartsWith("Already done: 1 metering point(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.Contains($"MP {MpId1} ---", StringComparison.Ordinal));
        Assert.Equal(2, DoneMarkLines().Length);
        Assert.False(File.Exists(Path.Combine(OutputDir, "snapshot", "test", "done.jsonl")));
    }

    // The done marks are the ledger's "meteringPoint" lines.
    private string[] DoneMarkLines()
    {
        var ledger = Path.Combine(OutputDir, "snapshot", "test", TimeSeries.Cli.Services.PostLedger.FileName);
        return File.Exists(ledger)
            ? File.ReadAllLines(ledger).Where(l => l.Contains("\"kind\":\"meteringPoint\"", StringComparison.Ordinal)).ToArray()
            : [];
    }

    [Fact]
    public async Task Done_MarksInTheOldDoneFileMoveIntoTheLedgerOnce()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        string[] done = [MpId1, MpId2, "571313100000000003"];
        foreach (var mp in done)
        {
            _xellent.SeedHourlyTimeSeries(mp, supplyStart.AddYears(-1), hours: 24);
            _xellent.SeedContract(Account, mp, migratedOn: supplyStart);
        }
        InSettl(done.Select(mp => (mp, Guid.NewGuid())).ToArray());
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        var folder = Path.Combine(OutputDir, "snapshot", "test");
        var old = new TimeSeries.Cli.Services.RunSnapshot(folder, reuse: true);
        foreach (var mp in done)
            old.Put("done", mp, new TimeSeriesRunner.DoneEntry(Guid.NewGuid(), Account, DanishMidnight(supplyStart),
                DateOnly.FromDateTime(supplyStart.AddYears(-3)), DateOnly.FromDateTime(supplyStart), 24, DateTimeOffset.UtcNow));
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false));

        Assert.Equal(3, DoneMarkLines().Length);
        Assert.False(File.Exists(Path.Combine(folder, "done.jsonl")));
        Assert.True(File.Exists(Path.Combine(folder, "done.jsonl.moved")));
        Assert.Contains(logs, l => l.StartsWith("Already done: 3 metering point(s)", StringComparison.Ordinal));
        Assert.Empty(_ts.GetPointsCalls);

        // A second run copies nothing again.
        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: true, continueOnError: false));
        Assert.Equal(3, DoneMarkLines().Length);
    }

    [Fact]
    public async Task Done_ALivePostWritesTheValuesThePeriodAndTheDoneMark_AndTheNextRunLeavesItOut()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());

        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false));
        var ledger = File.ReadAllLines(Path.Combine(OutputDir, "snapshot", "test", TimeSeries.Cli.Services.PostLedger.FileName));
        Assert.Contains(ledger, l => l.Contains("\"kind\":\"points\"") && l.Contains("\"status\":\"done\""));
        Assert.Contains(ledger, l => l.Contains("\"kind\":\"period\"") && l.Contains("\"status\":\"done\""));
        Assert.Contains(MpId1, Assert.Single(DoneMarkLines()));

        var logs = new List<string>();
        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));
        Assert.Single(_ts.PostCalls);
        Assert.Contains(logs, l => l.StartsWith("Already done: 1 metering point(s)", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Snapshot_Refresh_ReadsEverythingAgain_AndKeepsDoneMarks()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _ts.GetPointsResponse = (_, _, _) => (new SettlPointsSummaryDto { Count = 24 }, null);

        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test"));
        var (lists, statuses) = (_mp.FindMeteringPointsCalls, _mp.GetDeliveryStatusesCalls.Count);

        // Without --refresh nothing is read again.
        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test"));
        Assert.Equal((lists, statuses), (_mp.FindMeteringPointsCalls, _mp.GetDeliveryStatusesCalls.Count));

        // With it, Settl is read again, and the done mark is still there.
        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test", refresh: true));
        Assert.True(_mp.FindMeteringPointsCalls > lists);
        Assert.True(_mp.GetDeliveryStatusesCalls.Count > statuses);
        Assert.Single(DoneMarkLines());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task HourlyValuesWhereXellentReadsQuarterHourly_AreConvertedUnlessResolutionChangesAreSkipped(bool skipResolutionChange, bool dryRun)
    {
        // MP 571313185621006160 (production, 2026-09-11): one hourly series with hourly values throughout,
        // but Xellent reads the meter hourly only until 2023-04-25 and quarter-hourly from 2023-04-26.
        var supplyStart = new DateTime(2026, 1, 1);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 4, 24, 22, 0, 0, DateTimeKind.Utc), hours: 72,
            companyId: "for", agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 4, 26), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        BothSeriesInSettl();
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: dryRun, continueOnError: false,
            skipResolutionChange: skipResolutionChange, allowSwitches: true);

        // The outgoing window is mixed: 24 original hours and 48 converted hours.
        Assert.Equal(0, rc);
        Assert.Equal(dryRun || skipResolutionChange ? 0 : 2, _ts.PostCalls.Count);
        if (!dryRun && !skipResolutionChange)
            Assert.Equal(216, _ts.PostCalls.Sum(c => c.Payload.Points.Count));
        var knownIssue = File.ReadAllLines(Path.Combine(OutputDir, "known-issue-xellent-resolution.csv"));
        Assert.Equal(2, knownIssue.Length);
        Assert.Contains("is Hour, its 48 values are Hour", knownIssue[1]);
        Assert.Contains("TimeReadingPeriod is Quarter (2023-04-26 → 2025-12-31)", knownIssue[1]);
        Assert.Equal(skipResolutionChange, File.Exists(Path.Combine(OutputDir, "skip-resolution-change.csv")));
        if (dryRun && !skipResolutionChange)
            Assert.Contains(logs, l => l.Contains("points=216"));
    }

    [Fact]
    public async Task Payer6Series_IsLeftOutAndListed_AndDonenessIsReported()
    {
        // MP1's payer is 612345 (6##### — not migrated), MP2's is 412345. Neither is in Settl yet.
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, migratedOn: supplyStart);
        var (mp1Guid, mp2Guid) = (Guid.NewGuid(), Guid.NewGuid());
        InSettl((MpId1, mp1Guid), (MpId2, mp2Guid));
        _payers.PayerOf[mp1Guid] = "612345";
        _payers.PayerOf[mp2Guid] = "412345";
        var seedLogs = new List<string>();
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: seedLogs.Add).RunSeedSnapshotAsync("test"));
        // Only MP2 counts toward the migration; nothing is done yet.
        Assert.Contains("Migration: 0 of 1 metering points done (0%), 1 still to post", seedLogs);
        var payerCalls = _payers.FindPayersForMeteringPointsCalls;

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false, offline: true));
        Assert.Equal(mp2Guid, Assert.Single(_mp.AddEipCalls).MpGuid);
        Assert.Contains("Payer 6#####: 1 metering point(s) left out (skip-payer-6-series.csv)", logs);
        Assert.Contains("payer 612345", File.ReadAllText(Path.Combine(OutputDir, "skip-payer-6-series.csv")));
        Assert.Equal(payerCalls, _payers.FindPayersForMeteringPointsCalls); // payers came from the snapshot
        Assert.Contains(logs, l => l.StartsWith("Migration: 0 of 1 metering points done (0%)", StringComparison.Ordinal));

        // The post marked MP2 done: the next run reports 1 of 1.
        logs.Clear();
        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false, offline: true));
        Assert.Contains(logs, l => l.StartsWith("Migration: 1 of 1 metering points done (100%)", StringComparison.Ordinal));
    }

    // The known-issue meter from above: hourly values throughout, Xellent says 15-minute from 2023-04-26.
    private void SeedHourlyValuesWhereXellentSaysQuarter(string mp, string account)
    {
        _xellent.SeedHourlyTimeSeries(mp, new DateTime(2023, 4, 24, 22, 0, 0, DateTimeKind.Utc), hours: 72, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(mp, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), seriesResolution: 0);
        _xellent.SeedReadingPeriod(mp, "for", new DateTime(2023, 4, 26), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedContract(account, mp, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
    }

    [Fact]
    public async Task ConvertedReadingPeriod_HasSeparatePayloadAndInvoicedPeriod()
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        SeedHourlyValuesWhereXellentSaysQuarter(MpId1, Account);
        InSettl((MpId1, Guid.NewGuid()));
        BothSeriesInSettl();
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false,
            skipResolutionChange: false, allowSwitches: true);

        Assert.True(rc == 0, string.Join(Environment.NewLine, logs.Where(l => l.Contains("ERROR") || l.StartsWith("SKIP"))));
        Assert.Equal(new[] { 24, 192 }, _ts.PostCalls.Select(c => c.Payload.Points.Count));
        Assert.Equal(72m, _ts.PostCalls.SelectMany(c => c.Payload.Points).Sum(p => p.Quantity));
        var periods = _mp.AddEipCalls.SelectMany(c => c.Request.Periods).Select(p => p.Period!).ToList();
        Assert.Equal((new DateOnly(2023, 1, 1), new DateOnly(2023, 4, 26)), (periods[0].Start, periods[0].End));
        Assert.Equal((new DateOnly(2023, 4, 26), new DateOnly(2026, 1, 1)), (periods[1].Start, periods[1].End));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(OutputDir, "known-issue-xellent-resolution.csv")).Length);
    }

    [Theory]
    [InlineData(true)]   // Settl has only the hourly series: the 15-minute series is missing
    [InlineData(false)]  // Settl has only a 15-minute series: the hourly series is missing
    public async Task ResolutionChanges_TakeTheSwitchFromTheReadingPeriods(bool settlHasHour)
    {
        // Every stored point is hourly, but the reading periods go hourly to 15-minute on 2023-04-26: the post
        // converts the 15-minute stretch, so Settl receives both, and the switch is the reading period's.
        InDeliveryFrom(new DateTime(2026, 1, 1));
        SeedHourlyValuesWhereXellentSaysQuarter(MpId1, Account);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => [new TimeSeriesInfoDto { Id = Guid.NewGuid(), TimeResolution = settlHasHour
            ? new TimeResolutionInfoDto { Length = 1, Unit = "Hour" }
            : new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "changes.csv");

        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));

        // On the list because Settl lacks one side of the switch; ChangeDate is where the 15-minute reading period starts.
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal(settlHasHour
            ? $"\"{MpId1}\";\"Parent\";\"2023-04-26\";\"Hour\";\"Hour\";\"Quarter\";\"2023-01-01\";\"2026-01-01\";\"\""
            : $"\"{MpId1}\";\"Parent\";\"2023-04-26\";\"Hour\";\"Quarter\";\"Hour\";\"2023-01-01\";\"2026-01-01\";\"\"", lines[1]);
    }

    [Fact]
    public async Task ResolutionChanges_AQuarterToHourSwitch_IsListed()
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        _xellent.SeedQuarterTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2024, 5, 31), seriesResolution: 1);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2024, 6, 1), new DateTime(2154, 12, 31), seriesResolution: 0);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => [new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "changes.csv");

        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));

        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Equal($"\"{MpId1}\";\"Parent\";\"2024-06-01\";\"Quarter\";\"Quarter\";\"Hour\";\"2023-01-01\";\"2026-01-01\";\"\"", lines[1]);
    }

    [Fact]
    public async Task ResolutionChanges_SeveralSwitches_GetARowEach_AndAWarning()
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 4, 26), new DateTime(2024, 5, 31), seriesResolution: 1);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2024, 6, 1), new DateTime(2154, 12, 31), seriesResolution: 0);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => [new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "changes.csv");
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunResolutionChangesAsync("test", outputPath: file));

        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith($"\"{MpId1}\";\"Parent\";\"2023-04-26\";", lines[1]);
        Assert.StartsWith($"\"{MpId1}\";\"Parent\";\"2024-06-01\";", lines[2]);
        Assert.All(lines.Skip(1), l => Assert.EndsWith(";\"multiple switches (2) – check by hand\"", l));
        Assert.Contains(logs, l => l.StartsWith("WARNING: 1 metering point(s) switch resolution more than once", StringComparison.Ordinal));
        Assert.Contains(logs, l => l == $"  {MpId1}: 2023-04-26, 2024-06-01");
    }

    [Theory]
    [InlineData(false)]  // no reading periods: values and Settl differ, but there is no switch
    [InlineData(true)]   // the 15-minute reading period starts on the window start: no switch inside the window
    public async Task ResolutionChanges_NoSwitchInsideTheWindow_IsNotListed(bool switchOnWindowStart)
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        if (switchOnWindowStart)
        {
            _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2020, 1, 1), new DateTime(2022, 12, 31), seriesResolution: 0);
            _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2154, 12, 31), seriesResolution: 1);
        }
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => [new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "changes.csv");

        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));

        Assert.Single(File.ReadAllLines(file));
    }

    [Fact]
    public async Task ResolutionChanges_QuarterPointsAgainstAnHourSeries_AreLeftOut()
    {
        // The mirror of the listed case: 15-minute points, Settl has only an hourly series. Off the list.
        var supplyStart = new DateTime(2026, 1, 1);
        InDeliveryFrom(supplyStart);
        _xellent.SeedQuarterTimeSeries(MpId1, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));  // default Settl response: an hourly series only
        var file = Path.Combine(OutputDir, "changes.csv");

        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));

        Assert.Single(File.ReadAllLines(file));
    }

    [Fact]
    public async Task ResolutionChanges_LeavesOutSwitchesWhenBothTargetSeriesExist()
    {
        // MP1's points go hourly → 15-minute on 2024-06-01: a switch. MP2's go the other way,
        // 15-minute → hourly, and is not one.
        var supplyStart = new DateTime(2026, 1, 1);
        var (hourly, quarterly) = (new DateTime(2023, 5, 31, 22, 0, 0, DateTimeKind.Utc), new DateTime(2024, 5, 31, 22, 0, 0, DateTimeKind.Utc));
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, hourly, hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedQuarterTimeSeries(MpId1, quarterly, quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedQuarterTimeSeries(MpId2, hourly, quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedHourlyTimeSeries(MpId2, quarterly, hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2024, 5, 31), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2024, 6, 1), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedReadingPeriod(MpId2, "for", new DateTime(2023, 1, 1), new DateTime(2024, 5, 31), seriesResolution: 1);
        _xellent.SeedReadingPeriod(MpId2, "for", new DateTime(2024, 6, 1), new DateTime(2154, 12, 31), seriesResolution: 0);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()), (MpId2, Guid.NewGuid()));
        BothSeriesInSettl();
        var file = Path.Combine(OutputDir, "changes.csv");

        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));

        Assert.Single(File.ReadAllLines(file));
    }

    [Fact]
    public async Task ResolutionChanges_LeavesOutMeteringPointsSettlDoesNotStateDeliveryFor()
    {
        // A real switch (hourly until 2023-04-25, quarter-hourly after), but Settl marks no delivery
        // status InDelivery=true, so the window rests on the current delivery status. Not for Systemate.
        var supplyStart = new DateTime(2026, 1, 1);
        NotInDeliveryInSettl(Guid.NewGuid(), supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 4, 24, 22, 0, 0, DateTimeKind.Utc), hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedQuarterTimeSeries(MpId1, new DateTime(2023, 4, 25, 22, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 4, 26), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        _mp.GetTimeSeriesResponse = _ => [new TimeSeriesInfoDto { Id = Guid.NewGuid(), TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "changes.csv");
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunResolutionChangesAsync("test", outputPath: file));

        Assert.Single(File.ReadAllLines(file));  // header only — not on the list for Systemate
        var unclear = File.ReadAllLines(Path.Combine(OutputDir, "changes-unclear-delivery.csv"));
        Assert.Equal(2, unclear.Length);
        Assert.StartsWith($"\"{MpId1}\";\"Parent\";\"2023-04-26\"", unclear[1]);
        Assert.Contains(logs, l => l.Contains("Settl does not state their delivery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPostAllAsync_DryRun_BlocksMissingSeriesPerResolution_AndListsThemInExcel()
    {
        // What readiness used to list: the dry run blocks a metering point whose series is missing in
        // Settl, one CSV per resolution.
        const string mpId3 = "571313100000000003";
        var supplyStart = new DateTime(2026, 1, 1);
        InDeliveryFrom(supplyStart);
        // MP1 and MP2: plain hourly values. MP3: hourly values where Xellent says 15-minute.
        foreach (var (mp, account) in new[] { (MpId1, Account), (MpId2, "acct2") })
        {
            _xellent.SeedHourlyTimeSeries(mp, new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24, companyId: "for",
                agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
            _xellent.SeedContract(account, mp, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        }
        SeedHourlyValuesWhereXellentSaysQuarter(mpId3, "acct3");
        var (mp1Guid, mp2Guid, mp3Guid) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InSettl((MpId1, mp1Guid), (MpId2, mp2Guid), (mpId3, mp3Guid));
        // Settl has an hourly series for MP1 and MP3, only a 15-minute series for MP2.
        _mp.GetTimeSeriesResponse = id => [new TimeSeriesInfoDto
        {
            Id = Guid.NewGuid(),
            TimeResolution = id == mp2Guid
                ? new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" }
                : new TimeResolutionInfoDto { Length = 1, Unit = "Hour" },
        }];
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false,
            allowSwitches: true));

        Assert.Contains(logs, l => l.StartsWith("  Ready: 1", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("  Blocked: 2", StringComparison.Ordinal));
        var hour = Path.Combine(OutputDir, "blocked-missing-hour-series-in-settl.csv");
        var quarter = Path.Combine(OutputDir, "blocked-missing-quarter-series-in-settl.csv");
        Assert.Contains(MpId2, File.ReadAllText(hour));
        // MP3 needs a quarter-hour target for the converted part of its window.
        Assert.Contains(mpId3, File.ReadAllText(quarter));

        // Opens straight in Excel here: BOM, semicolons, every field quoted, CRLF.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(hour).Take(3));
        var text = File.ReadAllText(hour);
        Assert.StartsWith("\"MeteringPointIdentification\";\"Type\";\"SettlMeteringPointId\"", text.TrimStart((char)0xFEFF));
        Assert.All(text.TrimEnd((char)10).Split((char)10), line => Assert.EndsWith(((char)13).ToString(), line));
    }
    [Fact]
    public async Task ResolutionChanges_AnHourlyReadingPeriodInTheWindow_IsListed_AndBlocksThePost()
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 4, 26), new DateTime(2154, 12, 31), 1);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => [new() { Id = Guid.NewGuid(), TimeResolution = new() { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "missing-hour.csv");
        var unclearFile = Path.Combine(OutputDir, "missing-hour-unclear-delivery.csv");
        Directory.CreateDirectory(OutputDir);
        File.WriteAllText(unclearFile, "stale row from an earlier report");
        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));
        // The values are all in the 15-minute stretch, but the window also holds an hourly reading period and
        // Settl has no hourly series: listed, and the post is blocked on the same rule.
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith($"\"{MpId1}\";\"Parent\";\"2023-04-26\";", lines[1]);
        Assert.Contains("\"Hour\";\"2023-01-01\";\"2026-01-01\"", lines[1]);
        Assert.Single(File.ReadAllLines(unclearFile));
        Assert.DoesNotContain("stale row", File.ReadAllText(unclearFile));
        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false, skipResolutionChange: true,
            allowSwitches: true));
        Assert.Empty(_ts.PostCalls);
        Assert.Contains(MpId1, File.ReadAllText(Path.Combine(OutputDir, "blocked-missing-hour-series-in-settl.csv")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversionPrecision_BlocksWholePoint_InLiveAndCountedBatch(bool dryRun)
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        SeedHourlyValuesWhereXellentSaysQuarter(MpId1, Account);
        using (var ctx = _xellent.CreateContext())
        {
            var last = ctx.EmsTimeseriesValues.OrderByDescending(v => v.Timeofvalue).First();
            last.Value = 0.0001m;
            ctx.SaveChanges();
        }
        InSettl((MpId1, Guid.NewGuid()));
        BothSeriesInSettl();
        var logs = new List<string>();
        // Blocked, not failed: the run does not stop and exits 0.
        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: dryRun, continueOnError: false,
            allowSwitches: true));
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Contains(logs, l => l.Contains($"Conversion failed for MP {MpId1}") && l.Contains("0.0001"));
        Assert.Contains(logs, l => l.StartsWith($"OUTGOING MP {MpId1}: resolutions=Hour+Quarter, convertedHours="));
        Assert.Contains(MpId1, File.ReadAllText(Path.Combine(OutputDir, "blocked-hourly-values-cannot-be-split-into-quarters.csv")));
    }

    private const string SwitchBlockedFile = "blocked-resolution-switch-waiting-for-settl-to-confirm-series.csv";

    [Theory]
    [InlineData(true, false)]   // hourly → 15-minute on 2023-04-26
    [InlineData(false, false)]  // 15-minute → hourly on 2024-06-01
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RunPostAllAsync_AReadingPeriodSwitchInsideTheWindow_IsBlockedUnlessSwitchesAreAllowed(bool hourToQuarter, bool allowSwitches)
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        if (hourToQuarter)
            SeedHourlyValuesWhereXellentSaysQuarter(MpId1, Account);
        else
        {
            _xellent.SeedQuarterTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
                agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
            _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2024, 5, 31), seriesResolution: 1);
            _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2024, 6, 1), new DateTime(2154, 12, 31), seriesResolution: 0);
            _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        }
        InSettl((MpId1, Guid.NewGuid()));
        BothSeriesInSettl();  // blocked even when Settl has both series: Settl has to confirm them first
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false,
            allowSwitches: allowSwitches));

        var blocked = Path.Combine(OutputDir, SwitchBlockedFile);
        if (allowSwitches)
        {
            Assert.NotEmpty(_ts.PostCalls);
            Assert.False(File.Exists(blocked));
            Assert.Contains("Resolution switches: sent (--allow-switches)", logs);
            return;
        }
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
        Assert.Empty(DoneMarkLines());
        var switchDate = hourToQuarter ? "2023-04-26" : "2024-06-01";
        var rows = File.ReadAllLines(blocked);
        Assert.Equal(2, rows.Length);
        Assert.Contains(MpId1, rows[1]);
        Assert.Contains("resolution switch – waiting for Settl to confirm series", rows[1]);
        Assert.Contains($"reading periods switch resolution on {switchDate}", rows[1]);
        Assert.Contains($"BLOCKED:   MP {MpId1}: reading periods switch resolution on {switchDate}", logs);
        Assert.Contains(logs, l => l.StartsWith("  Blocked: 1", StringComparison.Ordinal) && !l.Contains("→"));
        Assert.Contains(logs, l => l.Contains($"→ {SwitchBlockedFile}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPostAllAsync_SeveralSwitches_AreAllNamedInTheBlockedDetail()
    {
        InDeliveryFrom(new DateTime(2026, 1, 1));
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 4, 26), new DateTime(2024, 5, 31), seriesResolution: 1);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2024, 6, 1), new DateTime(2154, 12, 31), seriesResolution: 0);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        BothSeriesInSettl();

        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: true, continueOnError: false));

        Assert.Contains("reading periods switch resolution on 2023-04-26, 2024-06-01",
            File.ReadAllText(Path.Combine(OutputDir, SwitchBlockedFile)));
    }

    [Fact]
    public async Task RunPostAllAsync_ASwitchOnTheWindowStart_IsNotBlocked()
    {
        // Hourly reading period until 2022-12-31, 15-minute from 2023-01-01 — the window's first day. Not inside the window.
        InDeliveryFrom(new DateTime(2026, 1, 1));
        _xellent.SeedQuarterTimeSeries(MpId1, new DateTime(2023, 5, 1, 0, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2020, 1, 1), new DateTime(2022, 12, 31), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: new DateTime(2026, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        BothSeriesInSettl();

        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.NotEmpty(_ts.PostCalls);
        Assert.False(File.Exists(Path.Combine(OutputDir, SwitchBlockedFile)));
    }

    [Fact]
    public async Task Snapshot_StoresTheXellentPointCounts_SoTheReportsDoNotCountAgain()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));

        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test"));

        var counts = Path.Combine(OutputDir, "snapshot", "test", "xellent-point-counts-v3.jsonl");
        Assert.True(File.Exists(counts), "the seed should leave the Xellent counts in the snapshot");
        Assert.Contains(MpId1, File.ReadAllText(counts));
    }

    [Fact]
    public async Task ResolutionChanges_ListsOnlyChangesInsideThePostWindow()
    {
        // Delivery from 2026-01-01, so the window is 2023-01-01 → 2026-01-01 for both.
        // MP1: hourly until 2023-04-25, quarter-hourly from 2023-04-26 — a change inside the window.
        // MP2: hourly until 2019, quarter-hourly since 2020 — a change, but before its window.
        var supplyStart = new DateTime(2026, 1, 1);
        InDeliveryFrom(supplyStart);
        // MP1: hourly points until 2023-04-25, 15-minute from 2023-04-26. MP2 switched back in 2020,
        // so inside its window every point is 15-minute.
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 4, 24, 22, 0, 0, DateTimeKind.Utc), hours: 24, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedQuarterTimeSeries(MpId1, new DateTime(2023, 4, 25, 22, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedQuarterTimeSeries(MpId2, new DateTime(2023, 4, 24, 22, 0, 0, DateTimeKind.Utc), quarters: 96, companyId: "for",
            agreementStart: new DateTime(2023, 1, 1), agreementEnd: new DateTime(2025, 12, 31));
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 1, 1), new DateTime(2023, 4, 25), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId1, "for", new DateTime(2023, 4, 26), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedReadingPeriod(MpId2, "for", new DateTime(2015, 1, 1), new DateTime(2019, 12, 31), seriesResolution: 0);
        _xellent.SeedReadingPeriod(MpId2, "for", new DateTime(2020, 1, 1), new DateTime(2154, 12, 31), seriesResolution: 1);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, companyId: "for", partStart: new DateTime(2023, 1, 1), migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()), (MpId2, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => [new TimeSeriesInfoDto { Id = Guid.NewGuid(), TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } }];
        var file = Path.Combine(OutputDir, "changes.csv");

        Assert.Equal(0, await BuildRunner().RunResolutionChangesAsync("test", outputPath: file));

        Assert.Equal([
            "\"MeteringPointId\";\"Type\";\"ChangeDate\";\"XellentResolutions\";\"SettlSeries\";\"MissingSeries\";\"WindowStart\";\"WindowEnd\";\"Warning\"",
            $"\"{MpId1}\";\"Parent\";\"2023-04-26\";\"Hour + Quarter\";\"Quarter\";\"Hour\";\"2023-01-01\";\"2026-01-01\";\"\""], File.ReadAllLines(file));
        Assert.DoesNotContain(Directory.GetFiles(OutputDir, "*.csv"), p => !p.EndsWith("changes.csv", StringComparison.Ordinal));
    }
    [Fact]
    public async Task DryRun_CountsPoints_AndReportsWhatALiveRunPosts()
    {
        // 48 hours from a day before the boundary: 24 before it, 24 on or after it.
        var supplyStart = new DateTime(2023, 6, 15);
        var boundary = DanishMidnight(new DateTime(2023, 6, 1));
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses =
                [
                    new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = DanishMidnight(supplyStart) } },
                    new DeliveryStatusDto { InDelivery = false, Period = new TimePeriodDto { Start = boundary, End = DanishMidnight(supplyStart) } },
                ]
            },
            null);
        _xellent.SeedHourlyTimeSeries(MpId1, boundary.UtcDateTime.AddHours(-24), hours: 48);
        _xellent.SeedContract(Account, MpId1, partStart: new DateTime(2020, 1, 1), migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        var dryLogs = new List<string>();
        var liveLogs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: dryLogs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false));
        Assert.Equal(0, await BuildRunner(log: liveLogs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.Contains(dryLogs, l => l.StartsWith($"DELIVERY FILTER MP {MpId1}:", StringComparison.Ordinal) && l.EndsWith("excluded=0, included=24", StringComparison.Ordinal));
        Assert.Contains(dryLogs, l => l.Trim().StartsWith("[dry-run] would POST 24 points", StringComparison.Ordinal));
        Assert.Equal(24, Assert.Single(_ts.PostCalls).Payload.Points.Count);
        // Same invoiced period whether counted or read.
        var eipLine = dryLogs.Single(l => l.Contains("[dry-run] would add EIP", StringComparison.Ordinal));
        var eip = Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods).Period!;
        Assert.Contains($"({eip.Start:yyyy-MM-dd} → {eip.End:yyyy-MM-dd},", eipLine);
    }

    [Fact]
    public async Task Snapshot_KeepsOnlyTheDeliveryStatusOfTheMeteringPoint()
    {
        var outputDirectory = Path.Combine(OutputDir, Guid.NewGuid().ToString("N"));
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetByIdV4Response = _ => new MeteringPointV4Dto
        {
            Consumer = new MeteringPointV4ConsumerDto { CustomerType = "SENTINEL-CONSUMER-DATA" },
            DeliveryStatus = new MeteringPointV4DeliveryStatusDto { Start = new DateTime(2023, 6, 14, 22, 0, 0, DateTimeKind.Utc), InDelivery = true },
        };

        try
        {
            await BuildRunner(outputDirectory).RunSeedSnapshotAsync("test");

            var files = Directory.GetFiles(Path.Combine(outputDirectory, "snapshot", "test"));
            Assert.Contains(files, f => f.EndsWith("settl-current-delivery-status.jsonl", StringComparison.Ordinal));
            Assert.DoesNotContain(files, f => File.ReadAllText(f).Contains("SENTINEL-CONSUMER-DATA", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunPostAllAsync_NotInDeliveryButMigrated_FetchesTheMeteringPointOnce()
    {
        var mpGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2024, 1, 1);
        NotInDeliveryInSettl(mpGuid, supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24);
        _xellent.SeedContract(Account, MpId1, partStart: new DateTime(2022, 1, 1), migratedOn: supplyStart);

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Equal([mpGuid], _mp.GetByIdV4Calls);
    }

    [Fact]
    public async Task RunPostAllAsync_NotInDeliveryWithoutDeliveryStart_IsSkippedQuietly()
    {
        NotInDeliveryInSettl(Guid.NewGuid(), currentDeliveryStart: null);
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Empty(_ts.PostCalls);
        Assert.DoesNotContain(logs, l => l.StartsWith("ERROR", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("1 never delivered", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunPostAllAsync_NoMeterInstalled_IsSkippedToItsOwnFile_NotCountedAsPosted(bool dryRun)
    {
        // MP2 has no values and Xellent status 5 "Oprettet": no meter at the address, nothing to post.
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, companyId: "for", partStart: new DateTime(2020, 1, 1), migratedOn: supplyStart);
        _xellent.SeedMeterPointStatus(MpId2, "for", status: 5, start: new DateTime(2020, 1, 1));
        var (mp1Guid, mp2Guid, tsGuid) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InSettl((MpId1, mp1Guid), (MpId2, mp2Guid));
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: dryRun, continueOnError: false));

        Assert.Contains(logs, l => l.StartsWith($"SKIP MP {MpId2}: no meter installed", StringComparison.Ordinal));
        Assert.Contains(MpId2, File.ReadAllText(Path.Combine(OutputDir, "skip-not-installed.csv")));
        Assert.Contains(logs, l => l.StartsWith("Run completed:", StringComparison.Ordinal) && l.Contains(" 1 posted,", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("1  no meter installed (Xellent status Oprettet)", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith(dryRun ? "  Ready: 1" : "  Done: 1", StringComparison.Ordinal));
        if (!dryRun)
            Assert.Equal(mp1Guid, Assert.Single(_mp.AddEipCalls).MpGuid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunPostAllAsync_MeterInstalledButNoValues_IsBlocked(bool dryRun)
    {
        // MP1 has a meter that delivers (status 6) but no values: that is an error, not a skip.
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2020, 1, 1), migratedOn: supplyStart);
        _xellent.SeedMeterPointStatus(MpId1, "for", status: 6, start: new DateTime(2020, 1, 1));
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: dryRun, continueOnError: false));

        Assert.Contains(logs, l => l.StartsWith("ERROR:", StringComparison.Ordinal) && l.Contains("a meter is installed", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("1  meter installed, but no values in Xellent", StringComparison.Ordinal));
        // Blocked ones are listed per reason, so the group can be handed over.
        Assert.Contains(MpId1, File.ReadAllText(Path.Combine(OutputDir, "blocked-meter-installed-but-no-values-in-xellent.csv")));
        Assert.Contains(logs, l => l.Contains("→ blocked-meter-installed-but-no-values-in-xellent.csv", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(OutputDir, "skip-not-installed.csv")));
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAllAsync_ExcludedByHand_IsNeverSent_AndCountedWithItsReason()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, migratedOn: supplyStart);
        var (mp1Guid, mp2Guid, tsGuid) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        InSettl((MpId1, mp1Guid), (MpId2, mp2Guid));
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);
        var folder = Path.Combine(OutputDir, "snapshot", "test");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, TimeSeriesRunner.ExcludedFileName),
            $"MeteringPoint;Reason;By;Date\n{MpId2};decided not to migrate;Martin;2026-10-16\n");
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.Equal(mp1Guid, Assert.Single(_mp.AddEipCalls).MpGuid);
        Assert.Contains(logs, l => l.StartsWith("Excluded by hand: 1 metering point(s) left out", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("  Excluded: 1", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("1  decided not to migrate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_RefreshBlocked_ReadsOnlyTheBlockedMeteringPointsAgain()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, migratedOn: supplyStart);
        var (mp1Guid, mp2Guid) = (Guid.NewGuid(), Guid.NewGuid());
        InSettl((MpId1, mp1Guid), (MpId2, mp2Guid));
        // MP2 has only a 15-minute series in Settl: blocked until Systemate creates the hourly one.
        var mp2Fixed = false;
        _mp.GetTimeSeriesResponse = id => [new TimeSeriesInfoDto
        {
            Id = Guid.NewGuid(),
            TimeResolution = id == mp2Guid && !mp2Fixed
                ? new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" }
                : new TimeResolutionInfoDto { Length = 1, Unit = "Hour" },
        }];
        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test"));
        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: true, continueOnError: false, offline: true));
        Assert.Contains(MpId2, File.ReadAllText(Path.Combine(OutputDir, "blocked-missing-hour-series-in-settl.csv")));

        mp2Fixed = true;
        var mp1Reads = _mp.GetTimeSeriesCalls.Count(g => g == mp1Guid);
        var logs = new List<string>();
        Assert.Equal(0, await BuildRunner(log: logs.Add).RunSeedSnapshotAsync("test", refreshBlocked: true));

        Assert.Contains(logs, l => l.StartsWith("Refresh blocked: 1 metering point(s) from 1 blocked file(s)", StringComparison.Ordinal));
        Assert.Equal(mp1Reads, _mp.GetTimeSeriesCalls.Count(g => g == mp1Guid));
        logs.Clear();
        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false, offline: true));
        Assert.Contains(logs, l => l.StartsWith("  Ready: 2", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.StartsWith("  Blocked:", StringComparison.Ordinal));
    }

    // Production 571313103103035224: read hourly until 2026-08-27, 15-minute from 2026-08-28; done with the window
    // 2026-05-29 → 2026-09-01. Settl's Hour series holds values to the window end, the Quarter series from it.
    [Theory]
    [InlineData(true, 0, true)]     // the example: listed
    [InlineData(false, 0, false)]   // read hourly all through: nothing to do
    [InlineData(true, 384, false)]  // the Quarter series already has the values
    public async Task Requarter_ListsDoneMeteringPointsPostedAsHourlyInAQuarterReadingPeriod(bool switchesToQuarter, int quarterValues, bool listed)
    {
        var mpGuid = Guid.NewGuid();
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2026, 5, 28, 22, 0, 0, DateTimeKind.Utc), hours: 24);
        var company = "tst";
        if (switchesToQuarter)
        {
            _xellent.SeedReadingPeriod(MpId1, company, new DateTime(2025, 8, 20), new DateTime(2026, 8, 27), 0);
            _xellent.SeedReadingPeriod(MpId1, company, new DateTime(2026, 8, 28), new DateTime(1900, 1, 1), 1);
        }
        else
            _xellent.SeedReadingPeriod(MpId1, company, new DateTime(2025, 8, 20), new DateTime(1900, 1, 1), 0);
        new TimeSeries.Cli.Services.RunSnapshot(Path.Combine(OutputDir, "snapshot", "test"), reuse: true).Put("done", MpId1,
            new TimeSeriesRunner.DoneEntry(mpGuid, Account, DanishMidnight(new DateTime(2026, 9, 1)),
                new DateOnly(2026, 5, 29), new DateOnly(2026, 9, 1), 2280, DateTimeOffset.UtcNow));
        var (hour, quarter) = (Guid.NewGuid(), Guid.NewGuid());
        _mp.GetTimeSeriesResponse = _ =>
        [
            new TimeSeriesInfoDto { Id = hour, TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" } },
            new TimeSeriesInfoDto { Id = quarter, TimeResolution = new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" } },
        ];
        _ts.GetPointsResponse = (id, _, _) => (new SettlPointsSummaryDto { Count = id == hour ? 96 : quarterValues }, null);
        var file = Path.Combine(OutputDir, "requarter.csv");

        Assert.Equal(0, await BuildRunner().RunRequarterAsync("test", outputPath: file));

        var lines = File.ReadAllLines(file);
        Assert.StartsWith("\"MeteringPointId\";\"SettlId\"", lines[0].TrimStart('﻿'));
        if (!listed)
        {
            Assert.Single(lines);
            return;
        }
        Assert.Equal($"\"{MpId1}\";\"{mpGuid}\";\"{Account}\";\"2026-05-29\";\"2026-09-01\";\"2026-08-28\";\"2026-09-01\";\"96\";\"0\"", lines[1]);
        Assert.All(_ts.GetPointsCalls, c => Assert.Equal((new DateOnly(2026, 8, 28), new DateOnly(2026, 9, 1)), (c.Start, c.End)));
        Assert.Empty(_ts.PostCalls);
        Assert.Empty(_mp.AddEipCalls);
    }

    [Fact]
    public async Task Snapshot_Refresh_KeepsTheLedger()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false));
        var ledger = Path.Combine(OutputDir, "snapshot", "test", TimeSeries.Cli.Services.PostLedger.FileName);
        var written = File.ReadAllText(ledger);

        Assert.Equal(0, await BuildRunner().RunSeedSnapshotAsync("test", refresh: true));

        Assert.Equal(written, File.ReadAllText(ledger));
    }

    [Fact]
    public async Task RunPostAllAsync_WritesItsStatesForProgress()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());

        Assert.Equal(0, await BuildRunner().RunPostAllAsync("test", dryRun: true, continueOnError: false));

        var states = TimeSeries.Cli.Services.PostRunStates.TryRead(Path.Combine(OutputDir, "snapshot", "test"));
        Assert.NotNull(states);
        Assert.Equal("Completed", states!.Status);
        Assert.True(states.DryRun);
        Assert.Equal(1, states.TotalOf("Ready"));
        Assert.Equal((1, 1), (states.Checked, states.Total));
    }

    [Fact]
    public async Task RunPostAllAsync_NotInDeliveryAndNotClosedAsAMigration_IsSkippedAsNotMigrated()
    {
        var outputDirectory = Path.Combine(OutputDir, Guid.NewGuid().ToString("N"));
        NotInDeliveryInSettl(Guid.NewGuid(), new DateTime(2024, 1, 1));
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24);
        _xellent.SeedContract(Account, MpId1); // open contract: never migrated
        var logs = new List<string>();

        try
        {
            var rc = await BuildRunner(outputDirectory, logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

            Assert.Equal(0, rc);
            Assert.Empty(_ts.PostCalls);
            Assert.Contains(logs, l => l.StartsWith($"SKIP MP {MpId1}: not migrated — customer {Account}: contract does not end on 2023-12-31", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "skip-not-migrated.csv")));
            // The same wording as for a metering point Settl has in delivery.
            Assert.Contains(logs, l => l.EndsWith("1  contract not closed as a migration at COS − 1", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunPostAllAsync_DeliveryStartRangeAndLimit_NarrowTheSelection()
    {
        // Delivery starts (Danish): MP1 2023-06-01, MP2 2023-06-15, MP3 2023-06-15, MP4 2023-07-01.
        // The range 2023-06-10 → 2023-06-30 keeps MP2 and MP3. Both are skipped (no Xellent contract),
        // so neither uses up the limit.
        var starts = new[] { new DateTime(2023, 6, 1), new DateTime(2023, 6, 15), new DateTime(2023, 6, 15), new DateTime(2023, 7, 1) };
        var mps = starts.Select((s, i) => new MeteringPointInfoDto
        {
            Id = Guid.NewGuid(),
            MeteringPointIdentification = $"57131310000000000{i + 1}",
            InDelivery = true,
            CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = DanishMidnight(s) },
        }).ToList();
        _mp.MeteringPoints = mps;
        _mp.GetDeliveryStatusesResponse = id => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses = [new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = mps.Single(m => m.Id == id).CurrentOrNextDeliveryPeriod }]
            },
            null);

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: true, continueOnError: true,
            cosFrom: new DateOnly(2023, 6, 10), cosTo: new DateOnly(2023, 6, 30), limit: 1);

        Assert.Equal(0, rc);
        Assert.Equal([mps[1].Id, mps[2].Id], _mp.GetDeliveryStatusesCalls);
        Assert.Equal(TimeSeriesRunner.BatchDeliveryCategory, _mp.LastDeliveryCategory);
    }

    [Theory]
    [InlineData(0, 2, new[] { 1, 2 })]
    [InlineData(2, 2, new[] { 3, 4 })]
    [InlineData(3, 5, new[] { 4 })]
    public async Task RunPostAllAsync_SkipAndLimit_RunTheMeteringPointsInBatches(int skip, int limit, int[] expected)
    {
        // Four in-delivery MPs, all ready; one outside the delivery-start range does not count toward skip or limit.
        var supplyStart = new DateTime(2023, 6, 15);
        var inRange = DanishMidnight(supplyStart);
        var mps = Enumerable.Range(1, 4).Select(n => new MeteringPointInfoDto
        {
            Id = Guid.NewGuid(),
            MeteringPointIdentification = $"57131310000000000{n}",
            InDelivery = true,
            CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = inRange },
        }).ToList();
        foreach (var mp in mps)
        {
            _xellent.SeedHourlyTimeSeries(mp.MeteringPointIdentification, supplyStart.AddYears(-1), hours: 24);
            _xellent.SeedContract(Account, mp.MeteringPointIdentification, migratedOn: supplyStart);
        }
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        mps.Insert(1, new MeteringPointInfoDto
        {
            Id = Guid.NewGuid(),
            MeteringPointIdentification = "571313100000000001x",
            InDelivery = true,
            CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = DanishMidnight(new DateTime(2025, 1, 1)) },
        });
        _mp.MeteringPoints = mps;
        _mp.GetDeliveryStatusesResponse = id => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses = [new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = mps.Single(m => m.Id == id).CurrentOrNextDeliveryPeriod }]
            },
            null);
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false,
            cosFrom: new DateOnly(2023, 6, 1), cosTo: new DateOnly(2023, 6, 30), limit: limit, skip: skip);

        Assert.Equal(0, rc);
        Assert.Equal(expected, Checked(logs));
    }

    // The metering points a batch run got as far as checking (its "--- [i/N] MP … ---" headers), by last digit.
    private static int[] Checked(List<string> logs) => logs
        .Where(l => l.StartsWith("--- [", StringComparison.Ordinal))
        .Select(l => l[(l.IndexOf(" MP ", StringComparison.Ordinal) + 4)..].Split(' ')[0])
        .Select(id => int.Parse(id[^1..]))
        .ToArray();

    [Fact]
    public async Task RunPostAllAsync_Limit_CountsOnlyTheMeteringPointsSent()
    {
        // MP1 has no Xellent contract (skipped), MP2 lacks its hourly series in Settl (blocked), MP3 and
        // MP4 are ready. --limit 1 goes past the skip and the block, sends MP3 and stops before MP4.
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        const string mp3 = "571313100000000003", mp4 = "571313100000000004";
        var mp2Guid = Guid.NewGuid();
        InSettl((MpId1, Guid.NewGuid()), (MpId2, mp2Guid), (mp3, Guid.NewGuid()), (mp4, Guid.NewGuid()));
        foreach (var mp in new[] { MpId1, MpId2, mp3, mp4 })
            _xellent.SeedHourlyTimeSeries(mp, supplyStart.AddYears(-1), hours: 24);
        foreach (var mp in new[] { MpId2, mp3, mp4 })
            _xellent.SeedContract(Account, mp, migratedOn: supplyStart);
        _mp.GetTimeSeriesResponse = id => [new TimeSeriesInfoDto
        {
            Id = Guid.NewGuid(),
            TimeResolution = id == mp2Guid
                ? new TimeResolutionInfoDto { Length = 15, Unit = "Minutes" }
                : new TimeResolutionInfoDto { Length = 1, Unit = "Hour" },
        }];
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true, continueOnError: false, limit: 1));

        Assert.Equal([1, 2, 3], Checked(logs));
        Assert.Contains("Limit of 1 reached.", logs);
        Assert.Contains(logs, l => l.StartsWith("  Ready: 1", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("  Skipped: 1", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.StartsWith("  Blocked: 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListBatchMeteringPointsAsync_ReturnsWhatTheBatchRunTakes()
    {
        // MP1 in delivery in range, MP2 a Xellent child of MP1, "…03" not in delivery (kept whatever the
        // range), "…04" in delivery outside the range, "…05" a Settl child. "…04" and the two children
        // are left out: children are never in scope.
        var parentGuid = Guid.NewGuid();
        _xellent.SeedContract(Account, MpId1);
        _xellent.SeedContract(Account, MpId2);
        _xellent.SeedChildRelation(MpId1, MpId2);
        var start = DanishMidnight(new DateTime(2023, 6, 15));
        _mp.MeteringPoints =
        [
            new MeteringPointInfoDto { Id = parentGuid, MeteringPointIdentification = MpId1, InDelivery = true, CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = start } },
            new MeteringPointInfoDto { Id = Guid.NewGuid(), MeteringPointIdentification = MpId2, InDelivery = true, CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = start } },
            new MeteringPointInfoDto { Id = Guid.NewGuid(), MeteringPointIdentification = "571313100000000003", InDelivery = false },
            new MeteringPointInfoDto { Id = Guid.NewGuid(), MeteringPointIdentification = "571313100000000004", InDelivery = true,
                CurrentOrNextDeliveryPeriod = new TimePeriodDto { Start = DanishMidnight(new DateTime(2025, 1, 1)) } },
            new MeteringPointInfoDto { Id = Guid.NewGuid(), MeteringPointIdentification = "571313100000000005", InDelivery = true,
                ParentMeteringPoint = new ResLinkDto { Id = parentGuid } },
        ];

        var list = await BuildRunner().ListBatchMeteringPointsAsync(new DateOnly(2023, 6, 1), new DateOnly(2023, 6, 30));

        Assert.Equal(
            [
                new BatchMeteringPoint(MpId1, parentGuid, true, new DateOnly(2023, 6, 15)),
                new BatchMeteringPoint("571313100000000003", list[1].Id, false, null),
            ],
            list);
        Assert.Equal(TimeSeriesRunner.BatchDeliveryCategory, _mp.LastDeliveryCategory);
    }

    [Fact]
    public async Task RunPostAllAsync_OnlyContractsInTheElCategoryCountAsHolders()
    {
        var outputDirectory = Path.Combine(OutputDir, Guid.NewGuid().ToString("N"));
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedContract(Account, MpId1, deliveryCategory: "Varme", migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        try
        {
            var rc = await BuildRunner(outputDirectory, logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

            Assert.Equal(0, rc);
            Assert.Empty(_ts.PostCalls);
            Assert.Contains($"SKIP MP {MpId1}: no Xellent contract on 2023-06-14 (the day before delivery starts in Settl) (parent)", logs);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    // Boundary-tests: bekræfter at extract-vinduet slutter ved DK-midnat (= UTC 22:00 i sommertid,
    // UTC 23:00 i vintertid). Point seedet på begge sider af grænsen — kun dem før forsyningsstart
    // må indgå i POST'en. Hvis konvertering DK→UTC mangler, lækker 1-2 timer ind i den nye periode.

    [Fact]
    public async Task RunPostAllAsync_ExcludesPointsAfterDkMidnight_Summer()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        // DK 2023-06-15 00:00 (sommertid, UTC+2) = UTC 2023-06-14 22:00
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Unspecified);
        InDeliveryFrom(supplyStart);
        // 5 timer: UTC 20, 21, 22, 23, 2023-06-15 00 — kun 20+21 ligger før forsyningsstart.
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 6, 14, 20, 0, 0, DateTimeKind.Utc), hours: 5);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, mpGuid));
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        PostSnapshot.AssertMatchesGolden(PostSnapshot.From(_ts, _mp), "expected-posts-boundary-summer.json");
    }

    [Fact]
    public async Task RunPostAllAsync_ExcludesPointsAfterDkMidnight_Winter()
    {
        var mpGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var tsGuid = new Guid("22222222-2222-2222-2222-222222222222");
        // DK 2023-12-15 00:00 (vintertid, UTC+1) = UTC 2023-12-14 23:00
        var supplyStart = new DateTime(2023, 12, 15, 0, 0, 0, DateTimeKind.Unspecified);
        InDeliveryFrom(supplyStart);
        // 5 timer: UTC 21, 22, 23, 2023-12-15 00, 01 — kun 21+22 ligger før forsyningsstart.
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 12, 14, 21, 0, 0, DateTimeKind.Utc), hours: 5);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, mpGuid));
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        PostSnapshot.AssertMatchesGolden(PostSnapshot.From(_ts, _mp), "expected-posts-boundary-winter.json");
    }

    [Fact]
    public async Task RunPostAllAsync_AbortsOnFirstFailure()
    {
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        InDeliveryFrom(supplyStart);

        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedContract("acct1", MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()), (MpId2, Guid.NewGuid()));
        _ts.PostResponse = (_, _) => (false, "boom");

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(1, rc);
        // Failure on MP 1 must prevent MP 2 from being processed.
        Assert.Single(_mp.GetDeliveryStatusesCalls);
    }

    [Fact]
    public async Task RunPostAllAsync_SkipIfExistingPoints_SkipsMpsWithExistingData()
    {
        var mpWithDataGuid = new Guid("11111111-1111-1111-1111-111111111111");
        var mpEmptyGuid = new Guid("44444444-4444-4444-4444-444444444444");
        var tsWithDataGuid = new Guid("22222222-2222-2222-2222-222222222222");
        var tsEmptyGuid = new Guid("55555555-5555-5555-5555-555555555555");
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        InDeliveryFrom(supplyStart);

        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        _xellent.SeedContract(Account, MpId2, migratedOn: supplyStart);
        InSettl((MpId1, mpWithDataGuid), (MpId2, mpEmptyGuid));
        _mp.GetTimeSeriesResponse = mpGuid => HourTs(mpGuid == mpWithDataGuid ? tsWithDataGuid : tsEmptyGuid);
        _ts.GetPointsResponse = (tsGuid, _, _) =>
            tsGuid == tsWithDataGuid
                ? (new SettlPointsSummaryDto { Count = 12, From = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(2023, 6, 14, 0, 0, 0, TimeSpan.Zero) }, null)
                : (new SettlPointsSummaryDto { Count = 0 }, null);

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: false, skipResolutionChange: false, skipIfExistingPoints: true);

        Assert.Equal(0, rc);
        // Only the empty MP should reach POST/EIP.
        Assert.Single(_ts.PostCalls);
        Assert.Single(_mp.AddEipCalls);
        Assert.Equal(mpEmptyGuid, _mp.AddEipCalls[0].MpGuid);
    }

    [Fact]
    public async Task RunPostAllAsync_SkipIfExistingPoints_AsksSettlInPiecesOfAtMostOneYear()
    {
        // A three-year window; Settl rejects a points summary longer than one year.
        var tsGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);
        // Points exist only in the last piece.
        _ts.GetPointsResponse = (_, from, _) => from >= new DateOnly(2022, 6, 1)
            ? (new SettlPointsSummaryDto { Count = 5, From = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero), To = new DateTimeOffset(2023, 1, 2, 0, 0, 0, TimeSpan.Zero) }, null)
            : (new SettlPointsSummaryDto { Count = 0 }, null);
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false, skipIfExistingPoints: true);

        Assert.Equal(0, rc);
        var calls = _ts.GetPointsCalls;
        Assert.All(calls, c => Assert.True(c.End <= c.Start.AddYears(1)));
        Assert.Equal(new DateOnly(2020, 6, 15), calls[0].Start);
        Assert.Equal(new DateOnly(2023, 6, 15), calls[^1].End);
        for (var i = 1; i < calls.Count; i++)
            Assert.Equal(calls[i - 1].End, calls[i].Start);
        Assert.Empty(_ts.PostCalls);
        Assert.Contains(logs, l => l.StartsWith($"SKIP MP {MpId1}: existing Settl TS data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPostAllAsync_ContinueOnError_ProcessesAllAndExitsNonZero()
    {
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        InDeliveryFrom(supplyStart);

        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedContract("acct1", MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId2, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()), (MpId2, Guid.NewGuid()));
        _ts.PostResponse = (_, _) => (false, "boom");

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: false, continueOnError: true);

        // Non-zero overall exit because both MPs failed...
        Assert.Equal(1, rc);
        // ...but --force means MP 2 was still attempted after MP 1 failed.
        Assert.Equal(2, _mp.GetDeliveryStatusesCalls.Count);
        Assert.Equal(2, _mp.GetTimeSeriesCalls.Count);
    }

    [Fact]
    public async Task RunPostAllAsync_OlderRelationship_IsIgnored_PostedForTheHolder()
    {
        // acct1 held the MP until 2019; acct2 has held it since. Settl delivery starts 2023-06-15.
        var mpGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract("acct1", MpId1, partStart: new DateTime(2010, 1, 1), partEnd: new DateTime(2019, 12, 31));
        _xellent.SeedContract("acct2", MpId1, partStart: new DateTime(2020, 1, 1), migratedOn: supplyStart);
        InSettl((MpId1, mpGuid));
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Contains("  customer acct2", logs);
        Assert.Single(_ts.PostCalls);
        Assert.Single(_mp.AddEipCalls);
    }

    [Fact]
    public async Task RunPostAllAsync_HeldBySeveralAccountsOnTheDayBeforeDelivery_IsBlocked()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract("acct1", MpId1, migratedOn: supplyStart);
        _xellent.SeedContract("acct2", MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Empty(_ts.PostCalls);
        Assert.Contains($"BLOCKED:   MP {MpId1}: held by several accounts on 2023-06-14 (acct1/acct2)", logs);
    }

    [Fact]
    public async Task RunPostAllAsync_NoContractOnTheDayBeforeDelivery_IsSkippedAsMissingInXellent()
    {
        // Seen on MP 571313179103608959: contract closed 2026-02-27, Settl delivery from 2026-03-01,
        // nobody holds the MP on 2026-02-28.
        var outputDirectory = Path.Combine(OutputDir, Guid.NewGuid().ToString("N"));
        var supplyStart = new DateTime(2026, 3, 1);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), hours: 24);
        _xellent.SeedContract(Account, MpId1, partStart: new DateTime(2024, 1, 1), partEnd: new DateTime(2026, 2, 27));
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        try
        {
            var rc = await BuildRunner(outputDirectory, logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

            Assert.Equal(0, rc);
            Assert.Empty(_ts.PostCalls);
            Assert.Contains($"SKIP MP {MpId1}: no Xellent contract on 2026-02-28 (the day before delivery starts in Settl) (parent)", logs);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "skip-missing-in-xellent.csv")));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunPostAllAsync_AmbiguousRevision_SkipsOnlyAffectedMp_WritesCsv_AndContinues()
    {
        var outputDirectory = Path.Combine(OutputDir, Guid.NewGuid().ToString("N"));
        var ambiguousMpGuid = Guid.NewGuid();
        var resolvedMpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var firstStart = new DateTimeOffset(2023, 6, 15, 1, 2, 3, TimeSpan.FromHours(2));
        var resolvedRevision = new DateTimeOffset(2023, 7, 1, 4, 5, 6, TimeSpan.FromHours(-3));
        var logs = new List<string>();

        // Inside the window: a metering point with a meter but no values is blocked before Settl is asked for its series.
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 1);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-1), hours: 1);
        _xellent.SeedContract(Account, MpId1, migratedOn: new DateTime(2023, 7, 1));
        _xellent.SeedContract(Account, MpId2, migratedOn: new DateTime(2023, 7, 1));
        InSettl((MpId1, ambiguousMpGuid), (MpId2, resolvedMpGuid));
        _mp.GetDeliveryStatusesResponse = id => id == ambiguousMpGuid
            ? (
                new GetDeliveryStatusesResponse
                {
                    DeliveryStatuses =
                    [
                        new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = firstStart } },
                        // Same first day, another start reason: Settl does not say which delivery it was.
                        new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.MoveIn, Period = new TimePeriodDto { Start = firstStart.AddHours(1) } }
                    ]
                },
                null)
            : (
                new GetDeliveryStatusesResponse
                {
                    DeliveryStatuses =
                    [
                        new DeliveryStatusDto
                        {
                            InDelivery = true,
                            StartReason = DeliveryStartReason.ChangeOfSupplier,
                            Period = new TimePeriodDto { Start = resolvedRevision }
                        }
                    ]
                },
                null);
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        try
        {
            var rc = await BuildRunner(outputDirectory, logs.Add).RunPostAllAsync("test", dryRun: true,
                continueOnError: false);

            Assert.Equal(0, rc);
            Assert.Equal(2, _mp.GetDeliveryStatusesCalls.Count);
            Assert.Single(_mp.GetTimeSeriesCalls);
            Assert.Equal(resolvedMpGuid, _mp.GetTimeSeriesCalls[0]);
            Assert.Contains($"SKIP MP {MpId1}: ambiguous delivery statuses (different start reasons on the first in-delivery day) (parent)", logs);
            Assert.Contains($"REVISION MP {MpId2}: {resolvedRevision:O}", logs);

            var csvPath = Path.Combine(outputDirectory, TimeSeriesRunner.AmbiguousRevisionCsvFileName);
            var lines = File.ReadAllLines(csvPath);
            Assert.Equal(
                "\"CustomerNumber\";\"MeteringPointIdentification\";\"Type\";\"SettlMeteringPointId\";\"ActiveInDeliveryCount\";\"ActivePeriodStarts\"",
                lines[0]);
            Assert.Equal(
                $"\"\";\"{MpId1}\";\"Parent\";\"{ambiguousMpGuid}\";\"2\";\"{firstStart:O} | {firstStart.AddHours(1):O}\"",
                lines[1]);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunPostAllAsync_UnresolvedDeliveryBoundary_SkipsOnlyAffectedMp_AndContinues()
    {
        var skippedMpGuid = Guid.NewGuid();
        var resolvedMpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var revision = new DateTimeOffset(supplyStart);
        var logs = new List<string>();

        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedContract(Account, MpId1, migratedOn: new DateTime(2024, 1, 2));
        _xellent.SeedContract(Account, MpId2, migratedOn: new DateTime(2024, 1, 2));
        InSettl((MpId1, skippedMpGuid), (MpId2, resolvedMpGuid));
        _mp.GetDeliveryStatusesResponse = id => id == skippedMpGuid
            ? (
                new GetDeliveryStatusesResponse
                {
                    DeliveryStatuses =
                    [
                        new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = revision } },
                        new DeliveryStatusDto
                        {
                            InDelivery = false,
                            Period = new TimePeriodDto { Start = revision.AddYears(-1), End = revision.AddDays(-1) }
                        }
                    ]
                },
                null)
            : (
                new GetDeliveryStatusesResponse
                {
                    DeliveryStatuses =
                    [
                        new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = revision } }
                    ]
                },
                null);
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: true,
            continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Equal(2, _mp.GetDeliveryStatusesCalls.Count);
        Assert.Single(_mp.GetTimeSeriesCalls);
        Assert.Equal(resolvedMpGuid, _mp.GetTimeSeriesCalls[0]);
        Assert.Contains(
            logs,
            line => line.StartsWith($"SKIP MP {MpId1}: delivery boundary unresolved", StringComparison.Ordinal));
        Assert.Contains($"REVISION MP {MpId2}: {revision:O}", logs);
    }

    [Theory]
    [InlineData("zero", false)]
    [InlineData("zero", true)]
    [InlineData("malformed", false)]
    [InlineData("malformed", true)]
    [InlineData("api", false)]
    [InlineData("api", true)]
    public async Task RunPostAllAsync_RevisionError_ObeysFailFastAndContinueOnError(
        string failureKind,
        bool continueOnError)
    {
        var failingMpGuid = Guid.NewGuid();
        var succeedingMpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        var goodRevision = new DateTimeOffset(2023, 6, 15, 0, 0, 0, TimeSpan.Zero);

        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedHourlyTimeSeries(MpId2, supplyStart.AddYears(-3), hours: 1);
        _xellent.SeedContract(Account, MpId1, migratedOn: new DateTime(2023, 6, 15));
        _xellent.SeedContract(Account, MpId2, migratedOn: new DateTime(2023, 6, 15));
        InSettl((MpId1, failingMpGuid), (MpId2, succeedingMpGuid));
        _mp.GetDeliveryStatusesResponse = id =>
        {
            if (id == succeedingMpGuid)
            {
                return (
                    new GetDeliveryStatusesResponse
                    {
                        DeliveryStatuses =
                        [
                            new DeliveryStatusDto
                            {
                                InDelivery = true,
                                StartReason = DeliveryStartReason.ChangeOfSupplier,
                                Period = new TimePeriodDto { Start = goodRevision }
                            }
                        ]
                    },
                    null);
            }

            return failureKind switch
            {
                "zero" => (new GetDeliveryStatusesResponse { DeliveryStatuses = [] }, null),
                "malformed" => (
                    new GetDeliveryStatusesResponse
                    {
                        DeliveryStatuses = [new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = null }]
                    },
                    null),
                "api" => (null, "delivery endpoint failed"),
                _ => throw new ArgumentOutOfRangeException(nameof(failureKind))
            };
        };
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        var rc = await BuildRunner().RunPostAllAsync("test", dryRun: true,
            continueOnError: continueOnError);

        Assert.Equal(1, rc);
        Assert.Equal(continueOnError ? 2 : 1, _mp.GetDeliveryStatusesCalls.Count);
        Assert.Equal(continueOnError ? 1 : 0, _mp.GetTimeSeriesCalls.Count);
    }

    [Fact]
    public async Task RunPostAllAsync_CompanySwitch_PostsOnlyTheSeriesWhoseAgreementCoversThePeriod()
    {
        // Production data for MP 571313113160561482 (2026-09-10):
        //   Xellent 'meh' series, agreement 2008-12-01 → 2023-12-31, points run on to 2024-01-01 22:00 UTC
        //   Xellent 'for' series, agreement 2024-01-01 → 2026-02-28, points from 2023-12-31 23:00 UTC
        //   Settl: InDelivery=false 2023-12-31T23:00Z → 2026-02-28T23:00Z, InDelivery=true from 2026-02-28T23:00Z
        // The run used to post 'meh' with EIP 2024-01-01 → 2023-12-31, which Settl rejected.
        var mpGuid = Guid.NewGuid();
        var tsGuid = Guid.NewGuid();
        var boundary = new DateTimeOffset(2023, 12, 31, 23, 0, 0, TimeSpan.Zero);
        var revision = new DateTimeOffset(2026, 2, 28, 23, 0, 0, TimeSpan.Zero);
        var logs = new List<string>();

        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2023, 12, 30, 23, 0, 0, DateTimeKind.Utc), hours: 48,
            companyId: "meh", agreementStart: new DateTime(2008, 12, 1), agreementEnd: new DateTime(2023, 12, 31));
        _xellent.SeedHourlyTimeSeries(MpId1, boundary.UtcDateTime, hours: 72,
            companyId: "for", agreementStart: new DateTime(2024, 1, 1), agreementEnd: new DateTime(2026, 2, 28));
        _xellent.SeedContract(Account, MpId1, companyId: "meh", partStart: new DateTime(2008, 12, 1), partEnd: new DateTime(2023, 12, 31));
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2024, 1, 1), partEnd: new DateTime(2026, 2, 28));
        InSettl((MpId1, mpGuid));
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses =
                [
                    new DeliveryStatusDto { InDelivery = false, Period = new TimePeriodDto { Start = boundary, End = revision } },
                    new DeliveryStatusDto { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier, Period = new TimePeriodDto { Start = revision } }
                ]
            },
            null);
        _mp.GetTimeSeriesResponse = _ => HourTs(tsGuid);

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.True(rc == 0, string.Join(Environment.NewLine, logs.Where(l => l.Contains("ERROR") || l.Contains("failed"))));
        Assert.Contains($"  MP {MpId1}: end=2026-03-01  start=2024-01-01", logs);
        var post = Assert.Single(_ts.PostCalls);
        Assert.Equal(72, post.Payload.Points.Count);
        Assert.Equal(boundary.UtcDateTime, post.Payload.Points[0].Time);
        Assert.Equal(revision, post.Payload.Revision);
        var eip = Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods);
        Assert.Equal(new DateOnly(2024, 1, 1), eip.Period!.Start);
        Assert.Equal(new DateOnly(2026, 3, 1), eip.Period.End);
    }

    [Theory]
    [InlineData("ChangeOfSupplier", true)]
    [InlineData("Na", true)]
    [InlineData(null, false)]
    public async Task RunPostAllAsync_NoInDeliveryTrue_FallsBackToCurrentDeliveryStatusStart(string? startReason, bool posts)
    {
        // Settl bug (2026-09): delivery from 2026-09-01 (Danish) is not marked InDelivery=true; the list
        // keeps one open InDelivery=false period from 2023-12-31. The MP's current delivery status still
        // has the start, with reason ChangeOfSupplier or Na (MP 571313113140168212). null = no current
        // delivery status at all. Xellent closed the migrated contract on 2026-08-31.
        var mpGuid = Guid.NewGuid();
        var cos = new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
        var boundary = new DateTimeOffset(2023, 12, 31, 23, 0, 0, TimeSpan.Zero);
        _xellent.SeedHourlyTimeSeries(MpId1, new DateTime(2026, 8, 30, 22, 0, 0, DateTimeKind.Utc), hours: 24);
        _xellent.SeedContract(Account, MpId1, partStart: new DateTime(2024, 1, 1), migratedOn: new DateTime(2026, 9, 1));
        InSettl((MpId1, mpGuid));
        _mp.GetDeliveryStatusesResponse = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses = [new DeliveryStatusDto { InDelivery = false, Period = new TimePeriodDto { Start = boundary } }]
            },
            null);
        _mp.GetByIdV4Response = _ => new MeteringPointV4Dto
        {
            DeliveryStatus = startReason == null
                ? null
                : new MeteringPointV4DeliveryStatusDto { Start = cos.UtcDateTime, InDelivery = false, StartReason = startReason }
        };
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        if (posts)
        {
            Assert.Equal(0, rc);
            Assert.Contains($"  MP {MpId1}: end=2026-09-01  start=2024-01-01", logs);
            var eip = Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods);
            Assert.Equal((new DateOnly(2024, 1, 1), new DateOnly(2026, 9, 1)), (eip.Period!.Start, eip.Period.End));
            Assert.Equal(cos, eip.Revision);
        }
        else
        {
            Assert.Equal(1, rc);
            Assert.Empty(_ts.PostCalls);
            Assert.Contains(logs, l => l.Contains("No delivery status has InDelivery=true.") && l.Contains("no current delivery status start"));
        }
    }

    [Fact]
    public async Task RunPostAllAsync_BackwardsAgreementAndPreviousOccupant_PostsOnlyTheAccountsOwnPeriod()
    {
        // Production data for MP 571313113140168212 (2026-09-11): one hourly series 'for' from 2024-10-07;
        // agreement 3104600 runs 2026-07-01 → 2026-06-30 (backwards); 486014 lived there 2024-10-07 →
        // 2025-02-28, account 493867 from 2025-03-01 → 2026-06-30; Settl delivery from 2026-07-01.
        var supplyStart = new DateTime(2026, 7, 1);
        InDeliveryFrom(supplyStart);
        var seriesStart = new DateTime(2024, 10, 6, 22, 0, 0, DateTimeKind.Utc);
        var hours = (int)(new DateTime(2026, 6, 30, 22, 0, 0, DateTimeKind.Utc) - seriesStart).TotalHours;
        _xellent.SeedHourlyTimeSeries(MpId1, seriesStart, hours: hours,
            companyId: "for", agreementStart: new DateTime(2026, 7, 1), agreementEnd: new DateTime(2026, 6, 30));
        _xellent.SeedContract("486014", MpId1, companyId: "for", partStart: new DateTime(2024, 10, 7), partEnd: new DateTime(2025, 2, 28));
        _xellent.SeedContract(Account, MpId1, companyId: "for", partStart: new DateTime(2025, 3, 1), partEnd: new DateTime(2026, 6, 30));
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.True(rc == 0, string.Join(Environment.NewLine, logs.Where(l => l.Contains("ERROR"))));
        Assert.Contains($"  MP {MpId1}: end=2026-07-01  start=2025-03-01", logs);
        var points = Assert.Single(_ts.PostCalls).Payload.Points;
        Assert.Equal(new DateTime(2025, 2, 28, 23, 0, 0, DateTimeKind.Utc), points[0].Time);
        var eip = Assert.Single(Assert.Single(_mp.AddEipCalls).Request.Periods);
        Assert.Equal((new DateOnly(2025, 3, 1), new DateOnly(2026, 7, 1)), (eip.Period!.Start, eip.Period.End));
    }

    [Theory]
    [InlineData("contract-open")]
    [InlineData("agreement-status")]
    [InlineData("agreement-end")]
    public async Task RunPostAllAsync_NotClosedAsAMigration_IsSkipped(string problem)
    {
        // Delivery from 2023-06-15, so the account's contract and its agreement should both end
        // 2023-06-14 and the agreement should be "Leverance ophørt" (5).
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        switch (problem)
        {
            case "contract-open":
                _xellent.SeedContract(Account, MpId1);
                break;
            case "agreement-status":
                _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart, agreementStatus: 7);
                break;
            case "agreement-end":
                _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart, agreementEnd: new DateTime(2023, 5, 31));
                break;
        }
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        // Not closed as a migration at COS − 1 means not migrated: a skip, not a failure (decided 2026-10-16).
        Assert.Equal(0, rc);
        Assert.Empty(_ts.PostCalls);
        var expected = problem == "contract-open"
            ? "contract does not end on 2023-06-14"
            : "agreement is not \"Leverance ophørt\"";
        Assert.Contains(logs, l => l.StartsWith($"SKIP MP {MpId1}: not closed as a migration — customer {Account}: {expected}", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(OutputDir, "skip-not-migrated.csv")));
        Assert.Contains(logs, l => l.StartsWith("  Skipped: 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunPostAllAsync_ALaterCurrentDeliveryStatus_DoesNotMoveTheWindow()
    {
        // Production 2026-10: Verdo took over 2023-06-15 by change of supplier; the metering point's current
        // delivery status in Settl starts later (the customer left again). The migration date still holds.
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        var mpGuid = Guid.NewGuid();
        InSettl((MpId1, mpGuid));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        _mp.GetByIdV4Response = _ => new MeteringPointV4Dto
        {
            DeliveryStatus = new MeteringPointV4DeliveryStatusDto { Start = new DateTime(2023, 6, 30, 22, 0, 0, DateTimeKind.Utc), InDelivery = false }
        };
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.Equal(mpGuid, Assert.Single(_mp.AddEipCalls).MpGuid);
        Assert.Contains(logs, l => l.Contains($"MP {MpId1}: end=2023-06-15", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, l => l.StartsWith("BLOCKED:", StringComparison.Ordinal));
    }

    // A Settl delivery history: each period runs until the next one starts, the last one stays open.
    private void DeliveryHistory(params (bool InDelivery, DateTime From, DeliveryStartReason Reason)[] periods) =>
        _mp.GetDeliveryStatusesResponse = _ => (new GetDeliveryStatusesResponse
        {
            DeliveryStatuses = periods.Select((p, i) => new DeliveryStatusDto
            {
                InDelivery = p.InDelivery,
                StartReason = p.Reason,
                Period = new TimePeriodDto
                {
                    Start = DanishMidnight(p.From),
                    End = i + 1 < periods.Length ? DanishMidnight(periods[i + 1].From) : default,
                },
            }).ToList()
        }, null);

    [Fact]
    public async Task RunPostAllAsync_SeveralInDelivery_TheFirstChangeOfSupplierIsTheMigration()
    {
        // As DataMigration: the first delivery decides; a later gap and return never replace it.
        var supplyStart = new DateTime(2023, 6, 15);
        DeliveryHistory(
            (false, new DateTime(2020, 1, 1), DeliveryStartReason.Na),
            (true, supplyStart, DeliveryStartReason.ChangeOfSupplier),
            (false, new DateTime(2023, 8, 1), DeliveryStartReason.Na),
            (true, new DateTime(2023, 8, 2), DeliveryStartReason.Na));
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        _mp.GetTimeSeriesResponse = _ => HourTs(Guid.NewGuid());
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.Single(_mp.AddEipCalls);
        Assert.Contains(logs, l => l.Contains($"MP {MpId1}: end=2023-06-15", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DeliveryStartReason.MoveIn, "first delivery in Settl is a move-in")]
    [InlineData(DeliveryStartReason.Na, "first delivery in Settl is not a change of supplier")]
    public async Task RunPostAllAsync_FirstDeliveryNotAChangeOfSupplier_IsSkippedAsNotMigrated(DeliveryStartReason reason, string stateReason)
    {
        var supplyStart = new DateTime(2023, 6, 15);
        DeliveryHistory((true, supplyStart, reason));
        _xellent.SeedHourlyTimeSeries(MpId1, supplyStart.AddYears(-1), hours: 24);
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.Empty(_ts.PostCalls);
        Assert.Contains(logs, l => l.StartsWith($"SKIP MP {MpId1}: not migrated — the first delivery in Settl", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.EndsWith($"1  {stateReason}", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(OutputDir, "skip-not-migrated.csv")));
    }

    [Fact]
    public async Task RunPostAllAsync_FirstDeliveryWithUnknownStartReason_IsBlocked()
    {
        var supplyStart = new DateTime(2023, 6, 15);
        DeliveryHistory((true, supplyStart, DeliveryStartReason.Unknown));
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, Guid.NewGuid()));
        var logs = new List<string>();

        Assert.Equal(0, await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false));

        Assert.Empty(_ts.PostCalls);
        Assert.Contains(logs, l => l.StartsWith($"BLOCKED:   MP {MpId1}: the first delivery in Settl", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(OutputDir, "blocked-unclear-delivery-dates-in-settl-first-delivery-has-start-reason-unknown.csv")));
    }
    [Fact]
    public async Task RunPostAllAsync_SeriesWithoutAgreement_BlocksTheMeteringPoint()
    {
        var mpGuid = Guid.NewGuid();
        var supplyStart = new DateTime(2023, 6, 15);
        InDeliveryFrom(supplyStart);
        _xellent.SeedHourlyTimeSeriesWithoutAgreement(MpId1, supplyStart.AddYears(-1), hours: 24, companyId: "xxx");
        _xellent.SeedContract(Account, MpId1, migratedOn: supplyStart);
        InSettl((MpId1, mpGuid));
        var logs = new List<string>();

        var rc = await BuildRunner(log: logs.Add).RunPostAllAsync("test", dryRun: false, continueOnError: false);

        Assert.Equal(0, rc);
        Assert.Empty(_ts.PostCalls);
        Assert.Contains(logs, l => l.Contains("Xellent series without an agreement period") && l.Contains("/xxx"));
    }
}
