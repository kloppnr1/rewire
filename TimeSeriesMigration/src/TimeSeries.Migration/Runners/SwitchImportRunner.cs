using System.Globalization;
using ClosedXML.Excel;
using DataMigration.SettlApi.Clients;

namespace TimeSeries.Cli.Runners;

/// <summary>
/// What switch-import reads from Settl's market API for one metering point. A narrow seam over
/// <see cref="ISettlMeteringPointClient"/>.
/// </summary>
public interface ISwitchImportSettlReader
{
    /// <summary>The market metering point id, null when Settl has none (or more than one) for the identification.</summary>
    Task<(Guid? Id, string? Error)> FindAsync(string identification, CancellationToken cancellationToken);

    /// <summary>Start of every MeterCharacteristics version, as Settl returns them (UTC).</summary>
    Task<(List<DateTime>? Starts, string? Error)> MeterCharacteristicsStartsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The MeterCharacteristics version in force at <paramref name="at"/>: its estimated annual volume and meters.</summary>
    Task<(MeterEntry? Entry, string? Error)> MeterEntryAtAsync(Guid id, DateTime at, CancellationToken cancellationToken);
}

/// <summary>What switch-import copies from a Settl MeterCharacteristics version into its import row.</summary>
public sealed record MeterEntry(double? EstimatedAnnualVolume, IReadOnlyList<MeterValues> Meters);

/// <summary>
/// One meter of a MeterCharacteristics version. MeterType is read for the backup and the by-hand list only: the import
/// template has no column for it.
/// </summary>
public sealed record MeterValues(string Occurrence, int DigitsOfPrecision, int Decimals, double? MultiplicationFactor, string? Ampere,
    string? MeterType = null);

public sealed class SettlSwitchImportReader(ISettlMeteringPointClient meteringPoints)
    : ISwitchImportSettlReader
{
    public async Task<(Guid? Id, string? Error)> FindAsync(string identification, CancellationToken cancellationToken)
    {
        var (mp, error) = await meteringPoints.SearchByIdentificationAsync(identification, cancellationToken);
        return (mp?.Id, error);
    }

    public async Task<(List<DateTime>? Starts, string? Error)> MeterCharacteristicsStartsAsync(Guid id, CancellationToken cancellationToken)
    {
        var (history, error) = await meteringPoints.GetMarketHistoryAsync(id, cancellationToken);
        return (history?.MeterCharacteristicsDates ?? (error == null ? [] : null), error);
    }

    public async Task<(MeterEntry? Entry, string? Error)> MeterEntryAtAsync(Guid id, DateTime at, CancellationToken cancellationToken)
    {
        var (mp, error) = await meteringPoints.GetByIdAsync(id, at, cancellationToken);
        if (mp == null)
            return (null, error ?? "metering point not returned");
        var characteristics = mp.MeterCharacteristics;
        var volume = characteristics?.EstimatedAnnualVolume is { IsEmpty: false } eav ? eav.Value : (double?)null;
        var meters = characteristics?.Meters
            .Select(m => new MeterValues(m.MeterReadingOccurrence ?? "", m.DigitsOfPrecision, m.Decimals, m.MultiplicationFactor, m.Ampere, m.MeterType))
            .ToList() ?? [];
        return (new MeterEntry(volume, meters), null);
    }
}

/// <summary>
/// switch-import: fills Settl's MeteringPointMeters_V8 import workbook for the metering points in post --all's
/// resolution-switches-&lt;env&gt;.csv, so Settl holds the resolution from before the switch until the
/// DataHub message's date (ChangeDate). Read-only towards Settl and Xellent; it writes only the file.
/// Two Update rows per metering point — the old MeterReadingOccurrence and series from Settl's earliest
/// MeterCharacteristics entry, the new ones from ChangeDate. OverwriteFuture stays False. Each row also carries what
/// Settl holds at its date (EstimatedAnnualVolume; DigitsOfPrecision, Decimals, MultiplicationFactor and Ampere of a
/// single meter): rows with those columns empty left the new history without them (production, Systemate,
/// 2026-11-11). MeterType is not a column of the template, so the import cannot carry it. Settl creates a series the
/// rows name that it lacks (<c>&lt;MP&gt;_PT1H</c>, like the <c>&lt;MP&gt;_PT15M</c> it has): featuretest
/// 571313115101027361, 2026-11-10.
/// Left out: a switch on or before Settl's earliest entry (Settl is right already), one whose earliest entry already
/// has the old value (imported before), and a metering point with more than one switch (by hand).
/// <para>
/// With <c>correct</c> it writes the correction for switches imported before the rows carried those values: for each
/// Settl version from the earliest up to the first one that still has an EstimatedAnnualVolume or an Ampere, an Update
/// row with that version's own date, MeterReadingOccurrence and series, and that first version's values. Every
/// version read is saved first to <c>&lt;workbook&gt;_backup.csv</c>, so what Settl held before the import is kept.
/// The template has no MeterType column, so the import cannot set or overwrite it; <c>&lt;workbook&gt;_metertype.csv</c>
/// lists each version that lacks one, with the MeterType to set by hand in Settl.
/// </para>
/// </summary>
public sealed class SwitchImportRunner(ISwitchImportSettlReader settl, Action<string> log)
{
    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    public const string MetersFilePrefix = "MeteringPointMeters_V8";
    public const string CorrectionSuffix = "_correction";
    public const string BackupSuffix = "_backup";
    public const string MeterTypeSuffix = "_metertype";

    /// <summary>A row of resolution-switches: the metering point, ChangeDate and the switched-to MeterReadingOccurrence.</summary>
    internal sealed record SwitchRow(string MeteringPoint, DateOnly ChangeDate, string NewOccurrence);

    /// <summary>One Update row of the workbook: its date, resolution, and the values it carries.</summary>
    internal sealed record ImportRow(string MeteringPoint, DateOnly Date, Resolution Resolution, MeterEntry Values);

    /// <summary>Per metering point: the rows that go in the file, or why none do, and what the output says about them.</summary>
    internal sealed record Outcome(string MeteringPoint, string Result, IReadOnlyList<ImportRow>? Rows = null,
        IReadOnlyList<string>? Notes = null, IReadOnlyList<Version>? Versions = null)
    {
        /// <summary>Every MeterCharacteristics version read (correction only), for the backup and the MeterType list.</summary>
        public IReadOnlyList<Version> Versions { get; } = Versions ?? [];
        public IReadOnlyList<ImportRow> Rows { get; } = Rows ?? [];
        public IReadOnlyList<string> Notes { get; } =
            [.. Notes ?? [], .. (Rows ?? []).Where(r => r.Values.Meters.Count != 1)
                .Select(r => $"{r.Values.Meters.Count} meters at {r.Date:yyyy-MM-dd}, meter columns left empty")];
    }

    /// <summary>A Settl MeterCharacteristics version: its Danish start date and what holds from it.</summary>
    internal sealed record Version(DateOnly Date, MeterEntry Entry);

    /// <summary>A version whose meter must get a MeterType by hand; MeterType null when no version has one.</summary>
    internal sealed record MeterTypeFix(string MeteringPoint, DateOnly Date, string? MeterType);

    /// <summary>Hour or Quarter, as the file names them.</summary>
    internal sealed record Resolution(string Occurrence, string SeriesSuffix)
    {
        public static readonly Resolution Hour = new("Hourly", "PT1H");
        public static readonly Resolution Quarter = new("QuarterHourly", "PT15M");
        public static Resolution FromDataHub(string value) => value == "PT1H" ? Hour : Quarter;
        public static Resolution? FromSettl(string value) =>
            value == Hour.Occurrence ? Hour : value == Quarter.Occurrence ? Quarter : null;
        public Resolution Other => this == Hour ? Quarter : Hour;
    }

    public async Task<int> RunAsync(string environment, string switchesCsv, string outputDirectory, int parallel,
        DateOnly today, CancellationToken cancellationToken = default, bool correct = false)
    {
        if (!File.Exists(switchesCsv))
        {
            log($"ERROR: {Path.GetFullPath(switchesCsv)} not found. Run post --all --env {environment} --dry-run first.");
            return 1;
        }
        var (rows, readError) = ReadSwitches(switchesCsv);
        if (readError != null)
        {
            log($"ERROR: {readError}");
            return 1;
        }
        log($"Switch import{(correct ? " correction" : "")} ({environment}): {rows.Count} switch(es) in {Path.GetFullPath(switchesCsv)}");

        var groups = rows.GroupBy(r => r.MeteringPoint, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var results = new Outcome[groups.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var done = 0;
        await Task.WhenAll(groups.Select(async (group, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[i] = correct
                    ? await DecideCorrectionAsync(group.ToList(), cancellationToken)
                    : await DecideAsync(group.ToList(), cancellationToken);
                if (Interlocked.Increment(ref done) % 250 == 0)
                    log($"  {done}/{groups.Count} metering points checked");
            }
            finally
            {
                gate.Release();
            }
        }));
        var outcomes = results;

        var failed = outcomes.Where(o => o.Result.StartsWith("ERROR", StringComparison.Ordinal)).ToList();
        foreach (var o in outcomes.Where(o => o.Result != "Import"))
            log($"  MP {o.MeteringPoint}: {o.Result}");
        foreach (var o in outcomes.Where(o => o.Result == "Import"))
            foreach (var note in o.Notes)
                log($"  MP {o.MeteringPoint}: {note}");
        if (failed.Count > 0)
        {
            log($"ERROR: {failed.Count} metering point(s) could not be read from Settl; no files written.");
            return 1;
        }

        var imports = outcomes.Where(o => o.Result == "Import").ToList();
        Directory.CreateDirectory(outputDirectory);
        var stamp = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var metersPath = Path.Combine(outputDirectory,
            $"{MetersFilePrefix}_{environment}_{stamp}{(correct ? CorrectionSuffix : "")}.xlsx");
        var importRows = imports.SelectMany(o => o.Rows).ToList();
        string? backupPath = null, meterTypePath = null;
        var meterTypeFixes = new List<MeterTypeFix>();
        if (correct)
        {
            // The backup goes first: it is what Settl holds before the workbook is imported.
            backupPath = Path.ChangeExtension(metersPath, null) + BackupSuffix + ".csv";
            WriteBackup(backupPath, outcomes);
            meterTypeFixes = outcomes.SelectMany(MeterTypeFixes).ToList();
            meterTypePath = Path.ChangeExtension(metersPath, null) + MeterTypeSuffix + ".csv";
            WriteMeterTypeFixes(meterTypePath, meterTypeFixes);
            foreach (var mp in meterTypeFixes.GroupBy(f => f.MeteringPoint))
                log(mp.First().MeterType is { } type
                    ? $"  MP {mp.Key}: set MeterType {type} by hand at {string.Join(", ", mp.Select(f => f.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}"
                    : $"  MP {mp.Key}: no Settl meter entry has a MeterType, set it by hand at {string.Join(", ", mp.Select(f => f.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}");
        }
        WriteMeters(metersPath, importRows);

        log("");
        foreach (var g in outcomes.GroupBy(o => o.Result).OrderByDescending(g => g.Count()))
            log($"  {g.Count(),5}  {g.Key}");
        if (backupPath != null)
            log($"Backup: {outcomes.Sum(o => o.Versions.Count)} meter entr(ies) of {outcomes.Count(o => o.Versions.Count > 0)} metering point(s) as Settl holds them now → {Path.GetFullPath(backupPath)}");
        log($"MeteringPointMeters: {importRows.Count} row(s) for {imports.Count} metering point(s) → {Path.GetFullPath(metersPath)}");
        if (meterTypePath != null)
            log($"MeterType by hand (not in the template; set it after the import): {meterTypeFixes.Count} meter entr(ies) of " +
                $"{meterTypeFixes.Select(f => f.MeteringPoint).Distinct().Count()} metering point(s) → {Path.GetFullPath(meterTypePath)}");
        return 0;
    }

    private async Task<Outcome> DecideAsync(List<SwitchRow> switches, CancellationToken cancellationToken)
    {
        var mp = switches[0].MeteringPoint;
        if (switches.Count > 1)
            return new Outcome(mp, "left out: more than one switch, by hand");
        var row = switches[0];
        var newResolution = Resolution.FromDataHub(row.NewOccurrence);
        var oldResolution = newResolution.Other;

        var (id, findError) = await settl.FindAsync(mp, cancellationToken);
        if (findError != null)
            return new Outcome(mp, $"ERROR: market lookup failed: {findError}");
        if (id is not { } marketId)
            return new Outcome(mp, "left out: not found in Settl");

        var (starts, historyError) = await settl.MeterCharacteristicsStartsAsync(marketId, cancellationToken);
        if (historyError != null || starts == null)
            return new Outcome(mp, $"ERROR: history failed: {historyError}");
        if (starts.Count == 0)
            return new Outcome(mp, "left out: no meter entry in Settl");
        var earliest = starts.Select(ToUtc).Min();
        var from = ToDanishDate(earliest);
        if (row.ChangeDate <= from)
            return new Outcome(mp, "left out: switch on or before Settl's earliest meter entry");

        // Read each version an hour into it, clear of the boundary.
        var (oldEntry, versionError) = await settl.MeterEntryAtAsync(marketId, earliest.AddHours(1), cancellationToken);
        if (versionError != null || oldEntry == null)
            return new Outcome(mp, $"ERROR: meter entry failed: {versionError}");
        if (oldEntry.Meters.Count > 0 && oldEntry.Meters.All(m => m.Occurrence == oldResolution.Occurrence))
            return new Outcome(mp, $"left out: Settl's earliest meter entry is already {oldResolution.Occurrence}");

        var (newEntry, changeError) = await settl.MeterEntryAtAsync(marketId, DanishMidnightUtc(row.ChangeDate).AddHours(1), cancellationToken);
        if (changeError != null || newEntry == null)
            return new Outcome(mp, $"ERROR: meter entry at {row.ChangeDate:yyyy-MM-dd} failed: {changeError}");

        return new Outcome(mp, "Import",
        [
            new ImportRow(mp, from, oldResolution, oldEntry),
            new ImportRow(mp, row.ChangeDate, newResolution, newEntry),
        ]);
    }

    /// <summary>
    /// The correction for a switch already imported with empty value columns. Every Settl version is read; the first
    /// that has an EstimatedAnnualVolume or an Ampere is the source, and each earlier version missing either gets a row
    /// with its own date and resolution and the source's values.
    /// </summary>
    private async Task<Outcome> DecideCorrectionAsync(List<SwitchRow> switches, CancellationToken cancellationToken)
    {
        var mp = switches[0].MeteringPoint;
        if (switches.Count > 1)
            return new Outcome(mp, "left out: more than one switch, by hand");
        var row = switches[0];
        var oldResolution = Resolution.FromDataHub(row.NewOccurrence).Other;

        var (id, findError) = await settl.FindAsync(mp, cancellationToken);
        if (findError != null)
            return new Outcome(mp, $"ERROR: market lookup failed: {findError}");
        if (id is not { } marketId)
            return new Outcome(mp, "left out: not found in Settl");

        var (starts, historyError) = await settl.MeterCharacteristicsStartsAsync(marketId, cancellationToken);
        if (historyError != null || starts == null)
            return new Outcome(mp, $"ERROR: history failed: {historyError}");
        var ordered = starts.Select(ToUtc).Distinct().Order().ToList();
        if (ordered.Count == 0)
            return new Outcome(mp, "left out: no meter entry in Settl");

        var versions = new List<Version>();
        foreach (var start in ordered)
        {
            var (entry, error) = await settl.MeterEntryAtAsync(marketId, start.AddHours(1), cancellationToken);
            if (error != null || entry == null)
                return new Outcome(mp, $"ERROR: meter entry at {ToDanishDate(start):yyyy-MM-dd} failed: {error}");
            versions.Add(new Version(ToDanishDate(start), entry));
        }
        Outcome LeftOut(string result) => new(mp, result, Versions: versions);

        if (!(versions[0].Entry.Meters.Count > 0 && versions[0].Entry.Meters.All(m => m.Occurrence == oldResolution.Occurrence)))
            return LeftOut("left out: not imported yet (Settl's earliest meter entry is not the old resolution), use switch-import");
        if (versions.All(v => v.Date != row.ChangeDate))
            return LeftOut("left out: Settl has no meter entry on ChangeDate, check by hand");

        var sourceIndex = versions.FindIndex(v => HasValues(v.Entry));
        if (sourceIndex < 0)
            return LeftOut("left out: no Settl meter entry has an EstimatedAnnualVolume or an Ampere");
        var source = versions[sourceIndex];

        var rows = new List<ImportRow>();
        foreach (var (date, entry) in versions.Take(sourceIndex).Where(v => LacksValues(v.Entry)))
        {
            var occurrences = entry.Meters.Select(m => m.Occurrence).Distinct().ToList();
            if (occurrences is not [var occurrence] || Resolution.FromSettl(occurrence) is not { } resolution)
                return LeftOut($"left out: meter entry at {date:yyyy-MM-dd} has no single Hourly or QuarterHourly reading, by hand");
            // Meter values only when both versions have one meter; the row names no meter.
            var meters = entry.Meters.Count == 1 && source.Entry.Meters.Count == 1
                ? [source.Entry.Meters[0] with { Occurrence = occurrence }]
                : entry.Meters.Count == 1 ? [] : entry.Meters;
            rows.Add(new ImportRow(mp, date, resolution, new MeterEntry(source.Entry.EstimatedAnnualVolume, meters)));
        }
        if (rows.Count == 0)
            return LeftOut("left out: Settl's history already has the values");

        return new Outcome(mp, "Import", rows,
            [$"values from Settl's meter entry at {source.Date:yyyy-MM-dd} → {string.Join(", ", rows.Select(r => r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))}"],
            versions);
    }

    /// <summary>
    /// The versions of one metering point whose single meter has no MeterType. The template has no MeterType column, so
    /// the import neither sets nor overwrites it: these are set by hand. The MeterType to set is that of the first
    /// version that has one, like the other values. Versions with several meters are left to the person.
    /// </summary>
    internal static IEnumerable<MeterTypeFix> MeterTypeFixes(Outcome outcome)
    {
        var source = outcome.Versions
            .Select(v => v.Entry.Meters is [var m] ? m.MeterType : null)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
        foreach (var version in outcome.Versions)
            if (version.Entry.Meters is [var meter] && string.IsNullOrEmpty(meter.MeterType))
                yield return new MeterTypeFix(outcome.MeteringPoint, version.Date, source);
    }

    private static readonly System.Text.Encoding CsvEncoding = new System.Text.UTF8Encoding(true);

    private static string Csv(params object?[] fields) => string.Join(";", fields.Select(f => f switch
    {
        null => "",
        double d => d.ToString(CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => f.ToString(),
    }));

    /// <summary>
    /// Every MeterCharacteristics version read, one line per meter (a version without meters gets one line with the
    /// meter columns empty). Semicolons, a BOM and CRLF, so it opens in Danish Excel.
    /// </summary>
    private static void WriteBackup(string path, IEnumerable<Outcome> outcomes)
    {
        var text = new System.Text.StringBuilder();
        text.Append(Csv("MeteringPointId", "Date", "EstimatedAnnualVolume", "MeterCount", "MeterReadingOccurrence", "MeterType",
            "DigitsOfPrecision", "Decimals", "MultiplicationFactor", "Ampere")).Append("\r\n");
        foreach (var outcome in outcomes)
            foreach (var (date, entry) in outcome.Versions)
            {
                if (entry.Meters.Count == 0)
                    text.Append(Csv(outcome.MeteringPoint, date, entry.EstimatedAnnualVolume, 0)).Append("\r\n");
                foreach (var m in entry.Meters)
                    text.Append(Csv(outcome.MeteringPoint, date, entry.EstimatedAnnualVolume, entry.Meters.Count, m.Occurrence,
                        m.MeterType, m.DigitsOfPrecision, m.Decimals, m.MultiplicationFactor, m.Ampere)).Append("\r\n");
            }
        File.WriteAllText(path, text.ToString(), CsvEncoding);
    }

    private static void WriteMeterTypeFixes(string path, IEnumerable<MeterTypeFix> fixes)
    {
        var text = new System.Text.StringBuilder();
        text.Append(Csv("MeteringPointId", "Date", "SetMeterType")).Append("\r\n");
        foreach (var f in fixes)
            text.Append(Csv(f.MeteringPoint, f.Date, f.MeterType)).Append("\r\n");
        File.WriteAllText(path, text.ToString(), CsvEncoding);
    }

    private static bool HasValues(MeterEntry entry) =>
        entry.EstimatedAnnualVolume != null || entry.Meters.Any(m => !string.IsNullOrEmpty(m.Ampere));

    private static bool LacksValues(MeterEntry entry) =>
        entry.EstimatedAnnualVolume == null || entry.Meters.Any(m => string.IsNullOrEmpty(m.Ampere));

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

    /// <summary>
    /// Reads resolution-switches-&lt;env&gt;.csv: semicolons, a BOM, fields unquoted unless they hold a semicolon or a
    /// line break. Columns are found by name; the switched-to resolution is the DataHub message's MeterReadingOccurrence.
    /// </summary>
    internal static (List<SwitchRow> Rows, string? Error) ReadSwitches(string path)
    {
        var records = SplitRecords(File.ReadAllText(path).TrimStart('﻿'));
        if (records.Count == 0)
            return ([], $"{path} is empty");
        var header = records[0];
        int Column(string name) => header.FindIndex(h => h == name);
        var (mpCol, dateCol, msgCol) = (Column("MeteringPointId"), Column("ChangeDate"), Column("DataHubMessage"));
        if (mpCol < 0 || dateCol < 0 || msgCol < 0)
            return ([], $"{path} lacks MeteringPointId, ChangeDate or DataHubMessage — is it a resolution-switches file?");
        var rows = new List<SwitchRow>();
        foreach (var record in records.Skip(1).Where(r => r.Count > 1 || r[0].Length > 0))
        {
            if (record.Count <= Math.Max(mpCol, Math.Max(dateCol, msgCol)))
                return ([], $"{path}: short row for {record[0]}");
            if (!DateOnly.TryParseExact(record[dateCol], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return ([], $"{path}: ChangeDate '{record[dateCol]}' for {record[mpCol]} is not yyyy-MM-dd");
            var occurrence = MeterReadingOccurrenceOf(record[msgCol]);
            if (occurrence is not ("PT1H" or "PT15M"))
                return ([], $"{path}: no PT1H/PT15M MeterReadingOccurrence in the DataHub message for {record[mpCol]}");
            rows.Add(new SwitchRow(record[mpCol], date, occurrence));
        }
        return (rows, null);
    }

    private static string? MeterReadingOccurrenceOf(string xml)
    {
        try
        {
            return System.Xml.Linq.XDocument.Parse(xml).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "MeterReadingOccurrence")?.Value.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    // A field is quoted only when it starts with a quote; then "" is a quote and ; and line breaks are text.
    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new System.Text.StringBuilder();
        var i = 0;
        var atFieldStart = true;
        var quoted = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (atFieldStart)
            {
                atFieldStart = false;
                if (c == '"')
                {
                    quoted = true;
                    i++;
                    continue;
                }
            }
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                if (c == '"') { quoted = false; i++; continue; }
                field.Append(c);
                i++;
                continue;
            }
            if (c == ';') { record.Add(field.ToString()); field.Clear(); atFieldStart = true; i++; continue; }
            if (c == '\r' || c == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
                atFieldStart = true;
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }
            field.Append(c);
            i++;
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }
        return records;
    }

    // Settl's MeteringPointMeter import, version 8: the two header rows of its template, then one row per change.
    internal static readonly string[] MetersColumns =
    [
        "Action", "OverwriteFuture", "MeteringPointIdentification", "InstallationNo", "Date", "CountryCode",
        "MeterIdentification", "MeterMethod", "MeteringPointType", "EstimatedAnnualVolume", "EstimatedAnnualVolume2",
        "BalanceVolume1", "BalanceVolume2", "ReadingMethod", "ConsumptionProfile", "QuantityUnit", "DigitsOfPrecision",
        "MultiplicationFactor", "Decimals", "Ampere", "MeterReadingOccurrence", "MeasurementLocation",
        "MeasuredTimeSeriesName", "ProductionType",
    ];

    private static void WriteMeters(string path, List<ImportRow> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("MeteringPointMeters");
        sheet.Cell(1, 1).Value = "Type";
        sheet.Cell(1, 2).Value = "MeteringPointMeter";
        sheet.Cell(1, 3).Value = "Version";
        sheet.Cell(1, 4).Value = 8;
        sheet.Cell(1, 5).Value = "Identification";
        sheet.Cell(1, 6).Value = "MeteringPointIdentification";
        WriteHeader(sheet, MetersColumns);
        var r = 3;
        foreach (var row in rows)
            WriteMeterRow(sheet, r++, row.MeteringPoint, row.Date, row.Resolution, row.Values);
        book.SaveAs(path);
    }

    private static void WriteMeterRow(IXLWorksheet sheet, int row, string meteringPoint, DateOnly date, Resolution resolution,
        MeterEntry entry)
    {
        IXLCell Cell(string column) => sheet.Cell(row, Array.IndexOf(MetersColumns, column) + 1);
        void Set(string column, string value) => Cell(column).Value = value;
        Set("Action", "Update");
        Set("OverwriteFuture", "False");
        Set("MeteringPointIdentification", meteringPoint);
        // A real Excel date shown as yyyy-mm-dd. Settl's importer parses the shown text month first, whatever its
        // template says: the text "09-12-2025" became 12 September (featuretest 571313175200223668) and production
        // rejected "22-10-2025" (2026-11-09). yyyy-mm-dd reads the same either way.
        var dateCell = Cell("Date");
        dateCell.Value = date.ToDateTime(TimeOnly.MinValue);
        dateCell.Style.DateFormat.Format = "yyyy-mm-dd";
        Set("CountryCode", "DK");
        Set("MeterReadingOccurrence", resolution.Occurrence);
        Set("MeasuredTimeSeriesName", SeriesName(meteringPoint, resolution));
        // Settl rejects a volume without a unit: "QuantityUnit must be set if EstimatedAnnualVolume is set"
        // (featuretest, 2026-11-11).
        if (entry.EstimatedAnnualVolume is { } volume)
        {
            Cell("EstimatedAnnualVolume").Value = volume;
            Set("QuantityUnit", "kWh");
        }
        // The row names no meter, so meter values are copied only when the version has exactly one.
        if (entry.Meters is [var meter])
        {
            Cell("DigitsOfPrecision").Value = meter.DigitsOfPrecision;
            Cell("Decimals").Value = meter.Decimals;
            if (meter.MultiplicationFactor is { } factor)
                Cell("MultiplicationFactor").Value = factor;
            if (!string.IsNullOrEmpty(meter.Ampere))
                Set("Ampere", meter.Ampere);
        }
    }

    private static void WriteHeader(IXLWorksheet sheet, string[] columns)
    {
        for (var c = 0; c < columns.Length; c++)
            sheet.Cell(2, c + 1).Value = columns[c];
        // Identifications are 18 digits: kept as text so Excel shows them whole.
        sheet.Column(Array.IndexOf(columns, "MeteringPointIdentification") + 1).Style.NumberFormat.Format = "@";
    }

    // Settl names a metering point's measured series <identification>_<resolution>, e.g. 571313104200130218_PT15M.
    internal static string SeriesName(string meteringPoint, Resolution resolution) => $"{meteringPoint}_{resolution.SeriesSuffix}";
}
