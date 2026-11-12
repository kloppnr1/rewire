using ClosedXML.Excel;

namespace DataMigration.OpsCli.Services;

/// <summary>One meter of a Settl MeterCharacteristics version, as an import row carries it.</summary>
public sealed record MeterVersionMeter(string Occurrence, int DigitsOfPrecision, int Decimals, double? MultiplicationFactor, string? Ampere);

/// <summary>
/// One Update row of Settl's MeteringPointMeter import (version 8): the version's date, the volume to hold, and the
/// single meter whose values the row repeats.
/// </summary>
public sealed record MeteringPointMeterRow(string MeteringPointIdentification, DateOnly Date, double EstimatedAnnualVolume, MeterVersionMeter Meter);

/// <summary>
/// Writes Settl's MeteringPointMeter import workbook (version 8) with full rows. The import ignores an empty cell: it
/// does not blank the value in Settl (clarified 2026-11-12; misread on 2026-11-11 as blanking it). Each row still
/// repeats MeterReadingOccurrence, the measured series and the meter values next to the volume, so a version the row
/// creates on its Date has them too.
/// </summary>
public static class MeteringPointMeterExcelWriter
{
    internal static readonly string[] Columns =
    [
        "Action", "OverwriteFuture", "MeteringPointIdentification", "InstallationNo", "Date", "CountryCode",
        "MeterIdentification", "MeterMethod", "MeteringPointType", "EstimatedAnnualVolume", "EstimatedAnnualVolume2",
        "BalanceVolume1", "BalanceVolume2", "ReadingMethod", "ConsumptionProfile", "QuantityUnit", "DigitsOfPrecision",
        "MultiplicationFactor", "Decimals", "Ampere", "MeterReadingOccurrence", "MeasurementLocation",
        "MeasuredTimeSeriesName", "ProductionType",
    ];

    /// <summary>Settl's series suffix for a MeterReadingOccurrence; null for one the import cannot name.</summary>
    public static string? SeriesSuffix(string occurrence) => occurrence switch
    {
        "Hourly" => "PT1H",
        "QuarterHourly" => "PT15M",
        _ => null,
    };

    public static void Write(string path, IEnumerable<MeteringPointMeterRow> rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("MeteringPointMeters");
        sheet.Cell(1, 1).Value = "Type";
        sheet.Cell(1, 2).Value = "MeteringPointMeter";
        sheet.Cell(1, 3).Value = "Version";
        sheet.Cell(1, 4).Value = 8;
        sheet.Cell(1, 5).Value = "Identification";
        sheet.Cell(1, 6).Value = "MeteringPointIdentification";
        for (var c = 0; c < Columns.Length; c++)
            sheet.Cell(2, c + 1).Value = Columns[c];
        // Identifications are 18 digits: kept as text so Excel shows them whole.
        sheet.Column(Array.IndexOf(Columns, "MeteringPointIdentification") + 1).Style.NumberFormat.Format = "@";

        var r = 3;
        foreach (var row in rows)
            WriteRow(sheet, r++, row);
        book.SaveAs(path);
    }

    private static void WriteRow(IXLWorksheet sheet, int r, MeteringPointMeterRow row)
    {
        IXLCell Cell(string column) => sheet.Cell(r, Array.IndexOf(Columns, column) + 1);
        Cell("Action").Value = "Update";
        Cell("OverwriteFuture").Value = "False";
        Cell("MeteringPointIdentification").Value = row.MeteringPointIdentification;
        // A real Excel date shown year first: Settl's importer reads the shown text month first, so "22-10-2025" was
        // rejected and "09-12-2025" became 12 September. yyyy-mm-dd reads the same either way.
        var date = Cell("Date");
        date.Value = row.Date.ToDateTime(TimeOnly.MinValue);
        date.Style.DateFormat.Format = "yyyy-mm-dd";
        Cell("CountryCode").Value = "DK";
        Cell("EstimatedAnnualVolume").Value = row.EstimatedAnnualVolume;
        // Settl rejects a volume without its unit: "QuantityUnit must be set if EstimatedAnnualVolume is set" (featuretest, 2026-11-11).
        Cell("QuantityUnit").Value = "kWh";
        Cell("MeterReadingOccurrence").Value = row.Meter.Occurrence;
        if (SeriesSuffix(row.Meter.Occurrence) is { } suffix)
            Cell("MeasuredTimeSeriesName").Value = $"{row.MeteringPointIdentification}_{suffix}";
        Cell("DigitsOfPrecision").Value = row.Meter.DigitsOfPrecision;
        Cell("Decimals").Value = row.Meter.Decimals;
        if (row.Meter.MultiplicationFactor is { } factor)
            Cell("MultiplicationFactor").Value = factor;
        if (!string.IsNullOrEmpty(row.Meter.Ampere))
            Cell("Ampere").Value = row.Meter.Ampere;
    }
}
