using System.Globalization;
using DataMigration.OpsCli.Services;
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
    /// <summary>
    /// Identifications that were part of the migration (the migration model's metering points). They are left out:
    /// the command is for metering points that came into Settl some other way.
    /// </summary>
    public IReadOnlySet<string> Migrated { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    public int Parallel { get; set; } = 6;
    public DateOnly Today { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

/// <summary>
/// export-missing-eav-excel: writes Settl's MeteringPointMeter import workbook (version 8) that gives every active
/// metering point that was not part of the migration an EstimatedAnnualVolume. Read-only towards Settl; the file is
/// imported in Settl by hand.
/// <para>
/// Active: a parent metering point that Settl's market list shows in delivery. Not part of the migration: absent from
/// the migration model (<see cref="ExportMissingEavExcelRunOptions.Migrated"/>). Versions in scope: the
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
public sealed class ExportMissingEavExcelRunner(IMissingEavSettlReader settl, Action<string> log)
{
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
        var activeAll = candidates.Where(c => !c.IsChild && c.InDelivery)
            .GroupBy(c => c.Identification, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(c => c.Identification, StringComparer.Ordinal).ToList();
        var active = activeAll.Where(c => !options.Migrated.Contains(c.Identification)).ToList();
        log($"  {candidates.Count} metering point(s) read; {active.Count} active parent(s) not migrated, " +
            $"{activeAll.Count - active.Count} migrated, {children} child(ren) and {notInDelivery} not in delivery left out");

        var results = new Outcome[active.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, options.Parallel));
        var done = 0;
        await Task.WhenAll(active.Select(async (candidate, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[i] = await DecideAsync(candidate, options.Volume, options.Today, cancellationToken);
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
