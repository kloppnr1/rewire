using DataMigration.Migration.Configuration;
using DataMigration.Domain.Customers;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using ClosedXML.Excel;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class CustomerPayerRepairTests
{
    private const string Mp = "571313104402063390";
    private static readonly DateOnly Cos = new(2026, 9, 1);
    private static readonly Guid CustomerId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid PayerId = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid MpId = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public void Scope_accepts_mixed_entries_deduplicates_and_reports_invalid_lines()
    {
        var entries = StatePullScope.Parse([" 456192 ", "", Mp, Mp]);
        Assert.Equal(2, entries.Length);
        Assert.False(entries[0].IsMeteringPoint);
        Assert.True(entries[1].IsMeteringPoint);
        Assert.Contains("line 2", Assert.Throws<ArgumentException>(() => StatePullScope.Parse([Mp, "1234567890123"])).Message);
        Assert.Throws<ArgumentException>(() => StatePullScope.Parse([" "]));
    }

    [Fact]
    public void Missing_entities_produce_create_decisions_using_original_account()
    {
        var d = Assert.Single(Plan(Source(), Settl(), [Mp]));
        Assert.Equal("Ready", d.Status);
        Assert.Equal("456192", d.PayerIdentification);
        Assert.True(d.CreateCustomer && d.CreatePayer && d.CreateCustomerLink && d.CreatePayerLink);
        Assert.Equal(Cos, d.Start);
    }

    [Fact]
    public void Existing_standalone_payer_and_customer_are_reused()
    {
        var settl = Settl() with { Customers = [new(new() { Id = CustomerId, CustomerNumber = "456192", CountryCode = "DK" }, Cos)],
            Payers = [new(new() { Id = PayerId, CustomerId = CustomerId, PayerIdentification = "456192", CountryCode = "DK" }, Cos) { CustomerAtDate = new() { [Cos] = CustomerId } }] };
        var d = Assert.Single(Plan(Source(), settl, [Mp]));
        Assert.Equal("Ready", d.Status);
        Assert.False(d.CreateCustomer || d.CreatePayer);
        Assert.True(d.CreateCustomerLink && d.CreatePayerLink);
    }

    [Theory]
    [InlineData(0, "Ready")]
    [InlineData(1, "Existing payer ownership or period conflicts.")]
    public void Saved_historical_payer_dates_are_checked_during_preparation(int startOffset, string status)
    {
        var settl = Settl() with { Customers = [new(new() { Id = CustomerId, CustomerNumber = "456192", CountryCode = "DK" }, Cos)],
            Payers = [new(new() { Id = PayerId, CustomerId = CustomerId, PayerIdentification = "456192", CountryCode = "DK" }, Cos)
                { CustomerAtDate = new() { [Cos] = CustomerId }, CapturedVersions = new() { [Cos] = new(CustomerId, Cos.AddDays(startOffset), Cos) } }] };
        Assert.Equal(status, Assert.Single(Plan(Source(), settl, [Mp])).Status);
    }

    [Fact]
    public void Conflicting_later_or_finite_links_are_unresolved()
    {
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.NeedsLink([(CustomerId, Cos.AddDays(2), null)], CustomerId, Cos));
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.NeedsLink([(CustomerId, Cos, Cos.AddDays(10))], CustomerId, Cos));
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.NeedsLink([(PayerId, Cos, null)], CustomerId, Cos));
        Assert.False(CustomerPayerRepair.NeedsLink([(CustomerId, Cos, null)], CustomerId, Cos));
    }

    [Fact]
    public void Missing_routing_and_unsupported_invoice_channel_never_create_rows()
    {
        Assert.NotEqual("Ready", Assert.Single(Plan(Source() with { Routes = [] }, Settl(), [Mp])).Status);
        var source = Source();
        source.Customers[0].Payers[0].PayerBilling!.InvoiceChannel = "Email";
        var d = Assert.Single(Plan(source, Settl(), [Mp]));
        Assert.Contains("channel", d.Status);
        Assert.False(d.CreateCustomer || d.CreatePayer || d.CreateCustomerLink || d.CreatePayerLink);
    }

    [Fact]
    public async Task Snapshot_rejects_scope_mixing_and_checksum_changes()
    {
        var root = Temp();
        try
        {
            var scope = new StatePullScope([Mp], [Mp], ["456192"], ["456192"]);
            var store = StateSourceSnapshotStore.Open(root, "production", scope);
            await store.BeginUpdateAsync(default);
            await store.WriteAsync("repair-xellent", new[] { Source() }, default);
            await store.CompleteAsync(default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => StateSourceSnapshotStore.Open(root, "production").BeginUpdateAsync(default));
            await File.AppendAllTextAsync(Path.Combine(root, "repair-xellent.jsonl"), " ");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadSectionAsync<RepairXellentEvidence>("repair-xellent", default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Old_snapshot_writes_refusal_report_without_needing_invoice_section()
    {
        var root = Temp();
        try
        {
            var input = Path.Combine(root, "input");
            var store = StateSourceSnapshotStore.Open(input, "production");
            await store.BeginAsync(default);
            await store.CompleteAsync(default);
            var scope = Path.Combine(root, "scope.txt");
            await File.WriteAllTextAsync(scope, Mp);
            var output = Path.Combine(root, "output");
            Assert.Equal(1, await CustomerPayerRepair.PrepareAsync(input, "production", scope, "unused", output, default));
            Assert.Empty(Directory.GetFiles(output, "*.xlsx"));
            Assert.Contains("include-sensitive-data", await File.ReadAllTextAsync(Path.Combine(output, "report.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task End_to_end_workbooks_preserve_metadata_comments_text_identifiers_and_native_dates()
    {
        var root = Temp();
        try
        {
            var input = Path.Combine(root, "input");
            var store = StateSourceSnapshotStore.Open(input, "production");
            await store.BeginAsync(default);
            const string sibling = "571313104402063391";
            var source = Source(); var settl = Settl(); var siblingId = Guid.NewGuid();
            source.Customers[0].Customer.CustomerNumber = "912345";
            source.Customers[0].Payers[0].PayerInformation!.PayerIdentifier = "912345";
            source.Customers[0] = source.Customers[0] with { SourceAccount = "12345" };
            source.Routes[0] = source.Routes[0] with { SourceAccount = "12345", CustomerNumber = "912345", PayerIdentification = "912345" };
            source.Routes.Add(source.Routes[0] with { MeteringPoint = sibling });
            source.ExpectedContracts!.Add(source.ExpectedContracts[0] with { MeteringPoint = sibling, End = new DateOnly(2027, 10, 31) });
            settl.MeteringPoints.Add(settl.MeteringPoints[0] with { Identification = sibling, Id = siblingId });
            settl.Histories.Add(settl.Histories[0] with { MeteringPoint = sibling, Id = siblingId });
            await store.WriteAsync("repair-xellent", new[] { source }, default);
            await store.WriteAsync("repair-settl", new[] { settl }, default);
            await store.WriteAsync("settl-contract-links", Array.Empty<SettlContractLinkRow>(), default);
            await store.CompleteAsync(default);
            var scope = Path.Combine(root, "scope.txt");
            await File.WriteAllLinesAsync(scope, [Mp, sibling]);
            var templates = Environment.GetEnvironmentVariable("REPAIR_TEST_TEMPLATE_DIR") ?? Path.Combine(root, "templates");
            if (!Directory.Exists(templates))
            {
                Directory.CreateDirectory(templates);
                Template(templates, "Customers", "Customer", 4, CustomerPayerRepair.CustomerHeaders);
                Template(templates, "Payers", "Payer", 6, CustomerPayerRepair.PayerHeaders);
                Template(templates, "CustomerMeteringPoints", "CustomerMeteringPoint", 4, CustomerPayerRepair.CustomerLinkHeaders);
                Template(templates, "PayerMeteringPoints", "PayerMeteringPoint", 3, CustomerPayerRepair.PayerLinkHeaders);
                Template(templates, "ContractMeteringPoints", "ContractMeteringPoint", 2, CustomerPayerRepair.ContractHeaders);
            }
            var output = Environment.GetEnvironmentVariable("REPAIR_TEST_OUTPUT_DIR") ?? Path.Combine(root, "output");
            var exit = await CustomerPayerRepair.PrepareAsync(input, "production", scope, templates, output, default);
            Assert.True(exit == 0, await File.ReadAllTextAsync(Path.Combine(output, "report.json")));
            Assert.Equal(5, Directory.GetFiles(output, "*.xlsx").Length);
            var csv = await File.ReadAllTextAsync(Path.Combine(output, "verification.csv"));
            Assert.Contains("5-ContractMeteringPoints.xlsx", csv);
            Assert.Contains("Xellent-kontraktdele og Settl-kontrakter", csv);
            Assert.DoesNotContain("jsonl", csv);
            using var links = new XLWorkbook(Path.Combine(output, "3-CustomerMeteringPoints.xlsx"));
            var sheet = links.Worksheet(1);
            Assert.Equal("MeteringPointIdentification", sheet.Cell(1, 6).GetString());
            Assert.Equal(Mp, sheet.Cell(3, 5).GetString());
            Assert.Equal(XLDataType.Text, sheet.Cell(3, 5).DataType);
            Assert.Equal(XLDataType.DateTime, sheet.Cell(3, 7).DataType);
            Assert.Equal("yyyy-mm-dd", sheet.Cell(3, 7).Style.DateFormat.Format);
            Assert.True(DateTime.TryParse(sheet.Cell(3, 7).GetFormattedString(System.Globalization.CultureInfo.InvariantCulture),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var importedDate));
            Assert.Equal(sheet.Cell(3, 7).GetDateTime(), importedDate);
            Assert.True(sheet.Cell(3, 9).IsEmpty());
            using var customer = new XLWorkbook(Path.Combine(output, "1-Customers.xlsx"));
            Assert.Equal("912345", customer.Worksheet(1).Cell(3, 2).GetString());
            Assert.True(customer.Worksheet(1).Cell(2, 1).HasComment);
            Assert.Equal(3, customer.Worksheet(1).LastRowUsed()!.RowNumber());
            Assert.Equal(4, sheet.LastRowUsed()!.RowNumber());
            Assert.Contains(sibling, csv);
            var verification = ReadCsv(Path.Combine(output, "verification.csv"));
            Assert.Equal(AborabatVerification.Headers, verification[0].Keys);
            Assert.Equal(new byte[] { 239, 187, 191 }, (await File.ReadAllBytesAsync(Path.Combine(output, "verification.csv"))).Take(3));
            foreach (var file in Directory.GetFiles(output, "*.xlsx"))
            {
                using var actual = new XLWorkbook(file);
                var actualSheet = actual.Worksheet(1);
                foreach (var actualRow in actualSheet.RowsUsed().Where(r => r.RowNumber() >= 3))
                foreach (var cell in actualRow.CellsUsed().Where(c => !c.IsEmpty()))
                {
                    var references = verification.Where(v => v["Importfil"] == Path.GetFileName(file)
                        && v["Excel-række"] == actualRow.RowNumber().ToString()
                        && v["Excel-kolonne"] == actualSheet.Cell(2, cell.Address.ColumnNumber).GetString()).ToArray();
                    if (actualSheet.Cell(2, cell.Address.ColumnNumber).GetString() is not ("CustomerNumber" or "PayerIdentification" or "ContractName" or "Date" or "Start" or "End"))
                    { Assert.Empty(references); continue; }
                    Assert.NotEmpty(references);
                    var value = cell.DataType == XLDataType.DateTime ? cell.GetDateTime().ToString("yyyy-MM-dd") : cell.GetString();
                    Assert.All(references, v => { Assert.Equal(value, v["Excel-værdi"]); Assert.NotEmpty(v["Kildepost og felt"]); Assert.NotEmpty(v["Forklaring"]); });
                }
            }
            Assert.Equal(2, verification.Count(v => v["Importfil"] == "1-Customers.xlsx" && v["Excel-kolonne"] == "CustomerNumber"));
            Assert.Equal(2, Directory.GetFiles(output, "*.csv").Length);
            Assert.Equal(2, ReadCsv(Path.Combine(output, "contract-evidence.csv")).Length);
            // The same prepared fields remain reviewable when another MP holds the package.
            const string blocked = "571313104402063399";
            await File.WriteAllLinesAsync(scope, [Mp, sibling, blocked]);
            var heldOutput = Path.Combine(root, "held");
            Assert.Equal(1, await CustomerPayerRepair.PrepareAsync(input, "production", scope, templates, heldOutput, default));
            Assert.Empty(Directory.GetFiles(heldOutput, "*.xlsx"));
            Assert.Empty(Directory.GetFiles(heldOutput, "*.pending"));
            var held = ReadCsv(Path.Combine(heldOutput, "verification.csv"));
            var fields = held.Where(v => v["Excel-kolonne"] != "").ToArray();
            var releasedFields = verification.Where(v => v["Excel-kolonne"] != "").ToArray();
            Assert.Equal(releasedFields.Length, fields.Length);
            for (var i = 0; i < fields.Length; i++)
            {
                foreach (var key in new[] { "Målepunkt", "Excel-kolonne", "Excel-værdi", "Kildepost og felt", "Kildeværdi", "Forklaring" })
                    Assert.Equal(releasedFields[i][key], fields[i][key]);
                Assert.Equal(RepairVerification.Held, fields[i]["Resultat"]);
                Assert.Empty(fields[i]["Importfil"]); Assert.Empty(fields[i]["Excel-række"]);
            }
            Assert.All(fields.Where(v => v["Excel-kolonne"] is "CustomerNumber" or "PayerIdentification"), v =>
            {
                Assert.Equal("912345", v["Excel-værdi"]); Assert.Equal("12345", v["Kildeværdi"]);
                Assert.Contains("Tilføj 9", v["Forklaring"]);
            });
            Assert.Equal("Uafklaret", Assert.Single(held, v => v["Målepunkt"] == blocked)["Resultat"]);
            var templateHashes = Directory.GetFiles(templates, "*.xlsx").ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
            var draftOutput = Path.Combine(root, "draft-output");
            Assert.Equal(1, await CustomerPayerRepair.PrepareAsync(input, "production", scope, templates, draftOutput, default, draftImports: true));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(heldOutput, "report.json")), await File.ReadAllTextAsync(Path.Combine(draftOutput, "report.json")));
            Assert.Empty(Directory.GetFiles(draftOutput, "*.xlsx"));
            var draftFiles = Directory.GetFiles(Path.Combine(draftOutput, "draft-imports"), "*.xlsx");
            Assert.Equal(5, draftFiles.Length);
            foreach (var draft in draftFiles)
            {
                Assert.StartsWith("DRAFT-NOT-FOR-IMPORT-", Path.GetFileName(draft));
                using var draftBook = new XLWorkbook(draft);
                using var releasedBook = new XLWorkbook(Path.Combine(output, Path.GetFileName(draft)["DRAFT-NOT-FOR-IMPORT-".Length..]));
                var draftSheet = draftBook.Worksheet(1); var releasedSheet = releasedBook.Worksheet(1);
                Assert.Equal(releasedSheet.LastRowUsed()!.RowNumber(), draftSheet.LastRowUsed()!.RowNumber());
                foreach (var cell in releasedSheet.CellsUsed())
                {
                    var actual = draftSheet.Cell(cell.Address.RowNumber, cell.Address.ColumnNumber);
                    Assert.Equal(cell.Value, actual.Value); Assert.Equal(cell.DataType, actual.DataType);
                }
                Assert.DoesNotContain(draftSheet.CellsUsed(), c => c.GetString() == blocked);
            }
            foreach (var (file, hash) in templateHashes)
                Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Danish_midnight_supplier_change_is_compared_in_Danish_time(int day)
    {
        var settl = Settl();
        var h = settl.Histories[0];
        var expected = new DateOnly(2026, 9, day);
        var utc = new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero).AddHours(-2);
        h.Statuses!.DeliveryStatuses![0].Period!.Start = utc;
        h.CurrentOrNextDeliveryPeriod!.Start = utc;
        settl.Histories[0] = h with { FirstDate = expected };
        var decision = Assert.Single(Plan(Source(), settl, [Mp]));
        Assert.Equal("Ready", decision.Status);
        Assert.Equal(expected, decision.Start);
    }

    [Fact]
    public void Merged_account_scope_does_not_include_representative_siblings()
    {
        var source = Source() with { SourceAccountPoints = new() { ["512627"] = [Mp], ["380711"] = ["571313104402063437"] } };
        Assert.Equal(new[] { Mp }, CustomerPayerRepair.ResolveTargets(StatePullScope.Parse(["512627"]), null, source));
    }

    [Fact]
    public void Supplier_change_date_is_taken_from_each_point_including_September_fifth()
    {
        var settl = Settl();
        var h = settl.Histories[0];
        h.Statuses!.DeliveryStatuses![0].Period!.Start = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        h.CurrentOrNextDeliveryPeriod!.Start = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        settl.Histories[0] = h with { FirstDate = new DateOnly(2026, 9, 5) };
        Assert.Equal(new DateOnly(2026, 9, 5), Assert.Single(Plan(Source(), settl, [Mp])).Start);
    }

    [Fact]
    public void Same_scope_request_with_changed_resolution_or_reference_is_incompatible()
    {
        var original = new StatePullScope([Mp], [Mp], ["456192"], ["456192"]) { ReferenceDate = Cos };
        Assert.False(original.SameResolution(original with { ReferenceDate = Cos.AddDays(1) }));
        Assert.False(original.SameResolution(original with { SupportingAccounts = ["456192", "380711"] }));
        Assert.False(original.SameResolution(original with { CustomerMeteringPoints = new() { ["456192"] = [Mp] } }));
    }

    [Fact]
    public void Historical_closed_charger_and_electricity_ends_are_restored_without_blanket_open_periods()
    {
        var reference = new DateOnly(2026, 8, 31);
        var start = new DateTime(2026, 9, 1);
        var closed = new DateTime(2026, 9, 4);
        var finite = new DateTime(2027, 3, 31);
        var electricity = new XellentContractPartRow(Mp, "524744", "EL", "AG", "V AboRabat", start, closed, "Live", null)
            { CompanyId = "hol", AmrCode = "RATE" };
        var electricityLog = electricity with { Source = "Log", End = finite, ChangeType = "Slutdato er ændret", LoggedAt = new DateTime(2026, 9, 4, 22, 20, 25), LogRecordId = 1 };
        var charger = new XellentChargeSubscriptionRow("524744", "CHARGE", "OAboEje0", start, closed, "Live", null)
            { AgreementNumber = "AG", CompanyId = "hol", AmrCode = "RATE" };
        var chargerLog = charger with { Source = "Log", End = new DateTime(1900, 1, 1), ChangeType = "Slutdato er ændret", LoggedAt = new DateTime(2026, 9, 4, 12, 55, 0), LogRecordId = 2, LogEmployeeId = "Xellent" };
        var result = AborabatRepair.RestoreCustomerPayerSources([electricity], [electricityLog], [charger, chargerLog], reference);
        Assert.Equal(finite, Assert.Single(result.Electricity).End);
        Assert.Equal(new DateTime(1900, 1, 1), Assert.Single(result.Chargers).End);
        Assert.Empty(result.AmbiguousPoints); Assert.Empty(result.AmbiguousChargerAccounts);
        var unchanged = AborabatRepair.RestoreCustomerPayerSources([electricity], [], [charger], reference);
        Assert.Equal(closed, Assert.Single(unchanged.Electricity).End);
        Assert.Equal(closed, Assert.Single(unchanged.Chargers).End);
    }
    [Fact]
    public void Scoped_contract_result_retains_unscoped_charger_sibling_context()
    {
        const string chargerMp = "571313104402063399";
        var start = new DateTime(2026, 8, 1); var end = new DateTime(1900, 1, 1);
        var options = new CreateOptions { ReferenceDate = start, DeliveryCategory = "El-ekstern",
            ElectricityChargerContractMap = new() { ["V LadVariP"] = new() {
                ["OAboEje0"] = "Charger contract", [ElectricityChargerContractRules.NoChargeKey] = "Pending charger" } } };
        var mapping = new Dictionary<string, List<(string, DateTime, DateTime, DateTime, bool, string, string, string, string)>>
        { ["456192"] = [(chargerMp, start, start, end, false, "C1", "V LadVariP", "", ""), (Mp, start, start, end, false, "C2", "V LadVariP", "", "")] };
        var chargers = new Dictionary<string, IReadOnlyList<EvCharger>> { [chargerMp] = [new("INST", "456192", "CHARGE", null,
            [new("Charge_Abo", EvChargerPartKind.Subscription, "OAboEje0", DateOnly.FromDateTime(start), null)])] };
        var result = CustomerPayerRepairCapture.ResolveAccountContractContext(options, [], mapping,
            new Dictionary<string, CustomerIdentity>(), chargers, new Dictionary<string, IReadOnlyList<EvCharger>>());
        var scoped = Assert.Single(result.Links, l => l.MeteringPointId == Mp);
        Assert.Equal("V LadVariP", scoped.ContractName);
        Assert.Equal("Charger contract", Assert.Single(result.Links, l => l.MeteringPointId == chargerMp).ContractName);
    }
    [Fact]
    public async Task Held_and_unresolved_reports_have_no_cell_references_and_preserve_original_padding()
    {
        var root = Temp();
        try
        {
            var source = Source();
            source.Customers[0].Customer.CustomerNumber = "912345";
            source.Customers[0].Payers[0].PayerInformation!.PayerIdentifier = "912345";
            source.Customers[0] = source.Customers[0] with { SourceAccount = "12345" };
            source.Routes[0] = source.Routes[0] with { SourceAccount = "12345", CustomerNumber = "912345", PayerIdentification = "912345" };
            var decision = Assert.Single(Plan(source, Settl(), [Mp]));
            await RepairVerification.WriteAsync(root, [decision], source, Settl(), false, default);
            var row = Assert.Single(ReadCsv(Path.Combine(root, "verification.csv")));
            Assert.Equal("Afklaret – afventer frigivelse", row["Resultat"]);
            Assert.All(new[] { "Importfil", "Excel-række", "Excel-kolonne", "Excel-værdi" }, key => Assert.Empty(row[key]));
            Assert.Equal("12345", row["Xellent-kundenummer"]);
            Assert.Contains("Kunde 912345", row["Kildeværdi"]); Assert.Contains("betaler 912345", row["Kildeværdi"]);
            const string reason = "Uafklaret; siger \"nej\"\r\nÆØÅ";
            await RepairVerification.WriteAsync(root, [decision with { Status = reason }], source, Settl(), false, default);
            var unresolved = Assert.Single(ReadCsv(Path.Combine(root, "verification.csv")));
            Assert.Equal("Uafklaret", unresolved["Resultat"]); Assert.Equal(reason, unresolved["Forklaring"]);
            Assert.Empty(Directory.GetFiles(root, "*.xlsx"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Contract_evidence_uses_effective_end_exact_log_and_noncounting_other_owner()
    {
        var root = Temp();
        try
        {
            var source = Source(); var settl = Settl();
            var original = new DateTime(2025, 9, 1); var finite = new DateTime(2027, 3, 31);
            var live = new XellentContractPartRow(Mp, "456192", "TEST-CONTRACT", "AG", "TEST-PRODUCT", original, new DateTime(2026, 8, 31), "Live", null) { CompanyId = "hol", AmrCode = "RATE" };
            var log = live with { Source = "Log", End = finite, LoggedAt = new DateTime(2026, 8, 31, 9, 20, 0), ChangeType = "Slutdato er ændret", LogRecordId = 123 };
            var other = live with { Account = "999999", ContractNumber = "OTHER", ProductNumber = "OTHER-PRODUCT", End = new DateTime(1900, 1, 1) };
            var charger = new XellentChargeSubscriptionRow("456192", "CHARGE", "OAboEje0", original, new DateTime(2026, 9, 4), "Live", null) { CompanyId = "hol", AgreementNumber = "AG", AmrCode = "RATE" };
            var chargerLog = charger with { Source = "Log", End = new DateTime(1900, 1, 1), LoggedAt = new DateTime(2026, 9, 4, 12, 55, 0), ChangeType = "Slutdato er ændret", LogRecordId = 456, LogEmployeeId = "Xellent" };
            var snapshot = new ContractSourceSnapshot([live, other], [log], []) { ChargeSubscriptions = [charger, chargerLog], HasChargerAssignments = true,
                ChargerAssignments = [new(Mp, new("INST", "456192", "CHARGE", "SYNTHETIC ADDRESS", []), Cos.AddDays(-1))] };
            snapshot.ChargeSubscriptions.Add(charger with { ContractNumber = "OTHER-CHARGER", ProductNumber = "OTHER-CHARGER-PRODUCT" });
            snapshot.ChargerAssignments.Add(new("571313104402063399", new("OTHER-INST", "456192", "OTHER-CHARGER", "OTHER SYNTHETIC ADDRESS", []), Cos.AddDays(-1)));
            var d = Assert.Single(Plan(source, settl, [Mp])) with { Contracts = [new("Test contract", Cos, DateOnly.FromDateTime(finite), true, "Source evidence")] };
            await RepairVerification.WriteAsync(root, [d], source, settl, false, default, snapshot);
            var rows = ReadCsv(Path.Combine(root, "contract-evidence.csv"));
            Assert.DoesNotContain(rows, r => r["Produkt"] == "OTHER-CHARGER-PRODUCT");
            var electric = Assert.Single(rows, r => r["Produkt"] == "TEST-PRODUCT");
            Assert.Equal("31-03-2027", electric["Slutdato"]); Assert.Equal("31-03-2027", electric["Slutdato brugt i beregning"]);
            Assert.Contains("31-08-2026 09:20:00", electric["Xellent-grundlag for slutdato"]);
            Assert.Contains("31-03-2027", electric["Xellent-grundlag for slutdato"]);
            Assert.Contains("Nuværende slutdato 31-08-2026 bruges ikke", electric["Xellent-grundlag for slutdato"]);
            Assert.DoesNotContain("RECID", electric["Xellent-grundlag for slutdato"]);
            Assert.DoesNotContain("ikke anvendt", Assert.Single(rows, r => r["Produkt"] == "OTHER-PRODUCT")["Xellent-grundlag for slutdato"]);
            Assert.Contains("01-09-2026 – 31-03-2027", electric["Settl-kontrakter og perioder"]);
            Assert.Equal("Nej", Assert.Single(rows, r => r["Produkt"] == "OTHER-PRODUCT")["Tæller som kontraktdel"]);
            var charge = Assert.Single(rows, r => r["Deltype"] == "Laderabonnement");
            Assert.Equal("Åben", charge["Slutdato"]); Assert.Contains("04-09-2026 12:55:00", charge["Xellent-grundlag for slutdato"]);
            Assert.DoesNotContain("RECID", charge["Xellent-grundlag for slutdato"]);
            Assert.Equal("SYNTHETIC ADDRESS", charge["Laderadresse i Xellent"]);
            await RepairVerification.WriteAsync(root, [d with { Status = "Missing catalog" }], source, settl, false, default, snapshot);
            Assert.All(ReadCsv(Path.Combine(root, "contract-evidence.csv")), r => Assert.StartsWith("Forslag:", r["Settl-kontrakter og perioder"]));
            source.ContractMappingErrors[Mp] = "Unresolved charger linkage";
            await RepairVerification.WriteAsync(root, [d with { Status = "Unresolved charger linkage" }], source, settl, false, default, snapshot);
            var unrelated = Assert.Single(ReadCsv(Path.Combine(root, "contract-evidence.csv")), r => r["Produkt"] == "OTHER-CHARGER-PRODUCT");
            Assert.Equal("Nej", unrelated["Tæller som kontraktdel"]);
            Assert.Contains("ikke dokumenteret tilknyttet", unrelated["Grundlag for kontraktvalg"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Retained_records_have_no_cell_references_and_empty_import_sheets()
    {
        var root = Temp();
        try
        {
            var source = Source(); var settl = Settl() with
            {
                Customers = [new(new() { Id = CustomerId, CustomerNumber = "456192", CountryCode = "DK" }, Cos)],
                Payers = [new(new() { Id = PayerId, CustomerId = CustomerId, PayerIdentification = "456192", CountryCode = "DK" }, Cos) { CustomerAtDate = new() { [Cos] = CustomerId } }],
                CustomerLinks = [new(CustomerId, new() { MeteringPointId = MpId, Start = Cos })],
                PayerLinks = [new(PayerId, new() { MeteringPointId = MpId, Start = Cos })]
            };
            var store = StateSourceSnapshotStore.Open(Path.Combine(root, "input"), "production");
            await store.BeginAsync(default);
            await store.WriteAsync("repair-xellent", new[] { source }, default);
            await store.WriteAsync("repair-settl", new[] { settl }, default);
            await store.WriteAsync("settl-contract-links", new[] { new SettlContractLinkRow(Mp, settl.ContractCatalog![0].Id, "Test contract", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue, Guid.NewGuid()) }, default);
            await store.CompleteAsync(default);
            var templates = Path.Combine(root, "templates"); Directory.CreateDirectory(templates);
            Template(templates, "Customers", "Customer", 4, CustomerPayerRepair.CustomerHeaders);
            Template(templates, "Payers", "Payer", 6, CustomerPayerRepair.PayerHeaders);
            Template(templates, "CustomerMeteringPoints", "CustomerMeteringPoint", 4, CustomerPayerRepair.CustomerLinkHeaders);
            Template(templates, "PayerMeteringPoints", "PayerMeteringPoint", 3, CustomerPayerRepair.PayerLinkHeaders);
            Template(templates, "ContractMeteringPoints", "ContractMeteringPoint", 2, CustomerPayerRepair.ContractHeaders);
            var scope = Path.Combine(root, "scope.txt"); await File.WriteAllTextAsync(scope, Mp);
            var output = Path.Combine(root, "output");
            Assert.Equal(0, await CustomerPayerRepair.PrepareAsync(Path.Combine(root, "input"), "production", scope, templates, output, default));
            var row = Assert.Single(ReadCsv(Path.Combine(output, "verification.csv")));
            Assert.Equal("Afklaret", row["Resultat"]); Assert.Contains("Behold", row["Forklaring"]);
            Assert.Contains("Kunde 456192: findes", row["Kildeværdi"]);
            Assert.Contains("betaler 456192: findes", row["Kildeværdi"]);
            Assert.DoesNotContain(CustomerId.ToString(), row["Kildepost og felt"]);

        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("Expected contract is absent or ambiguous in Settl catalog: V LadVariP", "Kontrakt mangler eller er tvetydig i Settl: V LadVariP.")]
    [InlineData("MP 571313174113291382 (account 524744): V AboRabat has no unique UA contract at 05-09-2026.", "V AboRabat: entydig UA-kontrakt mangler.")]
    public async Task Reports_translate_known_blockers_without_changing_decisions(string status, string explanation)
    {
        var root = Temp();
        try
        {
            var decision = Assert.Single(Plan(Source(), Settl(), [Mp])) with { Status = status };
            await RepairVerification.WriteAsync(root, [decision], Source(), Settl(), false, default);
            Assert.Equal(status, decision.Status);
            Assert.Contains(explanation, Assert.Single(ReadCsv(Path.Combine(root, "verification.csv")))["Forklaring"]);
            Assert.Contains(explanation, Assert.Single(ReadCsv(Path.Combine(root, "contract-evidence.csv")))["Hvorfor uafklaret"]);
        }
        finally { Directory.Delete(root, true); }
    }

    private static Dictionary<string, string>[] ReadCsv(string path)
    {
        using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(path, System.Text.Encoding.UTF8);
        parser.TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited;
        parser.SetDelimiters(";"); parser.HasFieldsEnclosedInQuotes = true; parser.TrimWhiteSpace = false;
        var headers = parser.ReadFields()!; var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData) { var values = parser.ReadFields()!; Assert.Equal(headers.Length, values.Length); rows.Add(headers.Zip(values).ToDictionary(x => x.First, x => x.Second)); }
        return rows.ToArray();
    }
    private static List<CustomerPayerRepairDecision> Plan(RepairXellentEvidence source, RepairSettlEvidence settl, IEnumerable<string> targets)
        => CustomerPayerRepair.Plan(source, settl, targets, []);

    [Fact]
    public void Different_payer_and_representative_are_unresolved()
    {
        var source = Source();
        source.Routes[0] = source.Routes[0] with { PayerIdentification = "456192-2" };
        Assert.Contains("differs from original account", Assert.Single(Plan(source, Settl(), [Mp])).Status);
        source = Source();
        source.Routes[0] = source.Routes[0] with { SourceAccount = "512627" };
        Assert.Contains("differs from original account", Assert.Single(Plan(source, Settl(), [Mp])).Status);
        Assert.Contains("Original-account evidence missing", Assert.Single(Plan(Source() with { OriginalAccountsPreserved = false }, Settl(), [Mp])).Status);
    }

    [Fact]
    public void Five_digit_account_is_padded_once_for_both_identifiers()
    {
        var source = Source();
        source.Customers[0].Customer.CustomerNumber = "912345";
        source.Customers[0].Payers[0].PayerInformation!.PayerIdentifier = "912345";
        source.Customers[0] = source.Customers[0] with { SourceAccount = "12345" };
        source.Routes[0] = source.Routes[0] with { SourceAccount = "12345", CustomerNumber = "912345", PayerIdentification = "912345" };
        var d = Assert.Single(Plan(source, Settl(), [Mp]));
        Assert.Equal("Ready", d.Status);
        Assert.Equal("912345", d.CustomerNumber);
        Assert.Equal(d.CustomerNumber, d.PayerIdentification);
        Assert.Equal("912345", CustomerPayerRepair.PadOriginalAccount("912345"));
    }

    [Fact]
    public void Contract_links_are_missing_only_and_conflicts_block()
    {
        var source = Source(); var settl = Settl();
        var contract = settl.ContractCatalog![0];
        var link = new SettlContractLinkRow(Mp, contract.Id, contract.ContractName!,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue, Guid.NewGuid());
        Assert.True(Assert.Single(CustomerPayerRepair.PlanContracts(source, settl, [], Mp, Cos)).Create);
        Assert.False(Assert.Single(CustomerPayerRepair.PlanContracts(source, settl, [link], Mp, Cos)).Create);
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.PlanContracts(source, settl, [link with { End = link.Start.AddDays(10) }], Mp, Cos));
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.PlanContracts(source, settl with { ContractCatalog = [] }, [], Mp, Cos));
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.PlanContracts(source, settl, [link with { ContractId = Guid.NewGuid(), ContractName = "Unexpected" }], Mp, Cos));
    }

    [Fact]
    public void Settl_exclusive_contract_end_is_compared_with_inclusive_migration_end()
    {
        var source = Source(); var settl = Settl();
        source = source with { ExpectedContracts = source.ExpectedContracts!.Select(c => c with { End = new DateOnly(2026, 10, 31) }).ToList() };
        var contract = settl.ContractCatalog![0];
        var old = new SettlContractLinkRow(Mp, Guid.NewGuid(), "Old contract",
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), Guid.NewGuid());
        var current = new SettlContractLinkRow(Mp, contract.Id, contract.ContractName!,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(1)), Guid.NewGuid());
        Assert.False(Assert.Single(CustomerPayerRepair.PlanContracts(source, settl, [old, current], Mp, Cos)).Create);
        Assert.True(Assert.Single(CustomerPayerRepair.PlanContracts(source, settl, [old], Mp, Cos)).Create);
    }

    [Fact]
    public void Migration_dates_and_accounts_are_used_without_supplier_change_history_or_xellent_routing()
    {
        var first = Cos.AddYears(-1);
        var source = Source() with { MigrationModelVersion = 2, Routes = [],
            MigrationRelationships = new() { [Mp] = new("456192", "456192", "456192", first, first, first) } };
        var settl = Settl() with { Histories = [] };
        var result = Assert.Single(CustomerPayerRepair.Plan(source, settl, [Mp], []));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(first, result.Start);
        Assert.Equal(first, result.CustomerStart);
        Assert.Equal(first, result.PayerStart);
        Assert.Equal(first, Assert.Single(result.Contracts).Start);
        Assert.True(result.CreateCustomerLink && result.CreatePayerLink);
    }

    [Fact]
    public void Partial_existing_period_does_not_move_original_migration_contract_start()
    {
        var source = Source() with { MigrationModelVersion = 2 };
        var settl = Settl(); var contract = settl.ContractCatalog![0];
        var link = new SettlContractLinkRow(Mp, contract.Id, contract.ContractName!,
            new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.PlanContracts(source, settl, [link], Mp, Cos));
        Assert.Equal(Cos.AddYears(-1), Assert.Single(source.ExpectedContracts!).Start);
        Assert.Equal(Cos.AddYears(-1), Assert.Single(CustomerPayerRepair.PlanContracts(source, settl, [], Mp, Cos)).Start);
    }

    [Fact]
    public void Continuous_existing_periods_cover_original_migration_period_without_new_rows()
    {
        var source = Source() with { MigrationModelVersion = 2 };
        var settl = Settl(); var contract = settl.ContractCatalog![0];
        var old = new SettlContractLinkRow(Mp, contract.Id, contract.ContractName!,
            new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), Guid.NewGuid());
        var current = old with { Start = old.End, End = DateTimeOffset.MaxValue, AssociationId = Guid.NewGuid() };
        var decision = Assert.Single(CustomerPayerRepair.PlanContracts(source, settl, [old, current], Mp, Cos));
        Assert.False(decision.Create);
        Assert.Equal(Cos.AddYears(-1), decision.Start);
        Assert.False(CustomerPayerRepair.NeedsLink([(CustomerId, Cos.AddYears(-1), Cos), (CustomerId, Cos, null)], CustomerId, Cos.AddYears(-1)));
        Assert.Throws<InvalidOperationException>(() => CustomerPayerRepair.NeedsLink([(CustomerId, Cos.AddYears(-1), Cos), (CustomerId, Cos.AddDays(1), null)], CustomerId, Cos.AddYears(-1)));
    }

    [Fact]
    public async Task Unresolved_scope_emits_verification_and_no_workbooks()
    {
        var root = Temp();
        try
        {
            var store = StateSourceSnapshotStore.Open(Path.Combine(root, "input"), "production");
            await store.BeginAsync(default);
            var source = Source() with { ContractMappingErrors = new() { [Mp] = "Unproven product period" } };
            await store.WriteAsync("repair-xellent", new[] { source }, default);
            await store.WriteAsync("repair-settl", new[] { Settl() }, default);
            await store.WriteAsync("settl-contract-links", Array.Empty<SettlContractLinkRow>(), default);
            await store.CompleteAsync(default);
            var scope = Path.Combine(root, "scope.txt"); await File.WriteAllTextAsync(scope, Mp);
            var output = Path.Combine(root, "output");
            Assert.Equal(1, await CustomerPayerRepair.PrepareAsync(Path.Combine(root, "input"), "production", scope, "unused", output, default));
            Assert.Empty(Directory.GetFiles(output, "*.xlsx"));
            var csv = await File.ReadAllTextAsync(Path.Combine(output, "verification.csv"));
            Assert.Contains(Mp, csv); Assert.Contains("Unproven product period", csv);
            Assert.DoesNotContain("!A3", csv);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void Full_model_import_keeps_all_rows_and_original_dates_despite_existing_partial_periods()
    {
        var first = Cos.AddYears(-1);
        var source = Source() with { MigrationModelVersion = 2, IncludeAllMigrationRecords = true,
            MigrationRelationships = new() { [Mp] = new("456192", "456192", "456192", first, first, first) } };
        var settl = Settl(); var contract = settl.ContractCatalog![0];
        var link = new SettlContractLinkRow(Mp, contract.Id, contract.ContractName!,
            new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)), Guid.NewGuid());
        var decision = Assert.Single(CustomerPayerRepair.Plan(source, settl, [Mp], [link]));
        Assert.Equal("Ready", decision.Status);
        Assert.True(decision.CreateCustomer && decision.CreatePayer && decision.CreateCustomerLink && decision.CreatePayerLink);
        Assert.True(Assert.Single(decision.Contracts).Create);
        Assert.Equal(first, decision.Contracts[0].Start);
        Assert.Contains("Existing contract periods overlap partially or conflict; period review required.", decision.Warnings);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Delivery_history_option_writes_link_ends_and_holds_missing_history(bool enabled, bool missing)
    {
        var root = Temp();
        try
        {
            var source = Source() with { MigrationModelVersion = 2, IncludeAllMigrationRecords = true,
                MigrationRelationships = new() { [Mp] = new("456192", "456192", "456192", Cos, Cos, Cos) } };
            var settl = Settl();
            var moveIn = Cos.AddMonths(1);
            settl = settl with { Histories = missing ? [] : [settl.Histories[0] with
                { Statuses = new() { DeliveryStatuses = [
                    new() { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier,
                        Period = new() { Start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(2)),
                            End = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)) } },
                    new() { InDelivery = true, StartReason = DeliveryStartReason.MoveIn,
                        Period = new() { Start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)), End = DateTimeOffset.MaxValue } }
                ] } }] };
            var input = Path.Combine(root, "input");
            var store = StateSourceSnapshotStore.Open(input, "production");
            await store.BeginAsync(default);
            await store.WriteAsync("repair-xellent", new[] { source }, default);
            await store.WriteAsync("repair-settl", new[] { settl }, default);
            await store.WriteAsync("settl-contract-links", Array.Empty<SettlContractLinkRow>(), default);
            await store.CompleteAsync(default);
            var scope = Path.Combine(root, "scope.txt"); await File.WriteAllLinesAsync(scope, [Mp]);
            var templates = Path.Combine(root, "templates"); Directory.CreateDirectory(templates);
            Template(templates, "Customers", "Customer", 4, CustomerPayerRepair.CustomerHeaders);
            Template(templates, "Payers", "Payer", 6, CustomerPayerRepair.PayerHeaders);
            Template(templates, "CustomerMeteringPoints", "CustomerMeteringPoint", 4, CustomerPayerRepair.CustomerLinkHeaders);
            Template(templates, "PayerMeteringPoints", "PayerMeteringPoint", 3, CustomerPayerRepair.PayerLinkHeaders);
            Template(templates, "ContractMeteringPoints", "ContractMeteringPoint", 2, CustomerPayerRepair.ContractHeaders);
            var output = Path.Combine(root, "output");
            var exit = await CustomerPayerRepair.PrepareAsync(input, "production", scope, templates, output,
                default, draftImports: true, checkDeliveryHistory: enabled);
            Assert.Equal(missing ? 1 : 0, exit);
            var csv = await File.ReadAllTextAsync(Path.Combine(output, "verification.csv"));
            Assert.Contains(enabled ? "Leveringshistorik: til." : "Leveringshistorik: fra.", csv);
            Assert.Contains(enabled ? "Leveringshistorik: til." : "Leveringshistorik: fra.",
                await File.ReadAllTextAsync(Path.Combine(output, "contract-evidence.csv")));
            if (missing)
            {
                Assert.Empty(Directory.GetFiles(output, "*.xlsx"));
                Assert.Equal(5, Directory.GetFiles(Path.Combine(output, "draft-imports"), "*.xlsx").Length);
                return;
            }
            foreach (var file in new[] { "3-CustomerMeteringPoints.xlsx", "4-PayerMeteringPoints.xlsx", "5-ContractMeteringPoints.xlsx" })
            {
                using var book = new XLWorkbook(Path.Combine(output, file)); var sheet = book.Worksheet(1);
                var end = sheet.Row(2).CellsUsed().Single(c => c.GetString() == "End").Address.ColumnNumber;
                if (enabled) Assert.Equal(moveIn.AddDays(-1).ToDateTime(TimeOnly.MinValue), sheet.Cell(3, end).GetDateTime());
                else Assert.True(sheet.Cell(3, end).IsEmpty());
            }
            using var customers = new XLWorkbook(Path.Combine(output, "1-Customers.xlsx"));
            Assert.Equal(Cos.ToDateTime(TimeOnly.MinValue), customers.Worksheet(1).Cell(3, 4).GetDateTime());
            if (enabled) { Assert.Contains("Indflytning 01-10-2026", csv); Assert.Contains("2026-09-30", csv); }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Template(string path, string sheet, string type, int version, string[] headers)
    {
        using var book = new XLWorkbook();
        var s = book.AddWorksheet(sheet);
        s.Cell(1, 1).Value = "Type"; s.Cell(1, 2).Value = type;
        s.Cell(1, 3).Value = "Version"; s.Cell(1, 4).Value = version;
        if (sheet != "ContractMeteringPoints" && sheet.EndsWith("MeteringPoints")) { s.Cell(1, 5).Value = "Identification"; s.Cell(1, 6).Value = "MeteringPointIdentification"; }
        for (var i = 0; i < headers.Length; i++) s.Cell(2, i + 1).Value = headers[i];
        s.Cell(2, 1).CreateComment().AddText("Create/Update/Delete");
        book.SaveAs(Path.Combine(path, sheet + "_V" + version + "_test.xlsx"));
    }

    private static string Temp() { var p = Path.Combine(Path.GetTempPath(), "repair-test-" + Guid.NewGuid()); Directory.CreateDirectory(p); return p; }
    private static PostalAddressDto Address() => new() { Name1 = new() { FullName = "SYNTHETIC TEST CUSTOMER" }, PhysicalAddress = new() { StreetName = "Test Street", BuildingNumber = "1", PostalCode = "8900", City = "Test City", CountryCode = "DK" } };
    private static RepairXellentEvidence Source() => new(DateTimeOffset.UtcNow, Cos.AddDays(-1),
        [new(new() { Id = CustomerId, CustomerNumber = "456192", CountryCode = "DK", CustomerType = "Company", Address = new() { Start = Cos, PostalAddress = Address() } },
            [new() { Id = PayerId, CustomerId = CustomerId, CountryCode = "DK", PayerInformation = new() { PayerIdentifier = "456192", PostalAddress = Address() },
                PayerBilling = new() { InvoiceChannel = "PBS_BS", PaymentChannel = "BS", PayerType = "Company" } }], "456192")],
        [new(Mp, "456192", "456192", "TEST-CONTRACT", Cos.AddYears(-1), new DateOnly(1900, 1, 1)) { SourceAccount = "456192" }])
        { OriginalAccountsPreserved = true, ExpectedContracts = [new(Mp, "Test contract", Cos.AddYears(-1), DateOnly.MaxValue, "TEST-PRODUCT", "TEST-CONTRACT")] };
    private static RepairSettlEvidence Settl()
    {
        var period = new TimePeriodDto { Start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), End = DateTimeOffset.MaxValue };
        return new(DateTimeOffset.UtcNow,
            [new(Mp, MpId, Cos, new() { InDelivery = true, CurrentOrNextDeliveryPeriod = period })],
            [new(Mp, MpId, true, period, null, new() { DeliveryStatuses = [new() { InDelivery = true, Period = period, StartReason = DeliveryStartReason.ChangeOfSupplier }] }, null, Cos, "ChangeOfSupplier")], [], [], [], []) { ContractCatalog = [new() { Id = Guid.Parse("00000000-0000-0000-0000-000000000004"), ContractName = "Test contract", CommodityType = "Electricity" }] };
    }
}
