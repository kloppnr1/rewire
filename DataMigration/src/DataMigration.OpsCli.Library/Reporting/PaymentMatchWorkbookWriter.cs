using ClosedXML.Excel;

namespace DataMigration.OpsCli.Reporting;

/// <summary>How much attention a row needs. Drives the row fill, nothing else.</summary>
public enum PaymentMatchRowTone
{
    /// <summary>Nothing to check.</summary>
    None,

    /// <summary>Usable, but the pick rests on an assumption worth confirming.</summary>
    Review,

    /// <summary>Cannot be used as it stands — a human has to decide or investigate.</summary>
    Blocking
}

/// <param name="Cells">Cell values, left to right. Shorter than the header row leaves the rest
/// blank; longer is a bug the writer will not hide.</param>
public sealed record PaymentMatchSheetRow(IReadOnlyList<string> Cells, PaymentMatchRowTone Tone = PaymentMatchRowTone.None);

/// <summary>
/// Writes a payment-match sheet: bold grey header, frozen first row, autofilter, columns sized to
/// content, and a fill on the rows that need attention.
///
/// <para>Shared by both halves of <c>payment-match</c> so the two spreadsheets look like one
/// report rather than two. It knows nothing about PBS or MobilePay — the headers and the cells
/// arrive as strings, and the caller decides what they mean.</para>
/// </summary>
public static class PaymentMatchWorkbookWriter
{
    public static string Write(
        string outputPath,
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyList<PaymentMatchSheetRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);

        for (var i = 0; i < headers.Count; i++)
            sheet.Cell(1, i + 1).Value = headers[i];

        var headerRange = sheet.Range(1, 1, 1, headers.Count);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        var rowIdx = 2;
        foreach (var row in rows)
        {
            if (row.Cells.Count > headers.Count)
                throw new ArgumentException(
                    $"Row {rowIdx} has {row.Cells.Count} cells but the sheet has {headers.Count} columns. " +
                    "A row wider than its header is a column that was added in one place and not the other.",
                    nameof(rows));

            for (var i = 0; i < row.Cells.Count; i++)
                sheet.Cell(rowIdx, i + 1).Value = row.Cells[i];

            // Colour the whole row: red where the sheet cannot be used as it stands, amber where the
            // pick is defensible but rests on an assumption worth checking.
            if (row.Tone != PaymentMatchRowTone.None)
            {
                sheet.Range(rowIdx, 1, rowIdx, headers.Count).Style.Fill.BackgroundColor =
                    row.Tone == PaymentMatchRowTone.Blocking
                        ? XLColor.LightSalmon
                        : XLColor.LightGoldenrodYellow;
            }

            rowIdx++;
        }

        sheet.Columns(1, headers.Count).AdjustToContents();
        sheet.SheetView.FreezeRows(1);
        if (rows.Count > 0)
            sheet.Range(1, 1, rowIdx - 1, headers.Count).SetAutoFilter();

        workbook.SaveAs(outputPath);
        return outputPath;
    }
}
