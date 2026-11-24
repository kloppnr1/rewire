using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;

namespace DataMigration.OpsCli.Services;

internal static class RepairVerification
{
    internal const string Held = "Afklaret – afventer frigivelse";
    internal static string Outcome(CustomerPayerRepairDecision d, bool released) => d.Status != "Ready" ? "Uafklaret"
        : d.Warnings.Length > 0 ? "Med advarsler" : released ? "Afklaret" : Held;
    internal static string Account(RepairXellentEvidence source, string mp) => source.MigrationAccounts.TryGetValue(mp, out var accounts)
        ? string.Join(" | ", accounts) : string.Join(" | ", source.Routes.Where(r => r.MeteringPoint == mp).Select(r => r.SourceAccount).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct());
    internal static string Contracts(RepairXellentEvidence source, string mp) => string.Join(" | ", source.Routes.Where(r => r.MeteringPoint == mp).Select(r => r.ContractNumber).Distinct());

    internal static async Task WriteFailureAsync(string output, StateScopeEntry[] entries, string reason, CancellationToken ct)
    {
        await RepairCsv.WriteAsync(Path.Combine(output, "verification.csv"), AborabatVerification.Headers,
            entries.Select(e => new[] { e.Value, "", "", "", "", "", "", "Afgrænsning", $"Scope, linje {e.Line}", e.Value, RepairEvidenceReports.Reason(reason), "Uafklaret" }), ct);
        await RepairEvidenceReports.WriteFailureAsync(output, entries, reason, ct);
    }

    internal static async Task WriteAsync(string output, IReadOnlyList<CustomerPayerRepairDecision> decisions,
        RepairXellentEvidence source, RepairSettlEvidence settl, bool released, CancellationToken ct,
        ContractSourceSnapshot? contractSources = null,
        IReadOnlyList<(XLWorkbook Book, string Name)>? proposedWorkbooks = null)
    {
        var rows = new List<string[]>();
        // Summary rows keep retained and unresolved MPs visible alongside proposed fields.
        foreach (var d in decisions)
            rows.Add([d.MeteringPoint, Account(source, d.MeteringPoint), Contracts(source, d.MeteringPoint), "", "", "", "",
                source.MigrationModelVersion is null ? "Xellent + Settl" : "Migration + Xellent + Settl",
                source.MigrationReports.TryGetValue(d.MeteringPoint, out var supporting) ? string.Join(" | ", supporting) : "Xellent-konto og Settl leveringshistorik",
                SummaryValues(d, source, settl) + " " + RepairDeliveryHistory.Explanation(d, settl, source.CapturedAt),
                (d.Status == "Ready" ? RepairEvidenceReports.Actions(d) : RepairEvidenceReports.Reason(d.Status))
                    + (d.Warnings.Length > 0 ? " Advarsler: " + string.Join(" ", d.Warnings.Select(RepairEvidenceReports.Reason)) : ""), Outcome(d, released)]);
        if (released)
        {
            var files = new[] { "1-Customers.xlsx", "2-Payers.xlsx", "3-CustomerMeteringPoints.xlsx", "4-PayerMeteringPoints.xlsx", "5-ContractMeteringPoints.xlsx" };
            foreach (var file in files)
            {
                using var book = new XLWorkbook(Path.Combine(output, file));
                AddWorkbook(book, file);
            }
        }
        else if (proposedWorkbooks is not null)
            foreach (var (book, file) in proposedWorkbooks) AddWorkbook(book, file);

        void AddWorkbook(XLWorkbook book, string file)
        {
                var sheet = book.Worksheet(1);
                var headers = sheet.Row(2).CellsUsed().ToDictionary(c => c.GetString(), c => c.Address.ColumnNumber);
                foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() >= 3))
                {
                    var matches = file.StartsWith("1-", StringComparison.Ordinal)
                        ? decisions.Where(d => d.Status == "Ready" && d.CreateCustomer && d.CustomerNumber == row.Cell(headers["CustomerNumber"]).GetString())
                        : file.StartsWith("2-", StringComparison.Ordinal)
                            ? decisions.Where(d => d.Status == "Ready" && d.CreatePayer && d.PayerIdentification == row.Cell(headers["PayerIdentification"]).GetString())
                            : decisions.Where(d => d.Status == "Ready" && d.MeteringPoint == row.Cell(headers["MeteringPointIdentification"]).GetString());
                    var owners = matches.ToArray();
                    if (owners.Length == 0) throw new InvalidOperationException("Workbook row has no scoped repair decision: " + file);
                    foreach (var d in owners)
                    foreach (var cell in row.CellsUsed().Where(c => !c.IsEmpty()))
                    {
                        ct.ThrowIfCancellationRequested();
                        var header = sheet.Cell(2, cell.Address.ColumnNumber).GetString();
                        if (header is not ("CustomerNumber" or "PayerIdentification" or "ContractName" or "Date" or "Start" or "End")) continue;
                        var origin = CellOrigin(sheet.Name, header, row, headers, d, decisions, source, settl);
                        rows.Add([d.MeteringPoint, Account(source, d.MeteringPoint), Contracts(source, d.MeteringPoint), released ? file : "",
                            released ? row.RowNumber().ToString(CultureInfo.InvariantCulture) : "", header, CellValue(cell), origin.System,
                            header == "End" && d.DeliveryEvent is not null ? "Settl leveringshistorik" : header is "CustomerNumber" or "PayerIdentification" ? source.MigrationReports.TryGetValue(d.MeteringPoint, out var accountReports)
                                ? string.Join(" | ", accountReports) : "Originalkonto i Xellent"
                                : header is "ContractName" or "End" || file.StartsWith("5-", StringComparison.Ordinal)
                                    ? source.MigrationReports.TryGetValue(d.MeteringPoint, out var reports) ? string.Join(" | ", reports) : "Xellent-kontraktdele og Settl-kontrakter"
                                : source.MigrationReports.TryGetValue(d.MeteringPoint, out var dateReports) ? string.Join(" | ", dateReports) : "Settl leveringshistorik", origin.Value, origin.Explanation, Outcome(d, released)]);
                    }
                }
        }
        var orderedRows = rows.OrderBy(r => r[0], StringComparer.Ordinal)
            .ThenBy(r => string.IsNullOrEmpty(r[5]) ? 1 : 0);
        await RepairCsv.WriteAsync(Path.Combine(output, "verification.csv"), AborabatVerification.Headers, orderedRows, ct);
        await RepairEvidenceReports.WriteAsync(output, decisions, source, settl, contractSources, released, ct);
    }

    private static string SummaryValues(CustomerPayerRepairDecision d, RepairXellentEvidence source, RepairSettlEvidence settl)
    {
        var customer = d.CustomerNumber is null ? "ikke afklaret" : settl.Customers.Any(c => c.Customer.CustomerNumber == d.CustomerNumber) ? "findes" : "findes ikke";
        var payer = d.PayerIdentification is null ? "ikke afklaret" : settl.Payers.Any(p => p.Payer.PayerIdentification == d.PayerIdentification) ? "findes" : "findes ikke";
        var contracts = d.Contracts.Count > 0
            ? d.Contracts.Select(c => (c.ContractName, c.Start, c.End))
            : (source.ExpectedContracts ?? []).Where(c => c.MeteringPoint == d.MeteringPoint).Select(c => (c.ContractName, c.Start, c.End));
        var periods = string.Join(" | ", contracts.Select(c => $"{c.ContractName}: {c.Start:dd-MM-yyyy} – {(c.End == DateOnly.MaxValue || c.End.Year <= 1900 ? "Åben" : c.End.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture))}"));
        return $"Kunde {d.CustomerNumber}: {customer}; betaler {d.PayerIdentification}: {payer}; "
            + (source.MigrationModelVersion is null ? $"leverandørskifte {d.Start:dd-MM-yyyy}" : $"linkstart i migration {d.Start:dd-MM-yyyy}")
            + (periods.Length == 0 ? "" : "; kontraktgrundlag: " + periods);
    }

    private sealed record Origin(string System, string Record, string Value, string Explanation);
    private static string CellValue(IXLCell cell) => cell.DataType == XLDataType.DateTime
        ? cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : cell.GetString();

    private static Origin CellOrigin(string sheet, string field, IXLRow row, Dictionary<string, int> columns,
        CustomerPayerRepairDecision d, IReadOnlyList<CustomerPayerRepairDecision> decisions,
        RepairXellentEvidence source, RepairSettlEvidence settl)
    {
        var account = Account(source, d.MeteringPoint);
        if (field == "Action") return new("Regel", "Oprettelsesregel; report.json.Decisions", "Create", "Opret kun det, der mangler.");
        if (field is "CustomerNumber" or "PayerIdentification") return new(source.MigrationModelVersion is null ? "Xellent + regel" : "Migration + regel",
            $"repair-xellent.jsonl.Routes[MeteringPoint={d.MeteringPoint}].SourceAccount", account,
            account.Length == 5 ? "Tilføj 9 foran femcifret konto. Samme kunde- og betalernummer."
                : "Originalkontoen bruges uændret til kunde og betaler.");
        if (field == "MeteringPointIdentification") return new("Settl",
            $"repair-settl.jsonl.MeteringPoints[Identification={d.MeteringPoint}].Identification", d.MeteringPoint, "Alle cifre bevares som tekst.");
        if (field == "CommodityType") return new("Regel", "Regel: el", "Electricity", "Fast værdi for el.");
        if (field == "CountryCode" && sheet.EndsWith("MeteringPoints", StringComparison.Ordinal))
            return new("Regel", "Regel: Danmark", "DK", "Fast landekode: DK.");
        if (field == "End" && d.DeliveryEvent is not null && sheet.EndsWith("MeteringPoints", StringComparison.Ordinal))
            return new("Migration + Settl", "Settl leveringshistorik", sheet == "ContractMeteringPoints"
                ? string.Join(" | ", d.OriginalContracts.Where(c => c.ContractName == row.Cell(columns["ContractName"]).GetString())
                    .Select(c => $"{c.Start:dd-MM-yyyy} – {(c.End == DateOnly.MaxValue ? "Åben" : c.End.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture))}"))
                : $"{d.Start:dd-MM-yyyy} – Åben", RepairDeliveryHistory.Explanation(d, settl, source.CapturedAt));
        if (field is "Date" or "Start" && sheet != "ContractMeteringPoints")
        {
            var dependencies = sheet == "Customers" ? decisions.Where(x => x.Status == "Ready" && x.CreateCustomer && x.CustomerNumber == d.CustomerNumber)
                : sheet == "Payers" ? decisions.Where(x => x.Status == "Ready" && x.CreatePayer && x.PayerIdentification == d.PayerIdentification) : [d];
            if (source.MigrationModelVersion is not null)
                return new("Migration", string.Join(" | ", source.MigrationReports.GetValueOrDefault(d.MeteringPoint, [])),
                    string.Join(" | ", dependencies.Select(x => (sheet == "Customers" ? x.CustomerStart : sheet == "Payers" ? x.PayerStart : x.Start)?.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)).Distinct()),
                    "Dato fra afsluttede migrationsrapporter. Datoen ændres ikke.");
            return new("Settl + regel", string.Join(" | ", dependencies.Select(x => $"repair-settl.jsonl.Histories[MeteringPoint={x.MeteringPoint}].FirstDate")),
                string.Join(" | ", dependencies.Select(x => $"{x.Start:dd-MM-yyyy}").Distinct()),
                sheet is "Customers" or "Payers" ? "Tidligste bekræftede leverandørskifte for fælles kunde/betaler."
                    : "Start ved bekræftet leverandørskifte, dansk dato.");
        }
        if (sheet == "ContractMeteringPoints")
        {
            var name = row.Cell(columns["ContractName"]).GetString();
            var expected = source.ExpectedContracts!.Select((c, i) => (c, i)).Where(x => x.c.MeteringPoint == d.MeteringPoint && x.c.ContractName == name).ToArray();
            var refs = string.Join(" | ", expected.Select(x => $"repair-xellent.jsonl.ExpectedContracts[{x.i}]"));
            if (field == "ContractName") return new(source.MigrationModelVersion is null ? "Xellent + Settl + regel" : "Migration + kontrol", refs + ".ContractName; repair-settl.jsonl.ContractCatalog.ContractName",
                string.Join(" | ", expected.Select(x => $"Produkt {x.c.Product}; kontrakt {x.c.ContractName}")),
                source.MigrationModelVersion is null ? "Valgt ud fra produkt og lader. Kontrolleret i Settl."
                    : "Fra afsluttede migrationsrapporter. Kontrolleret mod Xellent og Settl. "
                        + string.Join(" ", expected.Select(x => x.c.MappingNote).Where(note => note is not null).Distinct()));
            if (field is "Start" or "End") return new(source.MigrationModelVersion is null ? "Xellent + Settl + regel" : "Migration", refs + ".Start/End; " + $"repair-settl.jsonl.Histories[MeteringPoint={d.MeteringPoint}].FirstDate",
                string.Join(" | ", expected.Select(x => $"{x.c.Start:dd-MM-yyyy} – {(x.c.End == DateOnly.MaxValue || x.c.End.Year <= 1900 ? "Åben" : x.c.End.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture))}"))
                    + (source.MigrationModelVersion is null ? $"; leverandørskifte {d.Start:dd-MM-yyyy}" : ""),
                source.MigrationModelVersion is not null ? expected.Any(x => x.c.MappingNote?.Contains("V AboRabat", StringComparison.Ordinal) == true)
                    ? "Perioden er opdelt efter rabatdatoerne i migrationsmodellen."
                    : "Kontraktdato fra migrationsrapporten. Datoen ændres ikke." : field == "Start" ? "Seneste af kontraktstart og leverandørskifte."
                    : "Dokumenteret slutdato. Tom End betyder åben periode.");
        }
        var customerIndex = source.Customers.FindIndex(c => c.Customer.CustomerNumber == d.CustomerNumber);
        if (customerIndex < 0) throw new InvalidOperationException("Missing mapped source customer for workbook evidence.");
        var customer = source.Customers[customerIndex];
        object dto; string prefix; string path;
        if (sheet == "Customers")
        {
            dto = customer.Customer; prefix = $"repair-xellent.jsonl.Customers[{customerIndex}].Customer";
            path = field switch { "CountryCode" or "CustomerType" => field, "OrganizationIdCvr" => "VatIdentificationNumber", "PersonalNumber" => "NationalIdentificationNumber",
                "Phone" or "Cellphone" or "Fax" or "Email" => "Address." + field, _ => "Address.PostalAddress." + PostalPath(field) };
        }
        else if (sheet == "Payers")
        {
            var payerIndex = customer.Payers.FindIndex(p => p.PayerInformation?.PayerIdentifier == d.PayerIdentification);
            if (payerIndex < 0) throw new InvalidOperationException("Missing mapped source payer for workbook evidence.");
            dto = customer.Payers[payerIndex]; prefix = $"repair-xellent.jsonl.Customers[{customerIndex}].Payers[{payerIndex}]";
            path = field == "CountryCode" ? field : field is "Email" or "ElectronicNumber" or "InvoiceChannel" or "PaymentChannel" or "BuyerReferenceIdentifier" or "AgreementIdentifier" or "OrderIdentifier" or "PayerType" or "VatIdentificationNumber" or "NationalIdentificationNumber"
                ? "PayerBilling." + field : "PayerInformation.PostalAddress." + PostalPath(field);
        }
        else throw new InvalidOperationException($"No source rule for populated cell {sheet}.{field}.");
        var value = JsonSerializer.SerializeToElement(dto, dto.GetType());
        foreach (var segment in path.Split('.'))
            if (!value.TryGetProperty(segment, out value)) throw new InvalidOperationException($"Source field {prefix}.{path} does not exist.");
        return new("Xellent-kopi", prefix + "." + path, value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString(),
            "Kopieret fra gemte Xellent-data.");
    }

    private static string PostalPath(string field) => field switch
    {
        "Name1" or "Name2" => field + ".FullName", "PostalCountry" => "PhysicalAddress.CountryCode",
        "AddressIdentifier" => "PhysicalAddress.AddressIdentifier.Identification",
        "StreetCode" or "StreetName" or "BuildingNumber" or "BuildingFloor" or "RoomIdentification" or "CitySubDivisionName" or "PostalCode" or "City" or "MunicipalityCode" => "PhysicalAddress." + field,
        _ => throw new InvalidOperationException("No postal source rule for " + field)
    };
}

internal static class RepairCsv
{
    internal static Task WriteAsync(string path, IEnumerable<string> headers, IEnumerable<string[]> rows, CancellationToken ct)
    {
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var lines = new[] { string.Join(';', headers.Select(Escape)) }.Concat(rows.Select(r => string.Join(';', r.Select(Escape))));
        return File.WriteAllLinesAsync(path, lines, new UTF8Encoding(true), ct);
    }
}
