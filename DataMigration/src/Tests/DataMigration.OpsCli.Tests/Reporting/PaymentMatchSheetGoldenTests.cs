using ClosedXML.Excel;
using DataMigration.Core.Reporting;
using DataMigration.OpsCli.Reporting;
using DataMigration.XellentData.Reporting;

namespace DataMigration.OpsCli.Tests.Reporting;

/// <summary>
/// What the two payment-match sheets actually contain, locked against golden files.
///
/// <para>Both halves of <c>payment-match</c> answer the same question about different payment
/// methods, and until now said so in different languages and orders. The point of these goldens is
/// that a column change shows up as a readable line diff instead of a failed assertion with no
/// context — and that the shared eleven columns cannot drift apart again unnoticed.</para>
///
/// <para>Hand-built rows, no database and no Settl: the sheet layout is worth locking on every
/// commit, and anything that needs a VPN would not run here.</para>
/// </summary>
public class PaymentMatchSheetGoldenTests
{
    private static string GoldenPath(string name) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Goldens", "payment-match", name));

    [Fact]
    public void The_two_sheets_agree_on_the_first_eleven_columns()
    {
        // Asserted against the sheets themselves rather than against PaymentMatchColumns.Shared:
        // the constant agreeing with itself proves nothing about what was written.
        Assert.Equal(
            PaymentMatchColumns.Shared,
            HeadersOf(WritePbsSheet()).Take(PaymentMatchColumns.Shared.Length));

        Assert.Equal(
            PaymentMatchColumns.Shared,
            HeadersOf(WriteMobilePaySheet()).Take(PaymentMatchColumns.Shared.Length));
    }

    [Fact]
    public void The_pbs_sheet_matches_its_golden()
    {
        CsvGoldenAsserter.AssertMatchesGolden(SheetAsCsv(WritePbsSheet()), GoldenPath("pbs-sheet.csv"));
    }

    [Fact]
    public void The_mobilepay_sheet_matches_its_golden()
    {
        CsvGoldenAsserter.AssertMatchesGolden(SheetAsCsv(WriteMobilePaySheet()), GoldenPath("mb-sheet.csv"));
    }

    [Fact]
    public void The_pbs_csv_and_the_pbs_sheet_carry_the_same_columns()
    {
        // Three outputs, one column list. Nothing stops HTML, CSV and XLSX from drifting except
        // this — and a column present in the spreadsheet but missing from the CSV is exactly the
        // kind of gap nobody notices until a handover.
        var csvHeader = PbsMatchReportGenerator.BuildCsv(PbsRows(), ContractStatuses, Cos)
            .Split('\n')[0].TrimEnd('\r').Split(';');

        Assert.Equal(HeadersOf(WritePbsSheet()), csvHeader);
    }

    [Fact]
    public void The_borrowed_agreement_is_named_on_the_row_that_borrowed_it()
    {
        // A row whose PBS number came from another contract names two contracts. The remark is the
        // only thing that says which is which — without it the row silently contradicts itself.
        var csv = PbsMatchReportGenerator.BuildCsv(PbsRows(), ContractStatuses, Cos);
        var line = csv.Split('\n').Single(l => l.StartsWith("947686;", StringComparison.Ordinal));

        Assert.Contains("PBS fra kontrakt varme-kontrakt (Varme)", line);
        // And it did not displace the note the row already carried.
        Assert.Contains("2 Settl-rækker på samme målepunkt", line);
    }

    // --- Fixtures ---

    private static readonly Dictionary<string, string> ContractStatuses = new()
    {
        ["el-kontrakt"] = "Aktiv",
        ["c-match"] = "Aktiv",
        ["c-joint"] = "Aktiv",
        ["c-unmatched"] = "Aktiv",
        ["c-mismatch"] = "Afmeldt",
    };

    /// <summary>
    /// One row per <see cref="PbsMatchStatus"/>, plus one per construction path in
    /// <c>PbsMatchService</c>. The three paths build rows separately, so a field can be dropped on
    /// one while the other two look right.
    /// </summary>
    private static List<PbsMatchReportRow> PbsRows() =>
    [
        new()
        {
            CustomerNumber = "111111", ContractNumber = "c-match", SourceKind = PbsSourceKind.Standalone,
            ContractEndDate = new DateOnly(2026, 6, 30), AgreementCountOnAccount = 1,
            CandidatesOnMeteringPoint = 1, ExpiredCandidatesOnMeteringPoint = 0,
            PbsAgreementNumber = 111, MeteringpointId = "571000000000000001",
            ExternalCustomerNumber = "E-1001", PaymentMethod = "Betalingsservice",
            MatchStatus = PbsMatchStatus.Matched
        },
        new()
        {
            CustomerNumber = "222222", ContractNumber = "c-joint", SourceKind = PbsSourceKind.SubContract,
            ContractEndDate = null, AgreementCountOnAccount = 2,
            CandidatesOnMeteringPoint = 2, ExpiredCandidatesOnMeteringPoint = 1,
            PbsAgreementNumber = 222, JointInvoiceNumber = "J-500",
            ExternalCustomerNumber = "E-1002", PaymentMethod = "FI-Kort",
            MatchStatus = PbsMatchStatus.MatchedJoint
        },
        new()
        {
            CustomerNumber = "333333", ContractNumber = "c-unmatched", SourceKind = PbsSourceKind.MainContract,
            ContractEndDate = new DateOnly(2026, 6, 30), AgreementCountOnAccount = 1,
            CandidatesOnMeteringPoint = 1, ExpiredCandidatesOnMeteringPoint = 0,
            PbsAgreementNumber = 333, MeteringpointId = "571000000000000003",
            MatchStatus = PbsMatchStatus.UnmatchedXellent
        },
        new()
        {
            // Settl-only: no Xellent contract behind it, so the provenance columns stay blank
            // rather than claiming a contract that was never found.
            MeteringpointId = "571000000000000004", ExternalCustomerNumber = "E-1004",
            PaymentMethod = "Betalingsservice", MatchStatus = PbsMatchStatus.UnmatchedSettl
        },
        new()
        {
            CustomerNumber = "555555", ContractNumber = "c-mismatch", SourceKind = PbsSourceKind.Standalone,
            ContractEndDate = new DateOnly(2026, 6, 30), AgreementCountOnAccount = 1,
            CandidatesOnMeteringPoint = 1, ExpiredCandidatesOnMeteringPoint = 0,
            PbsAgreementNumber = 555, MeteringpointId = "571000000000000005",
            ExternalCustomerNumber = "E-1005", PaymentMethod = "Betalingsservice",
            MatchStatus = PbsMatchStatus.Mismatch, Notes = "Afmeldt i Xellent"
        },
        BorrowedAgreementRow(),
    ];

    /// <summary>
    /// The case behind the fallback: the electricity contract carries no PBS, so the agreement was
    /// read off another of the customer's contracts — a heating one.
    /// </summary>
    private static PbsMatchReportRow BorrowedAgreementRow()
    {
        var row = new PbsMatchReportRow
        {
            CustomerNumber = "947686", ContractNumber = "el-kontrakt", SourceKind = PbsSourceKind.Standalone,
            ContractEndDate = new DateOnly(2026, 6, 30), AgreementCountOnAccount = 1,
            CandidatesOnMeteringPoint = 1, ExpiredCandidatesOnMeteringPoint = 0,
            PbsAgreementNumber = 98765, MeteringpointId = "571000000000000006",
            PbsAgreementSourceContract = "varme-kontrakt", PbsAgreementSourceDeliveryCategory = "Varme",
            ExternalCustomerNumber = "E-1006", PaymentMethod = "Betalingsservice",
            MatchStatus = PbsMatchStatus.Matched,
            Notes = "2 Settl-rækker på samme målepunkt"
        };
        row.AddNote("PBS fra kontrakt varme-kontrakt (Varme)");
        return row;
    }

    private static List<MobilePayMatchReportRow> MobilePayRows() =>
    [
        new()
        {
            Account = "111111", SourceContractNumber = "mb-main", SourceKind = MobilePaySourceKind.MainContract,
            SourceContractEndDate = new DateOnly(2026, 6, 30), AgreementCountOnAccount = 1,
            Status = MobilePayMatchStatus.Matched, MeteringPointId = "571000000000000001",
            MatchKey = "571000000000000001", MigratedContractNumber = "mb-main",
            Cos = new DateOnly(2026, 7, 1), IsOnMigratedContract = true,
            EnummerSettl = "E-1001", EnummerSourcePaymentMethod = "Betalingsservice",
            MobilePayAgreementId = "MP-1"
        },
        new()
        {
            // Joint match: the key is a joint invoice number, which looks nothing like a metering
            // point and has to say so.
            Account = "222222", SourceContractNumber = "mb-sub", SourceKind = MobilePaySourceKind.SubContract,
            AgreementCountOnAccount = 2, Status = MobilePayMatchStatus.Matched,
            MeteringPointId = "571000000000000002", MatchKey = "J-500", IsJoint = true,
            MigratedContractNumber = "mb-other", Cos = new DateOnly(2026, 7, 1),
            IsOnMigratedContract = false, EnummerSettl = "E-1002",
            EnummerSourcePaymentMethod = "FI-Kort", MobilePayAgreementId = "MP-2",
            Remarks = ["Konkurrerende aftaler på kundenummeret"],
            Severity = MobilePayRowSeverity.Blocking
        },
        new()
        {
            Account = "333333", SourceContractNumber = "mb-alone", SourceKind = MobilePaySourceKind.Standalone,
            AgreementCountOnAccount = 1, Status = MobilePayMatchStatus.NoSettlCustomerNumber,
            MeteringPointId = "571000000000000003", MatchKey = "",
            MigratedContractNumber = "mb-alone", Cos = new DateOnly(2026, 7, 1),
            IsOnMigratedContract = true, MobilePayAgreementId = "MP-3",
            Remarks = ["INTET E-NUMMER i Settl"], Severity = MobilePayRowSeverity.Review
        },
        new()
        {
            // Opsagt af migreringen selv — dagen efter COS. Kommer kun med på en
            // --include-terminated-kørsel, og det er den række der giver statuskolonnen indhold:
            // uden den ville arket stå "Aktiv" hele vejen ned, som det gjorde før 0.8.33.
            Account = "444444", SourceContractNumber = "mb-opsagt", SourceKind = MobilePaySourceKind.Standalone,
            AgreementCountOnAccount = 0, Status = MobilePayMatchStatus.Matched,
            MeteringPointId = "571000000000000004", MatchKey = "571000000000000004",
            MigratedContractNumber = "mb-opsagt", Cos = new DateOnly(2026, 7, 1),
            IsOnMigratedContract = true, EnummerSettl = "E-1004",
            EnummerSourcePaymentMethod = "Betalingsservice", MobilePayAgreementId = "MP-4",
            CancelDate = new DateOnly(2026, 7, 2)
        },
    ];

    // --- Sheet plumbing ---

    /// <summary>Profilens skiftedato — én for hele kørslen, hvor MobilePay har én pr. målepunkt.</summary>
    private static readonly DateOnly Cos = new(2026, 7, 1);

    private static string WritePbsSheet()
    {
        var dir = Directory.CreateTempSubdirectory("pbs-sheet-").FullName;
        return new PbsMatchReportGenerator()
            .GenerateXlsxReportAsync(dir, PbsRows(), ContractStatuses, Cos).GetAwaiter().GetResult();
    }

    private static string WriteMobilePaySheet()
    {
        var path = Path.Combine(
            Directory.CreateTempSubdirectory("mb-sheet-").FullName, "MobileMatchReport.xlsx");
        PaymentMatchWorkbookWriter.Write(
            path, "MobilePay", PaymentMatchColumns.MobilePay, MobilePayMatchSheet.Build(MobilePayRows()));
        return path;
    }

    private static string[] HeadersOf(string workbookPath)
    {
        using var wb = new XLWorkbook(workbookPath);
        var sheet = wb.Worksheets.First();
        return [.. sheet.Row(1).CellsUsed().Select(c => c.GetString())];
    }

    /// <summary>
    /// The sheet as semicolon-separated text, so a golden diff reads as lines rather than as a
    /// binary blob. Formatting (fills, freeze, autofilter) is deliberately not captured — it is not
    /// what a column change breaks.
    /// </summary>
    private static string SheetAsCsv(string workbookPath)
    {
        using var wb = new XLWorkbook(workbookPath);
        var sheet = wb.Worksheets.First();
        var lastColumn = sheet.Row(1).CellsUsed().Count();
        var lastRow = sheet.LastRowUsed()!.RowNumber();

        var sb = new System.Text.StringBuilder();
        for (var r = 1; r <= lastRow; r++)
        {
            var cells = Enumerable.Range(1, lastColumn).Select(c => Escape(sheet.Cell(r, c).GetString()));
            sb.AppendLine(string.Join(';', cells));
        }
        return sb.ToString();
    }

    // A remark joining two notes contains "; ", which is the delimiter. Unescaped, the golden would
    // show a column boundary where the sheet has none — and a diff on it would point at the wrong
    // column.
    private static string Escape(string value) =>
        value.Contains(';') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
