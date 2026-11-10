using System.Globalization;
using ClosedXML.Excel;
using DataMigration.Core;

namespace DataMigration.OpsCli.Services;

public record CustomerPriceListAssociationRow(
    string CustomerNumber,
    string ProductElementCode,
    string PriceListIdentifier,
    DateTime Start,
    DateTime End);

public static class CustomerPriceListAssociationExcelWriter
{
    private static readonly string[] HeaderRow =
    [
        "Action", "CustomerNumber", "CountryCode", "ProductElementCode",
        "ContractOrderGuid", "MarketBalanceArea", "PriceListIdentifier", "Start", "End"
    ];

    public static void Write(string outputPath, IEnumerable<CustomerPriceListAssociationRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");

        sheet.Cell(1, 1).Value = "Type";
        sheet.Cell(1, 2).Value = "CustomerPriceListAssociation";
        sheet.Cell(1, 3).Value = "Version";
        sheet.Cell(1, 4).Value = 2;

        for (var i = 0; i < HeaderRow.Length; i++)
            sheet.Cell(2, i + 1).Value = HeaderRow[i];

        var rowIdx = 3;
        foreach (var row in rows)
        {
            sheet.Cell(rowIdx, 1).Value = "Create";
            sheet.Cell(rowIdx, 2).Value = row.CustomerNumber;
            sheet.Cell(rowIdx, 3).Value = "DK";
            sheet.Cell(rowIdx, 4).Value = row.ProductElementCode;
            // ContractOrderGuid (col 5) + MarketBalanceArea (col 6) intentionally blank.
            sheet.Cell(rowIdx, 7).Value = row.PriceListIdentifier;
            sheet.Cell(rowIdx, 8).Value = FormatDate(row.Start);
            sheet.Cell(rowIdx, 9).Value = FormatDate(row.End);
            rowIdx++;
        }

        workbook.SaveAs(outputPath);
    }

    private static string FormatDate(DateTime value) =>
        value == MigrationConstants.EndDateNull
            ? string.Empty
            : value.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);
}
