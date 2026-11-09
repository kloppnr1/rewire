using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using System.Text.Json;
using TimeSeries.Cli.Services;
using TimeSeries.Core.Models;

namespace TimeSeries.Cli.Runners;

/// <summary>
/// The distinct reasons a metering point can be left out of a run. Each maps to its own CSV so an
/// operator can work one failure mode at a time instead of grepping the log.
/// </summary>
public enum TimeSeriesSkipCategory
{
    /// <summary>More than one time resolution in the window and --skip-resolution-change was set.</summary>
    ResolutionChange,

    /// <summary>More than one InDelivery=true delivery status, so the revision is not derivable.</summary>
    AmbiguousRevision,

    /// <summary>InDelivery=false periods exist but none ends at the revision.</summary>
    DeliveryBoundaryUnresolved,

    /// <summary>Several InDelivery=false periods end at the revision.</summary>
    DeliveryBoundaryAmbiguous,

    /// <summary>The matching InDelivery=false period has an unusable Period.</summary>
    DeliveryBoundaryInvalid,

    /// <summary>Every extracted point fell before the delivery boundary.</summary>
    NoPointsAfterBoundary,

    /// <summary>Settl already holds points in the window and --skip-if-existing-points was set.</summary>
    ExistingSettlData,

    /// <summary>
    /// Batch mode: no Xellent contract, on any account, runs on the day before delivery starts in
    /// Settl — the migrated relationship is missing (or was closed early) in Xellent.
    /// </summary>
    MissingInXellent,

    /// <summary>
    /// Batch mode: Settl does not mark the metering point as in delivery, and Xellent shows no
    /// migration on the day before its current delivery status starts — it was never migrated.
    /// </summary>
    NotMigrated,

    /// <summary>
    /// Known Xellent issue: values stored at a different resolution than Xellent says the metering point
    /// was read at, with no series of that resolution (MP 571313185621006160: hourly values where it reads
    /// quarter-hourly). Listed whether the metering point is then skipped or fails.
    /// </summary>
    KnownIssueXellentResolution,

    /// <summary>The metering point's current payer in Settl is in the 6##### series; those are not migrated.</summary>
    ExcludedPayer,

    /// <summary>No values in the window and Xellent status 5 "Oprettet": no meter is installed or connected, so there is nothing to post.</summary>
    NotInstalled
}

public sealed record MissingReadingsEntry(string MeteringPointId, Guid SettlId, string? Account,
    DateOnly? WindowStart, DateOnly? WindowEnd, string Status, string? Detail);

public sealed record MissingReadingsScanResult(IReadOnlyList<MissingReadingsEntry> Entries, int ExitCode);


public class TimeSeriesRunner
{
    public const string AmbiguousRevisionCsvFileName = "ambiguous-revisions.csv";

    /// <summary>
    /// One CSV per distinct skip code path, so an operator can see every metering point that was
    /// left behind and why. <see cref="AmbiguousRevisionCsvFileName"/> keeps its own richer schema
    /// (it carries the competing period starts) and is therefore not part of this map.
    /// </summary>
    private static readonly IReadOnlyDictionary<TimeSeriesSkipCategory, string> SkipCsvFileNames =
        new Dictionary<TimeSeriesSkipCategory, string>
        {
            [TimeSeriesSkipCategory.ResolutionChange] = "skip-resolution-change.csv",
            [TimeSeriesSkipCategory.DeliveryBoundaryUnresolved] = "skip-delivery-boundary-unresolved.csv",
            [TimeSeriesSkipCategory.DeliveryBoundaryAmbiguous] = "skip-delivery-boundary-ambiguous.csv",
            [TimeSeriesSkipCategory.DeliveryBoundaryInvalid] = "skip-delivery-boundary-invalid.csv",
            [TimeSeriesSkipCategory.NoPointsAfterBoundary] = "skip-no-points-after-boundary.csv",
            [TimeSeriesSkipCategory.ExistingSettlData] = "skip-existing-settl-data.csv",
            [TimeSeriesSkipCategory.MissingInXellent] = "skip-missing-in-xellent.csv",
            [TimeSeriesSkipCategory.NotMigrated] = "skip-not-migrated.csv",
            [TimeSeriesSkipCategory.KnownIssueXellentResolution] = "known-issue-xellent-resolution.csv",
            [TimeSeriesSkipCategory.ExcludedPayer] = "skip-payer-6-series.csv",
            [TimeSeriesSkipCategory.NotInstalled] = "skip-not-installed.csv"
        };

    /// <summary>Every skip artifact a run can produce, ambiguity CSV included. Used by the dashboard to bundle them.</summary>
    public static IReadOnlyList<string> AllSkipCsvFileNames { get; } =
        new[] { AmbiguousRevisionCsvFileName }.Concat(SkipCsvFileNames.Values).ToArray();

    private static readonly string[] SkipCsvHeader =
        ["CustomerNumber", "MeteringPointIdentification", "Type", "SettlMeteringPointId", "Revision", "DeliveryBoundary", "Detail"];

    private static readonly string[] AmbiguousRevisionCsvHeader =
        ["CustomerNumber", "MeteringPointIdentification", "Type", "SettlMeteringPointId", "ActiveInDeliveryCount", "ActivePeriodStarts"];

    // Every CSV this tool writes opens straight in Excel on a Danish machine: UTF-8 with a BOM,
    // semicolon between fields, CRLF, and every field quoted so a metering point number stays text
    // instead of turning into 5,71313E+17.
    private static readonly System.Text.UTF8Encoding CsvEncoding = new(true);

    internal static string CsvLine(IEnumerable<string> fields) => string.Join(";", fields.Select(EscapeCsv)) + "\r\n";

    // Appends one row, writing the header (with the BOM) when the file is new.
    private static void AppendCsvRow(string path, string[] header, IEnumerable<string> fields)
    {
        if (!File.Exists(path))
            File.WriteAllText(path, CsvLine(header), CsvEncoding);
        File.AppendAllText(path, CsvLine(fields), CsvEncoding);
    }

    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    private readonly string _connectionString;
    private readonly string _outputBaseDirectory;
    private readonly int _defaultTimeSeriesYears;
    private readonly ISettlTimeSeriesClient? _settlTimeSeriesClient;
    private readonly ISettlTimeSeriesMeteringPointClient? _settlMeteringPointClient;
    private readonly ISettlCustomerClient? _settlCustomerClient;
    private readonly ISettlPayerClient? _settlPayerClient;
    private readonly Func<XellentDbContext>? _contextFactory;
    private readonly Action<string> _defaultLog;
    private Action<string> _log;

    // Callback contract — mirrors DataMigration.Migration.Runners.CreateRunner so a single
    // dashboard host (Dashboard.Api) can wire log/progress/phase streaming uniformly.
    public Action<string>? OnLogLine
    {
        set => _log = value ?? _defaultLog;
    }
    public Action<int, int>? OnProgress { get; set; }

    /// <summary>
    /// Progress through a long step (step name, done, total), called often and from parallel work. A
    /// console host shows it as one line that updates in place. Without it, progress is logged as
    /// ordinary lines every 250 items.
    /// </summary>
    public Action<string, int, int>? OnStepProgress { get; set; }

    /// <summary>
    /// A post --all run's running count per state, as one line (e.g. "1,200/4,273 checked · done 56,010 ·
    /// ready 12 · …"), called for every metering point. A console host shows it as one line that updates in place.
    /// </summary>
    public Action<string>? OnStatus { get; set; }

    // The states of the time series migration process: every metering point a post --all run looks at ends
    // the run in exactly one, with a reason. "Wrong" (sent, but different from Xellent) needs a value
    // comparison and is not counted here.
    private static readonly string[] StateOrder = PostRunStates.Order;
    private readonly Dictionary<string, SortedDictionary<string, int>> _states = new(StringComparer.Ordinal);
    private TimeSeriesSkipCategory? _lastSkip;

    // One wording per reason, whichever check finds it.
    private const string NoContractReason = "no Xellent contract at COS − 1";
    private const string SeveralHoldersReason = "several Xellent account holders at COS − 1";
    private const string NotClosedReason = "contract not closed as a migration at COS − 1";
    private const string UnknownStartReason = "unclear delivery dates in Settl (first delivery has start reason Unknown)";
    private const string ResolutionSwitchReason = "resolution switch – waiting for Settl to confirm series";
    private const string BlankSubTypeReason = "subtype blank in Xellent";

    // EXU_METERTABLETRADE.METERINGPOINTDHSUBTYPE: 1 = physical, 2 = virtual, 3 = calculated. Anything else is blank.
    private static readonly int[] KnownSubTypes = [1, 2, 3];

    // The state reason for a first delivery in Settl that is not a change of supplier.
    private static string FirstDeliveryNotMigratedReason(string? detail) =>
        detail?.Contains("move-in", StringComparison.Ordinal) == true
            ? "first delivery in Settl is a move-in"
            : "first delivery in Settl is not a change of supplier";

    private static string SkipReason(TimeSeriesSkipCategory category) => category switch
    {
        TimeSeriesSkipCategory.NotInstalled => "no meter installed (Xellent status Oprettet)",
        TimeSeriesSkipCategory.NoPointsAfterBoundary => "no values after the delivery boundary",
        TimeSeriesSkipCategory.ResolutionChange => "resolution change (--skip-resolution-change)",
        TimeSeriesSkipCategory.ExistingSettlData => "Settl already has points (--skip-if-existing-points)",
        TimeSeriesSkipCategory.DeliveryBoundaryUnresolved or TimeSeriesSkipCategory.DeliveryBoundaryAmbiguous
            or TimeSeriesSkipCategory.DeliveryBoundaryInvalid => $"unclear delivery dates in Settl ({category})",
        TimeSeriesSkipCategory.MissingInXellent => NoContractReason,
        TimeSeriesSkipCategory.NotMigrated => NotClosedReason,
        TimeSeriesSkipCategory.ExcludedPayer => "payer in the 6##### series",
        _ => category.ToString(),
    };

    private void CountState(string state, string reason, int count = 1)
    {
        if (_markDoneMode || count <= 0)
            return;
        if (!_states.TryGetValue(state, out var reasons))
            _states[state] = reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        reasons[reason] = reasons.GetValueOrDefault(reason) + count;
    }

    /// <summary>The operator's list of metering points excluded by hand, in the environment's snapshot folder.</summary>
    public const string ExcludedFileName = "excluded.csv";

    // One row per metering point: identification, then the reason (further columns, such as who and when, are
    // for the operator). Semicolon or comma between fields; a header row is ignored.
    internal static Dictionary<string, string> ReadExcluded(string path)
    {
        var excluded = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return excluded;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var fields = line.Split(line.Contains(';') ? ';' : ',').Select(f => f.Trim().Trim('"').Trim()).ToArray();
            if (fields[0].Length == 0 || !fields[0].All(char.IsDigit))
                continue;
            excluded[fields[0]] = fields.Length > 1 && fields[1].Length > 0 ? fields[1] : "no reason given";
        }
        return excluded;
    }

    private static readonly string[] BlockedCsvHeader =
        ["MeteringPointIdentification", "Type", "SettlMeteringPointId", "CustomerNumber", "Reason", "Detail"];

    // One file per blocked reason, so each group can be handed to whoever has to fix it.
    private static string BlockedCsvFileName(string reason) =>
        "blocked-" + System.Text.RegularExpressions.Regex.Replace(reason.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') + ".csv";

    /// <summary>Counts a blocked metering point and lists it in the CSV for its reason. A resolution switch
    /// also gets the DataHub message behind it (<paramref name="dataHubMessage"/>), in a column of its own.</summary>
    private void CountBlocked(string reason, string meteringPoint, Guid settlId, string? account, string detail,
        string? dataHubMessage = null)
    {
        if (_markDoneMode)
            return;
        CountState("Blocked", reason);
        Directory.CreateDirectory(_outputBaseDirectory);
        string[] row = [meteringPoint, _meteringPointType, settlId.ToString(), account ?? "", reason, detail];
        AppendCsvRow(Path.Combine(_outputBaseDirectory, BlockedCsvFileName(reason)),
            dataHubMessage == null ? BlockedCsvHeader : [.. BlockedCsvHeader, "DataHubMessage"],
            dataHubMessage == null ? row : [.. row, dataHubMessage]);
    }

    /// <summary>
    /// Per switch date, the first incoming DK_NotifyMasterDataMeteringPoint (by Creation) whose Occurrence falls on
    /// that date (Danish calendar date: 2025-12-08T23:00:00Z is 2025-12-09) and whose MeterReadingOccurrence is the
    /// resolution switched to (PT15M or PT1H). One line each, in date order, joined by " | "; a date with no such
    /// message adds nothing. The installation address is taken out: no personal data in the reports.
    /// </summary>
    internal static string DataHubMessagesOn(IEnumerable<string> messages, IReadOnlyDictionary<DateOnly, string> readingOccurrenceFrom)
    {
        var matches = new List<(DateOnly Date, DateTimeOffset Created, string Xml)>();
        foreach (var message in messages)
        {
            System.Xml.Linq.XDocument doc;
            try
            {
                doc = System.Xml.Linq.XDocument.Parse(message);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }
            if (doc.Root?.Name.LocalName != "DK_NotifyMasterDataMeteringPoint")
                continue;
            DateTimeOffset? Time(string name) =>
                doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name) is { } e
                && DateTimeOffset.TryParse(e.Value.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            if (Time("Occurrence") is not { } occurrence
                || !readingOccurrenceFrom.TryGetValue(ToDanishDate(occurrence), out var expected)
                || doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "MeterReadingOccurrence")?.Value.Trim() != expected)
                continue;
            System.Xml.Linq.Extensions.Remove(doc.Descendants().Where(e => e.Name.LocalName == "InstallationLocationAddress"));
            matches.Add((ToDanishDate(occurrence), Time("Creation") ?? DateTimeOffset.MaxValue,
                doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting)));
        }
        return string.Join(" | ", matches
            .GroupBy(m => m.Date)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(m => m.Created).First().Xml));
    }

    /// <summary>The DataHub message behind each switch: the reading period starting on the switch date says
    /// which resolution it switched to.</summary>
    private async Task<string> DataHubMessagesOnAsync(string meteringPoint, IReadOnlyCollection<DateOnly> switchDates,
        IEnumerable<TimeSeriesExtractionService.ReadingPeriod> periods)
    {
        var readingOccurrenceFrom = periods
            .Where(p => switchDates.Contains(p.Start))
            .GroupBy(p => p.Start)
            .ToDictionary(g => g.Key, g => g.First().Resolution == "Quarter" ? "PT15M" : "PT1H");
        await using var context = BuildXellentContext();
        var messages = await new TimeSeriesExtractionService(context, BatchDeliveryCategory)
            .GetIncomingDataHubMessagesAsync(meteringPoint);
        return DataHubMessagesOn(messages, readingOccurrenceFrom);
    }

    /// <summary>
    /// snapshot --refresh-blocked: the metering points the last post --all listed in its blocked-*.csv files
    /// get their stored Settl and Xellent reads dropped, so the seed that follows reads only those again.
    /// Everything else stays as stored.
    /// </summary>
    private void ForgetBlockedReads()
    {
        var files = Directory.Exists(_outputBaseDirectory)
            ? Directory.GetFiles(_outputBaseDirectory, "blocked-*.csv")
            : [];
        var identifications = new HashSet<string>(StringComparer.Ordinal);
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var rows = File.ReadAllLines(file)
                .Select(l => l.TrimStart((char)0xFEFF).Split(';').Select(f => f.Trim().Trim('"')).ToArray())
                .ToList();
            if (rows.Count == 0)
                continue;
            var idColumn = Array.IndexOf(rows[0], "MeteringPointIdentification");
            var guidColumn = Array.IndexOf(rows[0], "SettlMeteringPointId");
            foreach (var row in rows.Skip(1))
            {
                if (idColumn >= 0 && idColumn < row.Length && row[idColumn].Length > 0)
                    identifications.Add(row[idColumn]);
                if (guidColumn >= 0 && guidColumn < row.Length && Guid.TryParse(row[guidColumn], out var g))
                    guids.Add(g.ToString());
            }
        }
        if (identifications.Count == 0)
        {
            _log($"Refresh blocked: no blocked metering points listed in {Path.GetFullPath(_outputBaseDirectory)} (blocked-*.csv). " +
                 "Run post --all --dry-run first.");
            return;
        }

        // The points-summary answers are keyed by time series; take those series before dropping the list.
        var seriesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guid in guids)
            if (_snapshot!.TryGet<List<TimeSeriesInfoDto>>(SnapshotTimeSeries, guid, out var series) && series != null)
                foreach (var ts in series)
                    seriesIds.Add(ts.Id.ToString());

        var dropped = 0;
        foreach (var kind in new[] { SnapshotDeliveryStatuses, SnapshotCurrentDeliveryStatus, SnapshotTimeSeries })
            dropped += _snapshot!.Forget(kind, guids.Contains);
        dropped += _snapshot!.Forget(SnapshotPointsSummary, key => seriesIds.Contains(key.Split('|')[0]));
        foreach (var kind in new[] { SnapshotXellentContracts, SnapshotXellentChildren, SnapshotMeterPointStatus })
            dropped += _snapshot.Forget(kind, identifications.Contains);
        dropped += _snapshot.Forget(SnapshotXellentPointCounts, key => identifications.Contains(key.Split('|')[0]));
        _log($"Refresh blocked: {identifications.Count} metering point(s) from {files.Length} blocked file(s); " +
             $"{dropped} stored read(s) dropped and read again, the rest kept.");
    }

    private int StateTotal(string state) => _states.TryGetValue(state, out var reasons) ? reasons.Values.Sum() : 0;

    private string StateLine() => PostRunStates.FormatLine(StateTotal);

    // states.json for the post --all run under way (null outside one), so progress can show it.
    private (string Directory, string Environment, bool DryRun, DateTimeOffset StartedAt)? _statesRun;
    private int _statesChecked, _statesTotal;
    private DateTimeOffset _statesLastWrite = DateTimeOffset.MinValue;
    private bool _statesCompleted;

    private void WriteStates(string status, bool force)
    {
        if (_statesRun is not { } run || _markDoneMode)
            return;
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _statesLastWrite < TimeSpan.FromSeconds(5))
            return;
        _statesLastWrite = now;
        new PostRunStates(run.Environment, run.DryRun, status, _statesChecked, _statesTotal, run.StartedAt, now,
                _states.ToDictionary(s => s.Key, s => (IReadOnlyDictionary<string, int>)new Dictionary<string, int>(s.Value), StringComparer.Ordinal))
            .TryWrite(run.Directory);
    }

    // A send that stopped: failed when a call to Settl itself went wrong, blocked when something has to be fixed first.
    private static (string State, string Reason) ClassifyPostError(string? error)
    {
        if (error == null)
            return ("Failed", "stopped without a reason (see log)");
        string[] settlCalls = ["Settl MP lookup failed", "GetTimeSeries failed", "GetPointsSummary failed",
            "Time-series POST failed", "externallyInvoicedPeriod POST failed", "Revision resolution failed"];
        if (settlCalls.Any(c => error.Contains(c, StringComparison.Ordinal)))
            return ("Failed", "a call to Settl went wrong");
        if (error.Contains("No Settl TimeSeries found", StringComparison.Ordinal))
        {
            var resolution = System.Text.RegularExpressions.Regex.Match(error, @"segment (\S+)").Groups[1].Value;
            return ("Blocked", $"missing {resolution} series in Settl".Replace("  ", " "));
        }
        if (error.Contains("without an agreement period", StringComparison.Ordinal))
            return ("Blocked", "Xellent series without an agreement period");
        if (error.Contains("a meter is installed", StringComparison.Ordinal))
            return ("Blocked", "meter installed, but no values in Xellent");
        if (error.Contains("Conversion failed", StringComparison.Ordinal))
            return ("Blocked", "hourly values cannot be split into quarters");
        if (error.Contains("Earlier post not finished", StringComparison.Ordinal))
            return ("Blocked", "earlier post did not finish - check in Settl");
        if (error.Contains("not found in Settl", StringComparison.Ordinal))
            return ("Blocked", "metering point not found in Settl");
        return ("Blocked", "other (see log)");
    }

    // progress.json for a running snapshot (null outside snapshot). Never allowed to slow or break the run.
    private SnapshotProgressWriter? _progressWriter;

    private void ReportProgress(string step, int done, int total)
    {
        _progressWriter?.Report(step, done, total);
        if (OnStepProgress != null)
            OnStepProgress(step, done, total);
        else if (done % 250 == 0 || done == total)
            _log($"  ...{step}: {done}/{total}");
    }
    public Action<string?>? OnPhase { get; set; }

    public TimeSeriesRunner(
        string connectionString,
        string outputBaseDirectory,
        int defaultTimeSeriesYears = 3,
        ISettlTimeSeriesClient? settlTimeSeriesClient = null,
        ISettlTimeSeriesMeteringPointClient? settlMeteringPointClient = null,
        ISettlCustomerClient? settlCustomerClient = null,
        Func<XellentDbContext>? contextFactory = null,
        Action<string>? log = null,
        ISettlPayerClient? settlPayerClient = null)
    {
        _connectionString = connectionString;
        _outputBaseDirectory = outputBaseDirectory;
        _defaultTimeSeriesYears = defaultTimeSeriesYears;
        _settlTimeSeriesClient = settlTimeSeriesClient;
        _settlMeteringPointClient = settlMeteringPointClient;
        _settlCustomerClient = settlCustomerClient;
        _settlPayerClient = settlPayerClient;
        _contextFactory = contextFactory;
        _defaultLog = log ?? (msg => Console.WriteLine(msg));
        _log = _defaultLog;
    }

    public async Task<int> RunExtractAsync(ExtractOptions options)
    {
        if (options.IncludeOutgoingPreview && options.SimulateMissing)
            return Error("Outgoing previews cannot include simulated values.");
        _log("=== Time Series Extraction ===");
        _log("");

        var startDate = options.StartDate ?? DateTime.Today.AddYears(-_defaultTimeSeriesYears);
        var endDate = options.EndDate ?? DateTime.Today;
        var deliveryCategory = options.DeliveryCategory ?? "El-ekstern";

        string runTitle;
        if (options.MeteringPointId != null)
            runTitle = $"timeseries_{options.MeteringPointId}";
        else if (options.AccountNumber != null)
            runTitle = $"timeseries_{options.AccountNumber}";
        else
            return Error("Either --account or --metering-point must be specified.");

        _log($"Mode: {GetModeDescription(options.MeteringPointId, options.AccountNumber)}");
        _log($"Delivery Category: {deliveryCategory}");
        _log($"Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        if (options.IncludeChildren) _log("Include children: Yes");
        if (options.SimulateMissing) _log("Simulate missing: Yes");
        _log("");

        await using var context = BuildXellentContext();

        if (!await TestConnectionAsync(context))
            return 1;

        var (meteringPointIds, accounts) = await ResolveMeteringPointsAsync(
            context, options.MeteringPointId, options.AccountNumber, deliveryCategory, options.IncludeChildren);

        if (meteringPointIds.Length == 0)
        {
            _log("No metering points found for this account.");
            return 0;
        }

        var outputDirectory = Path.Combine(_outputBaseDirectory, runTitle);

        if (Directory.Exists(outputDirectory))
        {
            _log($"Clearing existing output directory: {outputDirectory}");
            Directory.Delete(outputDirectory, recursive: true);
        }
        Directory.CreateDirectory(outputDirectory);

        _log($"Output: {outputDirectory}");
        _log("");

        var timeSeries = await ExtractTimeSeriesAsync(
            context, accounts, meteringPointIds, deliveryCategory, options.IncludeChildren, startDate, endDate);

        if (options.SimulateMissing)
        {
            timeSeries = FillGaps(timeSeries, startDate, endDate);
        }

        _log("Exporting TimeSeries to JSON files...");
        var jsonExportService = new JsonExportService();
        var fileCount = await jsonExportService.ExportTimeSeriesAsync(timeSeries, outputDirectory, _log);
        if (options.IncludeOutgoingPreview)
            await jsonExportService.ExportOutgoingPreviewsAsync(timeSeries, outputDirectory, options.DeliveryBoundary);
        _log($"Exported {fileCount} TimeSeries JSON files");

        _log("");
        _log("Time Series extraction completed successfully!");
        _log($"JSON files created in: {Path.GetFullPath(outputDirectory)}");

        return 0;
    }

    public Task<int> RunPostAsync(PostOptions options, CancellationToken cancellationToken = default)
    {
        if (options.ResetAmbiguityCsv)
            ResetSkipArtifacts();
        return RunPostCoreAsync(options, cancellationToken);
    }

    private async Task<int> RunPostCoreAsync(PostOptions options, CancellationToken cancellationToken)
    {
        var dryRun = options.DryRun;
        // Inside a batch run the run's own header already says all of this, once; per metering point only
        // what is about that metering point is logged.
        var inBatch = options.InBatch;
        void LogSetup(string message)
        {
            if (!inBatch)
                _log(message);
        }
        LogSetup(dryRun ? "=== Time Series Post to Settl (DRY RUN) ===" : "=== Time Series Post to Settl ===");
        LogSetup("");

        if (_settlTimeSeriesClient == null || _settlMeteringPointClient == null)
            return Error("Settl API clients not configured. DI registration missing.");

        var startDate = options.StartDate ?? DateTime.Today.AddYears(-_defaultTimeSeriesYears);
        var endDate = options.EndDate ?? DateTime.Today;
        var deliveryCategory = options.DeliveryCategory ?? "El-ekstern";

        if (options.MeteringPointId == null && options.AccountNumber == null)
            return Error("Either --account or --metering-point must be specified.");

        LogSetup($"Environment: {options.Environment}");
        if (dryRun) LogSetup("Dry run: Yes (no mutations will be made)");
        LogSetup($"Mode: {GetModeDescription(options.MeteringPointId, options.AccountNumber)}");
        LogSetup($"Delivery Category: {deliveryCategory}");
        LogSetup($"Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        if (options.IncludeChildren) LogSetup("Include children: Yes");
        LogSetup("");

        _settlMeteringPointClient.SetDeliveryCategory(deliveryCategory);

        await using var context = BuildXellentContext();

        // A batch run tested the connection once at its start.
        if (!inBatch && !await TestConnectionAsync(context))
            return 1;

        OnPhase?.Invoke("resolve-metering-point");
        var (meteringPointIds, accounts) = await ResolveMeteringPointsAsync(
            context, options.MeteringPointId, options.AccountNumber, deliveryCategory, options.IncludeChildren, quiet: inBatch);

        if (meteringPointIds.Length == 0)
        {
            _log("No metering points found.");
            OnPhase?.Invoke(null);
            return 0;
        }

        OnPhase?.Invoke("extract-xellent");
        // A batch dry run knows the metering point's delivery boundary up front and posts nothing, so it
        // only counts the points: what it reports and checks is the same, without reading every point.
        var countOnly = dryRun && inBatch && options.MeteringPointId != null && options.ResolvedRevision != null;
        var timeSeries = countOnly
            ? [await CountTimeSeriesAsync(context, options.MeteringPointId!, deliveryCategory, startDate, endDate, options.ResolvedDeliveryBoundary)]
            : await ExtractTimeSeriesAsync(
                context, accounts, meteringPointIds, deliveryCategory, options.IncludeChildren, startDate, endDate, quiet: inBatch);

        // A batch metering point Xellent has no values for. No meter installed: a skip. A meter but no
        // readings: an error.
        if (inBatch && timeSeries.Count == 0 && options.MeteringPointId != null)
        {
            OnPhase?.Invoke(null);
            if (await MeterPointStatusAsync(context, options.MeteringPointId) != MeterPointStatusCreated)
            {
                _log($"ERROR:   No values in Xellent in the window for MP {options.MeteringPointId}, and a meter is installed.");
                return 1;
            }
            _log($"SKIP MP {options.MeteringPointId}: no meter installed (Xellent status Oprettet){TypeSuffix()}");
            RecordSkip(TimeSeriesSkipCategory.NotInstalled, options.CustomerNumber ?? options.AccountNumber,
                options.MeteringPointId, options.MeteringPointGuid, options.ResolvedRevision, options.ResolvedDeliveryBoundary,
                "no values, Xellent status Oprettet");
            options.Skipped = true;
            return 0;
        }

        // Known Xellent issue: values stored at the wrong resolution. Listed in their own file whatever
        // happens next, so every such metering point can be found.
        foreach (var mpTs in timeSeries.Where(t => t.ValuesWithWrongResolution.Count > 0))
            RecordSkip(TimeSeriesSkipCategory.KnownIssueXellentResolution, options.CustomerNumber ?? options.AccountNumber,
                mpTs.MeteringPointId, options.MeteringPointGuid, options.ResolvedRevision, options.ResolvedDeliveryBoundary,
                string.Join("; ", mpTs.ValuesWithWrongResolution));

        LogSetup("");
        LogSetup(dryRun ? "Checking against Settl (dry run — nothing is posted)..." : "Posting to Settl API...");
        LogSetup("");

        var posted = 0;
        var skippedSafety = 0;
        var pointsPosted = 0;
        var failedMps = new List<string>();
        OnPhase?.Invoke("post-timeseries");
        OnProgress?.Invoke(0, timeSeries.Count);

        // Per-MP processing as a local function so a failure can become either an abort (default)
        // or a recorded error the run continues past (--force / ContinueOnError), mirroring
        // DataMigration's CreateRunner. Returns the error message (null = success) plus the number
        // of points posted before the failure, so the summary stays accurate either way. Skipped=true
        // means "no-op for safety reasons" (e.g. existing Settl data) — counted separately from
        // both posted and failed.
        async Task<(string? Error, int Points, bool Skipped)> PostMeteringPointAsync(MeteringPointTimeSeriesDto mpTs)
        {
            var points = 0;

            Guid mpGuid;
            if (options.MeteringPointGuid is { } preResolvedGuid)
            {
                mpGuid = preResolvedGuid;
            }
            else
            {
                var (mpInfo, searchError) = await _settlMeteringPointClient.SearchByIdentificationAsync(
                    mpTs.MeteringPointId, cancellationToken);
                if (searchError != null)
                    return ($"  Settl MP lookup failed: {searchError}", points, false);
                if (mpInfo == null)
                    return ($"  Metering point {mpTs.MeteringPointId} not found in Settl.", points, false);
                mpGuid = mpInfo.Id;
            }

            DateTimeOffset revision;
            DateTimeOffset? deliveryBoundary;
            if (options.ResolvedRevision is { } preResolvedRevision)
            {
                if (preResolvedRevision == default)
                    return ($"  Pre-resolved revision is default for MP {mpTs.MeteringPointId} ({mpGuid}).", points, false);
                revision = preResolvedRevision;
                deliveryBoundary = options.ResolvedDeliveryBoundary;
                if (deliveryBoundary is { } preResolvedBoundary && preResolvedBoundary == default)
                    return ($"  Pre-resolved delivery boundary is default for MP {mpTs.MeteringPointId} ({mpGuid}).", points, false);
            }
            else
            {
                var resolution = await ResolveRevisionAsync(mpGuid, cancellationToken);
                if (resolution.Outcome == RevisionResolutionOutcome.Ambiguous)
                {
                    var reason = $"ambiguous delivery statuses ({resolution.ActiveStatuses.Count} InDelivery=true)";
                    _log($"SKIP MP {mpTs.MeteringPointId}: {reason}{TypeSuffix()}");
                    RecordAmbiguousRevision(
                        options.CustomerNumber ?? options.AccountNumber,
                        mpTs.MeteringPointId,
                        mpGuid,
                        resolution.ActiveStatuses);
                    return (null, points, true);
                }
                if (resolution.Outcome == RevisionResolutionOutcome.SafetySkip)
                {
                    _log($"REVISION MP {mpTs.MeteringPointId}: {resolution.Revision:O}");
                    _log($"SKIP MP {mpTs.MeteringPointId}: {resolution.SkipReason}{TypeSuffix()}");
                    RecordSkip(
                        resolution.SkipCategory!.Value,
                        options.CustomerNumber ?? options.AccountNumber,
                        mpTs.MeteringPointId,
                        mpGuid,
                        resolution.Revision,
                        null,
                        resolution.SkipReason ?? "");
                    return (null, points, true);
                }
                if (resolution.Outcome == RevisionResolutionOutcome.NotMigrated)
                {
                    _log($"SKIP MP {mpTs.MeteringPointId}: not migrated — {resolution.SkipReason}{TypeSuffix()}");
                    RecordSkip(TimeSeriesSkipCategory.NotMigrated, options.CustomerNumber ?? options.AccountNumber,
                        mpTs.MeteringPointId, mpGuid, null, null, resolution.SkipReason ?? "");
                    return (null, points, true);
                }
                if (resolution.Outcome == RevisionResolutionOutcome.Unclear)
                    return ($"  Unclear delivery dates in Settl for MP {mpTs.MeteringPointId} ({mpGuid}): {resolution.SkipReason}", points, false);
                if (resolution.Outcome == RevisionResolutionOutcome.Error)
                    return ($"  Revision resolution failed for MP {mpTs.MeteringPointId} ({mpGuid}): {resolution.Error}", points, false);

                revision = resolution.Revision;
                deliveryBoundary = resolution.DeliveryBoundary;
            }

            _log($"REVISION MP {mpTs.MeteringPointId}: {revision:O}");

            if (mpTs.SeriesWithoutAgreement.Count > 0)
                return ($"  Xellent series without an agreement period for MP {mpTs.MeteringPointId} " +
                        $"(RecId/CompanyId: {string.Join(", ", mpTs.SeriesWithoutAgreement)}) — their points have no period to go in.",
                        points, false);

            var originalPointCount = mpTs.Segments.Sum(PointCount) + (mpTs.CountedPointsBeforeBoundary ?? 0);
            // No values: a skip only when no meter is installed (Xellent status Oprettet), otherwise an error.
            if (originalPointCount == 0)
            {
                if (await MeterPointStatusAsync(context, mpTs.MeteringPointId) != MeterPointStatusCreated)
                    return ($"  No values in Xellent in the window for MP {mpTs.MeteringPointId}, and a meter is installed.", points, false);
                _log($"SKIP MP {mpTs.MeteringPointId}: no meter installed (Xellent status Oprettet){TypeSuffix()}");
                RecordSkip(TimeSeriesSkipCategory.NotInstalled, options.CustomerNumber ?? options.AccountNumber,
                    mpTs.MeteringPointId, mpGuid, revision, deliveryBoundary, "no values, Xellent status Oprettet");
                return (null, points, true);
            }
            var filteredSegments = mpTs.Segments
                .Select(segment => segment.CountedPoints != null
                    ? segment // counted on or after the boundary already
                    : new TimeSeriesSegmentDto
                    {
                        TimeResolution = segment.TimeResolution,
                        ReadingResolution = segment.ReadingResolution,
                        CompanyId = segment.CompanyId,
                        PeriodStart = segment.PeriodStart,
                        PeriodEnd = segment.PeriodEnd,
                        Points = deliveryBoundary is { } boundary
                            ? segment.Points.Where(point => point.TimeOfValue >= boundary).ToList()
                            : segment.Points.ToList()
                    })
                .Where(segment => PointCount(segment) > 0)
                .ToList();
            var includedPointCount = filteredSegments.Sum(PointCount);
            var excludedPointCount = originalPointCount - includedPointCount;
            var boundaryText = deliveryBoundary?.ToString("O") ?? "none";
            _log($"DELIVERY FILTER MP {mpTs.MeteringPointId}: boundary={boundaryText}, excluded={excludedPointCount}, included={includedPointCount}");

            if (deliveryBoundary != null && originalPointCount > 0 && filteredSegments.Count == 0)
            {
                _log($"SKIP MP {mpTs.MeteringPointId}: no points on or after delivery boundary ({boundaryText}){TypeSuffix()}");
                RecordSkip(
                    TimeSeriesSkipCategory.NoPointsAfterBoundary,
                    options.CustomerNumber ?? options.AccountNumber,
                    mpTs.MeteringPointId,
                    mpGuid,
                    revision,
                    deliveryBoundary,
                    $"all {originalPointCount} extracted points fall before the delivery boundary");
                return (null, points, true);
            }

            // Use only included outgoing resolutions, identical in counted and live runs.
            var outgoingResolutions = filteredSegments.Select(OutgoingTimeSeries.Resolution)
                .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToArray();
            var convertedHours = filteredSegments.Where(OutgoingTimeSeries.RequiresConversion).Sum(PointCount);
            // Dashboard contract: describes the plan, including candidates later skipped or blocked.
            // Counts are after delivery filtering; zero means no conversion, not missing evidence.
            _log($"OUTGOING MP {mpTs.MeteringPointId}: resolutions={(outgoingResolutions.Length == 0 ? "none" : string.Join("+", outgoingResolutions))}, " +
                 $"convertedHours={convertedHours}, convertedQuarters={checked(convertedHours * 4)}");
            if (options.SkipResolutionChange && outgoingResolutions.Length > 1)
            {
                var reason = $"resolution change ({string.Join(" → ", outgoingResolutions)})";
                _log($"SKIP MP {mpTs.MeteringPointId}: {reason}{TypeSuffix()}");
                RecordSkip(TimeSeriesSkipCategory.ResolutionChange, options.CustomerNumber ?? options.AccountNumber,
                    mpTs.MeteringPointId, mpGuid, revision, deliveryBoundary, reason);
                return (null, points, true);
            }

            var (tsList, tsError) = await GetTimeSeriesListAsync(mpGuid, cancellationToken);
            if (tsError != null)
                return ($"  GetTimeSeries failed: {tsError}", points, false);
            tsList ??= [];
            var effectiveWindowStart = DateOnly.FromDateTime(startDate);
            if (deliveryBoundary is { } effectiveBoundary)
            {
                var boundaryDate = ToDanishDate(effectiveBoundary);
                if (boundaryDate > effectiveWindowStart)
                    effectiveWindowStart = boundaryDate;
            }

            // Pre-check: hvis brugeren har bedt om --skip-if-existing-points, så spørg Settl om
            // hver matchet TS allerede har points i [startDate, endDate]. Ét segment med data
            // → spring hele MP'et over (atomic — vi vil ikke have half-migrated MPs).
            if (options.SkipIfExistingPoints)
            {
                var summaryFrom = effectiveWindowStart;
                var summaryTo = DateOnly.FromDateTime(endDate);
                foreach (var segment in filteredSegments)
                {
                    var (resLength, resUnit) = ToSettlResolution(OutgoingTimeSeries.Resolution(segment));
                    var matchingTs = tsList.FirstOrDefault(t =>
                        t.TimeResolution?.Length == resLength &&
                        string.Equals(t.TimeResolution?.Unit, resUnit, StringComparison.OrdinalIgnoreCase));
                    if (matchingTs == null)
                        continue;

                    var (summary, summaryError) = await GetPointsSummaryInYearChunksAsync(
                        matchingTs.Id, summaryFrom, summaryTo, cancellationToken);
                    if (summaryError != null)
                        return ($"  GetPointsSummary failed: {summaryError}", points, false);
                    if (summary != null && summary.Count > 0)
                    {
                        // Samme kontrakt-format som --skip-resolution-change-skip (Dashboard
                        // useTimeSeriesRun.ts-parseren matcher uændret).
                        var fromStr = summary.From?.ToString("yyyy-MM-dd") ?? "?";
                        var toStr = summary.To?.ToString("yyyy-MM-dd") ?? "?";
                        _log($"SKIP MP {mpTs.MeteringPointId}: existing Settl TS data (count={summary.Count}, from={fromStr}, to={toStr}, ts={matchingTs.Id}){TypeSuffix()}");
                        RecordSkip(
                            TimeSeriesSkipCategory.ExistingSettlData,
                            options.CustomerNumber ?? options.AccountNumber,
                            mpTs.MeteringPointId,
                            mpGuid,
                            revision,
                            deliveryBoundary,
                            $"existing Settl TS data (count={summary.Count}, from={fromStr}, to={toStr}, ts={matchingTs.Id})");
                        return (null, points, true);
                    }
                }
            }

            // Validate every included value before making any write for this metering point.
            foreach (var sourceSegment in filteredSegments)
                if (OutgoingTimeSeries.Validate(sourceSegment) is { } conversionError)
                    return ($"  Conversion failed for MP {mpTs.MeteringPointId}: {conversionError}", points, false);
            filteredSegments = filteredSegments.Select(OutgoingTimeSeries.Prepare).ToList();

            // Settl needs a series for every resolution Xellent's reading periods (EXU_TIMEREADINGSPERIOD) give in
            // the window: hourly values are converted to the reading period's resolution, so that is what Settl
            // receives. The same rule as resolution-changes' MissingSeries. Without reading periods, the values decide.
            // A batch window already starts at the delivery boundary; a single post cuts it here, so the reading
            // periods before the boundary (nothing is sent there) are left out.
            var required = inBatch || effectiveWindowStart <= DateOnly.FromDateTime(startDate)
                ? RequiredResolutions(mpTs)
                : await RequiredResolutionsAsync(context, deliveryCategory, mpTs.MeteringPointId, effectiveWindowStart,
                    DateOnly.FromDateTime(endDate), filteredSegments);
            foreach (var resolution in required)
            {
                var (length, unit) = ToSettlResolution(resolution);
                if (!tsList.Any(t => t.TimeResolution?.Length == length &&
                    string.Equals(t.TimeResolution?.Unit, unit, StringComparison.OrdinalIgnoreCase)))
                    return ($"  No Settl TimeSeries found for MP {mpTs.MeteringPointId} segment {resolution} " +
                            $"(reading period in the window, resolution={length} {unit}). Required time series must already exist in Settl.", points, false);
            }

            foreach (var segment in filteredSegments)
            {
                var (length, unit) = ToSettlResolution(segment.TimeResolution);
                if (!tsList.Any(t => t.TimeResolution?.Length == length &&
                    string.Equals(t.TimeResolution?.Unit, unit, StringComparison.OrdinalIgnoreCase)))
                    return ($"  No Settl TimeSeries found for MP {mpTs.MeteringPointId} segment {segment.TimeResolution} " +
                            $"(company={segment.CompanyId}, resolution={length} {unit}). Required time series must already exist in Settl.", points, false);
            }

            var eipPeriods = ComputeEipPeriods(filteredSegments, effectiveWindowStart, DateOnly.FromDateTime(endDate));

            // The ledger decides before anything is sent: a write it has done is not sent again, and a write
            // that never finished, or overlaps one sent before, stops the metering point until a person checks Settl.
            var ledger = Ledger(options.Environment);
            Guid? SeriesFor(TimeSeriesSegmentDto segment)
            {
                var (length, unit) = ToSettlResolution(segment.TimeResolution);
                return tsList.FirstOrDefault(t => t.TimeResolution?.Length == length &&
                    string.Equals(t.TimeResolution?.Unit, unit, StringComparison.OrdinalIgnoreCase))?.Id;
            }
            var writes = 0;
            var writesInLedger = 0;
            for (int segIdx = 0; segIdx < filteredSegments.Count; segIdx++)
            {
                if (PointCount(filteredSegments[segIdx]) == 0 || SeriesFor(filteredSegments[segIdx]) is not { } series)
                    continue;
                var (from, to) = eipPeriods[segIdx];
                foreach (var kind in new[] { PostLedger.Points, PostLedger.Period })
                {
                    writes++;
                    var (standing, why) = ledger.Check(mpTs.MeteringPointId, series, kind, from, to);
                    if (standing == PostLedger.Standing.Blocked)
                        return ($"  Earlier post not finished for MP {mpTs.MeteringPointId}: {why}. Check Settl, then remove " +
                                $"those lines from {ledger.FilePath}.", points, false);
                    if (standing == PostLedger.Standing.Done)
                        writesInLedger++;
                }
            }
            if (writes > 0 && writesInLedger == writes)
            {
                _log($"SKIP MP {mpTs.MeteringPointId}: already in the ledger, nothing sent again ({ledger.FilePath}){TypeSuffix()}");
                options.AlreadyInLedger = true;
                return (null, points, true);
            }

            // Iterér segmenter — Settl har én TS per resolution. Begge TS forventes at finde
            // i Settl på forhånd; matcher et segment ikke en eksisterende TS, fejler vi.
            for (int segIdx = 0; segIdx < filteredSegments.Count; segIdx++)
            {
                var segment = filteredSegments[segIdx];
                var (resLength, resUnit) = ToSettlResolution(segment.TimeResolution);
                var matchingTs = tsList.FirstOrDefault(t =>
                    t.TimeResolution?.Length == resLength &&
                    string.Equals(t.TimeResolution?.Unit, resUnit, StringComparison.OrdinalIgnoreCase));

                if (matchingTs == null)
                    return ($"  No Settl TimeSeries found for MP {mpTs.MeteringPointId} segment {segment.TimeResolution} " +
                            $"(company={segment.CompanyId}, resolution={resLength} {resUnit}). " +
                            "Both time series must already exist in Settl.", points, false);

                var tsGuid = matchingTs.Id;
                _log($"  segment {segment.TimeResolution} (company={segment.CompanyId}, {PointCount(segment)} points) → matched Settl TS {tsGuid}");

                var payload = dryRun ? null : BuildSegmentRequest(segment, revision);
                var pointCount = payload?.Points.Count ?? PointCount(segment);

                var (segStart, segEnd) = eipPeriods[segIdx];
                if (segStart >= segEnd)
                    return ($"  Empty or inverted EIP period for MP {mpTs.MeteringPointId} segment {segment.TimeResolution} " +
                            $"(company={segment.CompanyId}): {segStart:yyyy-MM-dd} → {segEnd:yyyy-MM-dd}.", points, false);

                OnPhase?.Invoke("post-timeseries");
                var pointsInLedger = pointCount > 0 && ledger.Check(mpTs.MeteringPointId, tsGuid, PostLedger.Points, segStart, segEnd).Standing == PostLedger.Standing.Done;
                var periodInLedger = pointCount > 0 && ledger.Check(mpTs.MeteringPointId, tsGuid, PostLedger.Period, segStart, segEnd).Standing == PostLedger.Standing.Done;
                if (dryRun)
                {
                    _log(pointsInLedger
                        ? $"    [dry-run] points already in the ledger — would not POST them again (TS {tsGuid})"
                        : $"    [dry-run] would POST {pointCount} points to TS {tsGuid}");
                    _log(periodInLedger
                        ? $"    [dry-run] EIP already in the ledger — would not add it again (TS {tsGuid})"
                        : $"    [dry-run] would add EIP ({segStart:yyyy-MM-dd} → {segEnd:yyyy-MM-dd}, TS={tsGuid})");
                }
                else if (pointCount == 0)
                {
                    // Settl POST /points fejler med "Sequence contains no elements" på tomme arrays.
                    // Skip både POST og EIP — der er intet at migrere; vi vil ikke markere perioden
                    // som externally-invoiced uden faktiske points bag sig.
                    _log("    no points to post — skipping POST and EIP");
                }
                else
                {
                    if (pointsInLedger)
                    {
                        _log($"    points already in the ledger — not posted again (TS {tsGuid})");
                    }
                    else
                    {
                        ledger.Write(mpTs.MeteringPointId, tsGuid, PostLedger.Points, segStart, segEnd, PostLedger.Starting);
                        var (postSuccess, postError) = await _settlTimeSeriesClient.PostPointsAsync(
                            tsGuid, payload!, cancellationToken);
                        ledger.Write(mpTs.MeteringPointId, tsGuid, PostLedger.Points, segStart, segEnd,
                            postSuccess ? PostLedger.Done : PostLedger.Failed);
                        if (!postSuccess)
                            return ($"    Time-series POST failed: {postError}", points, false);
                        _log($"    posted {pointCount} points to TS {tsGuid}");
                        points += pointCount;
                    }

                    if (periodInLedger)
                    {
                        _log($"    externally invoiced period already in the ledger — not added again (TS {tsGuid})");
                        continue;
                    }
                    OnPhase?.Invoke("post-eip");
                    var eipRequest = new AddExternallyInvoicedPeriodsRequest
                    {
                        TransactionId = Guid.NewGuid(),
                        Periods =
                        [
                            new ExternallyInvoicedPeriodDto
                            {
                                Period = new BillingTimePeriodDto
                                {
                                    Start = segStart,
                                    End = segEnd
                                },
                                // All these BillableTypes must always be set on El metering points (clarified with Systemate on 2026-08-26).
                                BillableTypes = ["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"],
                                Revision = revision,
                                TimeSeriesId = tsGuid
                            }
                        ]
                    };

                    ledger.Write(mpTs.MeteringPointId, tsGuid, PostLedger.Period, segStart, segEnd, PostLedger.Starting);
                    var (eipSuccess, eipError) = await _settlMeteringPointClient.AddExternallyInvoicedPeriodsAsync(
                        mpGuid, eipRequest, cancellationToken);
                    ledger.Write(mpTs.MeteringPointId, tsGuid, PostLedger.Period, segStart, segEnd,
                        eipSuccess ? PostLedger.Done : PostLedger.Failed);
                    if (!eipSuccess)
                        return ($"    externallyInvoicedPeriod POST failed: {eipError}", points, false);
                    _log($"    + externally invoiced period added ({string.Join(", ", eipRequest.Periods[0].BillableTypes)})");
                    continue;
                }

                points += pointsInLedger ? 0 : pointCount;
            }

            _log($"RESULT MP {mpTs.MeteringPointId}: points={points}, excluded={excludedPointCount}, boundary={boundaryText}");
            return (null, points, false);
        }

        foreach (var mpTs in timeSeries)
        {
            var totalPoints = mpTs.Segments.Sum(PointCount) + (mpTs.CountedPointsBeforeBoundary ?? 0);
            _log($"MP {mpTs.MeteringPointId} ({mpTs.Segments.Count} segment(s), {totalPoints} points)");

            var (mpError, mpPoints, mpSkipped) = await PostMeteringPointAsync(mpTs);
            pointsPosted += mpPoints;

            if (mpError != null)
            {
                options.Error = mpError.Trim();
                _log($"ERROR: {mpError}");
                failedMps.Add(mpTs.MeteringPointId);
                if (!options.ContinueOnError)
                {
                    OnPhase?.Invoke(null);
                    return 1;
                }
                _log($"=== MP {mpTs.MeteringPointId} fejlede — fortsaetter (--force) ===");
                OnProgress?.Invoke(posted + skippedSafety + failedMps.Count, timeSeries.Count);
                continue;
            }

            if (mpSkipped)
            {
                skippedSafety++;
                OnProgress?.Invoke(posted + skippedSafety + failedMps.Count, timeSeries.Count);
                continue;
            }

            posted++;
            OnProgress?.Invoke(posted + skippedSafety + failedMps.Count, timeSeries.Count);
        }

        OnPhase?.Invoke(null);
        // In a batch run the batch's own summary covers this metering point; its ERROR lines are above.
        LogSetup("");
        var skipFragment = skippedSafety > 0 ? $", {skippedSafety} skipped (safety checks)" : "";
        if (failedMps.Count > 0)
        {
            LogSetup($"Post completed with errors: {posted} metering points succeeded{skipFragment}, {failedMps.Count} failed, {pointsPosted:N0} points posted.");
            LogSetup($"Failed MPs: {string.Join(", ", failedMps)}");
            return 1;
        }
        options.PointsPosted = dryRun ? 0 : pointsPosted;
        options.Skipped = posted == 0 && skippedSafety > 0;
        LogSetup(dryRun
            ? $"Dry run completed: {posted} metering points{skipFragment}, {pointsPosted:N0} points would be posted."
            : $"Post completed successfully: {posted} metering points{skipFragment}, {pointsPosted:N0} points posted.");

        return 0;
    }

    /// <summary>The only delivery category batch mode posts: every El profile used it.</summary>
    public const string BatchDeliveryCategory = "El-ekstern";

    /// <summary>
    /// Batch mode. Settl decides which metering points are posted: every parent electricity metering
    /// point in the environment with a delivery start. The account comes from Xellent — whoever held
    /// the metering point on the day before delivery starts in Settl.
    /// <para>Settl can fail to mark delivery (2026-09), so metering points the list shows as not in
    /// delivery are resolved too, through the current delivery status fallback. For those, having no
    /// delivery start, or not having been closed as a migration in Xellent, means "never migrated":
    /// they are skipped, where a metering point Settl does mark as in delivery fails.</para>
    /// <paramref name="cosFrom"/>/<paramref name="cosTo"/> (inclusive, Danish dates) narrow the run to
    /// a range of delivery starts; <paramref name="skip"/> passes over the first N metering points in
    /// range, in identification order; <paramref name="limit"/> stops once N metering points are sent
    /// (ready, in a dry run) — skipped, blocked and failed ones do not count toward it.
    /// <para><paramref name="offline"/> reads Settl and Xellent from the environment's snapshot (see
    /// <see cref="RunSeedSnapshotAsync"/>) instead of asking them again; anything the snapshot lacks is
    /// read live and added. Time series points are always read fresh, and posts always go to Settl.</para>
    /// </summary>
    public async Task<int> RunPostAllAsync(
        string environment,
        bool dryRun,
        bool continueOnError,
        bool skipResolutionChange = false,
        bool skipIfExistingPoints = false,
        DateOnly? cosFrom = null,
        DateOnly? cosTo = null,
        int? limit = null,
        DateTime? windowEndDateOverride = null,
        int? skip = null,
        bool offline = false,
        CancellationToken cancellationToken = default,
        bool allowSwitches = false)
    {
        // The run's states go to states.json in the snapshot folder while it runs and when it ends.
        _statesRun = (SnapshotDirectory(environment), environment, dryRun, DateTimeOffset.UtcNow);
        (_statesChecked, _statesTotal, _statesCompleted) = (0, 0, false);
        _statesLastWrite = DateTimeOffset.MinValue;
        try
        {
            return await RunPostAllWithDoneMarksAsync(environment, dryRun, continueOnError, skipResolutionChange,
                skipIfExistingPoints, cosFrom, cosTo, limit, windowEndDateOverride, skip, offline, allowSwitches, cancellationToken);
        }
        finally
        {
            WriteStates(_statesCompleted ? "Completed" : "Stopped", force: true);
            _statesRun = null;
        }
    }

    private async Task<int> RunPostAllWithDoneMarksAsync(string environment, bool dryRun, bool continueOnError,
        bool skipResolutionChange, bool skipIfExistingPoints, DateOnly? cosFrom, DateOnly? cosTo, int? limit,
        DateTime? windowEndDateOverride, int? skip, bool offline, bool allowSwitches, CancellationToken cancellationToken)
    {
        // Metering points a live run has posted are marked done in the environment's snapshot folder,
        // online or offline, and every batch run leaves them out from the start.
        _done = new RunSnapshot(SnapshotDirectory(environment), reuse: true);
        if (!offline)
        {
            try
            {
                return await RunPostAllCoreAsync(environment, dryRun, continueOnError, skipResolutionChange, skipIfExistingPoints,
                    cosFrom, cosTo, limit, windowEndDateOverride, skip, cancellationToken, allowSwitches);
            }
            finally
            {
                _done = null;
            }
        }

        _snapshot = new RunSnapshot(SnapshotDirectory(environment), reuse: true);
        _log($"Offline: reading Settl and Xellent from the snapshot in {_snapshot.DirectoryPath}; anything missing there is read live and added.");
        try
        {
            return await RunPostAllCoreAsync(environment, dryRun, continueOnError, skipResolutionChange, skipIfExistingPoints,
                cosFrom, cosTo, limit, windowEndDateOverride, skip, cancellationToken, allowSwitches);
        }
        finally
        {
            _log($"Snapshot: {_snapshot.Reused} reads from the snapshot, {_snapshot.Fetched} read live.");
            _snapshot = null;
            _done = null;
        }
    }

    /// <summary>
    /// Reads everything a batch run needs from Settl and Xellent for every metering point in the
    /// environment and stores it in the environment's snapshot, so <c>RunPostAllAsync(offline: true)</c>
    /// can run without asking them again. Settl: the metering point list, and per metering point its
    /// delivery statuses, current delivery status and time series. Xellent: child relations and
    /// contracts. Not the time series points. What the snapshot already holds is kept, so a second run
    /// only fills in what is missing (an interrupted seed resumes); delete the folder to start over.
    /// Reads per metering point run <paramref name="parallelism"/> at a time. A failed read is logged
    /// and left out (the next seed tries it again); the run then exits non-zero.
    /// <para><paramref name="refresh"/> throws the stored reads away and reads everything again. The done
    /// marks are kept — a metering point with points in Settl stays done — and the done check adds new ones.</para>
    /// </summary>
    public async Task<int> RunSeedSnapshotAsync(string environment, int parallelism = 6, bool refresh = false,
        CancellationToken cancellationToken = default, bool refreshBlocked = false)
    {
        if (_settlMeteringPointClient == null)
            return Error("Settl API clients not configured. DI registration missing.");
        _settlMeteringPointClient.SetDeliveryCategory(BatchDeliveryCategory);

        var directory = SnapshotDirectory(environment);
        if (refresh && Directory.Exists(directory))
        {
            var stale = Directory.GetFiles(directory, "*.jsonl")
                .Where(p => !string.Equals(Path.GetFileNameWithoutExtension(p), SnapshotDone, StringComparison.Ordinal)
                    && !string.Equals(Path.GetFileName(p), PostLedger.FileName, StringComparison.Ordinal))
                .ToList();
            foreach (var file in stale)
                File.Delete(file);
            _log($"Refresh: removed {stale.Count} stored read file(s) from {directory}; done marks and the ledger kept.");
        }
        _snapshot = new RunSnapshot(directory, reuse: true);
        if (refreshBlocked && !refresh)
            ForgetBlockedReads();
        _progressWriter = new SnapshotProgressWriter(directory, environment);
        try
        {
            _progressWriter.StartPhase(1, "Settl list, Xellent contracts, payers");
            _log($"=== Snapshot seed — {environment} → {_snapshot.DirectoryPath} ===");
            List<BatchMeteringPoint> meteringPoints;
            try
            {
                (meteringPoints, _) = await LoadBatchMeteringPointsAsync(null, null, cancellationToken);
                await using var context = BuildXellentContext();
                var extraction = new TimeSeriesExtractionService(context, BatchDeliveryCategory);
                _log("Xellent contracts...");
                await GetContractsByMeteringPointAsync(extraction, meteringPoints.Select(m => m.Identification).ToList());
                if (_settlPayerClient != null)
                {
                    _log("Settl payers...");
                    await GetPayersAsync(meteringPoints.Select(m => m.Id).ToList(), parallelism, cancellationToken);
                }
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }

            _progressWriter?.StartPhase(2, "Settl reads per metering point");
            _log($"Settl, per metering point: {meteringPoints.Count} metering points, {parallelism} at a time...");
            var done = 0;
            var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Parallel.ForEachAsync(meteringPoints,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
                async (mp, ct) =>
                {
                    var errors = new[]
                    {
                        (await GetDeliveryStatusesAsync(mp.Id, ct)).Error is { } e1 ? $"delivery statuses: {e1}" : null,
                        (await GetCurrentMeteringPointAsync(mp.Id, ct)).Error is { } e2 ? $"current delivery status: {e2}" : null,
                        (await GetTimeSeriesListAsync(mp.Id, ct)).Error is { } e3 ? $"time series: {e3}" : null,
                    }.OfType<string>().ToList();
                    if (errors.Count > 0)
                        failures.Add($"MP {mp.Identification} ({mp.Id}): {string.Join("; ", errors)}");
                    ReportProgress("Settl reads", Interlocked.Increment(ref done), meteringPoints.Count);
                });

            // Done check: run the batch's own decisions over the snapshot, and ask Settl whether each
            // metering point that would be posted already has points in its window.
            _progressWriter?.StartPhase(3, "Done check (asking Settl)");
            _log("Done check: metering points Settl already holds points for...");
            var runLog = _log;
            _markDoneMode = true;
            _doneCandidates.Clear();
            _done = new RunSnapshot(SnapshotDirectory(environment), reuse: true);
            var ledger = Ledger(environment);
            var doneBefore = meteringPoints.Count(m => ledger.IsDone(m.Identification));
            _log = _ => { };
            try
            {
                await RunPostAllCoreAsync(environment, dryRun: true, continueOnError: true, skipResolutionChange: false,
                    skipIfExistingPoints: false, cosFrom: null, cosTo: null, limit: null, windowEndDateOverride: null, skip: null,
                    cancellationToken);
            }
            finally
            {
                _log = runLog;
                _markDoneMode = false;
            }
            var (doneMarked, doneFailures) = await CheckDoneCandidatesAsync(ledger, parallelism, cancellationToken);
            foreach (var failure in doneFailures)
                failures.Add(failure);
            _log($"  {_doneCandidates.Count} metering point(s) would be posted; {doneMarked} of them already have points in Settl — marked done " +
                 $"({ledger.FilePath})");
            var doneTotal = doneBefore + doneMarked;
            var stillToPost = _doneCandidates.Count - doneMarked;
            var migrationTotal = doneTotal + stillToPost;
            _done.Put(SnapshotDoneSummary, "last", new DoneSummary(migrationTotal, doneTotal, DateTimeOffset.UtcNow));
            if (migrationTotal > 0)
                _log($"Migration: {doneTotal} of {migrationTotal} metering points done ({doneTotal * 100 / migrationTotal}%), {stillToPost} still to post");
            _done = null;

            // Xellent point counts for every candidate, in the same window the reports use, stored in the
            // snapshot. resolution-changes and offline dry runs then read them back instead of
            // counting in Xellent again.
            _progressWriter?.StartPhase(4, "Xellent counts");
            _log("Counting points in Xellent for each metering point that would be posted...");
            await using (var countContext = BuildXellentContext())
            {
                for (var i = 0; i < _doneCandidates.Count; i++)
                {
                    var candidate = _doneCandidates[i];
                    ReportProgress("Xellent counts", i + 1, _doneCandidates.Count);
                    try
                    {
                        var counted = await CountTimeSeriesAsync(countContext, candidate.Identification, BatchDeliveryCategory,
                            candidate.From.ToDateTime(TimeOnly.MinValue), candidate.To.ToDateTime(TimeOnly.MinValue), null);
                        // Only the ones with nothing to post need it: it is what tells "no meter yet" from
                        // "meter, but no readings" in the reports.
                        if (counted.Segments.Sum(PointCount) == 0)
                            await MeterPointStatusAsync(countContext, candidate.Identification);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
                    {
                        failures.Add($"MP {candidate.Identification}: Xellent count: {ex.Message}");
                    }
                }
            }

            _log("");
            _log($"Snapshot seed completed: {meteringPoints.Count} metering points, {_snapshot.Reused} reads already in the snapshot, " +
                 $"{_snapshot.Fetched} read now, {failures.Count} metering point(s) with a failed read.");
            foreach (var failure in failures.OrderBy(f => f, StringComparer.Ordinal))
                _log($"  - {failure}");
            _progressWriter?.Complete();
            return failures.IsEmpty ? 0 : 1;
        }
        finally
        {
            _progressWriter?.Stop();
            _progressWriter = null;
            _snapshot = null;
        }
    }

    /// <summary>
    /// Writes the CSV supplied to Settl/Systemate for the series they have to create. Listed: a metering point whose
    /// reading periods (EXU_TIMEREADINGSPERIOD) switch resolution inside the window, either direction, while Settl
    /// has no Hour or no Quarter series. One row per switch; ChangeDate is the start of the reading period after it.
    /// More than one switch puts a warning on each row and in the log. Done marks and unclear-delivery handling are unchanged.
    /// Default file: &lt;output&gt;/resolution-changes-&lt;env&gt;.csv.
    /// </summary>
    public async Task<int> RunResolutionChangesAsync(string environment, bool offline = false, string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (_settlMeteringPointClient == null)
            return Error("Settl API clients not configured. DI registration missing.");
        _settlMeteringPointClient.SetDeliveryCategory(BatchDeliveryCategory);
        if (offline)
            _snapshot = new RunSnapshot(SnapshotDirectory(environment), reuse: true);
        var runLog = _log;
        try
        {
            _log($"=== Resolution changes — {environment} ===");
            _log("Working out the window each metering point would be posted with...");
            _markDoneMode = true;
            _doneCandidates.Clear();
            // No _done here: the list covers every metering point the migration touches, the ones already
            // posted included. Only a real post run leaves done metering points out.
            _log = _ => { };
            try
            {
                await RunPostAllCoreAsync(environment, dryRun: true, continueOnError: true, skipResolutionChange: false,
                    skipIfExistingPoints: false, cosFrom: null, cosTo: null, limit: null, windowEndDateOverride: null, skip: null,
                    cancellationToken);
            }
            finally
            {
                _log = runLog;
                _markDoneMode = false;
                _done = null;
            }

            await using var context = BuildXellentContext();

            var changes = new List<ResolutionRow>();
            var unclearDelivery = new List<ResolutionRow>();
            // Metering points already posted count towards the totals but are not listed: there is
            // nothing left to do about them.
            var doneList = Ledger(environment);
            var doneNotListed = 0;
            var needed = 0;
            var multipleSwitches = new List<(string MeteringPoint, List<DateOnly> Dates)>();
            var candidates = _doneCandidates.OrderBy(c => c.Identification, StringComparer.Ordinal).ToList();
            // The switch comes from Xellent's reading periods (EXU_TIMEREADINGSPERIOD): hourly values are converted
            // to the reading period's resolution, so Settl receives what the table says.
            Dictionary<string, List<TimeSeriesExtractionService.ReadingPeriod>> readingPeriods;
            try
            {
                readingPeriods = await new TimeSeriesExtractionService(context, BatchDeliveryCategory)
                    .GetReadingPeriodsAsync(candidates.Select(c => c.Identification));
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                ReportProgress("Resolution changes", i + 1, candidates.Count);

                var counted = await CountTimeSeriesAsync(context, candidate.Identification, BatchDeliveryCategory,
                    candidate.From.ToDateTime(TimeOnly.MinValue), candidate.To.ToDateTime(TimeOnly.MinValue), null);

                // The switches, either direction. Each one's start is a ChangeDate. No switch, no row.
                var switchDates = ReadingPeriodSwitches(
                    readingPeriods.TryGetValue(candidate.Identification, out var periods) ? periods : [], candidate.From, candidate.To);
                if (switchDates.Count == 0)
                    continue;

                // A switch has an hourly and a 15-minute side; listed when Settl has no series for one of them.
                // Systemate has to create those.
                var (series, seriesError) = await GetTimeSeriesListAsync(candidate.Id, cancellationToken);
                if (seriesError != null)
                    return Error($"Settl GetTimeSeries failed for MP {candidate.Identification}: {seriesError}");
                var settlResolutions = (series ?? [])
                    .Select(t => SettlResolutionName(t.TimeResolution))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(r => r, StringComparer.Ordinal)
                    .ToList();
                var missing = new[] { "Hour", "Quarter" }.Except(settlResolutions, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count == 0)
                    continue;

                needed++;
                if (doneList.IsDone(candidate.Identification))
                {
                    doneNotListed++;
                    continue;
                }

                // Every switch gets a row. More than one in a window is unusual: each row says so, and so does the log.
                var warning = switchDates.Count > 1 ? $"multiple switches ({switchDates.Count}) – check by hand" : "";
                if (switchDates.Count > 1)
                    multipleSwitches.Add((candidate.Identification, switchDates));
                var sourceResolutions = counted.Segments.Where(s => PointCount(s) > 0)
                    .Select(s => s.TimeResolution).Distinct().OrderBy(r => r, StringComparer.Ordinal);
                foreach (var switchDate in switchDates)
                {
                    // Settl states no delivery for this one (the InDelivery bug), so the window it would be
                    // posted with rests on the current delivery status. It belongs on the list, but not as
                    // something settled: it goes to its own file instead.
                    var change = new ResolutionRow(candidate.Identification, candidate.Type, switchDate, string.Join(" + ", sourceResolutions),
                        string.Join(" + ", settlResolutions), string.Join(" + ", missing), candidate.From, candidate.To, warning);
                    if (candidate.FromCurrentDeliveryStatus)
                        unclearDelivery.Add(change);
                    else
                        changes.Add(change);
                }
            }
            var path = outputPath ?? Path.Combine(_outputBaseDirectory, $"resolution-changes-{environment}.csv");
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
                Directory.CreateDirectory(folder);
            // The three columns Systemate asked for, unquoted; semicolons and the BOM keep Danish Excel happy.
            // No field can hold a semicolon or a quote (identifications, dates, Hour/Quarter).
            static string PlainLine(params string[] fields) => string.Join(";", fields) + "\r\n";
            string Csv(List<ResolutionRow> rows) =>
                PlainLine("MeteringPointId", "ChangeDate", "MissingSeries") +
                string.Concat(rows.Select(r => PlainLine(r.MeteringPoint, r.ChangeDate is { } d ? $"{d:yyyy-MM-dd}" : "", r.MissingSeries)));
            await File.WriteAllTextAsync(path, Csv(changes), CsvEncoding, cancellationToken);
            _log($"{needed} of {_doneCandidates.Count} metering points switch resolution inside their window and miss a Settl series");
            _log($"{changes.Select(r => r.MeteringPoint).Distinct().Count()} listed, one row per switch ({changes.Count} rows) → {path}" +
                 (doneNotListed > 0 ? $" ({doneNotListed} already posted, left out of the list)" : ""));

            var unclearPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                Path.GetFileNameWithoutExtension(path) + "-unclear-delivery.csv");
            if (unclearDelivery.Count == 0 && File.Exists(unclearPath))
                await File.WriteAllTextAsync(unclearPath, Csv([]), CsvEncoding, cancellationToken);
            if (unclearDelivery.Count > 0)
            {
                await File.WriteAllTextAsync(unclearPath, Csv(unclearDelivery), CsvEncoding, cancellationToken);
                _log($"{unclearDelivery.Count} more belong on the list but Settl does not state their delivery (no InDelivery=true) " +
                     $"— not on the list above → {unclearPath}");
            }
            if (multipleSwitches.Count > 0)
            {
                _log("");
                _log($"WARNING: {multipleSwitches.Count} metering point(s) switch resolution more than once in their window – check by hand:");
                foreach (var (mp, dates) in multipleSwitches)
                    _log($"  {mp}: {string.Join(", ", dates.Select(d => d.ToString("yyyy-MM-dd")))}");
            }
            return 0;
        }
        finally
        {
            _log = runLog;
            _snapshot = null;
        }
    }

    private static readonly string[] RequarterColumns =
        ["MeteringPointId", "SettlId", "Account", "WindowStart", "WindowEnd", "QuarterFrom", "QuarterTo", "HourValuesInSettl", "QuarterValuesInSettl"];

    /// <summary>
    /// Lists the done metering points that were posted as hourly values where Xellent's reading periods
    /// (EXU_TIMEREADINGSPERIOD) say 15-minute: posted before hourly values were split into quarters (story
    /// 45902). For each done mark it takes the window stored with it and the parts of that window in a Quarter
    /// reading period; a part is listed when Settl's Hour series has values there and the Quarter series none.
    /// Read-only: sends nothing and changes no done marks. Default file: &lt;output&gt;/requarter-&lt;env&gt;.csv.
    /// </summary>
    public async Task<int> RunRequarterAsync(string environment, int parallelism = 6, string? outputPath = null,
        CancellationToken cancellationToken = default)
    {
        if (_settlMeteringPointClient == null || _settlTimeSeriesClient == null)
            return Error("Settl API clients not configured. DI registration missing.");
        _settlMeteringPointClient.SetDeliveryCategory(BatchDeliveryCategory);
        _log($"=== Requarter — {environment} ===");

        var done = Ledger(environment).DoneMarks()
            .Select(d => (Key: d.MeteringPoint, Value: new DoneEntry(d.SettlId ?? Guid.Empty, d.Account ?? "", d.Revision ?? default,
                d.From, d.To, d.Points ?? 0, d.At)))
            .OrderBy(d => d.Key, StringComparer.Ordinal).ToList();
        Dictionary<string, List<TimeSeriesExtractionService.ReadingPeriod>> readingPeriods;
        await using (var context = BuildXellentContext())
        {
            try
            {
                readingPeriods = await new TimeSeriesExtractionService(context, BatchDeliveryCategory)
                    .GetReadingPeriodsAsync(done.Select(d => d.Key));
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message);
            }
        }

        // The parts of each window that lie in a Quarter reading period, joined where they touch.
        var toCheck = new List<(string Mp, DoneEntry Mark, DateOnly From, DateOnly To)>();
        foreach (var (mp, mark) in done)
        {
            var parts = (readingPeriods.TryGetValue(mp, out var periods) ? periods : [])
                .Where(p => p.Resolution == "Quarter")
                .Select(p => (From: p.Start > mark.WindowStart ? p.Start : mark.WindowStart,
                              To: p.End is { } end && end < mark.WindowEnd ? end : mark.WindowEnd))
                .Where(p => p.From < p.To)
                .OrderBy(p => p.From);
            (DateOnly From, DateOnly To)? open = null;
            foreach (var part in parts)
            {
                if (open is { } o && part.From <= o.To)
                    open = (o.From, part.To > o.To ? part.To : o.To);
                else
                {
                    if (open is { } closed) toCheck.Add((mp, mark, closed.From, closed.To));
                    open = part;
                }
            }
            if (open is { } last) toCheck.Add((mp, mark, last.From, last.To));
        }
        _log($"{done.Count} done metering points, {toCheck.Select(c => c.Mp).Distinct().Count()} with a quarter-hour reading period in their window");

        var rows = new System.Collections.Concurrent.ConcurrentBag<(string Mp, DoneEntry Mark, DateOnly From, DateOnly To, int Hour, int Quarter)>();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var checkedCount = 0;
        await Parallel.ForEachAsync(toCheck,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                ReportProgress("Requarter", Interlocked.Increment(ref checkedCount), toCheck.Count);
                var (series, seriesError) = await _settlMeteringPointClient.GetTimeSeriesAsync(item.Mark.SettlId, ct);
                if (seriesError != null)
                {
                    errors.Add($"MP {item.Mp}: GetTimeSeries failed: {seriesError}");
                    return;
                }
                async Task<int?> CountAsync(string resolution)
                {
                    var (length, unit) = ToSettlResolution(resolution);
                    if ((series ?? []).FirstOrDefault(t => t.TimeResolution?.Length == length &&
                            string.Equals(t.TimeResolution?.Unit, unit, StringComparison.OrdinalIgnoreCase)) is not { } ts)
                        return 0;
                    var (summary, error) = await GetPointsSummaryInYearChunksAsync(ts.Id, item.From, item.To, ct);
                    if (error != null)
                    {
                        errors.Add($"MP {item.Mp}: GetPointsSummary failed: {error}");
                        return null;
                    }
                    return summary?.Count ?? 0;
                }
                if (await CountAsync("Hour") is not { } hour || await CountAsync("Quarter") is not { } quarter)
                    return;
                if (hour > 0 && quarter == 0)
                    rows.Add((item.Mp, item.Mark, item.From, item.To, hour, quarter));
            });

        var listed = rows.OrderBy(r => r.Mp, StringComparer.Ordinal).ThenBy(r => r.From).ToList();
        var path = outputPath ?? Path.Combine(_outputBaseDirectory, $"requarter-{environment}.csv");
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } folder)
            Directory.CreateDirectory(folder);
        // QuarterTo, like WindowEnd, is the first day not included.
        await File.WriteAllTextAsync(path,
            CsvLine(RequarterColumns) + string.Concat(listed.Select(r => CsvLine([r.Mp, r.Mark.SettlId.ToString(), r.Mark.Account,
                $"{r.Mark.WindowStart:yyyy-MM-dd}", $"{r.Mark.WindowEnd:yyyy-MM-dd}", $"{r.From:yyyy-MM-dd}", $"{r.To:yyyy-MM-dd}",
                r.Hour.ToString(), r.Quarter.ToString()]))),
            CsvEncoding, cancellationToken);
        foreach (var error in errors.OrderBy(e => e, StringComparer.Ordinal))
            _log($"ERROR: {error}");
        _log($"Requarter: {done.Count} done metering points checked, {listed.Select(r => r.Mp).Distinct().Count()} listed → {path}");
        return errors.IsEmpty ? 0 : 1;
    }

    // The resolutions Settl must have a series for: those of the reading periods in the window, or, when Xellent
    // records none there, those of the values.
    private static List<string> RequiredResolutions(MeteringPointTimeSeriesDto counted) =>
        counted.ReadingResolutionsInWindow.Count > 0
            ? counted.ReadingResolutionsInWindow.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToList()
            : counted.Segments.Where(s => PointCount(s) > 0).Select(OutgoingTimeSeries.Resolution)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToList();

    // The same rule for a window cut at the delivery boundary: the reading periods from the cut on.
    private static async Task<List<string>> RequiredResolutionsAsync(XellentDbContext context, string deliveryCategory,
        string meteringPoint, DateOnly from, DateOnly to, IEnumerable<TimeSeriesSegmentDto> sentSegments)
    {
        var periods = await new TimeSeriesExtractionService(context, deliveryCategory).GetReadingPeriodsAsync([meteringPoint]);
        var inWindow = (periods.TryGetValue(meteringPoint, out var list) ? list : [])
            .Where(p => p.Start < to && (p.End is not { } end || end > from))
            .Select(p => p.Resolution);
        var values = sentSegments.Where(s => PointCount(s) > 0).Select(OutgoingTimeSeries.Resolution);
        var required = inWindow.Any() ? inWindow : values;
        return required.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The reading-period switches inside a window: a reading period (EXU_TIMEREADINGSPERIOD) that starts after
    /// <paramref name="from"/> and before <paramref name="to"/>, right after one in the window with the other
    /// resolution. Either direction; the dates are those reading periods' starts. resolution-changes lists them,
    /// post --all blocks on them.
    /// </summary>
    private static List<DateOnly> ReadingPeriodSwitches(IEnumerable<TimeSeriesExtractionService.ReadingPeriod> periods,
        DateOnly from, DateOnly to)
    {
        var inWindow = periods
            .Where(p => p.Start < to && (p.End is not { } end || end > from))
            .OrderBy(p => p.Start)
            .ToList();
        return inWindow.Zip(inWindow.Skip(1))
            .Where(pair => pair.First.Resolution != pair.Second.Resolution && pair.Second.Start > from && pair.Second.Start < to)
            .Select(pair => pair.Second.Start)
            .ToList();
    }

    /// <summary>One row of the resolution list: a switch date (null when there is none), what the points are
    /// in, what Settl has, and what Settl is missing.</summary>
    private sealed record ResolutionRow(string MeteringPoint, string Type, DateOnly? ChangeDate, string XellentResolutions,
        string SettlSeries, string MissingSeries, DateOnly From, DateOnly To, string Warning);

    // EXU_METERPOINTSTATUSPERIOD.METERPOINTSTATUS: "Oprettet" — the metering point exists, but no meter
    // is installed at the address yet, so it never gets values. A delivering meter point sits at 6.
    private const int MeterPointStatusCreated = 5;

    // Settl states a resolution as length + unit; this run speaks of Hour and Quarter.
    private static string SettlResolutionName(TimeResolutionInfoDto? resolution) => resolution switch
    {
        { Length: 1 } when string.Equals(resolution.Unit, "Hour", StringComparison.OrdinalIgnoreCase) => "Hour",
        { Length: 15 } when string.Equals(resolution.Unit, "Minutes", StringComparison.OrdinalIgnoreCase) => "Quarter",
        null => "unknown",
        _ => $"{resolution.Length} {resolution.Unit}",
    };

    /// <summary>
    /// Checks current Settl readings in the same ownership window as a migration run.
    /// Reads every candidate, including previously marked done meters. Does not read Xellent
    /// consumption values, post data, or update the snapshot or done marks.
    /// Missing means no readings, not incomplete coverage. Unresolved candidates remain Unknown.
    /// </summary>
    public async Task<MissingReadingsScanResult> ScanMissingReadingsAsync(
        string environment, DateOnly? cosFrom = null, DateOnly? cosTo = null,
        int? skip = null, int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (_settlTimeSeriesClient == null || _settlMeteringPointClient == null)
            throw new InvalidOperationException("Settl API clients not configured.");
        var savedSnapshot = _snapshot;
        var savedDone = _done;
        var savedMode = _markDoneMode;
        var savedType = _meteringPointType;
        var savedCandidates = _doneCandidates.ToList();
        try
        {
            _snapshot = null;
            _done = null;
            _markDoneMode = true;
            _doneCandidates.Clear();
            _missingReadingsScan = new(StringComparer.Ordinal);
            var exitCode = await RunPostAllCoreAsync(environment, dryRun: true, continueOnError: true,
                skipResolutionChange: false, skipIfExistingPoints: false, cosFrom, cosTo,
                limit, windowEndDateOverride: null, skip, cancellationToken);

            // Presence checks can run independently. Stop as soon as a series has any readings.
            var checkedEntries = new System.Collections.Concurrent.ConcurrentBag<MissingReadingsEntry>();
            await Parallel.ForEachAsync(_doneCandidates,
                new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
                async (candidate, ct) =>
                {
                    var (present, error) = await HasLiveReadingsAsync(candidate, ct);
                    checkedEntries.Add(new(candidate.Identification, candidate.Id, candidate.Account,
                        candidate.From, candidate.To,
                        error != null ? "Unknown" : present ? "Present" : "Missing", error));
                });
            foreach (var entry in checkedEntries)
                _missingReadingsScan[entry.MeteringPointId] = entry;
            var entries = _missingReadingsScan.Values.OrderBy(e => e.MeteringPointId, StringComparer.Ordinal).ToList();
            return new(entries, exitCode != 0 || entries.Any(e => e.Status == "Unknown") ? 1 : 0);
        }
        finally
        {
            _missingReadingsScan = null;
            _scanCurrentMeteringPoint = null;
            _snapshot = savedSnapshot;
            _done = savedDone;
            _markDoneMode = savedMode;
            _meteringPointType = savedType;
            _doneCandidates.Clear();
            _doneCandidates.AddRange(savedCandidates);
        }
    }

    private async Task<(bool Present, string? Error)> HasLiveReadingsAsync(DoneCandidate candidate, CancellationToken ct)
    {
        if (candidate.From >= candidate.To)
            return (false, "No historical reading period could be checked.");
        var (series, seriesError) = await GetTimeSeriesListAsync(candidate.Id, ct);
        if (seriesError != null)
        {
            StopScanOnNetworkFailure(seriesError);
            return (false, seriesError);
        }
        if (series == null) return (false, "Settl returned no series response.");
        foreach (var ts in series.DistinctBy(s => s.Id))
        {
            for (var from = candidate.From; from < candidate.To;)
            {
                var to = from.AddYears(1).AddDays(-1);
                if (to > candidate.To) to = candidate.To;
                var (summary, error) = await _settlTimeSeriesClient!.GetPointsSummaryAsync(ts.Id, from, to, ct);
                if (error != null)
                {
                    StopScanOnNetworkFailure(error);
                    return (false, error);
                }
                if (summary == null) return (false, "Settl returned no readings response.");
                if (summary.Count > 0) return (true, null);
                from = to;
            }
        }
        return (false, null);
    }

    private static void StopScanOnNetworkFailure(string error)
    {
        string[] networkFailures = ["network-related", "network path", "no such host", "host is unknown",
            "name or service not known", "connection refused", "actively refused", "unable to connect",
            "connection timed out", "connection attempt failed", "could not open a connection"];
        if (networkFailures.Any(pattern => error.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The readings scan stopped because the connection failed. Check the corporate network/VPN.");
    }

    private async Task<int> RunPostAllCoreAsync(
        string environment,
        bool dryRun,
        bool continueOnError,
        bool skipResolutionChange,
        bool skipIfExistingPoints,
        DateOnly? cosFrom,
        DateOnly? cosTo,
        int? limit,
        DateTime? windowEndDateOverride,
        int? skip,
        CancellationToken cancellationToken,
        bool allowSwitches = false)
    {
        ResetSkipArtifacts();

        if (_settlMeteringPointClient == null)
            return Error("Settl API clients not configured. DI registration missing.");

        const string deliveryCategory = BatchDeliveryCategory;

        _log("=== Time Series Post — all metering points in Settl ===");
        _log($"Delivery: {deliveryCategory}");
        if (cosFrom != null || cosTo != null)
            _log($"Delivery start: {(cosFrom is { } f ? f.ToString("yyyy-MM-dd") : "…")} → {(cosTo is { } t ? t.ToString("yyyy-MM-dd") : "…")}");
        if (skip is { } sk) _log($"Skip: {sk}");
        if (limit is { } l) _log($"Limit: {l}");
        _log(windowEndDateOverride is null
            ? "Per-MP window: end = resolved revision date, start = end − 3 years"
            : $"Per-MP window override: end = {windowEndDateOverride:yyyy-MM-dd}, start = end − 3 years");
        _log(_snapshot != null
            ? "Data: Settl and Xellent contracts from the snapshot; time series points read from Xellent"
            : "Data: Settl and Xellent read live");
        if (dryRun) _log("Dry run: Yes (no mutations will be made)");
        if (continueOnError) _log("Continue on error: Yes (--force — failed MPs are skipped, run still exits non-zero)");
        if (skipIfExistingPoints) _log("Skip if existing points: Yes (--skip-if-existing-points)");
        if (allowSwitches) _log("Resolution switches: sent (--allow-switches)");
        _log("");

        _settlMeteringPointClient.SetDeliveryCategory(deliveryCategory);

        _states.Clear();
        var totalPosted = new Tally();
        var totalFailed = new Tally();
        var totalAmbiguousRevisions = new Tally();
        var failures = new List<string>();

        // Logs the error and records it. Returns true if the run should continue to the next
        // unit (--force), false if it should abort — mirrors DataMigration's ContinueOnError.
        bool RecordFailure(string message)
        {
            if (_missingReadingsScan != null) StopScanOnNetworkFailure(message);
            _log($"ERROR: {message}");
            failures.Add(message.Trim());
            if (!continueOnError)
                return false;
            _log("  → fejl — fortsaetter (--force)");
            return true;
        }

        // Settl is the source of truth for which metering points exist and when delivery started.
        List<BatchMeteringPoint> settlMeteringPoints;
        int totalChildren;
        try
        {
            (settlMeteringPoints, totalChildren) = await LoadBatchMeteringPointsAsync(cosFrom, cosTo, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }

        // Metering points already posted by an earlier live run are left out before anything else, and
        // do not count toward --skip/--limit: repeating a run with --limit N takes the next N not done.
        var totalAlreadyDone = new Tally();
        if (_done != null)
        {
            var ledger = Ledger(environment);
            foreach (var mp in settlMeteringPoints.Where(m => ledger.IsDone(m.Identification)))
                totalAlreadyDone.Add(mp.ParentIdentification != null);
            if (totalAlreadyDone.Total > 0)
            {
                settlMeteringPoints = settlMeteringPoints.Where(m => !ledger.IsDone(m.Identification)).ToList();
                CountState("Done", "already in Settl", totalAlreadyDone.Total);
                _log($"Already done: {totalAlreadyDone.Total} metering point(s) already in Settl (snapshot check or an earlier post), left out ({ledger.FilePath})");
            }
        }

        // Metering points the operator excluded by hand are never sent; each counts as excluded with its reason.
        var excludedPath = Path.Combine(SnapshotDirectory(environment), ExcludedFileName);
        var excludedByHand = ReadExcluded(excludedPath);
        if (excludedByHand.Count > 0)
        {
            var leftOut = settlMeteringPoints.Where(m => excludedByHand.ContainsKey(m.Identification)).ToList();
            foreach (var mp in leftOut)
            {
                RecordCoverageExclusion("Excluded by hand", mp.ParentIdentification != null);
                CountState("Excluded", excludedByHand[mp.Identification]);
            }
            settlMeteringPoints = settlMeteringPoints.Where(m => !excludedByHand.ContainsKey(m.Identification)).ToList();
            if (leftOut.Count > 0)
                _log($"Excluded by hand: {leftOut.Count} metering point(s) left out ({excludedPath})");
        }

        // How far the migration is: metering points done now, out of the total the last snapshot counted.
        if (_done != null && !_markDoneMode
            && _done.TryGet<DoneSummary>(SnapshotDoneSummary, "last", out var lastSummary) && lastSummary is { Total: > 0 })
        {
            var doneNow = Ledger(environment).DoneMarks().Count;
            _log($"Migration: {doneNow} of {lastSummary.Total} metering points done ({doneNow * 100 / lastSummary.Total}%) " +
                 $"— total from the snapshot of {lastSummary.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        }

        // Metering points whose payer in Settl is in the 6##### series are not migrated.
        try
        {
            settlMeteringPoints = await LeaveOutExcludedPayersAsync(settlMeteringPoints, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Error(ex.Message);
        }

        // Xellent tells who held each metering point.
        List<TimeSeriesExtractionService.AccountMeteringPoint> allContracts;
        Dictionary<string, List<TimeSeriesExtractionService.ReadingPeriod>>? readingPeriods = null;
        Dictionary<string, TimeSeriesExtractionService.MeteringPointSubType>? subTypes = null;
        await using (var context = BuildXellentContext())
        {
            var extraction = new TimeSeriesExtractionService(context, deliveryCategory);
            // Parents are asked for too, even when the range or the done check left them out: a child
            // with no contract of its own is held by whoever holds its parent.
            var contractLookups = settlMeteringPoints.Select(m => m.Identification)
                .Concat(settlMeteringPoints.Select(m => m.ParentIdentification).OfType<string>())
                .Distinct(StringComparer.Ordinal).ToList();
            allContracts = (await GetContractsByMeteringPointAsync(extraction, contractLookups))
                .SelectMany(p => p.Value).ToList();
            // A metering point whose reading periods switch resolution inside its window is blocked until Settl
            // confirms its series (--allow-switches sends it). The window checks only decide windows, so they skip it.
            if (!allowSwitches && !_markDoneMode)
            {
                try
                {
                    readingPeriods = await extraction.GetReadingPeriodsAsync(settlMeteringPoints.Select(m => m.Identification));
                }
                catch (InvalidOperationException ex)
                {
                    return Error(ex.Message);
                }
            }
            // A metering point whose subtype in Xellent is blank is blocked. The window checks only decide windows,
            // so they skip this.
            if (!_markDoneMode)
            {
                try
                {
                    subTypes = await extraction.GetMeteringPointSubTypesAsync(settlMeteringPoints.Select(m => m.Identification));
                }
                catch (InvalidOperationException ex)
                {
                    return Error(ex.Message);
                }
            }
        }
        _log($"  {settlMeteringPoints.Count} metering points to check " +
             $"({settlMeteringPoints.Count(m => m.InDelivery)} in delivery, {settlMeteringPoints.Count(m => !m.InDelivery)} not)");
        _log("");
        var contractsByMeteringPoint = allContracts.ToLookup(p => p.MeteringPoint);
        var totalInRange = new Tally();
        var totalPassedOver = new Tally();
        var totalNoDeliveryStart = new Tally();
        var totalOutOfRange = new Tally();
        var totalNotMigrated = new Tally();

        for (var i = 0; i < settlMeteringPoints.Count; i++)
        {
            _scanCurrentMeteringPoint = null;
            if (_markDoneMode)
                ReportProgress("Done check, deciding windows", i + 1, settlMeteringPoints.Count);
            else
            {
                OnStatus?.Invoke($"{i:N0}/{settlMeteringPoints.Count:N0} checked · {StateLine()}");
                (_statesChecked, _statesTotal) = (i, settlMeteringPoints.Count);
                WriteStates("Running", force: i == 0);
            }

            // --limit counts the metering points sent (ready in a dry run): skipped, blocked and failed ones
            // do not use it up. The done check and the missing-readings scan send nothing, so they count
            // every metering point in range.
            if (limit is { } take && take > 0 && (_markDoneMode ? totalInRange : totalPosted).Total >= take)
            {
                _log($"Limit of {take} reached.");
                break;
            }

            var (identification, mpGuid, inDelivery) = (settlMeteringPoints[i].Identification, settlMeteringPoints[i].Id, settlMeteringPoints[i].InDelivery);
            var parentIdentification = settlMeteringPoints[i].ParentIdentification;
            var isChild = parentIdentification != null;
            _meteringPointType = isChild ? "Child" : "Parent";
            // The block header says when the fallback is used; its own log line would print for every metering point checked.
            var revisionResolution = await ResolveRevisionAsync(mpGuid, cancellationToken, quiet: true);
            if (_missingReadingsScan != null && revisionResolution.Error is { } connectionError)
                StopScanOnNetworkFailure(connectionError);
            // A child metering point with no delivery status of its own is delivered under its parent:
            // the window comes from the parent. A child that has one of its own keeps it.
            var revisionGuid = mpGuid;
            if (revisionResolution.Outcome == RevisionResolutionOutcome.Error
                && settlMeteringPoints[i].ParentId is { } parentGuidForRevision)
            {
                var parentResolution = await ResolveRevisionAsync(parentGuidForRevision, cancellationToken, quiet: true);
                if (_missingReadingsScan != null && parentResolution.Error is { } parentConnectionError)
                    StopScanOnNetworkFailure(parentConnectionError);
                if (parentResolution.Outcome != RevisionResolutionOutcome.Error)
                {
                    revisionResolution = parentResolution;
                    revisionGuid = parentGuidForRevision;
                    inDelivery = settlMeteringPoints[i].ParentInDelivery;
                }
            }
            // Not in delivery and no delivery start to fall back on: never delivered, nothing to post.
            if (!inDelivery && revisionResolution.Outcome == RevisionResolutionOutcome.Error)
            {
                totalNoDeliveryStart.Add(isChild);
                CountState("Skipped", "never delivered (no delivery start in Settl)");
                RecordCoverageExclusion("Never delivered (no delivery start)", isChild);
                continue;
            }

            // Place the metering point before acting on it, so --skip/--limit batches never repeat each
            // other's skips or failures. The range needs a delivery start; one Settl marks as in delivery
            // but whose start does not resolve was already placed in range by the list.
            if (revisionResolution.Outcome is RevisionResolutionOutcome.Resolved or RevisionResolutionOutcome.SafetySkip
                && ToDanishDate(revisionResolution.Revision) is var placedOn
                && ((cosFrom is { } from && placedOn < from) || (cosTo is { } to && placedOn > to)))
            {
                totalOutOfRange.Add(isChild);
                RecordCoverageExclusion("Outside delivery-start range", isChild);
                continue;
            }
            if (skip is { } passOver && totalPassedOver.Total < passOver)
            {
                totalPassedOver.Add(isChild);
                RecordCoverageExclusion("Passed over by --skip", isChild);
                continue;
            }
            totalInRange.Add(isChild);
            if (_missingReadingsScan != null)
            {
                _scanCurrentMeteringPoint = identification;
                _missingReadingsScan[identification] = new(identification, mpGuid, null, null, null,
                    "Unknown", "The migration reading period could not be resolved.");
            }
            _log($"--- [{i + 1}/{settlMeteringPoints.Count}] MP {identification} ---" +
                 (parentIdentification is { } shownParent ? $" (child of {shownParent})" : "") +
                 (revisionGuid != mpGuid ? " (window from the parent)" : "") +
                 (inDelivery ? "" : " (not in delivery in Settl — using the current delivery status)"));

            // Records a metering point Settl does not mark as in delivery whose Xellent side does not show
            // a migration: it was never migrated, so it is skipped rather than failed.
            void RecordNotMigrated(string reason, string coverageReason)
            {
                totalNotMigrated.Add(isChild);
                CountState("Skipped", coverageReason);
                RecordCoverageExclusion(coverageReason, isChild);
                _missingReadingsScan?.Remove(identification);
                _log($"SKIP MP {identification}: not migrated — {reason}{TypeSuffix()}");
                RecordSkip(TimeSeriesSkipCategory.NotMigrated, null, identification, mpGuid,
                    revisionResolution.Revision, revisionResolution.DeliveryBoundary, reason);
            }

            if (revisionResolution.Outcome == RevisionResolutionOutcome.Ambiguous)
            {
                totalAmbiguousRevisions.Add(isChild);
                CountBlocked("unclear delivery dates in Settl (first delivery has several start reasons)", identification, mpGuid, null,
                    $"{revisionResolution.ActiveStatuses.Count} delivery statuses marked in delivery");
                RecordCoverageExclusion("Ambiguous delivery revision", isChild);
                _log($"SKIP MP {identification}: ambiguous delivery statuses (different start reasons on the first in-delivery day){TypeSuffix()}");
                RecordAmbiguousRevision(
                    null,
                    identification,
                    mpGuid,
                    revisionResolution.ActiveStatuses);
                continue;
            }
            if (revisionResolution.Outcome == RevisionResolutionOutcome.SafetySkip)
            {
                RecordCoverageExclusion($"Delivery safety: {revisionResolution.SkipCategory}", isChild);
                CountBlocked(SkipReason(revisionResolution.SkipCategory!.Value), identification, mpGuid, null,
                    revisionResolution.SkipReason ?? "");
                _log($"REVISION MP {identification}: {revisionResolution.Revision:O}");
                _log($"SKIP MP {identification}: {revisionResolution.SkipReason}{TypeSuffix()}");
                RecordSkip(
                    revisionResolution.SkipCategory!.Value,
                    null,
                    identification,
                    mpGuid,
                    revisionResolution.Revision,
                    null,
                    revisionResolution.SkipReason ?? "");
                continue;
            }
            // Settl's first delivery is not a change of supplier (a move-in, or no reason): not migrated.
            if (revisionResolution.Outcome == RevisionResolutionOutcome.NotMigrated)
            {
                RecordNotMigrated(revisionResolution.SkipReason ?? "", FirstDeliveryNotMigratedReason(revisionResolution.SkipReason));
                continue;
            }
            if (revisionResolution.Outcome == RevisionResolutionOutcome.Unclear)
            {
                CountBlocked(UnknownStartReason, identification, mpGuid, null, revisionResolution.SkipReason ?? "");
                _log($"BLOCKED:   MP {identification}: {revisionResolution.SkipReason}");
                continue;
            }
            if (revisionResolution.Outcome == RevisionResolutionOutcome.Error)
            {
                RecordCoverageExclusion("Delivery revision could not be resolved", isChild);
                CountState("Failed", "Settl delivery status could not be read");
                if (!RecordFailure($"  Revision resolution failed for MP {identification} ({mpGuid}): {revisionResolution.Error}")) return 1;
                continue;
            }

            var revisionDate = ToDanishDate(revisionResolution.Revision);

            // The migrated account is the one whose Xellent contract was still running on the day before
            // delivery starts in Settl: at migration the contract is closed on COS − 1 (XellentDomain
            // docs/xellent-datamodel.md) — DataMigration's reconcile picks owners the same way. "Running
            // on" rather than "ends on", since the 2026-08-31 batch restamped some ends.
            var periodStart = revisionDate.AddDays(-1);
            // A child metering point with no contract of its own sits on its parent's: the account,
            // the migration close and the hold history are then the parent's. A child that has its own
            // contract keeps it.
            var contractsFor = contractsByMeteringPoint[identification];
            var contractIdentification = identification;
            List<string> HoldersOn(IEnumerable<TimeSeriesExtractionService.AccountMeteringPoint> contracts) => contracts
                .Where(c => c.Covers(periodStart)).Select(c => c.Account).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
            var holders = HoldersOn(contractsFor);
            if (holders.Count == 0 && parentIdentification is { } contractParent)
            {
                var parentHolders = HoldersOn(contractsByMeteringPoint[contractParent]);
                if (parentHolders.Count > 0)
                {
                    contractsFor = contractsByMeteringPoint[contractParent];
                    contractIdentification = contractParent;
                    holders = parentHolders;
                    _log($"  contract from the parent ({contractParent})");
                }
            }
            if (holders.Count == 0)
            {
                var reason = $"no Xellent contract on {periodStart:yyyy-MM-dd} (the day before delivery starts in Settl)";
                if (!inDelivery)
                {
                    RecordNotMigrated(reason, NoContractReason);
                    continue;
                }
                RecordCoverageExclusion("Missing Xellent contract", isChild);
                CountState("Skipped", NoContractReason);
                _log($"SKIP MP {identification}: {reason}{TypeSuffix()}");
                RecordSkip(TimeSeriesSkipCategory.MissingInXellent, null, identification, mpGuid,
                    revisionResolution.Revision, revisionResolution.DeliveryBoundary, reason);
                continue;
            }
            if (holders.Count > 1)
            {
                var reason = $"held by several accounts on {periodStart:yyyy-MM-dd} ({string.Join("/", holders)})";
                if (!inDelivery)
                {
                    RecordNotMigrated(reason, SeveralHoldersReason);
                    continue;
                }
                RecordCoverageExclusion("Multiple Xellent account holders", isChild);
                // Blocked, not failed: the run goes on (only a failed call to Settl stops it).
                CountBlocked(SeveralHoldersReason, identification, mpGuid, null, reason);
                _log($"BLOCKED:   MP {identification}: {reason}");
                continue;
            }
            var accountNumber = holders[0];
            if (_missingReadingsScan != null)
                _missingReadingsScan[identification] = _missingReadingsScan[identification] with { Account = accountNumber };
            _log($"  customer {accountNumber}");

            // A migrated relationship is closed in Xellent the day before delivery starts in Settl:
            // the account's contract ends that day, and the agreement behind it has status
            // "Leverance ophørt" and ends that day too. Anything else is not a clean migration;
            // post nothing. The featuretest "latest 3 years" override is not tied to the migration
            // and is exempt — except for a metering point Settl does not mark as in delivery, where
            // the close is the only sign it was migrated at all.
            if ((windowEndDateOverride is null || !inDelivery)
                && CheckMigrationClose(contractsFor, accountNumber, periodStart) is { } closeProblem)
            {
                if (!inDelivery)
                {
                    RecordNotMigrated($"customer {accountNumber}: {closeProblem}", NotClosedReason);
                    continue;
                }
                // Decided 2026-10-16: not closed as a migration at COS − 1 means it was not migrated, so it is a skip.
                RecordCoverageExclusion("Migration close validation failed", isChild);
                CountState("Skipped", NotClosedReason);
                var closeReason = $"customer {accountNumber}{(contractIdentification != identification ? $", on the parent {contractIdentification}" : "")}: {closeProblem}";
                _log($"SKIP MP {identification}: not closed as a migration — {closeReason}{TypeSuffix()}");
                RecordSkip(TimeSeriesSkipCategory.NotMigrated, accountNumber, identification, mpGuid,
                    revisionResolution.Revision, revisionResolution.DeliveryBoundary, closeReason);
                continue;
            }

            DateTime endDate;
            if (windowEndDateOverride is { } overrideEndDate)
            {
                endDate = overrideEndDate.Date;
            }
            else
            {
                endDate = revisionDate.ToDateTime(TimeOnly.MinValue);
            }

            var startDate = endDate.AddYears(-3);
            // Points before the delivery boundary are dropped anyway, so the window starts there:
            // a series whose agreement ended before the boundary is then never read from Xellent.
            if (revisionResolution.DeliveryBoundary is { } boundary
                && ToDanishDate(boundary).ToDateTime(TimeOnly.MinValue) is var boundaryDate
                && boundaryDate > startDate)
                startDate = boundaryDate;
            // History before the account took over the metering point belongs to the previous
            // occupant (MP 571313113140168212: 486014 lived there until 2025-02-28, 493867 from
            // 2025-03-01), so the window starts no earlier than the account's own contract.
            if (AccountHeldSince(contractsFor, accountNumber, periodStart) is { } heldSince
                && heldSince.ToDateTime(TimeOnly.MinValue) > startDate)
                startDate = heldSince.ToDateTime(TimeOnly.MinValue);

            _log("");
            _log($"  MP {identification}: end={endDate:yyyy-MM-dd}  start={startDate:yyyy-MM-dd}");

            // The window end is not checked against the metering point's current delivery status in Settl: it shows
            // later events (the customer left, a new period), not the migration (production 2026-10-21: 1,706 such).

            // The snapshot seed's done check: everything above decided this metering point would be
            // posted with this window. It is collected here and asked about afterwards, in parallel
            // (CheckDoneCandidatesAsync).
            if (_markDoneMode)
            {
                var doneFrom = DateOnly.FromDateTime(startDate);
                if (revisionResolution.DeliveryBoundary is { } doneBoundary && ToDanishDate(doneBoundary) > doneFrom)
                    doneFrom = ToDanishDate(doneBoundary);
                _doneCandidates.Add(new DoneCandidate(identification, mpGuid, accountNumber, revisionResolution.Revision,
                    doneFrom, DateOnly.FromDateTime(endDate), revisionResolution.FromCurrentDeliveryStatus, _meteringPointType));
                continue;
            }

            // The subtype on the metering point's latest agreement in Xellent: blank (no meter row, or a value that is
            // not 1–3) is blocked. Physical, virtual (E18 production) and calculated go on. No meter installed
            // (no values, Xellent status Oprettet) comes before blank, as it does before the switch block: it stays skipped.
            if (subTypes != null)
            {
                var subType = subTypes.GetValueOrDefault(identification);
                if ((subType?.SubType is not { } known || !KnownSubTypes.Contains(known))
                    && !await NotInstalledAsync(identification, startDate, endDate, revisionResolution.DeliveryBoundary))
                {
                    var detail = subType == null ? "no delivery point in Xellent"
                        : subType.SubType == null ? $"agreement {subType.Agreement}: no meter row"
                        : $"agreement {subType.Agreement}: METERINGPOINTDHSUBTYPE {subType.SubType}";
                    RecordCoverageExclusion("Subtype blank in Xellent", isChild);
                    CountBlocked(BlankSubTypeReason, identification, mpGuid, accountNumber, detail);
                    _log($"BLOCKED:   MP {identification}: {BlankSubTypeReason} ({detail})");
                    continue;
                }
            }

            // Reading periods that switch resolution inside the window, the rule resolution-changes lists: nothing is
            // sent until Settl confirms the series. Blocked, so the run goes on. --allow-switches sends them.
            // No meter installed (no values, Xellent status Oprettet) comes first: that one goes on to the post,
            // which skips it, so it stays skipped.
            if (readingPeriods != null
                && ReadingPeriodSwitches(readingPeriods.TryGetValue(identification, out var periods) ? periods : [],
                    DateOnly.FromDateTime(startDate), DateOnly.FromDateTime(endDate)) is { Count: > 0 } switchDates
                && !await NotInstalledAsync(identification, startDate, endDate, revisionResolution.DeliveryBoundary))
            {
                RecordCoverageExclusion("Resolution switch", isChild);
                var detail = $"reading periods switch resolution on {string.Join(", ", switchDates.Select(d => d.ToString("yyyy-MM-dd")))}";
                CountBlocked(ResolutionSwitchReason, identification, mpGuid, accountNumber, detail,
                    await DataHubMessagesOnAsync(identification, switchDates, periods ?? []));
                _log($"BLOCKED:   MP {identification}: {detail}");
                continue;
            }

            // Each MP is the atomic unit: the inner post stays fail-fast (a single MP must not be
            // left half-posted), and this loop decides whether to continue past it.
            var postOptions = new PostOptions
            {
                InBatch = true,
                MeteringPointId = identification,
                MeteringPointGuid = mpGuid,
                ResolvedRevision = revisionResolution.Revision,
                ResolvedDeliveryBoundary = revisionResolution.DeliveryBoundary,
                CustomerNumber = accountNumber,
                StartDate = startDate,
                EndDate = endDate,
                DeliveryCategory = deliveryCategory,
                Environment = environment,
                DryRun = dryRun,
                SkipResolutionChange = skipResolutionChange,
                SkipIfExistingPoints = skipIfExistingPoints,
            };
            _lastSkip = null;
            var rc = await RunPostCoreAsync(postOptions, cancellationToken);

            // A live post that put points in Settl marks the metering point done, like the seed's check would.
            if (rc == 0 && !dryRun && postOptions.PointsPosted > 0 && _done != null)
                Ledger(environment).MarkDone(identification, mpGuid, accountNumber, revisionResolution.Revision,
                    DateOnly.FromDateTime(startDate), DateOnly.FromDateTime(endDate), postOptions.PointsPosted);

            if (rc != 0)
            {
                var (state, stateReason) = ClassifyPostError(postOptions.Error);
                if (state == "Blocked")
                {
                    // Blocked, not failed: something has to be fixed first. Listed, and the run goes on.
                    CountBlocked(stateReason, identification, mpGuid, accountNumber, postOptions.Error ?? "");
                    _log($"  → MP {identification} ({_meteringPointType.ToLowerInvariant()}) blocked: {stateReason}");
                    continue;
                }
                CountState(state, stateReason);
                failures.Add($"MP {identification} ({_meteringPointType.ToLowerInvariant()}, customer {accountNumber}) post failed");
                totalFailed.Add(isChild);
                if (!continueOnError)
                {
                    _log("");
                    _log($"Aborting run after failure on MP {identification} ({_meteringPointType.ToLowerInvariant()}, customer {accountNumber}).");
                    return rc;
                }
                _log($"  → MP {identification} ({_meteringPointType.ToLowerInvariant()}) fejlede — fortsaetter (--force)");
                continue;
            }

            // Everything for this metering point is in the ledger: sent before, so done.
            if (postOptions.AlreadyInLedger)
            {
                if (!dryRun && _done != null)
                    Ledger(environment).MarkDone(identification, mpGuid, accountNumber, revisionResolution.Revision,
                        DateOnly.FromDateTime(startDate), DateOnly.FromDateTime(endDate), 0);
                CountState("Done", "already in the ledger");
                continue;
            }
            // A metering point the post skipped (no meter installed, safety checks) is in a skip file, not posted.
            if (postOptions.Skipped)
            {
                CountState("Skipped",
                    _lastSkip switch
                    {
                        { } category => SkipReason(category),
                        null => "skipped during the post (see log)",
                    });
                continue;
            }
            totalPosted.Add(isChild);
            CountState(dryRun ? "Ready" : "Done", dryRun ? "would be posted" : "posted in this run");
        }

        _log("");
        // Format is contract to the dashboard's parser (useTimeSeriesRun.ts): plain totals, same order,
        // ending in "N children skipped." Children are never in scope.
        _log($"Run completed: {settlMeteringPoints.Count} metering points in Settl, {totalInRange.Total} processed, {totalPosted.Total} posted, " +
             $"{totalNoDeliveryStart.Total} never delivered, {totalNotMigrated.Total} not migrated, " +
             (cosFrom != null || cosTo != null ? $"{totalOutOfRange.Total} outside the delivery-start range, " : "") +
             (skip is > 0 ? $"{totalPassedOver.Total} passed over (--skip), " : "") +
             $"{totalAmbiguousRevisions.Total} ambiguous revision safety skips, {totalChildren} children skipped.");
        OnStatus?.Invoke($"{settlMeteringPoints.Count:N0}/{settlMeteringPoints.Count:N0} checked · {StateLine()}");
        (_statesChecked, _statesTotal, _statesCompleted) = (settlMeteringPoints.Count, settlMeteringPoints.Count, true);
        if (!_markDoneMode)
        {
            _log("");
            _log($"States{(dryRun ? " (dry run: ready = would be posted)" : "")}: {StateLine()}");
            foreach (var state in StateOrder.Where(st => StateTotal(st) > 0))
            {
                _log($"  {state}: {StateTotal(state):N0}");
                foreach (var (reason, count) in _states[state].OrderByDescending(r => r.Value))
                    _log($"    {count,8:N0}  {reason}" + (state == "Blocked" ? $"  → {BlockedCsvFileName(reason)}" : ""));
            }
            if (StateTotal("Blocked") > 0)
            {
                _log($"  Blocked metering points are listed per reason in {Path.GetFullPath(_outputBaseDirectory)}: " +
                     "hand each file to whoever has to fix that group.");
            }
            _log("");
        }
        if (failures.Count > 0)
        {
            _log($"{failures.Count} failure(s) skipped (--force):");
            foreach (var f in failures)
                _log($"  - {f}");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// The metering points a batch run (<see cref="RunPostAllAsync"/>) takes from Settl, in the order it
    /// takes them (identification): every electricity metering point, children included. A child (Settl
    /// relation or the Xellent relation table) carries its parent, so it can fall back to the parent's
    /// delivery status and Xellent contract when it has none of its own. Metering points the list shows
    /// as in delivery with a delivery
    /// start outside the range are left out. Those the list shows as not in delivery are kept whatever
    /// the range, because their delivery start is only known per metering point (Settl bug, 2026-09).
    /// Throws <see cref="InvalidOperationException"/> when Settl or Xellent cannot be read.
    /// </summary>
    public async Task<List<BatchMeteringPoint>> ListBatchMeteringPointsAsync(
        DateOnly? cosFrom = null, DateOnly? cosTo = null,
        CancellationToken cancellationToken = default)
    {
        if (_settlMeteringPointClient == null)
            throw new InvalidOperationException("Settl API clients not configured. DI registration missing.");
        _settlMeteringPointClient.SetDeliveryCategory(BatchDeliveryCategory);
        return (await LoadBatchMeteringPointsAsync(cosFrom, cosTo, cancellationToken)).MeteringPoints;
    }

    // The Settl list, with the Xellent relation table filling in the parent for children Settl does
    // not relate itself.
    private async Task<(List<BatchMeteringPoint> MeteringPoints, int Children)> LoadBatchMeteringPointsAsync(
        DateOnly? cosFrom, DateOnly? cosTo, CancellationToken cancellationToken)
    {
        var (meteringPoints, allByIdentification) = await LoadSettlMeteringPointsAsync(cosFrom, cosTo, cancellationToken);
        await using var context = BuildXellentContext();
        if (_snapshot is not { FetchMissing: false } && !await TestConnectionAsync(context))
            throw new InvalidOperationException("Could not connect to the Xellent database.");
        var extraction = new TimeSeriesExtractionService(context, BatchDeliveryCategory);
        var parentByChild = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in await GetChildrenByParentAsync(extraction, meteringPoints.Select(m => m.Identification).ToList()))
            foreach (var child in pair.Value)
                parentByChild[child] = pair.Key;

        var fromXellent = 0;
        for (var i = 0; i < meteringPoints.Count; i++)
        {
            var mp = meteringPoints[i];
            // Settl's own relation wins; Xellent only fills in what Settl does not relate.
            if (mp.ParentIdentification != null || !parentByChild.TryGetValue(mp.Identification, out var parentId))
                continue;
            fromXellent++;
            // The parent is taken from the whole Settl list, not the filtered one: a parent left out by
            // the delivery-start range is still the child's fallback.
            allByIdentification.TryGetValue(parentId, out var parent);
            meteringPoints[i] = mp with
            {
                ParentIdentification = parentId,
                ParentId = parent is { } p && p.Id != Guid.Empty ? p.Id : null,
                ParentInDelivery = parent is { } q && (q.InDelivery == true || q.DeliveryStart is not null),
            };
        }
        var childCount = meteringPoints.Count(m => m.ParentIdentification != null);
        // Children are never in scope. The relations above are still read: they are how a child is known.
        RecordCoverageExclusion("Children (never in scope)", isChild: true, childCount);
        if (childCount > 0)
            _log($"  skipped {childCount} child MP(s), {fromXellent} of them known from the Xellent relation table");
        return (meteringPoints.Where(m => m.ParentIdentification == null).ToList(), childCount);
    }

    /// <summary>
    /// Every electricity metering point in the Settl environment, ordered by identification, children
    /// included. A child (Settl relation) carries its parent, so it can fall back to the parent's
    /// delivery status when it has none of its own. Metering points the list shows as in delivery, with
    /// a delivery start outside the range, are dropped here; the rest get their delivery start resolved
    /// one by one later.
    /// </summary>
    private async Task<(List<BatchMeteringPoint> MeteringPoints, Dictionary<string, SettlListItem> AllByIdentification)>
        LoadSettlMeteringPointsAsync(
        DateOnly? cosFrom, DateOnly? cosTo, CancellationToken cancellationToken)
    {
        var list = await GetSettlListAsync(cancellationToken);
        // The parent is looked up by GUID: the relation carries the GUID, the fallback needs its
        // identification (Xellent contracts) and whether Settl has it in delivery.
        var byGuid = list
            .Where(mp => !string.IsNullOrEmpty(mp.Identification))
            .GroupBy(mp => mp.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var byIdentification = new Dictionary<string, BatchMeteringPoint>(StringComparer.Ordinal);
        var skippedOutOfRange = 0;
        foreach (var mp in list)
        {
            if (string.IsNullOrEmpty(mp.Identification))
                continue;

            if (mp.DeliveryStart is { } s
                && ToDanishDate(s) is var cos
                && ((cosFrom is { } from && cos < from) || (cosTo is { } to && cos > to)))
            {
                skippedOutOfRange++;
                continue;
            }
            SettlListItem? parent = null;
            if (mp.ParentId is { } parentId && parentId != Guid.Empty)
                byGuid.TryGetValue(parentId, out parent);
            byIdentification[mp.Identification] = new BatchMeteringPoint(
                mp.Identification, mp.Id, mp.InDelivery == true || mp.DeliveryStart is not null,
                mp.DeliveryStart is { } ds ? ToDanishDate(ds) : null,
                parent?.Identification,
                mp.ParentId is { } pid && pid != Guid.Empty ? pid : null,
                parent is { } p && (p.InDelivery == true || p.DeliveryStart is not null));
        }
        if (skippedOutOfRange > 0)
            _log($"  skipped {skippedOutOfRange} MP(s) with delivery start outside the range");

        var allByIdentification = list
            .Where(mp => !string.IsNullOrEmpty(mp.Identification))
            .GroupBy(mp => mp.Identification, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return (byIdentification.Values.OrderBy(m => m.Identification, StringComparer.Ordinal).ToList(), allByIdentification);
    }

    // ---- Reads that can come from a snapshot (see RunSnapshot) ----

    private const string SnapshotSettlList = "settl-metering-points";
    private const string SnapshotDeliveryStatuses = "settl-delivery-statuses";
    private const string SnapshotCurrentDeliveryStatus = "settl-current-delivery-status";
    private const string SnapshotTimeSeries = "settl-time-series";
    private const string SnapshotXellentContracts = "xellent-contracts";
    private const string SnapshotXellentChildren = "xellent-children";
    // v3: dated reading resolution and conversion precision validation, retaining source counts.
    private const string SnapshotXellentPointCounts = "xellent-point-counts-v3";
    private const string SnapshotMeterPointStatus = "xellent-meterpoint-status";
    private const string SnapshotDone = "done";
    private const string SnapshotDoneSummary = "done-summary";
    private const string SnapshotPayer = "settl-payer";

    /// <summary>The seed's count of how many metering points the migration covers (done + still to post).</summary>
    public sealed record DoneSummary(int Total, int Done, DateTimeOffset CheckedAt);

    /// <summary>A metering point's current payer in Settl, as the snapshot keeps it (null: none).</summary>
    public sealed record PayerEntry(string? PayerIdentification);

    // Payers in this series (six digits starting with 6) are not migrated.
    private static readonly System.Text.RegularExpressions.Regex ExcludedPayerPattern = new(@"^6\d{5}$");

    // Leaves out metering points whose current Settl payer is in the 6##### series and lists them.
    private async Task<List<BatchMeteringPoint>> LeaveOutExcludedPayersAsync(List<BatchMeteringPoint> meteringPoints, CancellationToken cancellationToken)
    {
        if (_settlPayerClient == null)
        {
            _log("Payer check: not available here — metering points with a 6##### payer are NOT left out.");
            return meteringPoints;
        }
        var payers = await GetPayersAsync(meteringPoints.Select(m => m.Id).ToList(), 6, cancellationToken);
        var excluded = meteringPoints
            .Where(m => payers.TryGetValue(m.Id, out var payer) && payer is { } p && ExcludedPayerPattern.IsMatch(p))
            .ToList();
        if (excluded.Count == 0)
            return meteringPoints;
        foreach (var mp in excluded)
        {
            _meteringPointType = mp.ParentIdentification != null ? "Child" : "Parent";
            RecordCoverageExclusion("Excluded payer (6#####)", mp.ParentIdentification != null);
            RecordSkip(TimeSeriesSkipCategory.ExcludedPayer, null, mp.Identification, mp.Id, null, null, $"payer {payers[mp.Id]}");
        }
        _meteringPointType = "";
        CountState("Skipped", SkipReason(TimeSeriesSkipCategory.ExcludedPayer), excluded.Count);
        _log($"Payer 6#####: {excluded.Count} metering point(s) left out ({SkipCsvFileNames[TimeSeriesSkipCategory.ExcludedPayer]})");
        var excludedIds = excluded.Select(m => m.Id).ToHashSet();
        return meteringPoints.Where(m => !excludedIds.Contains(m.Id)).ToList();
    }

    /// <summary>
    /// Each metering point's current payer identification in Settl (null when it has none), asked 100 at
    /// a time, <paramref name="parallelism"/> requests at once; kept in the snapshot when one is active.
    /// Throws <see cref="InvalidOperationException"/> when Settl can't be read.
    /// </summary>
    private async Task<Dictionary<Guid, string?>> GetPayersAsync(IReadOnlyList<Guid> meteringPointIds, int parallelism, CancellationToken cancellationToken)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<Guid, string?>();
        var missing = new List<Guid>();
        foreach (var id in meteringPointIds.Distinct())
        {
            if (_snapshot != null && _snapshot.TryGet<PayerEntry>(SnapshotPayer, id.ToString(), out var cached) && cached != null)
                result[id] = cached.PayerIdentification;
            else
                missing.Add(id);
        }
        if (missing.Count == 0)
            return new Dictionary<Guid, string?>(result);
        if (_snapshot is { FetchMissing: false })
            throw new InvalidOperationException($"snapshot has no '{SnapshotPayer}' entry for {missing.Count} metering point(s)");

        var chunks = missing.Chunk(100).ToList();
        var done = 0;
        await Parallel.ForEachAsync(chunks,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (chunk, ct) =>
            {
                var (entries, error) = await _settlPayerClient!.FindPayersForMeteringPointsAsync(chunk, null, ct);
                if (error != null)
                    throw new InvalidOperationException($"Settl payersForMeteringPoints failed: {error}");
                var found = new Dictionary<Guid, string?>();
                foreach (var entry in entries ?? [])
                {
                    var payer = entry.Payer?.PayerInformation?.PayerIdentifier;
                    foreach (var mp in entry.MeteringPoints ?? [])
                    {
                        // Should a metering point show up under two payers, a 6##### one wins: leaving it out is the safe side.
                        if (!found.TryGetValue(mp.MeteringPointId, out var existing) || existing is null
                            || (payer is { } p && ExcludedPayerPattern.IsMatch(p)))
                            found[mp.MeteringPointId] = payer;
                    }
                }
                foreach (var id in chunk)
                {
                    found.TryGetValue(id, out var payer);
                    result[id] = payer;
                    _snapshot?.Put(SnapshotPayer, id.ToString(), new PayerEntry(payer));
                }
                _snapshot?.CountFetched();
                ReportProgress("Settl payers", Interlocked.Add(ref done, chunk.Length), missing.Count);
            });
        return new Dictionary<Guid, string?>(result);
    }

    /// <summary>
    /// A metering point that is in Settl: either the snapshot seed found points in the window a post
    /// would use (<c>ExistingPoints</c> in the first year-chunk that has any), or a live batch post
    /// posted them (<c>ExistingPoints</c> = points posted). Kept so runs leave it out.
    /// </summary>
    public sealed record DoneEntry(Guid SettlId, string Account, DateTimeOffset Revision, DateOnly WindowStart, DateOnly WindowEnd,
        int ExistingPoints, DateTimeOffset CheckedAt);

    /// <summary>Where the done marks live during a batch run or seed (the environment's snapshot folder).</summary>
    private RunSnapshot? _done;

    /// <summary>"Parent" or "Child" for the metering point being handled, "" where it is not known
    /// (a profile post). Written wherever a metering point is named: CSV rows and skip logs.</summary>
    private string _meteringPointType = "";

    /// <summary>A count kept apart for parent and child metering points.</summary>
    private struct Tally
    {
        public int Parents;
        public int Children;
        public readonly int Total => Parents + Children;
        public void Add(bool isChild)
        {
            if (isChild) Children++;
            else Parents++;
        }
        public override readonly string ToString() => $"{Total} ({Parents} parents, {Children} children)";
    }

    // Set during the seed's done check: the batch loop collects the metering points it would post, with
    // their window, instead of posting them.
    private bool _markDoneMode;
    private readonly List<DoneCandidate> _doneCandidates = [];
    private Dictionary<string, MissingReadingsEntry>? _missingReadingsScan;
    private string? _scanCurrentMeteringPoint;

    // The missing-readings scan (dashboard) shows why a metering point it looked at was set aside.
    private void RecordCoverageExclusion(string reason, bool isChild, int count = 1)
    {
        if (_scanCurrentMeteringPoint is { } mp && _missingReadingsScan?.TryGetValue(mp, out var entry) == true)
            _missingReadingsScan[mp] = entry with { Detail = reason };
    }

    /// <summary><paramref name="FromCurrentDeliveryStatus"/>: Settl marks no delivery status InDelivery=true for
    /// this metering point, so the revision came from the current delivery status (the Settl bug) — delivery is
    /// not clearly stated.</summary>
    private sealed record DoneCandidate(string Identification, Guid Id, string Account, DateTimeOffset Revision, DateOnly From, DateOnly To,
        bool FromCurrentDeliveryStatus, string Type);

    private const string SnapshotPointsSummary = "settl-points-summary";

    /// <summary>
    /// Asks Settl, <paramref name="parallelism"/> at a time, whether any series of each candidate already
    /// has points in its window, and marks those done. Settl answers one year at a time; the first year
    /// with points settles it. Every answer is kept in the snapshot, so a later done check reads it
    /// instead of asking again (the user accepts it can go stale).
    /// </summary>
    private async Task<(int Marked, List<string> Failures)> CheckDoneCandidatesAsync(PostLedger ledger, int parallelism,
        CancellationToken cancellationToken)
    {
        var marked = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var checkedCount = 0;
        await Parallel.ForEachAsync(_doneCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
            async (candidate, ct) =>
            {
                var (existing, error) = await FindExistingPointsAsync(candidate, ct);
                if (error != null)
                    failures.Add($"MP {candidate.Identification} ({candidate.Id}): done check: {error}");
                else if (existing is { } count)
                {
                    Interlocked.Increment(ref marked);
                    ledger.MarkDone(candidate.Identification, candidate.Id, candidate.Account, candidate.Revision,
                        candidate.From, candidate.To, count);
                }
                ReportProgress("Done check, asking Settl", Interlocked.Increment(ref checkedCount), _doneCandidates.Count);
            });
        return (marked, failures.OrderBy(f => f, StringComparer.Ordinal).ToList());
    }

    // The points in the first year-chunk that has any, across the candidate's series; null when none do.
    private async Task<(int? Points, string? Error)> FindExistingPointsAsync(DoneCandidate candidate, CancellationToken cancellationToken)
    {
        var (tsList, tsError) = await GetTimeSeriesListAsync(candidate.Id, cancellationToken);
        if (tsError != null)
            return (null, $"GetTimeSeries failed: {tsError}");
        foreach (var ts in tsList ?? [])
        {
            // The same year-chunks as GetPointsSummaryInYearChunksAsync.
            var chunkStart = candidate.From;
            while (chunkStart < candidate.To)
            {
                var chunkEnd = chunkStart.AddYears(1).AddDays(-1);
                if (chunkEnd > candidate.To)
                    chunkEnd = candidate.To;
                var (summary, error) = await _snapshot!.GetOrFetchAsync(SnapshotPointsSummary,
                    $"{ts.Id}|{chunkStart:yyyy-MM-dd}|{chunkEnd:yyyy-MM-dd}",
                    () => _settlTimeSeriesClient!.GetPointsSummaryAsync(ts.Id, chunkStart, chunkEnd, cancellationToken));
                if (error != null)
                    return (null, $"GetPointsSummary failed: {error}");
                if (summary is { Count: > 0 })
                    return (summary.Count, null);
                chunkStart = chunkEnd;
            }
        }
        return (null, null);
    }

    /// <summary>
    /// The part of a Settl list entry the batch run uses — what a snapshot stores. Addresses, names and
    /// the rest of the entry are left out on purpose.
    /// </summary>
    public sealed record SettlListItem(string Identification, Guid Id, Guid? ParentId, bool? InDelivery, DateTimeOffset? DeliveryStart);

    /// <summary>The snapshot active for the current batch or seed run; null reads everything live.</summary>
    private RunSnapshot? _snapshot;

    /// <summary>Where the snapshot for an environment lives.</summary>
    public string SnapshotDirectory(string environment) => Path.Combine(_outputBaseDirectory, "snapshot", environment);

    // The environment's ledger of writes to Settl, read once per runner.
    private PostLedger? _ledger;
    private string? _ledgerEnvironment;

    private PostLedger Ledger(string environment)
    {
        if (_ledger == null || _ledgerEnvironment != environment)
            (_ledger, _ledgerEnvironment) = (new PostLedger(SnapshotDirectory(environment)), environment);
        return _ledger;
    }

    // Every electricity metering point in Settl's paged list, children included.
    private async Task<List<SettlListItem>> GetSettlListAsync(CancellationToken cancellationToken)
    {
        if (_snapshot != null && _snapshot.TryGet<List<SettlListItem>>(SnapshotSettlList, "all", out var cached) && cached != null)
        {
            _log($"Metering points from snapshot: {cached.Count} ({_snapshot.DirectoryPath})");
            return cached;
        }
        if (_snapshot is { FetchMissing: false })
            throw new InvalidOperationException($"snapshot has no '{SnapshotSettlList}' entry for key 'all'");

        const int pageSize = 500;
        var items = new List<SettlListItem>();
        var skip = 0;
        var total = 0;
        _log("Loading metering points from Settl...");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, count, err) = await _settlMeteringPointClient!.FindMeteringPointsAsync(pageSize, skip, skip == 0, cancellationToken);
            if (err != null)
                throw new InvalidOperationException($"Settl FindMeteringPoints failed: {err}");
            if (skip == 0 && count > 0) total = count;
            if (page.Count == 0) break;
            items.AddRange(page.Select(mp => new SettlListItem(
                mp.MeteringPointIdentification, mp.Id, mp.ParentMeteringPoint?.Id, mp.InDelivery,
                mp.CurrentOrNextDeliveryPeriod is { } p && p.Start != default ? p.Start : null)));
            _log($"  ...scanned {items.Count}{(total > 0 ? $"/{total}" : "")} MPs");
            skip += pageSize;
            if (page.Count < pageSize) break;
            if (total > 0 && skip >= total) break;
        }
        if (_snapshot != null)
        {
            _snapshot.CountFetched();
            _snapshot.Put(SnapshotSettlList, "all", items);
        }
        return items;
    }

    private async Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(
        Guid meteringPointId, CancellationToken cancellationToken)
    {
        if (_snapshot == null)
            return await _settlMeteringPointClient!.GetDeliveryStatusesAsync(meteringPointId, cancellationToken);
        var (statuses, error) = await _snapshot.GetOrFetchAsync<List<DeliveryStatusDto>>(
            SnapshotDeliveryStatuses, meteringPointId.ToString(), async () =>
            {
                var (response, err) = await _settlMeteringPointClient!.GetDeliveryStatusesAsync(meteringPointId, cancellationToken);
                if (err != null)
                    return (null, err);
                return response?.DeliveryStatuses is { } list
                    ? (list, null)
                    : (null, "returned no DeliveryStatuses list.");
            });
        return error != null
            ? (null, error)
            : (new GetDeliveryStatusesResponse { Id = meteringPointId, DeliveryStatuses = statuses }, null);
    }

    // The metering point as far as the run needs it: its current delivery status. A snapshot keeps only
    // that section (the rest carries consumer names and addresses).
    private async Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetCurrentMeteringPointAsync(
        Guid meteringPointId, CancellationToken cancellationToken)
    {
        if (_snapshot == null)
            return await _settlMeteringPointClient!.GetByIdV4Async(meteringPointId, cancellationToken: cancellationToken);
        var (status, error) = await _snapshot.GetOrFetchAsync<MeteringPointV4DeliveryStatusDto>(
            SnapshotCurrentDeliveryStatus, meteringPointId.ToString(), async () =>
            {
                var (mp, err) = await _settlMeteringPointClient!.GetByIdV4Async(meteringPointId, cancellationToken: cancellationToken);
                return (mp?.DeliveryStatus, err);
            });
        return error != null ? (null, error) : (new MeteringPointV4Dto { Id = meteringPointId, DeliveryStatus = status }, null);
    }

    private Task<(List<TimeSeriesInfoDto>? TimeSeries, string? Error)> GetTimeSeriesListAsync(
        Guid meteringPointId, CancellationToken cancellationToken) =>
        _snapshot == null
            ? _settlMeteringPointClient!.GetTimeSeriesAsync(meteringPointId, cancellationToken)
            : _snapshot.GetOrFetchAsync(SnapshotTimeSeries, meteringPointId.ToString(),
                () => _settlMeteringPointClient!.GetTimeSeriesAsync(meteringPointId, cancellationToken));

    private Task<Dictionary<string, List<TimeSeriesExtractionService.AccountMeteringPoint>>> GetContractsByMeteringPointAsync(
        TimeSeriesExtractionService extraction, IReadOnlyCollection<string> meteringPoints) =>
        GetPerMeteringPointAsync(SnapshotXellentContracts, meteringPoints, async missing =>
            (await extraction.GetMeteringPointContractsAsync(missing))
                .GroupBy(c => c.MeteringPoint)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal));

    private Task<Dictionary<string, List<string>>> GetChildrenByParentAsync(
        TimeSeriesExtractionService extraction, IReadOnlyCollection<string> meteringPoints) =>
        GetPerMeteringPointAsync(SnapshotXellentChildren, meteringPoints, extraction.GetChildrenByParentAsync);

    // A Xellent read per metering point: what the snapshot holds, the rest queried in one go and stored
    // (an empty answer too — "no contracts" is an answer worth keeping).
    private async Task<Dictionary<string, List<T>>> GetPerMeteringPointAsync<T>(
        string kind, IReadOnlyCollection<string> meteringPoints, Func<List<string>, Task<Dictionary<string, List<T>>>> query)
    {
        var result = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var mp in meteringPoints.Distinct())
        {
            if (_snapshot != null && _snapshot.TryGet<List<T>>(kind, mp, out var cached) && cached != null)
                result[mp] = cached;
            else
                missing.Add(mp);
        }
        if (missing.Count == 0)
            return result;
        if (_snapshot is { FetchMissing: false })
            throw new InvalidOperationException($"snapshot has no '{kind}' entry for {missing.Count} metering point(s)");

        var fetched = await query(missing);
        _snapshot?.CountFetched(missing.Count);
        foreach (var mp in missing)
        {
            var rows = fetched.TryGetValue(mp, out var list) ? list : [];
            result[mp] = rows;
            _snapshot?.Put(kind, mp, rows);
        }
        return result;
    }

    private XellentDbContext BuildXellentContext()
    {
        if (_contextFactory != null)
            return _contextFactory();

        var optionsBuilder = new DbContextOptionsBuilder<XellentDbContext>();
        optionsBuilder.UseSqlServer(_connectionString, sqlOptions =>
        {
            sqlOptions.UseCompatibilityLevel(110);
        });
        return new XellentDbContext(optionsBuilder.Options);
    }

    private async Task<bool> TestConnectionAsync(XellentDbContext context)
    {
        _log("Testing database connection...");
        try
        {
            await context.Database.CanConnectAsync();
            _log("Connected to Xellent database");
            _log("");
            return true;
        }
        catch (Exception ex)
        {
            _log($"ERROR: Failed to connect: {ex.Message}");
            return false;
        }
    }

    private async Task<(string[] MeteringPointIds, string[] Accounts)> ResolveMeteringPointsAsync(
        XellentDbContext context,
        string? meteringPointId,
        string? accountNumber,
        string deliveryCategory,
        bool includeChildren,
        bool quiet = false)
    {
        if (meteringPointId != null)
        {
            if (!quiet)
                _log($"Metering Point: {meteringPointId}");
            return ([meteringPointId], []);
        }

        var accounts = new[] { accountNumber! };
        _log($"Account: {accountNumber}");

        var extractionService = new TimeSeriesExtractionService(context, deliveryCategory, null, includeChildren);
        var meteringPointIds = await extractionService.GetAllMeteringPointsAsync(accounts);
        _log($"  {meteringPointIds.Length} metering points found");

        return (meteringPointIds, accounts);
    }

    private async Task<List<MeteringPointTimeSeriesDto>> ExtractTimeSeriesAsync(
        XellentDbContext context,
        string[] accounts,
        string[] meteringPointIds,
        string deliveryCategory,
        bool includeChildren,
        DateTime startDate,
        DateTime endDate,
        bool quiet = false)
    {
        if (!quiet)
            _log("Extracting time series data...");
        var dataExtractionService = new TimeSeriesExtractionService(
            context, deliveryCategory, meteringPointIds, includeChildren);

        var accountsForExtraction = accounts.Length > 0 ? accounts : ["_direct_"];
        var timeSeries = await dataExtractionService.ExtractTimeSeriesAsync(accountsForExtraction, startDate, endDate);

        var parentCount = timeSeries.Count(ts => ts.ParentMeteringPointId == null);
        var childCount = timeSeries.Count(ts => ts.ParentMeteringPointId != null);
        if (!quiet)
            _log($"  {timeSeries.Count} metering points ({parentCount} parents + {childCount} children)");

        return timeSeries;
    }

    private static SettlTimeSeriesRequestDto BuildSegmentRequest(TimeSeriesSegmentDto segment, DateTimeOffset revision)
    {
        var fileDto = new TimeSeriesFileDto
        {
            TransactionId = Guid.NewGuid(),
            RegistrationTime = DateTimeOffset.UtcNow,
            Revision = revision,
            AccountForQuantityQuality = true,
            Points = segment.Points.Select(ts => new SettlTimeSeriesPointDto
            {
                Time = ts.TimeOfValue.UtcDateTime,
                Quantity = ts.Value,
                Quality = "Measured"
            }).ToList()
        };
        return fileDto.ToApiRequest();
    }

    private async Task<RevisionResolutionResult> ResolveRevisionAsync(
        Guid meteringPointId,
        CancellationToken cancellationToken,
        bool quiet = false)
    {
        var (response, error) = await GetDeliveryStatusesAsync(meteringPointId, cancellationToken);

        if (error != null)
            return RevisionResolutionResult.Failed($"GetDeliveryStatuses failed: {error}");
        if (response == null)
            return RevisionResolutionResult.Failed("GetDeliveryStatuses returned a null response.");
        if (response.DeliveryStatuses == null)
            return RevisionResolutionResult.Failed("GetDeliveryStatuses returned a null DeliveryStatuses list.");

        var active = response.DeliveryStatuses.Where(status => status.InDelivery).ToList();
        if (active.Count == 0)
            return await ResolveWithoutInDeliveryAsync(meteringPointId, response.DeliveryStatuses, cancellationToken, quiet);

        // The migration is the first delivery, as DataMigration decides it (MoveInDeliveryFilter.FirstDelivery):
        // the earliest in-delivery period, and it must have started as a change of supplier. A later move-in,
        // gap or return never replaces it, and the metering point's current delivery status does not matter.
        var starts = active
            .Where(s => s.Period is { } p && p.Start != default)
            .OrderBy(s => s.Period!.Start)
            .ToList();
        if (starts.Count == 0)
            return RevisionResolutionResult.Failed("No in-delivery status has a Period.Start.");
        var firstDay = ToDanishDate(starts[0].Period!.Start);
        var firstReasons = starts.Where(s => ToDanishDate(s.Period!.Start) == firstDay).Select(s => s.StartReason).Distinct().ToList();
        if (firstReasons.Count != 1)
            return RevisionResolutionResult.Ambiguous(active);
        switch (firstReasons[0])
        {
            case DeliveryStartReason.ChangeOfSupplier:
                break;
            case DeliveryStartReason.MoveIn:
                return RevisionResolutionResult.NotMigrated(
                    $"the first delivery in Settl ({firstDay:yyyy-MM-dd}) is a move-in, not a change of supplier", active);
            case DeliveryStartReason.Unknown:
                return RevisionResolutionResult.Unclear(
                    $"the first delivery in Settl ({firstDay:yyyy-MM-dd}) has start reason Unknown", active);
            default:
                return RevisionResolutionResult.NotMigrated(
                    $"the first delivery in Settl ({firstDay:yyyy-MM-dd}) does not start as a change of supplier (start reason {firstReasons[0]})", active);
        }

        var status = starts[0];
        var revision = status.Period!.Start;
        var inactive = response.DeliveryStatuses.Where(deliveryStatus => !deliveryStatus.InDelivery).ToList();
        if (inactive.Count == 0)
            return RevisionResolutionResult.Resolved(revision, null, active);

        var matchingInactive = inactive
            .Where(deliveryStatus =>
                deliveryStatus.Period != null &&
                deliveryStatus.Period.End != default &&
                deliveryStatus.Period.End == revision)
            .ToList();

        if (matchingInactive.Count == 0)
        {
            // No InDelivery=false period before the first delivery (only later ones, e.g. after a move-out):
            // Settl's history starts with the change of supplier, so there is no boundary, the same as
            // with no InDelivery=false period at all (production 2026-10-29: 12, e.g. 571313113163113848).
            if (inactive.All(s => s.Period is { } p && p.Start != default && p.Start >= revision))
                return RevisionResolutionResult.Resolved(revision, null, active);
            return RevisionResolutionResult.SafetySkipped(
                TimeSeriesSkipCategory.DeliveryBoundaryUnresolved,
                $"delivery boundary unresolved (no InDelivery=false period ends at revision {revision:O})",
                revision,
                active);
        }

        if (matchingInactive.Count > 1)
        {
            return RevisionResolutionResult.SafetySkipped(
                TimeSeriesSkipCategory.DeliveryBoundaryAmbiguous,
                $"ambiguous delivery boundary ({matchingInactive.Count} InDelivery=false periods end at revision {revision:O})",
                revision,
                active);
        }

        var boundaryPeriod = matchingInactive[0].Period!;
        if (boundaryPeriod.Start == default)
        {
            return RevisionResolutionResult.SafetySkipped(
                TimeSeriesSkipCategory.DeliveryBoundaryInvalid,
                "invalid delivery boundary period (Period.Start is default)",
                revision,
                active);
        }

        if (boundaryPeriod.Start >= boundaryPeriod.End)
        {
            return RevisionResolutionResult.SafetySkipped(
                TimeSeriesSkipCategory.DeliveryBoundaryInvalid,
                $"invalid delivery boundary period (start {boundaryPeriod.Start:O} is not before end {boundaryPeriod.End:O})",
                revision,
                active);
        }

        return RevisionResolutionResult.Resolved(revision, boundaryPeriod.Start, active);
    }

    // Null when the account's relationship was closed as a migration on `dayBeforeCos`: a contract of the
    // account that runs on that day ends on it, and the agreement behind that contract has status
    // "Leverance ophørt" and ends on it too. Otherwise a message saying which part does not fit.
    internal static string? CheckMigrationClose(
        IEnumerable<TimeSeriesExtractionService.AccountMeteringPoint> contracts, string account, DateOnly dayBeforeCos)
    {
        var running = contracts.Where(c => c.Account == account && c.Covers(dayBeforeCos)).ToList();
        var endingThatDay = running.Where(c => c.End == dayBeforeCos.AddDays(1)).ToList();
        if (endingThatDay.Count == 0)
        {
            var ends = string.Join(", ", running.Select(c => c.End is { } e ? e.AddDays(-1).ToString("yyyy-MM-dd") : "open").Distinct());
            return $"contract does not end on {dayBeforeCos:yyyy-MM-dd}, the day before delivery starts in Settl (ends {ends})";
        }

        if (endingThatDay.Any(c => c.AgreementStatus == TimeSeriesExtractionService.AccountMeteringPoint.DeliveryCeased
                                   && c.AgreementLastDay == dayBeforeCos))
            return null;

        var agreements = string.Join(", ", endingThatDay
            .Select(c => $"{c.AgreementNum} status {c.AgreementStatus}, ends {(c.AgreementLastDay is { } d ? d.ToString("yyyy-MM-dd") : "open")}")
            .Distinct());
        return $"agreement is not \"Leverance ophørt\" (status {TimeSeriesExtractionService.AccountMeteringPoint.DeliveryCeased}) " +
               $"ending {dayBeforeCos:yyyy-MM-dd} ({agreements})";
    }

    // The first day of the account's unbroken hold on the metering point that runs through `day`: the
    // start of its contract covering that day, followed back through its own contracts that end the
    // day before (a company switch like meh → for under one customer is not a new occupant).
    internal static DateOnly? AccountHeldSince(
        IEnumerable<TimeSeriesExtractionService.AccountMeteringPoint> contracts, string account, DateOnly day)
    {
        var own = contracts.Where(c => c.Account == account).ToList();
        var current = own.Where(c => c.Covers(day)).OrderBy(c => c.Start).FirstOrDefault();
        if (current == null)
            return null;
        var since = current.Start;
        while (own.FirstOrDefault(c => c.End == since && c.Start < since) is { } previous)
            since = previous.Start;
        return since;
    }

    // Settl currently fails to mark delivery as InDelivery=true (seen 2026-09: the deliveryStatuses
    // list keeps one open InDelivery=false period). The metering point's current delivery status
    // still carries a start, whatever its reason (Na or ChangeOfSupplier), so that start is the
    // revision, and the InDelivery=false period running into it is the history to back-fill.
    private async Task<RevisionResolutionResult> ResolveWithoutInDeliveryAsync(
        Guid meteringPointId,
        IReadOnlyList<DeliveryStatusDto> statuses,
        CancellationToken cancellationToken,
        bool quiet = false)
    {
        const string noActive = "No delivery status has InDelivery=true.";
        var (current, error) = await GetCurrentMeteringPointAsync(meteringPointId, cancellationToken);
        if (error != null)
            return RevisionResolutionResult.Failed($"{noActive} Current delivery status lookup failed: {error}");
        if (current?.DeliveryStatus is not { Start: { } start })
            return RevisionResolutionResult.Failed($"{noActive} The metering point has no current delivery status start.");

        var revision = new DateTimeOffset(DateTime.SpecifyKind(start, start.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : start.Kind));
        var runningInto = statuses
            .Where(s => !s.InDelivery && s.Period is { } p && p.Start != default && p.Start < revision
                        && (p.End == default || p.End >= revision))
            .ToList();
        if (!quiet)
            _log($"REVISION FALLBACK MP {meteringPointId}: no InDelivery=true in Settl; using current delivery status start {revision:O}");

        var result = runningInto.Count switch
        {
            0 => RevisionResolutionResult.Resolved(revision, null, []),
            1 => RevisionResolutionResult.Resolved(revision, runningInto[0].Period!.Start, []),
            _ => RevisionResolutionResult.SafetySkipped(
                TimeSeriesSkipCategory.DeliveryBoundaryAmbiguous,
                $"ambiguous delivery boundary ({runningInto.Count} InDelivery=false periods run into current delivery status start {revision:O})",
                revision,
                [])
        };
        return result with { FromCurrentDeliveryStatus = true };
    }

    // Settl rejects a points summary longer than one year ("The requested period must not exceed
    // 1 year."), while the window can be three. Ask in pieces just under a year — each piece starts
    // on the previous one's last day, so no day is missed — and add them up: the caller only needs
    // to know whether any points exist, plus a first/last date for the log.
    private async Task<(SettlPointsSummaryDto? Summary, string? Error)> GetPointsSummaryInYearChunksAsync(
        Guid timeSeriesId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var total = new SettlPointsSummaryDto { Count = 0 };
        var chunkStart = from;
        while (chunkStart < to)
        {
            var chunkEnd = chunkStart.AddYears(1).AddDays(-1);
            if (chunkEnd > to)
                chunkEnd = to;
            var (summary, error) = await _settlTimeSeriesClient!.GetPointsSummaryAsync(timeSeriesId, chunkStart, chunkEnd, cancellationToken);
            if (error != null)
                return (null, error);
            if (summary is { Count: > 0 })
            {
                total.Count += summary.Count;
                if (summary.From is { } f && (total.From is null || f < total.From))
                    total.From = f;
                if (summary.To is { } t && (total.To is null || t > total.To))
                    total.To = t;
            }
            chunkStart = chunkEnd;
        }
        return (total, null);
    }

    private static DateOnly ToDanishDate(DateTimeOffset timestamp) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, DanishTimeZone).DateTime);

    // The EIP period for each segment, in segment order. A segment's base period is its agreement
    // period cut to the window (the whole window when it has no agreement period). Segments that
    // share a base period — e.g. an Hour and a Quarter series under the same company — split it:
    // each later one starts, and the one before it ends, on the Danish date of its first point.
    internal static List<(DateOnly Start, DateOnly End)> ComputeEipPeriods(
        List<TimeSeriesSegmentDto> segments, DateOnly windowStart, DateOnly windowEnd)
    {
        (DateOnly Start, DateOnly End) BasePeriod(TimeSeriesSegmentDto s) =>
            s.PeriodStart is { } start
                ? (start > windowStart ? start : windowStart,
                   s.PeriodEnd is { } end && end < windowEnd ? end : windowEnd)
                : (windowStart, windowEnd);

        var result = segments.Select(BasePeriod).ToList();
        foreach (var group in segments
                     .Select((segment, index) => (segment, index))
                     .GroupBy(x => (x.segment.CompanyId, x.segment.PeriodStart, x.segment.PeriodEnd)))
        {
            var ordered = group.OrderBy(x => FirstPoint(x.segment)).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (start, end) = result[ordered[i].index];
                if (i > 0 && ToDanishDate(FirstPoint(ordered[i].segment)) is var ownStart && ownStart > start)
                    start = ownStart;
                if (i + 1 < ordered.Count && ToDanishDate(FirstPoint(ordered[i + 1].segment)) is var nextStart && nextStart < end)
                    end = nextStart;
                result[ordered[i].index] = (start, end);
            }
        }
        return result;
    }

    // A segment's points — read, or only counted (a batch dry run).
    private static int PointCount(TimeSeriesSegmentDto segment) => segment.CountedPoints ?? segment.Points.Count;

    private static DateTimeOffset FirstPoint(TimeSeriesSegmentDto segment) =>
        segment.CountedPoints != null ? segment.FirstCountedPoint!.Value : segment.Points[0].TimeOfValue;

    // Counts instead of points for one metering point (see TimeSeriesExtractionService.CountTimeSeriesAsync).
    // With a snapshot the counts are kept per window and boundary, so a later offline dry run needs no Xellent.
    private async Task<MeteringPointTimeSeriesDto> CountTimeSeriesAsync(
        XellentDbContext context, string meteringPoint, string deliveryCategory, DateTime startDate, DateTime endDate,
        DateTimeOffset? deliveryBoundary)
    {
        Task<MeteringPointTimeSeriesDto> Count() =>
            new TimeSeriesExtractionService(context, deliveryCategory)
                .CountTimeSeriesAsync(meteringPoint, startDate, endDate, deliveryBoundary);
        if (_snapshot == null)
            return await Count();
        // Old counts lack dated reading resolution and conversion validation; never reuse them.
        var key = $"{meteringPoint}|{startDate:yyyy-MM-dd}|{endDate:yyyy-MM-dd}|{deliveryBoundary?.UtcDateTime.ToString("O") ?? "none"}" +
                  "|dated-quarter-conversion-v1";
        var (counted, error) = await _snapshot.GetOrFetchAsync<MeteringPointTimeSeriesDto>(
            SnapshotXellentPointCounts, key, async () => (await Count(), null));
        return error == null
            ? counted!
            : throw new InvalidOperationException(error);
    }

    /// <summary>The post's "no meter installed" rule, asked ahead of it: no values in the window (counted the way a
    /// batch dry run counts them) and Xellent status Oprettet.</summary>
    private async Task<bool> NotInstalledAsync(string meteringPoint, DateTime startDate, DateTime endDate,
        DateTimeOffset? deliveryBoundary)
    {
        await using var context = BuildXellentContext();
        if (await MeterPointStatusAsync(context, meteringPoint) != MeterPointStatusCreated)
            return false;
        var counted = await CountTimeSeriesAsync(context, meteringPoint, BatchDeliveryCategory, startDate, endDate, deliveryBoundary);
        return counted.Segments.Sum(PointCount) + (counted.CountedPointsBeforeBoundary ?? 0) == 0;
    }

    /// <summary>Xellent's own status for the metering point right now, cached in the snapshot like the
    /// point counts. Null when Xellent records none.</summary>
    private async Task<int?> MeterPointStatusAsync(XellentDbContext context, string meteringPoint)
    {
        Task<int?> Read() =>
            new TimeSeriesExtractionService(context, BatchDeliveryCategory).GetMeterPointStatusAsync(meteringPoint);
        if (_snapshot == null)
            return await Read();
        var (entry, error) = await _snapshot.GetOrFetchAsync<MeterPointStatusEntry>(
            SnapshotMeterPointStatus, meteringPoint, async () => (new MeterPointStatusEntry(await Read()), null));
        return error == null
            ? entry?.Status
            : throw new InvalidOperationException(error);
    }

    /// <summary>One snapshot line: the metering point's Xellent status.</summary>
    private sealed record MeterPointStatusEntry(int? Status);

    private void ResetSkipArtifacts()
    {
        // The seed's done check runs the batch loop too; it must not replace a post run's skip files.
        if (_markDoneMode)
            return;
        foreach (var fileName in AllSkipCsvFileNames)
        {
            var path = Path.Combine(_outputBaseDirectory, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        if (Directory.Exists(_outputBaseDirectory))
            foreach (var path in Directory.GetFiles(_outputBaseDirectory, "blocked-*.csv"))
                File.Delete(path);
    }

    /// <summary>
    /// Appends one skipped metering point to the CSV for its category, creating the file on first
    /// use. A file that never gets a row is never created, so the presence of a file already means
    /// "this happened at least once".
    /// </summary>
    // Goes at the end of a log line about one metering point, so the dashboard's SKIP parser still reads
    // the identification and the reason unchanged.
    private string TypeSuffix() => _meteringPointType.Length == 0 ? "" : $" ({_meteringPointType.ToLowerInvariant()})";

    private void RecordSkip(
        TimeSeriesSkipCategory category,
        string? customerNumber,
        string meteringPointIdentification,
        Guid? settlMeteringPointId,
        DateTimeOffset? revision,
        DateTimeOffset? deliveryBoundary,
        string detail)
    {
        if (category != TimeSeriesSkipCategory.KnownIssueXellentResolution)
            _lastSkip = category;
        if (_markDoneMode)
            return;
        Directory.CreateDirectory(_outputBaseDirectory);
        var path = Path.Combine(_outputBaseDirectory, SkipCsvFileNames[category]);
        AppendCsvRow(path, SkipCsvHeader,
        [
            customerNumber ?? "",
            meteringPointIdentification,
            _meteringPointType,
            settlMeteringPointId?.ToString() ?? "",
            revision?.ToString("O") ?? "",
            deliveryBoundary?.ToString("O") ?? "",
            detail
        ]);
    }

    private void RecordAmbiguousRevision(
        string? customerNumber,
        string meteringPointIdentification,
        Guid settlMeteringPointId,
        IReadOnlyList<DeliveryStatusDto> activeStatuses)
    {
        if (_markDoneMode)
            return;
        Directory.CreateDirectory(_outputBaseDirectory);
        var path = Path.Combine(_outputBaseDirectory, AmbiguousRevisionCsvFileName);
        var starts = string.Join(
            " | ",
            activeStatuses.Select(status =>
                status.Period == null
                    ? "MISSING_PERIOD"
                    : status.Period.Start == default
                        ? "DEFAULT_START"
                        : status.Period.Start.ToString("O")));

        AppendCsvRow(path, AmbiguousRevisionCsvHeader,
        [
            customerNumber ?? "",
            meteringPointIdentification,
            _meteringPointType,
            settlMeteringPointId.ToString(),
            activeStatuses.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            starts
        ]);
    }

    private static string EscapeCsv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private enum RevisionResolutionOutcome
    {
        Resolved,
        Ambiguous,
        SafetySkip,
        Error,
        /// <summary>Settl's first delivery is not a change of supplier: the metering point was not migrated.</summary>
        NotMigrated,
        /// <summary>Settl's first delivery has start reason Unknown: someone has to say what it was.</summary>
        Unclear
    }
    private sealed record RevisionResolutionResult(
        RevisionResolutionOutcome Outcome,
        DateTimeOffset Revision,
        DateTimeOffset? DeliveryBoundary,
        IReadOnlyList<DeliveryStatusDto> ActiveStatuses,
        string? Error,
        string? SkipReason,
        TimeSeriesSkipCategory? SkipCategory)
    {
        /// <summary>The revision is the start of the metering point's current delivery status.</summary>
        public bool FromCurrentDeliveryStatus { get; init; }

        public static RevisionResolutionResult Resolved(
            DateTimeOffset revision,
            DateTimeOffset? deliveryBoundary,
            IReadOnlyList<DeliveryStatusDto> activeStatuses) =>
            new(RevisionResolutionOutcome.Resolved, revision, deliveryBoundary, activeStatuses, null, null, null);

        public static RevisionResolutionResult Ambiguous(IReadOnlyList<DeliveryStatusDto> activeStatuses) =>
            new(RevisionResolutionOutcome.Ambiguous, default, null, activeStatuses, null, null, null);

        public static RevisionResolutionResult SafetySkipped(
            TimeSeriesSkipCategory category,
            string reason,
            DateTimeOffset revision,
            IReadOnlyList<DeliveryStatusDto> activeStatuses) =>
            new(RevisionResolutionOutcome.SafetySkip, revision, null, activeStatuses, null, reason, category);

        public static RevisionResolutionResult Failed(string error) =>
            new(RevisionResolutionOutcome.Error, default, null, [], error, null, null);

        public static RevisionResolutionResult NotMigrated(string reason, IReadOnlyList<DeliveryStatusDto> activeStatuses) =>
            new(RevisionResolutionOutcome.NotMigrated, default, null, activeStatuses, null, reason, null);

        public static RevisionResolutionResult Unclear(string reason, IReadOnlyList<DeliveryStatusDto> activeStatuses) =>
            new(RevisionResolutionOutcome.Unclear, default, null, activeStatuses, null, reason, null);
    }

    /// <summary>
    /// Mapper Xellent's TimeResolution-enum-navn til Settl's (Length, Unit). Settl bruger
    /// "Minutes" + length=15 for kvarter — ikke en separat "Quarter"-enum.
    /// </summary>
    private static (int Length, string Unit) ToSettlResolution(string xellentResolution) => xellentResolution switch
    {
        "Hour" => (1, "Hour"),
        "Quarter" => (15, "Minutes"),
        "Day" => (1, "Day"),
        "Month" => (1, "Month"),
        "Year" => (1, "Year"),
        "Minute" => (1, "Minutes"),
        _ => throw new InvalidOperationException(
            $"Unsupported Xellent TimeResolution '{xellentResolution}' — kan ikke mappes til Settl TimeResolutionUnit.")
    };

    private List<MeteringPointTimeSeriesDto> FillGaps(
        List<MeteringPointTimeSeriesDto> timeSeries,
        DateTime startDate,
        DateTime endDate)
    {
        var simulationService = new TimeSeriesSimulationService();
        var totalOriginalPoints = 0;
        var totalSimulatedPoints = 0;

        _log("Filling gaps in time series data...");

        for (int i = 0; i < timeSeries.Count; i++)
        {
            var ts = timeSeries[i];
            var originalCount = ts.Segments.Sum(s => s.Points.Count);
            totalOriginalPoints += originalCount;

            var filled = simulationService.FillGaps(ts, startDate, endDate);
            var filledCount = filled.Segments.Sum(s => s.Points.Count);
            var simulatedCount = filledCount - originalCount;
            totalSimulatedPoints += simulatedCount;

            timeSeries[i] = filled;

            if (simulatedCount > 0)
            {
                _log($"  {ts.MeteringPointId}: {originalCount} real + {simulatedCount} simulated = {filledCount} total");
            }
        }

        _log("");
        _log($"Gap filling complete:");
        _log($"  Original points: {totalOriginalPoints:N0}");
        _log($"  Simulated points: {totalSimulatedPoints:N0}");
        _log($"  Total points: {totalOriginalPoints + totalSimulatedPoints:N0}");
        _log("");

        return timeSeries;
    }

    private int Error(string message)
    {
        _log($"ERROR: {message}");
        return 1;
    }

    private static string GetModeDescription(string? meteringPointId, string? accountNumber)
    {
        if (meteringPointId != null)
            return $"Single Metering Point ({meteringPointId})";
        if (accountNumber != null)
            return $"Single Account ({accountNumber})";
        return "Unknown";
    }
}

/// <summary>A metering point a batch run takes from Settl.</summary>
/// <param name="Identification">18-digit metering point identification.</param>
/// <param name="Id">Settl GUID.</param>
/// <param name="InDelivery">The Settl list shows it in delivery (InDelivery=true, or a current or next
/// delivery period). False can still be a migrated metering point — Settl bug, 2026-09.</param>
/// <param name="DeliveryStart">Danish date the list's current or next delivery period starts; null when the
/// list has none (then only the metering point's own delivery status tells).</param>
/// <param name="ParentIdentification">The parent metering point's identification when this is a child
/// (Settl relation or the Xellent relation table); null for a parent. A child that carries no delivery
/// status or no Xellent contract of its own falls back to the parent's.</param>
/// <param name="ParentId">The parent's Settl GUID; null when the parent is not in Settl's list.</param>
/// <param name="ParentInDelivery">The parent's <paramref name="InDelivery"/>, used in place of the child's
/// own when the child falls back to the parent.</param>
public sealed record BatchMeteringPoint(
    string Identification, Guid Id, bool InDelivery, DateOnly? DeliveryStart,
    string? ParentIdentification = null, Guid? ParentId = null, bool ParentInDelivery = false);

public class ExtractOptions
{
    public string? MeteringPointId { get; set; }
    public string? AccountNumber { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? DeliveryCategory { get; set; }
    /// <summary>Not set by the CLI: children are never in scope. Kept because the dashboard still sets it (always false).</summary>
    public bool IncludeChildren { get; set; }
    public bool SimulateMissing { get; set; }
    /// <summary>Export paired original and prepared values for read-only dashboard inspection.</summary>
    public bool IncludeOutgoingPreview { get; set; }
    public DateTimeOffset? DeliveryBoundary { get; set; }
}

public class PostOptions
{
    public string? MeteringPointId { get; set; }
    public string? AccountNumber { get; set; }
    public string? CustomerNumber { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? DeliveryCategory { get; set; }
    /// <summary>Not set by the CLI: children are never in scope. Kept because the dashboard still sets it (always false).</summary>
    public bool IncludeChildren { get; set; }
    public string Environment { get; set; } = "staging";
    public bool DryRun { get; set; }

    /// <summary>Set by the batch run for each of its metering points: leave out the per-run setup lines
    /// and connection test the batch already did once.</summary>
    public bool InBatch { get; internal set; }

    /// <summary>After a live run that succeeded: how many points were posted.</summary>
    public int PointsPosted { get; internal set; }

    /// <summary>After a run that succeeded: nothing was posted because every metering point was skipped to a skip file.</summary>
    public bool Skipped { get; internal set; }

    /// <summary>After a run that succeeded: nothing was sent because the ledger shows every write already done.</summary>
    public bool AlreadyInLedger { get; internal set; }

    /// <summary>After a run that stopped on a metering point: why (the ERROR line's text).</summary>
    public string? Error { get; internal set; }

    /// <summary>
    /// When set, skip SearchByIdentificationAsync and use this Guid directly. Used by profile-mode
    /// where the Guid is already resolved via Settl customer lookup.
    /// </summary>
    public Guid? MeteringPointGuid { get; set; }

    /// <summary>
    /// The revision resolved from the single active Settl delivery status. When omitted, the runner
    /// resolves it from <see cref="MeteringPointGuid"/> before any mutation.
    /// </summary>
    public DateTimeOffset? ResolvedRevision { get; set; }

    /// <summary>
    /// Optional lower point boundary resolved from the single InDelivery=false period whose end
    /// matches <see cref="ResolvedRevision"/>. Null means the delivery-status history contains no
    /// previous out-of-delivery period.
    /// </summary>
    public DateTimeOffset? ResolvedDeliveryBoundary { get; set; }

    /// <summary>
    /// Clears a previous ambiguity CSV before this call. Keep true for standalone runs; callers
    /// that invoke RunPostAsync repeatedly as one logical run set it only on the first call.
    /// </summary>
    public bool ResetAmbiguityCsv { get; set; } = true;

    /// <summary>When true, a per-MP failure logs a warning and the run continues with the next MP
    /// instead of aborting. The run still returns a non-zero exit code if any MP failed.</summary>
    public bool ContinueOnError { get; set; }

    /// <summary>When true, metering points whose included outgoing segments span more than one
    /// TimeResolution (typically Hour → Quarter) are skipped before POST. Skipped MPs are logged
    /// but do not contribute to the failure count.</summary>
    public bool SkipResolutionChange { get; set; }

    /// <summary>When true, each metering point is pre-checked via Settl's points-summary endpoint
    /// before POST. If any of the matched Settl TS already has points in [StartDate, EndDate],
    /// the whole MP is skipped (no POST, no EIP) — atomic MPs are preserved. Skipped MPs are
    /// logged but do not contribute to the failure count.</summary>
    public bool SkipIfExistingPoints { get; set; }
}
