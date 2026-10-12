using System.Globalization;
using System.Text;
using DataMigration.Core;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Human-checkable sidecar for the Settl bulk-import Excel: same rows, but the catalog GUID
/// (<see cref="CustomerPriceListAssociationRow.PriceListIdentifier"/>) is resolved back to the
/// Settl <c>Name</c> ("ST 0500") and the short <c>PriceListIdentifier</c> code ("7129") shown in
/// the Settl UI's Price List screen (Name / ID columns) — the GUID alone can't be eyeballed
/// against that screen. Never read by the import; purely for the person reviewing the export.
/// </summary>
public static class CustomerPriceListVerificationCsvWriter
{
    public static void Write(
        string outputPath,
        IEnumerable<CustomerPriceListAssociationRow> rows,
        IReadOnlyDictionary<string, (string? Name, string? DisplayId)> priceListsByGuid)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CustomerNumber,ProductElementCode,PriceListName,PriceListId,PriceListGuid,Start,End");
        foreach (var row in rows)
        {
            var (name, displayId) = priceListsByGuid.TryGetValue(row.PriceListIdentifier, out var pl)
                ? pl
                : (null, null);
            sb.AppendLine(string.Join(",",
                CsvField(row.CustomerNumber),
                CsvField(row.ProductElementCode),
                CsvField(name ?? "(unknown)"),
                CsvField(displayId ?? "(unknown)"),
                CsvField(row.PriceListIdentifier),
                CsvField(FormatDate(row.Start)),
                CsvField(FormatDate(row.End))));
        }

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static string FormatDate(DateTime value) =>
        value == MigrationConstants.EndDateNull
            ? string.Empty
            : value.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture);

    private static string CsvField(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
