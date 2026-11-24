using ClosedXML.Excel;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class AborabatWorkbookTests
{
    [Fact]
    public async Task Workbook_uses_link_id_only_for_delete_and_importable_native_dates_for_create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-workbook-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "template.xlsx");
            var outputPath = Path.Combine(directory, "repair.xlsx");
            WriteTemplate(templatePath);
            var oldContractId = Guid.NewGuid();
            var targetContractId = Guid.NewGuid();
            var associationId = Guid.NewGuid();
            var plan = Plan(oldContractId, targetContractId, associationId);

            await AborabatRepair.ExportSettlImportWorkbookAsync(plan, templatePath, outputPath,
                CancellationToken.None);

            using var workbook = new XLWorkbook(outputPath);
            var sheet = workbook.Worksheet("ContractMeteringPoints");
            Assert.Equal(5, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal("Create", sheet.Cell(4, 1).GetString());
            Assert.Equal("V Variabel UA", sheet.Cell(4, 2).GetString());
            Assert.True(sheet.Cell(4, 4).IsEmpty());
            Assert.Equal("MP1", sheet.Cell(4, 5).GetString());
            AssertImportDate(sheet.Cell(4, 6), new(2026, 7, 25));
            AssertImportDate(sheet.Cell(4, 7), new(2026, 8, 31));
            Assert.Equal("Create", sheet.Cell(5, 1).GetString());
            Assert.Equal("V Kvartal UA", sheet.Cell(5, 2).GetString());
            AssertImportDate(sheet.Cell(5, 6), new(2026, 9, 1));
            AssertImportDate(sheet.Cell(5, 7), new(2026, 9, 30));
            Assert.Equal("Delete", sheet.Cell(3, 1).GetString());
            Assert.Equal(associationId.ToString("D"), sheet.Cell(3, 4).GetString());
            Assert.NotEqual(oldContractId.ToString("D"), sheet.Cell(3, 4).GetString());
            Assert.NotEqual(targetContractId.ToString("D"), sheet.Cell(3, 4).GetString());
            Assert.Equal("V AboRabat", sheet.Cell(3, 2).GetString());
            Assert.Equal("MP1", sheet.Cell(3, 5).GetString());
            Assert.True(sheet.Cell(3, 6).IsEmpty());
            Assert.True(sheet.Cell(3, 7).IsEmpty());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_only_repair_exports_one_action_and_verification_row()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-delete-only-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var template = Path.Combine(directory, "template.xlsx");
            var output = Path.Combine(directory, "delete-only.xlsx");
            var verification = Path.Combine(directory, "verification.csv");
            WriteTemplate(template);
            var link = new SettlContractLinkRow("MP1", Guid.NewGuid(), "V AboRabat",
                new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.FromHours(1)),
                DateTimeOffset.MaxValue, Guid.NewGuid());
            var plan = new AborabatRepairPlan(7, "production", "snapshot", DateTimeOffset.UtcNow, 0,
                [new AborabatRepairItem("MP1", link.ContractId, link.AssociationId, [])])
            {
                ReferenceDate = new DateOnly(2026, 8, 31),
                ExistingLinks = [link]
            };

            await AborabatRepair.ExportSettlImportWorkbookAsync(plan, template, output,
                CancellationToken.None);
            await AborabatVerification.WriteAsync(plan, output, verification, CancellationToken.None);

            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet("ContractMeteringPoints");
            Assert.Equal(3, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal("Delete", sheet.Cell(3, 1).GetString());
            Assert.Equal(link.AssociationId!.Value.ToString("D"), sheet.Cell(3, 4).GetString());
            var lines = await File.ReadAllTextAsync(verification);
            Assert.Contains("Rabatlinket slettes uden en ny kontrakt", lines);
            Assert.Contains("Afklaret forløb", lines);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Workbook_groups_by_MP_then_deletes_before_creates_in_start_order()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-order-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var template = Path.Combine(directory, "template.xlsx");
            var output = Path.Combine(directory, "output.xlsx");
            WriteTemplate(template);
            var old1 = Guid.NewGuid();
            var old2 = Guid.NewGuid();
            var replacement = Guid.NewGuid();
            var ua = new AborabatRepairTarget(Guid.NewGuid(), "LadeAbo_Leje_VariabelUA", new(2026, 7, 1), new(2026, 12, 31));
            var normal = new AborabatRepairTarget(Guid.NewGuid(), "LadeAbo_Leje_Variabel", new(2027, 1, 1), DateOnly.MaxValue);
            var plan = new AborabatRepairPlan(6, "production", "snapshot", DateTimeOffset.UtcNow, 0,
                [new("MP2", Guid.NewGuid(), old2, [ua]),
                 new("MP1", Guid.NewGuid(), old1, [normal, ua])
                 {
                     ReplacedLinks = [new("MP1", normal.ContractId, normal.ContractName,
                         DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, replacement)]
                 }]);
            await AborabatRepair.ExportSettlImportWorkbookAsync(plan, template, output, CancellationToken.None);
            using var workbook = new XLWorkbook(output);
            var sheet = workbook.Worksheet(1);
            Assert.Equal("Delete", sheet.Cell(3, 1).GetString());
            Assert.Equal("Delete", sheet.Cell(4, 1).GetString());
            foreach (var row in new[] { 3, 4 })
            {
                Assert.Equal("MP1", sheet.Cell(row, 5).GetString());
                Assert.Equal(sheet.Cell(row, 4).GetString() == old1.ToString("D")
                    ? "V AboRabat" : normal.ContractName, sheet.Cell(row, 2).GetString());
            }
            Assert.Equal(new[] { old1.ToString("D"), replacement.ToString("D") }.Order(),
                new[] { sheet.Cell(3, 4).GetString(), sheet.Cell(4, 4).GetString() }.Order());
            Assert.Equal("MP1", sheet.Cell(5, 5).GetString());
            AssertImportDate(sheet.Cell(5, 6), new(2026, 7, 1));
            Assert.Equal("MP1", sheet.Cell(6, 5).GetString());
            AssertImportDate(sheet.Cell(6, 6), new(2027, 1, 1));
            Assert.True(sheet.Cell(6, 7).IsEmpty());
            AssertImportDate(sheet.Cell(5, 7), new(2026, 12, 31));
            Assert.Equal("Delete", sheet.Cell(7, 1).GetString());
            Assert.Equal(old2.ToString("D"), sheet.Cell(7, 4).GetString());
            Assert.Equal("MP2", sheet.Cell(8, 5).GetString());
            AborabatVerification.VerifyReviewedWorkbook(output, plan);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workbook_refuses_missing_or_empty_link_id(bool empty)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-workbook-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "template.xlsx");
            var outputPath = Path.Combine(directory, "repair.xlsx");
            WriteTemplate(templatePath);
            var plan = Plan(Guid.NewGuid(), Guid.NewGuid(), empty ? Guid.Empty : null);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.ExportSettlImportWorkbookAsync(plan, templatePath, outputPath,
                    CancellationToken.None));

            Assert.Contains("AssociationId", error.Message);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Verification_references_import_cells_and_independent_source_records()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-verification-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var template = Path.Combine(directory, "template.xlsx");
            var workbook = Path.Combine(directory, "draft.xlsx");
            var csv = Path.Combine(directory, "verification.csv");
            WriteTemplate(template);
            var id = Guid.NewGuid();
            var plan = Plan(Guid.NewGuid(), Guid.NewGuid(), id) with
            {
                ExistingLinks = [new("MP1", Guid.NewGuid(), "V AboRabat",
                    new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.FromHours(2)),
                    new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), id)],
                Parts = [new("MP1", "A1", "C1", "G1", "Discount", "V AboRabat",
                    new DateOnly(2026, 7, 25), new DateOnly(2026, 9, 30),
                    "EXUCONTRACTPARTTABLE[C1,G1]", "EXUCONTRACTPARTTABLE[C1,G1].PRODUCTNUM",
                    "EXUCONTRACTPARTTABLE[C1,G1].STARTDATE", "EXUDELPOINT[G1].METERINGPOINT",
                    "2026-07-25",
                    "EXU_CONTRACTPARTLOG[RECID=123].ENDDATE", "2026-09-30")]
            };
            await AborabatRepair.ExportSettlImportWorkbookAsync(plan, template, workbook, default);
            await AborabatVerification.WriteAsync(plan, workbook, csv, default);
            var rows = System.IO.File.ReadAllLines(csv);
            Assert.Contains("\"Excel-række\";\"Excel-kolonne\";\"Excel-værdi\"", rows[0]);
            Assert.Contains(rows, r => r.Contains("\"draft.xlsx\";\"3\";\"AssociationId\";")
                && r.Contains(id.ToString("D")));
            Assert.Contains(rows, r => r.Contains("\"draft.xlsx\";\"4\";\"Start\";\"2026-07-25\"")
                && r.Contains("EXUCONTRACTPARTTABLE[C1,G1]"));
            Assert.Contains(rows, r => r.Contains("\"draft.xlsx\";\"5\";\"End\";\"2026-09-30\"")
                && r.Contains("EXU_CONTRACTPARTLOG[RECID=123].ENDDATE"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("da-DK")]
    public async Task Reviewed_workbook_must_match_the_complete_current_proposal(string culture)
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new(culture);
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-approval-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var template = Path.Combine(directory, "template.xlsx");
            var draft = Path.Combine(directory, "draft.xlsx");
            WriteTemplate(template);
            var plan = Plan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await AborabatRepair.ExportSettlImportWorkbookAsync(plan, template, draft, default);
            AborabatVerification.VerifyReviewedWorkbook(draft, plan);
            using (var book = new XLWorkbook(draft))
            {
                book.Worksheet(1).Cell(4, 2).SetValue("Wrong contract");
                book.Save();
            }
            Assert.Throws<InvalidOperationException>(() =>
                AborabatVerification.VerifyReviewedWorkbook(draft, plan));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("text")]
    [InlineData("display")]
    [InlineData("date")]
    public async Task Reviewed_workbook_rejects_invalid_import_dates(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-dates-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var template = Path.Combine(directory, "template.xlsx");
            var draft = Path.Combine(directory, "draft.xlsx");
            WriteTemplate(template);
            var plan = Plan(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            await AborabatRepair.ExportSettlImportWorkbookAsync(plan, template, draft, default);
            using (var book = new XLWorkbook(draft))
            {
                var start = book.Worksheet(1).Cell(4, 6);
                if (change == "text") start.SetValue("25-07-2026");
                else if (change == "display") start.Style.DateFormat.Format = "dd-mm-yyyy";
                else start.SetValue(new DateTime(2026, 7, 26));
                book.Save();
            }
            Assert.Throws<InvalidOperationException>(() =>
                AborabatVerification.VerifyReviewedWorkbook(draft, plan));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void AssertImportDate(IXLCell cell, DateOnly expected)
    {
        Assert.Equal(XLDataType.DateTime, cell.DataType);
        Assert.Equal(expected.ToDateTime(TimeOnly.MinValue), cell.GetDateTime());
        Assert.Equal("yyyy-mm-dd", cell.Style.DateFormat.Format);
        var displayed = cell.GetFormattedString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected.ToString("yyyy-MM-dd"), displayed);
        Assert.True(DateTime.TryParse(displayed, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var imported));
        Assert.Equal(cell.GetDateTime(), imported);
    }

    private static AborabatRepairPlan Plan(Guid oldContractId, Guid targetContractId,
        Guid? associationId) =>
        new(6, "production", "snapshot", DateTimeOffset.UtcNow, 0,
            [new("MP1", oldContractId, associationId,
                [new(targetContractId, "V Variabel UA", new DateOnly(2026, 7, 25), new DateOnly(2026, 8, 31)),
                 new(Guid.NewGuid(), "V Kvartal UA", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))])]);

    private static void WriteTemplate(string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("ContractMeteringPoints");
        sheet.Cell(1, 1).SetValue("Type");
        sheet.Cell(1, 2).SetValue("ContractMeteringPoint");
        sheet.Cell(1, 3).SetValue("Version");
        sheet.Cell(1, 4).SetValue(2);
        var headers = new[] { "Action", "ContractName", "CountryCode", "AssociationId",
            "MeteringPointIdentification", "Start", "End", "CommodityType" };
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(2, index + 1).SetValue(headers[index]);
        workbook.SaveAs(path);
    }
}
