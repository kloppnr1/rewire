using System.Collections.Concurrent;
using System.Globalization;
using DataMigration.Core;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
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
/// Which metering points were part of the migration, by the rule generate-state-from-settl uses: Xellent has an
/// el-ekstern FOR (or blank-company) delpoint for it with a contract ending on its change-of-supplier date minus one.
/// </summary>
public interface IMigrationOriginReader
{
    /// <summary>The identifications in <paramref name="cosByMeteringPoint"/> that Xellent has an account for at COS-1.</summary>
    Task<(IReadOnlySet<string>? Migrated, string? Error)> WithAccountAtCosMinusOneAsync(
        IReadOnlyDictionary<string, DateOnly> cosByMeteringPoint, CancellationToken cancellationToken);
}

/// <summary>Reads Xellent (read-only) through <see cref="MeteringPointAccountResolver"/>, as generate-state-from-settl does.</summary>
public sealed class XellentMigrationOriginReader(string connectionString) : IMigrationOriginReader
{
    public async Task<(IReadOnlySet<string>? Migrated, string? Error)> WithAccountAtCosMinusOneAsync(
        IReadOnlyDictionary<string, DateOnly> cosByMeteringPoint, CancellationToken cancellationToken)
    {
        if (cosByMeteringPoint.Count == 0)
            return (new HashSet<string>(StringComparer.Ordinal), null);
        try
        {
            var resolution = await MeteringPointAccountResolver.ResolveAsync(connectionString, cosByMeteringPoint,
                MigrationConstants.DataAreaId, ct: cancellationToken, companyId: ForInvoiceScope.CompanyId,
                includeBlankCompany: true);
            return (resolution.Pairs.Select(p => p.MpId).ToHashSet(StringComparer.Ordinal), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }
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
/// Active: a parent metering point that Settl's market list shows in delivery. Part of the migration, by the rule
/// generate-state-from-settl uses: Settl's first delivery start is a ChangeOfSupplier and Xellent has an account for
/// it at that date minus one (<see cref="IMigrationOriginReader"/>); those are left out. A first start by MoveIn, or a
/// ChangeOfSupplier without a Xellent account at COS-1, is not part of the migration. Any other first start (unknown,
/// conflicting, none) is left out, by hand. Versions in scope: the
/// MeterCharacteristics version in force at the start of the current (or next) delivery period, and every later one;
/// with no delivery period, the version in force today and every later one. Each version in scope without an
/// EstimatedAnnualVolume gets an Update row with its own date, so a metering point can get several rows.
/// </para>
/// <para>
/// An Update row replaces the version, so each row repeats the version's MeterReadingOccurrence, series and meter
/// values (<see cref="MeteringPointMeterExcelWriter"/>). A version whose meter cannot be carried (none, several, or no
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

        // Part of the migration or not: Settl's first delivery start, then Xellent at COS-1 for the ChangeOfSupplier ones.
        var settled = new ConcurrentDictionary<string, Outcome>(StringComparer.Ordinal);
        var cosByMp = new ConcurrentDictionary<string, DateOnly>(StringComparer.Ordinal);
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
                    if (disposition == "ChangeOfSupplier")
                        cosByMp[candidate.Identification] = first!.Value;
                    else if (disposition != "MoveIn")
                        settled[candidate.Identification] = new Outcome(candidate.Identification,
                            $"left out: first delivery start is {disposition}, by hand");
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
        log($"  Checking Xellent for an account at COS-1 for {cosByMp.Count} metering point(s) whose first delivery start is a ChangeOfSupplier...");
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

        log("");
        foreach (var g in results.GroupBy(o => o.Result).OrderByDescending(g => g.Count()))
            log($"  {g.Count(),5}  {g.Key}");
        foreach (var g in imports.GroupBy(o => o.Rows.Count).OrderBy(g => g.Key))
            log($"  {g.Count(),5}  metering point(s) with {g.Key} row(s)");
        log($"MeteringPointMeter: {rows.Count} row(s) for {imports.Count} metering point(s) → {Path.GetFullPath(options.OutputPath)}");
        return 0;
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
