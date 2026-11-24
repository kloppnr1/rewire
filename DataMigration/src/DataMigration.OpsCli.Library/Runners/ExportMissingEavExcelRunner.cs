using System.Collections.Concurrent;
using System.Globalization;
using DataMigration.Core;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

/// <summary>A metering point from Settl's market list: what the export needs to pick it and its versions.</summary>
public sealed record MissingEavCandidate(string Identification, Guid Id, bool InDelivery, bool IsChild, DateTime? DeliveryStart);

/// <summary>A Settl MeterCharacteristics version: its volume (null when empty) and its meters.</summary>
public sealed record MeterVersion(double? EstimatedAnnualVolume, IReadOnlyList<MeterVersionMeter> Meters);

/// <summary>What export-missing-eav-excel reads from Settl's market API. A narrow seam over <see cref="ISettlMeteringPointClient"/>.</summary>
public interface IMissingEavSettlReader
{
    /// <summary>Every metering point in Settl's market list, page by page.</summary>
    Task<(List<MissingEavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken);

    /// <summary>One metering point by its 18-digit identification; null when Settl has none.</summary>
    Task<(MissingEavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken);

    /// <summary>Start of every MeterCharacteristics version (UTC).</summary>
    Task<(List<DateTime>? Starts, string? Error)> VersionStartsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The MeterCharacteristics version in force at <paramref name="at"/>.</summary>
    Task<(MeterVersion? Version, string? Error)> VersionAtAsync(Guid id, DateTime at, CancellationToken cancellationToken);

    /// <summary>The metering point's delivery statuses, for its first delivery start and its reason.</summary>
    Task<(IReadOnlyList<DeliveryStatusDto>? Statuses, string? Error)> DeliveryStatusesAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>
/// Which metering points were part of the migration: Xellent has an el-ekstern contract for it, in any Xellent
/// company, running on the day before Settl's first delivery start. "Running on", not "ending on": the 2026-08-31
/// batch restamped some contract ends (TimeSeriesRunner picks the migrated account the same way). The migration read
/// every company, so the FOR filter generate-state-from-settl applies to invoices does not belong here.
/// </summary>
public interface IMigrationOriginReader
{
    /// <summary>The identifications in <paramref name="cosByMeteringPoint"/> with a Xellent contract running on COS-1.</summary>
    Task<(IReadOnlySet<string>? Migrated, string? Error)> WithAccountAtCosMinusOneAsync(
        IReadOnlyDictionary<string, DateOnly> cosByMeteringPoint, CancellationToken cancellationToken);
}

/// <summary>
/// Reads Xellent (read-only): contract, part, agreement and delivery point in El-ekstern, by the contract header's
/// dates, as TimeSeriesExtractionService does. A Xellent end date is the last day; 1900-01-01 is open.
/// </summary>
public sealed class XellentMigrationOriginReader(string connectionString) : IMigrationOriginReader
{
    private const int ChunkSize = 1000;

    public async Task<(IReadOnlySet<string>? Migrated, string? Error)> WithAccountAtCosMinusOneAsync(
        IReadOnlyDictionary<string, DateOnly> cosByMeteringPoint, CancellationToken cancellationToken)
    {
        var migrated = new HashSet<string>(StringComparer.Ordinal);
        if (cosByMeteringPoint.Count == 0)
            return (migrated, null);
        try
        {
            var options = new DbContextOptionsBuilder<XellentDbContext>()
                .UseXellent(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
                .Options;
            await using var context = new XellentDbContext(options);
            var category = MigrationConstants.DeliveryCategoryElectricity;
            foreach (var chunk in cosByMeteringPoint.Keys.Distinct(StringComparer.Ordinal).Chunk(ChunkSize))
            {
                var rows = await (
                    from contract in context.ExuContractTables
                    join part in context.ExuContractPartTables
                        on new { contract.Contractnum, contract.Dataareaid } equals new { part.Contractnum, part.Dataareaid }
                    join agreement in context.ExuAgreementTables
                        on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                        equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    join delpoint in context.ExuDelpoints
                        on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                        equals new { delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                    where contract.Dataareaid == MigrationConstants.DataAreaId
                        && contract.Deliverycategory == category
                        && delpoint.Deliverycategory == category
                        && chunk.Contains(delpoint.Meteringpoint)
                    select new { delpoint.Meteringpoint, contract.Contractstartdate, contract.Contractenddate }
                ).Distinct().ToListAsync(cancellationToken);
                foreach (var row in rows)
                    if (RunsOn(row.Contractstartdate, row.Contractenddate, cosByMeteringPoint[row.Meteringpoint].AddDays(-1)))
                        migrated.Add(row.Meteringpoint);
            }
            return (migrated, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>The contract runs on <paramref name="day"/>: started on or before it, and its last day is on or
    /// after it or open (1900-01-01).</summary>
    internal static bool RunsOn(DateTime start, DateTime end, DateOnly day) =>
        DateOnly.FromDateTime(start) <= day
        && (end.Date == MigrationConstants.EndDateNull.Date || DateOnly.FromDateTime(end) >= day);
}

public sealed class SettlMissingEavReader(ISettlMeteringPointClient meteringPoints) : IMissingEavSettlReader
{
    public async Task<(List<MissingEavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var items = new List<MissingEavCandidate>();
        var total = 0;
        for (var skip = 0; ; skip += pageSize)
        {
            var (page, count, error) = await meteringPoints.FindMeteringPointsAsync(pageSize, skip, doCount: skip == 0, cancellationToken);
            if (error != null)
                return (null, $"metering point list failed at skip {skip}: {error}");
            if (skip == 0)
                total = count;
            items.AddRange(page.Select(ToCandidate));
            if (page.Count < pageSize || (total > 0 && skip + pageSize >= total))
                return (items, null);
        }
    }

    public async Task<(MissingEavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken)
    {
        var (mp, error) = await meteringPoints.SearchByIdentificationAsync(identification, cancellationToken);
        return (mp == null ? null : ToCandidate(mp), error);
    }

    public async Task<(List<DateTime>? Starts, string? Error)> VersionStartsAsync(Guid id, CancellationToken cancellationToken)
    {
        var (history, error) = await meteringPoints.GetMarketHistoryAsync(id, cancellationToken);
        return (history?.MeterCharacteristicsDates ?? (error == null ? [] : null), error);
    }

    public async Task<(MeterVersion? Version, string? Error)> VersionAtAsync(Guid id, DateTime at, CancellationToken cancellationToken)
    {
        var (mp, error) = await meteringPoints.GetByIdAsync(id, at, cancellationToken);
        if (mp == null)
            return (null, error ?? "metering point not returned");
        var characteristics = mp.MeterCharacteristics;
        var volume = characteristics?.EstimatedAnnualVolume is { IsEmpty: false } eav ? eav.Value : (double?)null;
        var meters = characteristics?.Meters
            .Select(m => new MeterVersionMeter(m.MeterReadingOccurrence ?? "", m.DigitsOfPrecision, m.Decimals, m.MultiplicationFactor, m.Ampere))
            .ToList() ?? [];
        return (new MeterVersion(volume, meters), null);
    }

    public async Task<(IReadOnlyList<DeliveryStatusDto>? Statuses, string? Error)> DeliveryStatusesAsync(
        Guid id, CancellationToken cancellationToken)
    {
        var (response, error) = await meteringPoints.GetDeliveryStatusesAsync(id, cancellationToken);
        return response == null ? (null, error ?? "delivery statuses not returned") : (response.DeliveryStatuses ?? [], null);
    }

    private static MissingEavCandidate ToCandidate(MeteringPointInfoDto mp) => new(
        mp.MeteringPointIdentification, mp.Id, mp.InDelivery == true,
        mp.ParentMeteringPoint?.Id is { } parent && parent != Guid.Empty,
        mp.CurrentOrNextDeliveryPeriod?.Start.UtcDateTime);
}

public class ExportMissingEavExcelRunOptions
{
    public string OutputPath { get; set; } = string.Empty;
    public double Volume { get; set; } = ExportMissingEavExcelRunner.DefaultVolume;
    /// <summary>When set, just these identifications instead of Settl's whole list (still only active parents).</summary>
    public string[] MeteringPoints { get; set; } = [];
    public int Parallel { get; set; } = 6;
    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>
/// export-missing-eav-excel: writes Settl's MeteringPointMeter import workbook (version 8) that gives every active
/// metering point that was not part of the migration an EstimatedAnnualVolume. Read-only towards Settl and Xellent;
/// the file is imported in Settl by hand.
/// <para>
/// Active: a parent metering point that Settl's market list shows in delivery. Part of the migration: Settl's first
/// delivery start (the first period with InDelivery=true, whatever reason Settl records on it) is day D, and Xellent
/// has an account for it with a contract ending on D-1, in any Xellent company (<see cref="IMigrationOriginReader"/>);
/// those are left out. The migration shows as the change to InDelivery=true; Settl may record its reason as
/// ChangeOfSupplier, Unknown or something else. Without a Xellent account at D-1 the point is not part of the
/// migration. A point with no InDelivery=true start is left out, by hand. Every decision is written to
/// <c>&lt;workbook&gt;-decisions.csv</c>. Versions in scope: the
/// MeterCharacteristics version in force at the start of the current (or next) delivery period, and every later one;
/// with no delivery period, the version in force today and every later one. Each version in scope without an
/// EstimatedAnnualVolume gets an Update row with its own date, so a metering point can get several rows.
/// </para>
/// <para>
/// Each row repeats the version's MeterReadingOccurrence, series and meter values
/// (<see cref="MeteringPointMeterExcelWriter"/>). An empty cell would be ignored, not blanked; the full row keeps the
/// workbook a complete statement of the version. A version whose meter cannot be carried (none, several, or no
/// Hourly or QuarterHourly reading) leaves the whole metering point out, by hand.
/// </para>
/// </summary>
public sealed class ExportMissingEavExcelRunner(IMissingEavSettlReader settl, IMigrationOriginReader origin, Action<string> log)
{
    internal const string Migrated = "part of the migration, left out";
    public const double DefaultVolume = 2000;

    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    private sealed record Outcome(string MeteringPoint, string Result, IReadOnlyList<MeteringPointMeterRow>? Rows = null)
    {
        public IReadOnlyList<MeteringPointMeterRow> Rows { get; } = Rows ?? [];
    }

    private const string Import = "Import";

    public async Task<int> RunAsync(ExportMissingEavExcelRunOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            log("ERROR: --output is required");
            return 1;
        }
        if (options.Volume <= 0)
        {
            log("ERROR: --value must be above 0");
            return 1;
        }
        log($"=== export-missing-eav-excel: {options.Volume.ToString(CultureInfo.InvariantCulture)} kWh where Settl has none ===");

        var (candidates, listError) = await LoadCandidatesAsync(options.MeteringPoints, cancellationToken);
        if (listError != null || candidates == null)
        {
            log($"ERROR: {listError}");
            return 1;
        }
        var children = candidates.Count(c => c.IsChild);
        var notInDelivery = candidates.Count(c => !c.IsChild && !c.InDelivery);
        var active = candidates.Where(c => !c.IsChild && c.InDelivery)
            .GroupBy(c => c.Identification, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(c => c.Identification, StringComparer.Ordinal).ToList();
        log($"  {candidates.Count} metering point(s) read; {active.Count} active parent(s), " +
            $"{children} child(ren) and {notInDelivery} not in delivery left out");

        // Part of the migration or not: Settl's first InDelivery=true start (any reason), then Xellent at that date minus one.
        var settled = new ConcurrentDictionary<string, Outcome>(StringComparer.Ordinal);
        var cosByMp = new ConcurrentDictionary<string, DateOnly>(StringComparer.Ordinal);
        var firstStarts = new ConcurrentDictionary<string, (DateOnly? Date, string Reason)>(StringComparer.Ordinal);
        using (var startGate = new SemaphoreSlim(Math.Max(1, options.Parallel)))
            await Task.WhenAll(active.Select(async candidate =>
            {
                await startGate.WaitAsync(cancellationToken);
                try
                {
                    var (statuses, error) = await settl.DeliveryStatusesAsync(candidate.Id, cancellationToken);
                    if (error != null || statuses == null)
                    {
                        settled[candidate.Identification] = new Outcome(candidate.Identification, $"ERROR: delivery statuses failed: {error}");
                        return;
                    }
                    var (first, disposition) = MoveInDeliveryFilter.FirstDelivery(statuses);
                    firstStarts[candidate.Identification] = (first, disposition);
                    if (first is { } start)
                        cosByMp[candidate.Identification] = start;
                    else
                        settled[candidate.Identification] = new Outcome(candidate.Identification,
                            "left out: Settl has no InDelivery=true start, by hand");
                }
                finally
                {
                    startGate.Release();
                }
            }));
        var startFailures = settled.Values.Where(o => o.Result.StartsWith("ERROR", StringComparison.Ordinal))
            .OrderBy(o => o.MeteringPoint, StringComparer.Ordinal).ToList();
        if (startFailures.Count > 0)
        {
            foreach (var o in startFailures)
                log($"  MP {o.MeteringPoint}: {o.Result}");
            log($"ERROR: {startFailures.Count} metering point(s) could not be read from Settl; no file written.");
            return 1;
        }
        log($"  Checking Xellent for an account the day before the first delivery start for {cosByMp.Count} metering point(s)...");
        var (migrated, originError) = await origin.WithAccountAtCosMinusOneAsync(
            new Dictionary<string, DateOnly>(cosByMp, StringComparer.Ordinal), cancellationToken);
        if (originError != null || migrated == null)
        {
            log($"ERROR: Xellent read failed: {originError}; no file written.");
            return 1;
        }
        foreach (var mp in migrated)
            settled[mp] = new Outcome(mp, Migrated);
        log($"  {migrated.Count} part of the migration, {settled.Count(s => s.Value.Result.StartsWith("left out", StringComparison.Ordinal))} " +
            $"left out by hand, {active.Count - settled.Count} not part of the migration");

        var results = new Outcome[active.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
        var done = 0;
        await Task.WhenAll(active.Select(async (candidate, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[i] = settled.TryGetValue(candidate.Identification, out var outcome)
                    ? outcome
                    : await DecideAsync(candidate, options.Volume, options.Today, cancellationToken);
                if (Interlocked.Increment(ref done) % 250 == 0)
                    log($"  {done}/{active.Count} metering points checked");
            }
            finally
            {
                gate.Release();
            }
        }));

        foreach (var o in results.Where(o => o.Result.StartsWith("ERROR", StringComparison.Ordinal) || o.Result.StartsWith("left out", StringComparison.Ordinal)))
            log($"  MP {o.MeteringPoint}: {o.Result}");
        var failed = results.Count(o => o.Result.StartsWith("ERROR", StringComparison.Ordinal));
        if (failed > 0)
        {
            log($"ERROR: {failed} metering point(s) could not be read from Settl; no file written.");
            return 1;
        }

        var imports = results.Where(o => o.Result == Import).ToList();
        var rows = imports.SelectMany(o => o.Rows).ToList();
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        MeteringPointMeterExcelWriter.Write(options.OutputPath, rows);
        var decisionsPath = DecisionsPath(options.OutputPath);
        await File.WriteAllTextAsync(decisionsPath, BuildDecisions(results, firstStarts, migrated),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);

        log("");
        foreach (var g in results.GroupBy(o => o.Result).OrderByDescending(g => g.Count()))
            log($"  {g.Count(),5}  {g.Key}");
        foreach (var g in imports.GroupBy(o => o.Rows.Count).OrderBy(g => g.Key))
            log($"  {g.Count(),5}  metering point(s) with {g.Key} row(s)");
        log($"MeteringPointMeter: {rows.Count} row(s) for {imports.Count} metering point(s) → {Path.GetFullPath(options.OutputPath)}");
        log($"Why each metering point is in or out: {Path.GetFullPath(decisionsPath)}");
        return 0;
    }

    /// <summary><c>&lt;workbook&gt;-decisions.csv</c> next to the workbook.</summary>
    internal static string DecisionsPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? "",
            $"{Path.GetFileNameWithoutExtension(outputPath)}-decisions.csv");

    /// <summary>
    /// One line per active metering point: its first delivery start in Settl and the reason Settl records on it,
    /// whether Xellent has an account for it the day before, and what the export did. Ids, dates and
    /// codes only. Semicolons, BOM, for Excel.
    /// </summary>
    private static string BuildDecisions(IEnumerable<Outcome> results,
        IReadOnlyDictionary<string, (DateOnly? Date, string Reason)> firstStarts, IReadOnlySet<string> migrated)
    {
        var csv = new System.Text.StringBuilder("MeteringPoint;FirstDeliveryStart;FirstStartReason;XellentAccountAtCosMinusOne;Result\n");
        foreach (var o in results.OrderBy(o => o.MeteringPoint, StringComparer.Ordinal))
        {
            var (date, reason) = firstStarts.TryGetValue(o.MeteringPoint, out var f) ? f : (null, "");
            var xellent = date == null ? "" : migrated.Contains(o.MeteringPoint) ? "yes" : "no";
            var result = o.Result == Import ? $"row(s) written: {o.Rows.Count}" : o.Result;
            csv.Append(o.MeteringPoint).Append(';').Append(date?.ToString("yyyy-MM-dd")).Append(';').Append(reason)
                .Append(';').Append(xellent).Append(';').Append(result.Replace(';', ',')).Append('\n');
        }
        return csv.ToString();
    }

    private async Task<(List<MissingEavCandidate>? Items, string? Error)> LoadCandidatesAsync(
        string[] only, CancellationToken cancellationToken)
    {
        if (only.Length == 0)
            return await settl.ListAsync(cancellationToken);
        var items = new List<MissingEavCandidate>();
        foreach (var identification in only.Distinct(StringComparer.Ordinal))
        {
            var (item, error) = await settl.GetAsync(identification, cancellationToken);
            if (error != null)
                return (null, $"lookup of {identification} failed: {error}");
            if (item == null)
                log($"  MP {identification}: left out: not found in Settl");
            else
                items.Add(item);
        }
        return (items, null);
    }

    private async Task<Outcome> DecideAsync(MissingEavCandidate candidate, double volume, DateOnly today, CancellationToken cancellationToken)
    {
        var mp = candidate.Identification;
        var (starts, historyError) = await settl.VersionStartsAsync(candidate.Id, cancellationToken);
        if (historyError != null || starts == null)
            return new Outcome(mp, $"ERROR: history failed: {historyError}");
        var ordered = starts.Select(ToUtc).Distinct().Order().ToList();
        if (ordered.Count == 0)
            return new Outcome(mp, "left out: no meter entry in Settl, by hand");

        // The version in force at the scope start, and every later one.
        var scopeStart = candidate.DeliveryStart is { } start ? ToUtc(start) : DanishMidnightUtc(today);
        var first = ordered.FindLastIndex(s => s <= scopeStart);

        var rows = new List<MeteringPointMeterRow>();
        foreach (var versionStart in ordered.Skip(Math.Max(0, first)))
        {
            // Read each version an hour into it, clear of the boundary.
            var (version, error) = await settl.VersionAtAsync(candidate.Id, versionStart.AddHours(1), cancellationToken);
            var date = ToDanishDate(versionStart);
            if (error != null || version == null)
                return new Outcome(mp, $"ERROR: meter entry at {date:yyyy-MM-dd} failed: {error}");
            if (version.EstimatedAnnualVolume != null)
                continue;
            if (version.Meters is not [var meter])
                return new Outcome(mp, $"left out: meter entry at {date:yyyy-MM-dd} has {version.Meters.Count} meters, by hand");
            if (MeteringPointMeterExcelWriter.SeriesSuffix(meter.Occurrence) == null)
                return new Outcome(mp, $"left out: meter entry at {date:yyyy-MM-dd} has no Hourly or QuarterHourly reading, by hand");
            rows.Add(new MeteringPointMeterRow(mp, date, volume, meter));
        }
        return rows.Count == 0
            ? new Outcome(mp, "already has an EstimatedAnnualVolume")
            : new Outcome(mp, Import, rows);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime DanishMidnightUtc(DateOnly date) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), DanishTimeZone);

    private static DateOnly ToDanishDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, DanishTimeZone));
}
