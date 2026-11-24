using ClosedXML.Excel;

namespace DataMigration.OpsCli.Services;

public record EavRow(string MeteringPointIdentification, DateOnly Date, decimal EstimatedAnnualVolume);

public static class EavExcelWriter
{
    private static readonly string[] HeaderRow =
    [
        "Action", "OverwriteFuture", "MeteringPointIdentification", "InstallationNo",
        "Date", "CountryCode", "MeterIdentification", "MeterMethod", "MeteringPointType",
        "EstimatedAnnualVolume", "EstimatedAnnualVolume2", "BalanceVolume1", "BalanceVolume2",
        "ReadingMethod", "ConsumptionProfile", "QuantityUnit", "DigitsOfPrecision",
        "MultiplicationFactor", "Decimals", "Ampere", "MeterReadingOccurrence",
        "MeasurementLocation", "MeasuredTimeSeriesName", "ProductionType"
    ];

    public static void Write(string outputPath, IEnumerable<EavRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");

        sheet.Cell(1, 1).Value = "Type";
        sheet.Cell(1, 2).Value = "MeteringPointMeter";
        sheet.Cell(1, 3).Value = "Version";
        sheet.Cell(1, 4).Value = 8;
        sheet.Cell(1, 5).Value = "Identification";
        sheet.Cell(1, 6).Value = "MeteringPointIdentification";

        for (var i = 0; i < HeaderRow.Length; i++)
            sheet.Cell(2, i + 1).Value = HeaderRow[i];

        var rowIdx = 3;
        foreach (var row in rows)
        {
            sheet.Cell(rowIdx, 1).Value = "Update";
            sheet.Cell(rowIdx, 3).Value = row.MeteringPointIdentification;
            sheet.Cell(rowIdx, 5).Value = row.Date.ToString("dd-MM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
            sheet.Cell(rowIdx, 6).Value = "DK";
            sheet.Cell(rowIdx, 10).Value = row.EstimatedAnnualVolume;
            sheet.Cell(rowIdx, 16).Value = "kWh";
            rowIdx++;
        }

        workbook.SaveAs(outputPath);
    }
}
