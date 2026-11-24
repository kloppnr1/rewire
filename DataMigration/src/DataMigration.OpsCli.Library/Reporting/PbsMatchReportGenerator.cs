using System.Text;
using System.Web;
using DataMigration.Core.Reporting;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Generates the PBS match report in three shapes — HTML to read, CSV to diff, XLSX to hand on.
///
/// <para>All three take their columns from <see cref="PaymentMatchColumns.Pbs"/> and their cell
/// values from <see cref="Cells"/>, so a column cannot appear in one output and be forgotten in
/// another. The first eleven are the vocabulary the MobilePay sheet uses too.</para>
/// </summary>
public class PbsMatchReportGenerator
{
    /// <summary>
    /// One row's cells, in the order <see cref="PaymentMatchColumns.Pbs"/> declares.
    /// </summary>
    /// <param name="empty">What an absent value looks like. The HTML table has always written "-",
    /// the CSV and the spreadsheet an empty field.</param>
    /// <param name="cos">The profile's change of supply — one date for the whole run, unlike
    /// MobilePay's per-metering-point one. Repeated on every row so the two reports carry the same
    /// column, which is worth more than sparing the space.</param>
    private static string[] Cells(
        PbsMatchReportRow row, Dictionary<string, string>? contractStatuses, DateOnly? cos,
        string empty = "")
    {
        var pbsStatus = row.ContractNumber != null
                        && contractStatuses?.TryGetValue(row.ContractNumber, out var s) == true
            ? s
            : empty;

        // What actually keyed the row into Settl's export — and only where something did. An
        // unmatched row has no key, so printing its metering point here would claim a match it
        // never made. Same rule as the MobilePay sheet's column of this name.
        var matched = row.MatchStatus is PbsMatchStatus.Matched
                                       or PbsMatchStatus.MatchedJoint
                                       or PbsMatchStatus.Mismatch;
        var matchKey = matched
            ? PaymentMatchColumns.MatchKeyLabel(
                row.JointInvoiceNumber ?? row.MeteringpointId, isJoint: row.JointInvoiceNumber != null)
            : "";

        // Where the agreement actually sits. Usually the row's own contract; on an inherited row it
        // is the main contract, and saying "found on" about the metering point's contract would be
        // false — that contract is precisely the one carrying no agreement.
        var agreementContract = row.PbsAgreementSourceContract ?? row.ContractNumber;

        // The kind describes the contract in the column beside it. An inherited agreement was found
        // on a main contract by definition of the fallback, whatever kind the row's own contract is.
        var kind = row.PbsAgreementSourceContract is not null
            ? PaymentMatchColumns.ContractKindLabel(PbsSourceKind.MainContract)
            : PaymentMatchColumns.ContractKindLabel(row.SourceKind, empty);

        // Null on a Settl-only row: there is no contract, so neither "Ja" nor "Nej" is true.
        bool? onMigratedContract = row.ContractNumber is null
            ? null
            : row.PbsAgreementSourceContract is null;

        return
        [
            row.CustomerNumber ?? empty,
            agreementContract ?? empty,
            kind,
            PaymentMatchColumns.Date(row.ContractEndDate, empty),
            PaymentMatchColumns.Count(row.AgreementCountOnAccount, empty),
            PaymentMatchColumns.StatusLabel(row.MatchStatus),
            row.Notes ?? "",
            row.MeteringpointId ?? empty,
            matchKey,
            row.ExternalCustomerNumber ?? empty,
            row.PaymentMethod ?? empty,
            // The contract that owns the metering point — what was migrated. Equal to the column
            // above except on an inherited row, which is exactly when the distinction matters.
            row.ContractNumber ?? empty,
            onMigratedContract is null ? empty : PaymentMatchColumns.YesNo(onMigratedContract),
            PaymentMatchColumns.Date(cos, empty),
            row.PbsAgreementNumber != 0 ? row.PbsAgreementNumber.ToString() : empty,
            pbsStatus,
            PaymentMatchColumns.Count(row.CandidatesOnMeteringPoint, empty)
        ];
    }

    public async Task<string> GenerateHtmlReportAsync(string outputDirectory, List<PbsMatchReportRow> rows, Dictionary<string, string>? contractStatuses = null, DateOnly? cos = null)
    {
        var sb = new StringBuilder();
        var timestamp = DateTime.Now;

        var matched = rows.Count(r => r.MatchStatus == PbsMatchStatus.Matched);
        var matchedJoint = rows.Count(r => r.MatchStatus == PbsMatchStatus.MatchedJoint);
        var unmatchedXellent = rows.Count(r => r.MatchStatus == PbsMatchStatus.UnmatchedXellent);
        var unmatchedSettl = rows.Count(r => r.MatchStatus == PbsMatchStatus.UnmatchedSettl);
        var mismatch = rows.Count(r => r.MatchStatus == PbsMatchStatus.Mismatch);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>PBS Match Report</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 40px; background: #f5f5f5; }");
        sb.AppendLine("    .container { max-width: 1800px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
        sb.AppendLine("    h1 { color: #333; border-bottom: 2px solid #4a90d9; padding-bottom: 10px; }");
        sb.AppendLine("    h2 { color: #444; margin-top: 30px; }");
        sb.AppendLine("    .timestamp { color: #666; font-size: 14px; }");
        sb.AppendLine("    .summary { display: flex; gap: 20px; margin: 20px 0; flex-wrap: wrap; }");
        sb.AppendLine("    .summary-card { padding: 15px 25px; border-radius: 8px; text-align: center; min-width: 120px; }");
        sb.AppendLine("    .summary-card .count { font-size: 32px; font-weight: bold; }");
        sb.AppendLine("    .summary-card .label { font-size: 14px; margin-top: 5px; }");
        sb.AppendLine("    .card-matched { background: #d4edda; color: #155724; }");
        sb.AppendLine("    .card-matched-joint { background: #d1ecf1; color: #0c5460; }");
        sb.AppendLine("    .card-unmatched-xellent { background: #fff3cd; color: #856404; }");
        sb.AppendLine("    .card-unmatched-settl { background: #cce5ff; color: #004085; }");
        sb.AppendLine("    .card-mismatch { background: #f8d7da; color: #721c24; }");
        sb.AppendLine("    .card-total { background: #e2e3e5; color: #383d41; }");
        sb.AppendLine("    table { border-collapse: collapse; width: 100%; margin: 15px 0; }");
        sb.AppendLine("    th, td { border: 1px solid #ddd; padding: 8px 12px; text-align: left; }");
        sb.AppendLine("    th { background: #f8f9fa; font-weight: 600; position: sticky; top: 0; }");
        sb.AppendLine("    tr:hover { background: #f5f5f5; }");
        sb.AppendLine("    .status-matched { color: #155724; font-weight: bold; }");
        sb.AppendLine("    .status-matched-joint { color: #0c5460; font-weight: bold; }");
        sb.AppendLine("    .status-unmatched-xellent { color: #856404; font-weight: bold; }");
        sb.AppendLine("    .status-unmatched-settl { color: #004085; font-weight: bold; }");
        sb.AppendLine("    .status-mismatch { color: #721c24; font-weight: bold; }");
        sb.AppendLine("    .code { font-family: monospace; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("<div class=\"container\">");

        sb.AppendLine("<h1>PBS Match Report</h1>");
        sb.AppendLine($"<p class=\"timestamp\">Genereret: {timestamp:yyyy-MM-dd HH:mm:ss}</p>");
        sb.AppendLine("<p>Holder Xellents PBS-kontrakter op mod Settls betalingsaftaler på målepunkt og fællesfakturanummer.</p>");
        // Rapporten dækker profilens konti, ikke Settls. En profil migreres rutinemæssigt kun
        // delvist, og så ligner en umigreret kunde en migreret kunde uden betalingsaftale.
        sb.AppendLine("<p style=\"background:#fff3cd;border-left:4px solid #856404;padding:10px 15px;color:#856404\">"
            + "<b>Læs «"
            + HttpUtility.HtmlEncode(PaymentMatchColumns.StatusLabel(PbsMatchStatus.UnmatchedXellent))
            + "» med forbehold.</b> Rapportens population er profilens konti — ikke dem Settl faktisk holder. "
            + "Statussen dækker derfor både kunder der blev migreret uden at Settl fik en betalingsaftale, "
            + "og kunder der aldrig blev migreret. Tallet er ikke en fejlliste.</p>");

        // Summary cards
        sb.AppendLine("<h2>Opsummering</h2>");
        sb.AppendLine("<div class=\"summary\">");
        sb.AppendLine($"  <div class=\"summary-card card-total\"><div class=\"count\">{rows.Count}</div><div class=\"label\">i alt</div></div>");
        sb.AppendLine($"  <div class=\"summary-card card-matched\"><div class=\"count\">{matched}</div><div class=\"label\">{PaymentMatchColumns.StatusLabel(PbsMatchStatus.Matched)}</div></div>");
        sb.AppendLine($"  <div class=\"summary-card card-matched-joint\"><div class=\"count\">{matchedJoint}</div><div class=\"label\">{PaymentMatchColumns.StatusLabel(PbsMatchStatus.MatchedJoint)}</div></div>");
        sb.AppendLine($"  <div class=\"summary-card card-unmatched-xellent\"><div class=\"count\">{unmatchedXellent}</div><div class=\"label\">{PaymentMatchColumns.StatusLabel(PbsMatchStatus.UnmatchedXellent)}</div></div>");
        sb.AppendLine($"  <div class=\"summary-card card-unmatched-settl\"><div class=\"count\">{unmatchedSettl}</div><div class=\"label\">{PaymentMatchColumns.StatusLabel(PbsMatchStatus.UnmatchedSettl)}</div></div>");
        sb.AppendLine($"  <div class=\"summary-card card-mismatch\"><div class=\"count\">{mismatch}</div><div class=\"label\">{PaymentMatchColumns.StatusLabel(PbsMatchStatus.Mismatch)}</div></div>");
        sb.AppendLine("</div>");

        // Detail table
        sb.AppendLine("<h2>Detaljer</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("  <tr>"
            + string.Concat(PaymentMatchColumns.Pbs.Select(h => $"<th>{HttpUtility.HtmlEncode(h)}</th>"))
            + "</tr>");

        // The status column keeps its colour, so the table can still be scanned down its right-hand
        // side. Its index follows the shared vocabulary rather than a hard-coded number.
        var statusIndex = Array.IndexOf(PaymentMatchColumns.Pbs, PaymentMatchColumns.Status);

        foreach (var row in rows)
        {
            var statusClass = row.MatchStatus switch
            {
                PbsMatchStatus.Matched => "status-matched",
                PbsMatchStatus.MatchedJoint => "status-matched-joint",
                PbsMatchStatus.UnmatchedXellent => "status-unmatched-xellent",
                PbsMatchStatus.UnmatchedSettl => "status-unmatched-settl",
                PbsMatchStatus.Mismatch => "status-mismatch",
                _ => ""
            };

            var cells = Cells(row, contractStatuses, cos, empty: "-");

            sb.AppendLine("  <tr>");
            for (var i = 0; i < cells.Length; i++)
            {
                var css = i == statusIndex ? statusClass : "code";
                sb.AppendLine($"    <td class=\"{css}\">{HttpUtility.HtmlEncode(cells[i])}</td>");
            }
            sb.AppendLine("  </tr>");
        }

        sb.AppendLine("</table>");
        sb.AppendLine("</div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        var filePath = Path.Combine(outputDirectory, $"PbsMatchReport_{timestamp:yyyyMMdd_HHmmss}.html");
        await File.WriteAllTextAsync(filePath, sb.ToString());
        return filePath;
    }

    public async Task<string> GenerateCsvReportAsync(string outputDirectory, List<PbsMatchReportRow> rows, Dictionary<string, string>? contractStatuses = null, DateOnly? cos = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildCsv(rows, contractStatuses, cos));

        var timestamp = DateTime.Now;
        var filePath = Path.Combine(outputDirectory, $"PbsMatchReport_{timestamp:yyyyMMdd_HHmmss}.csv");
        await File.WriteAllTextAsync(filePath, sb.ToString(), new UTF8Encoding(true));
        return filePath;
    }

    /// <summary>
    /// The CSV body, without writing a file. Lets a test assert the columns without a temp
    /// directory and a timestamped filename to find again.
    /// </summary>
    public static string BuildCsv(
        IReadOnlyList<PbsMatchReportRow> rows, Dictionary<string, string>? contractStatuses = null, DateOnly? cos = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(';', PaymentMatchColumns.Pbs.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(';', Cells(row, contractStatuses, cos).Select(Escape)));
        return sb.ToString().TrimEnd('\r', '\n');
    }

    /// <summary>
    /// The same rows as a spreadsheet, so the PBS report can be handed on the way the MobilePay one
    /// already is. Rows are toned by status: a mismatch cannot be used as it stands, an unmatched
    /// row needs looking at.
    /// </summary>
    public Task<string> GenerateXlsxReportAsync(
        string outputDirectory, List<PbsMatchReportRow> rows,
        Dictionary<string, string>? contractStatuses = null, DateOnly? cos = null)
    {
        var filePath = Path.Combine(outputDirectory, $"PbsMatchReport_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");

        PaymentMatchWorkbookWriter.Write(
            filePath, "PBS", PaymentMatchColumns.Pbs,
            [.. rows.Select(r => new PaymentMatchSheetRow(Cells(r, contractStatuses, cos), Tone(r.MatchStatus)))]);

        return Task.FromResult(filePath);
    }

    private static PaymentMatchRowTone Tone(PbsMatchStatus status) => status switch
    {
        PbsMatchStatus.Mismatch => PaymentMatchRowTone.Blocking,
        PbsMatchStatus.UnmatchedXellent or PbsMatchStatus.UnmatchedSettl => PaymentMatchRowTone.Review,
        _ => PaymentMatchRowTone.None
    };

    private static string Escape(string value)
    {
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
