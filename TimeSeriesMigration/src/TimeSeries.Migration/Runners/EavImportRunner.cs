using System.Globalization;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace TimeSeries.Cli.Runners;

/// <summary>A metering point from Settl's market list: what eav-import needs to pick it and its versions.</summary>
public sealed record EavCandidate(string Identification, Guid Id, bool InDelivery, bool IsChild, DateTime? DeliveryStart);

/// <summary>
/// What eav-import reads from Settl's market API. The version reads are switch-import's; the list adds the
/// metering points themselves.
/// </summary>
public interface IEavImportSettlReader : ISwitchImportSettlReader
{
    /// <summary>Every metering point in Settl's market list, page by page.</summary>
    Task<(List<EavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken);

    /// <summary>One metering point by its 18-digit identification; null when Settl has none.</summary>
    Task<(EavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken);
}

public sealed class SettlEavImportReader(ISettlMeteringPointClient meteringPoints)
    : IEavImportSettlReader
{
    private readonly SettlSwitchImportReader _versions = new(meteringPoints);

    public Task<(Guid? Id, string? Error)> FindAsync(string identification, CancellationToken cancellationToken) =>
        _versions.FindAsync(identification, cancellationToken);

    public Task<(List<DateTime>? Starts, string? Error)> MeterCharacteristicsStartsAsync(Guid id, CancellationToken cancellationToken) =>
        _versions.MeterCharacteristicsStartsAsync(id, cancellationToken);

    public Task<(MeterEntry? Entry, string? Error)> MeterEntryAtAsync(Guid id, DateTime at, CancellationToken cancellationToken) =>
        _versions.MeterEntryAtAsync(id, at, cancellationToken);

    public async Task<(List<EavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var items = new List<EavCandidate>();
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

    public async Task<(EavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken)
    {
        var (mp, error) = await meteringPoints.SearchByIdentificationAsync(identification, cancellationToken);
        return (mp == null ? null : ToCandidate(mp), error);
    }

    private static EavCandidate ToCandidate(MeteringPointInfoDto mp) => new(
        mp.MeteringPointIdentification, mp.Id, mp.InDelivery == true,
        mp.ParentMeteringPoint?.Id is { } parent && parent != Guid.Empty,
        mp.CurrentOrNextDeliveryPeriod?.Start.UtcDateTime);
}

/// <summary>
/// eav-import: fills Settl's MeteringPointMeters_V8 import workbook so every active metering point has an
/// EstimatedAnnualVolume. Read-only towards Settl; it writes only the file, which is imported in Settl by hand.
/// <para>
/// Active: a parent metering point Settl's market list shows in delivery. Its versions in scope: the
/// MeterCharacteristics version in force at the start of its current (or next) delivery period, and every later one;
/// with no delivery period, the version in force today and every later one. Each version in scope without an
/// EstimatedAnnualVolume gets an Update row with its own date, so a metering point can get several rows.
/// </para>
/// <para>
/// An Update row replaces the version: empty columns leave it without those values (switch-import, production,
/// Systemate, 2026-11-11). So each row carries the version's MeterReadingOccurrence and series and its meter's
/// DigitsOfPrecision, Decimals, MultiplicationFactor and Ampere, plus the new volume. A version whose meters cannot be
/// carried (none, several, or no single Hourly or QuarterHourly reading) leaves the whole metering point out, by hand.
/// </para>
/// </summary>
public sealed class EavImportRunner(IEavImportSettlReader settl, Action<string> log)
{
    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    public const string FileSuffix = "_eav";
    public const double DefaultVolume = 2000;

    internal sealed record Outcome(string MeteringPoint, string Result, IReadOnlyList<SwitchImportRunner.ImportRow>? Rows = null)
    {
        public IReadOnlyList<SwitchImportRunner.ImportRow> Rows { get; } = Rows ?? [];
    }

    /// <param name="only">When set, just these identifications instead of Settl's whole list (still only active parents).</param>
    public async Task<int> RunAsync(string environment, string outputDirectory, double volume, int parallel,
        DateOnly today, IReadOnlyCollection<string>? only = null, CancellationToken cancellationToken = default)
    {
        if (volume <= 0)
        {
            log("ERROR: --value must be above 0");
            return 1;
        }
        log($"EstimatedAnnualVolume import ({environment}): {volume.ToString(CultureInfo.InvariantCulture)} kWh where Settl has none");

        var (candidates, listError) = await LoadCandidatesAsync(only, cancellationToken);
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

        var results = new Outcome[active.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var done = 0;
        await Task.WhenAll(active.Select(async (candidate, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[i] = await DecideAsync(candidate, volume, today, cancellationToken);
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

        var imports = results.Where(o => o.Result == "Import").ToList();
        var rows = imports.SelectMany(o => o.Rows).ToList();
        Directory.CreateDirectory(outputDirectory);
        var stamp = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Path.Combine(outputDirectory, $"{SwitchImportRunner.MetersFilePrefix}_{environment}_{stamp}{FileSuffix}.xlsx");
        SwitchImportRunner.WriteMeters(path, rows);

        log("");
        foreach (var g in results.GroupBy(o => o.Result).OrderByDescending(g => g.Count()))
            log($"  {g.Count(),5}  {g.Key}");
        foreach (var g in imports.GroupBy(o => o.Rows.Count).OrderBy(g => g.Key))
            log($"  {g.Count(),5}  metering point(s) with {g.Key} row(s)");
        log($"MeteringPointMeters: {rows.Count} row(s) for {imports.Count} metering point(s) → {Path.GetFullPath(path)}");
        return 0;
    }

    private async Task<(List<EavCandidate>? Items, string? Error)> LoadCandidatesAsync(
        IReadOnlyCollection<string>? only, CancellationToken cancellationToken)
    {
        if (only == null)
            return await settl.ListAsync(cancellationToken);
        var items = new List<EavCandidate>();
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

    private async Task<Outcome> DecideAsync(EavCandidate candidate, double volume, DateOnly today, CancellationToken cancellationToken)
    {
        var mp = candidate.Identification;
        var (starts, historyError) = await settl.MeterCharacteristicsStartsAsync(candidate.Id, cancellationToken);
        if (historyError != null || starts == null)
            return new Outcome(mp, $"ERROR: history failed: {historyError}");
        var ordered = starts.Select(ToUtc).Distinct().Order().ToList();
        if (ordered.Count == 0)
            return new Outcome(mp, "left out: no meter entry in Settl, by hand");

        // The version in force at the scope start, and every later one.
        var scopeStart = candidate.DeliveryStart is { } start ? ToUtc(start) : DanishMidnightUtc(today);
        var first = ordered.FindLastIndex(s => s <= scopeStart);
        var inScope = ordered.Skip(Math.Max(0, first)).ToList();

        var rows = new List<SwitchImportRunner.ImportRow>();
        foreach (var versionStart in inScope)
        {
            // Read each version an hour into it, clear of the boundary.
            var (entry, error) = await settl.MeterEntryAtAsync(candidate.Id, versionStart.AddHours(1), cancellationToken);
            var date = ToDanishDate(versionStart);
            if (error != null || entry == null)
                return new Outcome(mp, $"ERROR: meter entry at {date:yyyy-MM-dd} failed: {error}");
            if (entry.EstimatedAnnualVolume != null)
                continue;
            if (entry.Meters is not [var meter])
                return new Outcome(mp, $"left out: meter entry at {date:yyyy-MM-dd} has {entry.Meters.Count} meters, by hand");
            if (SwitchImportRunner.Resolution.FromSettl(meter.Occurrence) is not { } resolution)
                return new Outcome(mp, $"left out: meter entry at {date:yyyy-MM-dd} has no Hourly or QuarterHourly reading, by hand");
            rows.Add(new SwitchImportRunner.ImportRow(mp, date, resolution, entry with { EstimatedAnnualVolume = volume }));
        }
        return rows.Count == 0
            ? new Outcome(mp, "already has an EstimatedAnnualVolume")
            : new Outcome(mp, "Import", rows);
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
