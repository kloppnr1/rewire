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

    /// <summary>The MeterReadingOccurrence of each meter in the MeterCharacteristics version in force at <paramref name="at"/>.</summary>
    Task<(List<string>? Occurrences, string? Error)> MeterReadingOccurrencesAtAsync(Guid id, DateTime at, CancellationToken cancellationToken);
}

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

    public async Task<(List<string>? Occurrences, string? Error)> MeterReadingOccurrencesAtAsync(Guid id, DateTime at, CancellationToken cancellationToken)
    {
        var (mp, error) = await meteringPoints.GetByIdAsync(id, at, cancellationToken);
        return (mp?.MeterCharacteristics?.Meters.Select(m => m.MeterReadingOccurrence ?? "").ToList(), error);
    }
}

/// <summary>
/// switch-import: fills Settl's MeteringPointMeters_V8 import workbook for the metering points in post --all's
/// resolution-switches-&lt;env&gt;.csv, so Settl holds the resolution from before the switch until the
/// DataHub message's date (ChangeDate). Read-only towards Settl and Xellent; it writes only the file.
/// Two Update rows per metering point — the old MeterReadingOccurrence and series from Settl's earliest
/// MeterCharacteristics entry, the new ones from ChangeDate. OverwriteFuture stays False. Settl creates a series the
/// rows name that it lacks (<c>&lt;MP&gt;_PT1H</c>, like the <c>&lt;MP&gt;_PT15M</c> it has): featuretest
/// 571313115101027361, 2026-11-10.
/// Left out: a switch on or before Settl's earliest entry (Settl is right already), one whose earliest entry already
/// has the old value (imported before), and a metering point with more than one switch (by hand).
/// </summary>
public sealed class SwitchImportRunner(ISwitchImportSettlReader settl, Action<string> log)
{
    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    public const string MetersFilePrefix = "MeteringPointMeters_V8";

    /// <summary>A row of resolution-switches: the metering point, ChangeDate and the switched-to MeterReadingOccurrence.</summary>
    internal sealed record SwitchRow(string MeteringPoint, DateOnly ChangeDate, string NewOccurrence);

    /// <summary>Per metering point: what goes in the files, or why nothing does.</summary>
    internal sealed record Outcome(string MeteringPoint, string Result, DateOnly? From = null, DateOnly? ChangeDate = null,
        Resolution? Old = null, Resolution? New = null);

    /// <summary>Hour or Quarter, as the file names them.</summary>
    internal sealed record Resolution(string Occurrence, string SeriesSuffix)
    {
        public static readonly Resolution Hour = new("Hourly", "PT1H");
        public static readonly Resolution Quarter = new("QuarterHourly", "PT15M");
        public static Resolution FromDataHub(string value) => value == "PT1H" ? Hour : Quarter;
        public Resolution Other => this == Hour ? Quarter : Hour;
    }

    public async Task<int> RunAsync(string environment, string switchesCsv, string outputDirectory, int parallel,
        DateOnly today, CancellationToken cancellationToken = default)
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
        log($"Switch import ({environment}): {rows.Count} switch(es) in {Path.GetFullPath(switchesCsv)}");

        var groups = rows.GroupBy(r => r.MeteringPoint, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var results = new Outcome[groups.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, parallel));
        var done = 0;
        await Task.WhenAll(groups.Select(async (group, i) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[i] = await DecideAsync(group.ToList(), cancellationToken);
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
        if (failed.Count > 0)
        {
            log($"ERROR: {failed.Count} metering point(s) could not be read from Settl; no files written.");
            return 1;
        }

        var imports = outcomes.Where(o => o.Result == "Import").ToList();
        Directory.CreateDirectory(outputDirectory);
        var stamp = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var metersPath = Path.Combine(outputDirectory, $"{MetersFilePrefix}_{environment}_{stamp}.xlsx");
        WriteMeters(metersPath, imports);

        log("");
        foreach (var g in outcomes.GroupBy(o => o.Result).OrderByDescending(g => g.Count()))
            log($"  {g.Count(),5}  {g.Key}");
        log($"MeteringPointMeters: {imports.Count * 2} row(s) for {imports.Count} metering point(s) → {Path.GetFullPath(metersPath)}");        return 0;
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

        // Read the earliest version an hour into it, clear of the boundary.
        var (occurrences, versionError) = await settl.MeterReadingOccurrencesAtAsync(marketId, earliest.AddHours(1), cancellationToken);
        if (versionError != null || occurrences == null)
            return new Outcome(mp, $"ERROR: meter entry failed: {versionError}");
        if (occurrences.Count > 0 && occurrences.All(o => o == oldResolution.Occurrence))
            return new Outcome(mp, $"left out: Settl's earliest meter entry is already {oldResolution.Occurrence}");

        return new Outcome(mp, "Import", from, row.ChangeDate, oldResolution, newResolution);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

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

    private static void WriteMeters(string path, List<Outcome> imports)
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
        foreach (var o in imports)
        {
            WriteMeterRow(sheet, r++, o.MeteringPoint, o.From!.Value, o.Old!);
            WriteMeterRow(sheet, r++, o.MeteringPoint, o.ChangeDate!.Value, o.New!);
        }
        book.SaveAs(path);
    }

    private static void WriteMeterRow(IXLWorksheet sheet, int row, string meteringPoint, DateOnly date, Resolution resolution)
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
