using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace DataMigration.OpsCli.Services;

internal static class AborabatVerification
{
    internal static readonly string[] Headers =
    [
        "Målepunkt", "Xellent-kundenummer", "Xellent-kontraktnummer", "Importfil",
        "Excel-række", "Excel-kolonne", "Excel-værdi", "Kildesystem",
        "Kildepost og felt", "Kildeværdi", "Forklaring", "Resultat"
    ];

    private sealed record Entry(string Mp, string Account, string Contract, string File,
        string Row, string Column, string Value, string System, string Record,
        string SourceValue, string Explanation, string Outcome)
    {
        public string ToCsv() => string.Join(';', new[] { Mp, Account, Contract, File, Row,
            Column, Value, System, Record, SourceValue, Explanation, Outcome }.Select(Csv));
    }

    public static void VerifyReviewedWorkbook(string path, AborabatRepairPlan plan)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet("ContractMeteringPoints");
        var actual = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() >= 3))
        {
            var mp = row.Cell(5).GetString();
            if (!actual.TryGetValue(mp, out var rows)) actual[mp] = rows = [];
            rows.Add(string.Join('\u001F', Enumerable.Range(1, 8).Select(c => ImportValue(row.Cell(c)))));
        }
        if (!actual.Keys.ToHashSet(StringComparer.Ordinal)
            .SetEquals(plan.Items.Select(i => i.MeteringPoint)))
            throw new InvalidOperationException("Reviewed workbook does not contain exactly the resolved MPs in this plan.");
        foreach (var item in plan.Items)
        {
            var expected = new List<string>();
            foreach (var deletion in new[] { (Id: item.OldAssociationId!.Value, Name: "V AboRabat") }
                .Concat(item.ReplacedLinks.Select(l => (Id: l.AssociationId!.Value, Name: l.ContractName)))
                .DistinctBy(l => l.Id).OrderBy(l => l.Id))
                expected.Add(Row("Delete", deletion.Name, "", deletion.Id.ToString("D"), item.MeteringPoint,
                    "", "", ""));
            foreach (var target in item.Targets.OrderBy(t => t.Start)
                .ThenBy(t => t.ContractName, StringComparer.Ordinal).ThenBy(t => t.End))
                expected.Add(Row("Create", target.ContractName, "DK", "", item.MeteringPoint,
                    target.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    target.End == DateOnly.MaxValue ? "" : target.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    "Electricity"));
            if (!actual.TryGetValue(item.MeteringPoint, out var reviewed) || !reviewed.SequenceEqual(expected))
                throw new InvalidOperationException($"Reviewed workbook differs from the current proposal for MP {item.MeteringPoint}.");
        }
    }

    public static async Task WriteAsync(AborabatRepairPlan plan, string workbookPath,
        string output, CancellationToken ct)
    {
        if (File.Exists(output)) throw new IOException($"Output already exists: {output}");
        using var workbook = new XLWorkbook(workbookPath);
        var sheet = workbook.Worksheet("ContractMeteringPoints");
        var file = Path.GetFileName(workbookPath);
        var entries = new List<Entry>();
        var items = plan.Items.ToDictionary(i => i.MeteringPoint, StringComparer.Ordinal);
        var exported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() >= 3))
        {
            ct.ThrowIfCancellationRequested();
            var mp = row.Cell(5).GetString();
            if (!items.TryGetValue(mp, out var item)) throw new InvalidOperationException($"Unknown import MP {mp}.");
            exported.Add(mp);
            var parts = plan.Parts.Where(p => p.MeteringPoint == mp).ToList();
            var owner = parts.FirstOrDefault(p => p.Type == "Discount") ?? parts.FirstOrDefault();
            var action = row.Cell(1).GetString();
            const string outcome = "Afklaret";
            void Add(int column, string system, string sourceRecord, string sourceValue, string explanation,
                AborabatPartEvidence? part = null)
            {
                entries.Add(new Entry(mp, part?.Account ?? owner?.Account ?? "",
                    part?.ContractNumber ?? owner?.ContractNumber ?? "", file,
                    row.RowNumber().ToString(CultureInfo.InvariantCulture),
                    sheet.Cell(2, column).GetString(), ImportValue(row.Cell(column)), system,
                    sourceRecord, sourceValue, explanation, outcome));
            }
            if (action == "Delete")
            {
                var id = Guid.Parse(row.Cell(4).GetString());
                var link = plan.ExistingLinks.Single(l => l.MeteringPoint == mp && l.AssociationId == id);
                var period = LinkPeriod(link);
                var reason = link.ContractName == "V AboRabat" && item.Targets.Count == 0
                    ? $"Settls rabatlink dækker referencedatoen {plan.ReferenceDate:dd-MM-yyyy}, men Xellent har ingen V AboRabat-del, der er aktiv den dag eller starter senere. Rabatlinket slettes uden en ny kontrakt."
                    : link.ContractName == "V AboRabat"
                    ? "Rabatten er med i den valgte UA-kontrakt. Det særskilte rabatlink slettes."
                    : "Dette Settl-link passer ikke til kontraktforløbet i Xellent og erstattes.";
                var source = $"Settl ContractMeteringPoint[AssociationId={id}]";
                var dates = $"{period.Start:dd-MM-yyyy} - {End(period.End)}";
                Add(1, "Settl", source + ".AssociationId", id.ToString("D"), reason + " Registreret periode: " + dates);
                Add(2, "Settl", source + ".ContractName", link.ContractName, reason + " Registreret periode: " + dates);
                Add(4, "Settl", source + ".AssociationId", id.ToString("D"), reason + " Registreret periode: " + dates);
                Add(5, "Settl", source + ".MeteringPoint", link.MeteringPoint, reason + " Registreret periode: " + dates);
                if (item.Targets.Count == 0)
                    foreach (var expired in parts.Where(p => p.Type == "Discount"
                        && p.End < plan.ReferenceDate))
                        Add(2, "Xellent", expired.EndRecord, expired.EndValue,
                            $"Xellents V AboRabat-del sluttede {expired.End:dd-MM-yyyy}, før referencedatoen. {reason}", expired);
            }
            else if (action == "Create")
            {
                var start = ImportDate(row.Cell(6));
                var end = row.Cell(7).IsEmpty() ? DateOnly.MaxValue
                    : ImportDate(row.Cell(7));
                var target = item.Targets.Single(t => t.ContractName == row.Cell(2).GetString()
                    && t.Start == start && t.End == end);
                var basis = parts.Where(p => p.Type is "Electricity" or "Discount" or "Charger")
                    .OrderBy(p => p.Type).ThenBy(p => p.Start).ToList();
                var chargerActive = basis.Any(p => p.Type == "Charger" && p.Start <= start && p.End >= start);
                var discountActive = basis.Any(p => p.Type == "Discount" && p.Start <= start && p.End >= start);
                var rule = $"Den {start:dd-MM-yyyy} er laderabonnementet {(chargerActive ? "aktivt" : "ikke aktivt")}, "
                    + $"og V AboRabat er {(discountActive ? "aktiv" : "ikke aktiv")}. "
                    + (target.ContractName.StartsWith("LadeAbo_Afventer_Installation", StringComparison.Ordinal)
                        ? "Xellent har ingen matchende Charge_Abo-del. Settls link for afventende installation begrunder den valgte variant. "
                        : chargerActive ? "Laderproduktet bestemmer ladervarianten. "
                        : "El fortsætter uden laderabonnement. ")
                    + (discountActive ? "Rabatten giver UA."
                        : "Elproduktet afgør, om UA fortsætter.");
                var assignment = plan.Assignments.Where(a => a.MeteringPoint == mp).ToList();
                Add(1, "Xellent", owner?.ProductField ?? "", owner?.Product ?? "",
                    "Xellents kontraktforløb kræver dette nye Settl-link.");
                if (owner is not null)
                    Add(2, "Xellent", $"EXUCONTRACTTABLE[CONTRACTNUM={owner.ContractNumber}].CUSTACCOUNT",
                        owner.Account, "Kildekontrakten tilhører denne Xellent-kunde.", owner);
                foreach (var part in basis)
                    Add(2, "Xellent", part.ProductField, part.Product,
                        $"{PartType(part.Type)}: {part.Start:dd-MM-yyyy} - {End(part.End)}. {rule}", part);
                if (target.ContractName.StartsWith("LadeAbo_Afventer_Installation", StringComparison.Ordinal))
                    foreach (var pending in plan.ExistingLinks.Where(l => l.MeteringPoint == mp
                        && l.ContractName == "LadeAbo_Afventer_Installation"))
                        Add(2, "Settl", $"ContractMeteringPoint[AssociationId={pending.AssociationId}].ContractName",
                            pending.ContractName, rule);
                foreach (var match in assignment)
                    Add(2, "Xellent", $"Charger assignment[MP={mp}, ACCOUNT={match.Account}, CONTRACTNUM={match.ContractNumber}, DATE={match.ReferenceDate:yyyy-MM-dd}].AddressMatchesMeteringPoint",
                        match.AddressMatches ? "true" : "false", "Laderens fysiske adresse er knyttet til dette målepunkt.");
                Add(3, "Regel", "ContractMeteringPoints V2.CountryCode", "",
                    "Settl kræver landekoden DK for et elmålepunkt i Danmark.");
                Add(5, "Xellent", owner?.MpField ?? "", mp,
                    "Den registrerede Xellent-kontraktdel tilhører dette målepunkt.");
                foreach (var part in basis)
                    Add(6, "Xellent",
                        part.End != DateOnly.MaxValue && part.End.AddDays(1) == start
                            ? part.EndRecord : part.StartField,
                        part.End != DateOnly.MaxValue && part.End.AddDays(1) == start
                            ? part.EndValue : part.StartValue,
                        $"{PartType(part.Type)} starter {part.Start:dd-MM-yyyy} og slutter {End(part.End)}. "
                        + (part.Start == start ? "Denne startdato bestemmer startdatoen i importen."
                            : part.End != DateOnly.MaxValue && part.End.AddDays(1) == start
                                ? "Importens startdato er dagen efter denne dels slutdato."
                                : "Perioden bidrager til valget af kontrakt."), part);
                foreach (var part in basis)
                    Add(7, "Xellent", part.EndRecord, part.EndValue,
                        $"{PartType(part.Type)} starter {part.Start:dd-MM-yyyy} og slutter {End(part.End)}. "
                        + (part.End == end ? end == DateOnly.MaxValue
                            ? "Den åbne Xellent-periode begrunder et tomt End-felt."
                            : "Denne slutdato bestemmer slutdatoen i importen."
                            : "Perioden bidrager til valget af kontrakt."), part);
                Add(8, "Regel", "ContractMeteringPoints V2.CommodityType", "",
                    "Kildeaftalen gælder el.");
            }
            else throw new InvalidOperationException($"Unsupported import action {action}.");
        }
        foreach (var item in plan.Items)
        {
            var mp = item.MeteringPoint;
            var owner = plan.Parts.FirstOrDefault(p => p.MeteringPoint == mp && p.Type == "Discount");
            foreach (var link in item.KeptLinks)
            {
                var period = LinkPeriod(link);
                entries.Add(new Entry(mp, owner?.Account ?? "", owner?.ContractNumber ?? "",
                    "", "", "", "", "Settl",
                    $"ContractMeteringPoint[AssociationId={link.AssociationId}].ContractName, Start, End",
                    $"{link.ContractName}; {period.Start:dd-MM-yyyy} - {End(period.End)}",
                    "Settl-linket passer til perioden i Xellent og beholdes uændret.", "Beholdt"));
            }
            if (!exported.Contains(mp))
                throw new InvalidOperationException($"Resolved MP {mp} is absent from the workbook.");
            var timeline = item.KeptLinks.Select(l => (l.ContractName, Period: LinkPeriod(l)))
                .Concat(item.Targets.Select(t => (t.ContractName, Period: (t.Start, t.End))))
                .OrderBy(x => x.Period.Item1).ThenBy(x => x.ContractName)
                .Select(x => $"{x.ContractName}: {x.Period.Item1:dd-MM-yyyy} - {End(x.Period.Item2)}");
            entries.Add(new Entry(mp, owner?.Account ?? "", owner?.ContractNumber ?? "",
                "", "", "", "", "Xellent + Settl", "", "",
                item.Targets.Count == 0
                    ? "Det forældede V AboRabat-link slettes uden en ny kontrakt. Andre Settl-links er uden for denne rettelse."
                    : "Samlet kontraktforløb, inklusive beholdte links: " + string.Join(" | ", timeline),
                "Afklaret forløb"));
        }
        foreach (var group in plan.Review.Where(r => r.Status == "Unresolved").GroupBy(r => r.MeteringPoint))
        {
            var row = group.First();
            entries.Add(new Entry(row.MeteringPoint, row.Account, row.ContractNumber,
                "", "", "", "", "Xellent / Settl", row.Evidence, row.ProductNumber,
                row.Reason, "Uafklaret - udeladt"));
            if (plan.Assignments.Any(a => a.Account == row.Account
                && a.MeteringPoint == row.MeteringPoint && !a.AddressMatches))
            {
                foreach (var assignment in plan.Assignments.Where(a => a.Account == row.Account
                    && a.MeteringPoint == row.MeteringPoint && !a.AddressMatches)
                    .DistinctBy(a => (a.MeteringPoint, a.ContractNumber, a.InstallationNumber)))
                    entries.Add(new Entry(row.MeteringPoint, row.Account, row.ContractNumber,
                        "", "", "", "", "Xellent",
                        $"Charger assignment[INSTALLATION={assignment.InstallationNumber}, ACCOUNT={assignment.Account}, CONTRACTNUM={assignment.ContractNumber}, DATE={assignment.ReferenceDate:yyyy-MM-dd}].Address, AddressMatchesMeteringPoint",
                        $"Lader: {assignment.AddressEvidence?.InstallationAddress ?? assignment.Address}; DAR: {assignment.AddressEvidence?.InstallationDarId ?? "mangler"}. Målepunkt: {AborabatAddressEvidence.CandidateSummary(assignment.AddressEvidence)}",
                        AborabatAddressEvidence.Explain(assignment.AddressEvidence, false),
                        "Uafklaret - udeladt"));
            }
            if (row.Reason.Contains("er knyttet til målepunkt", StringComparison.Ordinal))
                foreach (var assignment in plan.Assignments.Where(a => a.Account == row.Account
                    && a.MeteringPoint != row.MeteringPoint)
                    .DistinctBy(a => (a.MeteringPoint, a.ContractNumber, a.InstallationNumber)))
                    entries.Add(new Entry(row.MeteringPoint, row.Account, row.ContractNumber,
                        "", "", "", "", "Xellent",
                        $"Charger assignment[INSTALLATION={assignment.InstallationNumber}, ACCOUNT={assignment.Account}, CONTRACTNUM={assignment.ContractNumber}, DATE={assignment.ReferenceDate:yyyy-MM-dd}].MeteringPoint, Address",
                        $"{assignment.MeteringPoint}; {assignment.Address}",
                        "Xellent knytter laderen til et andet målepunkt. Laderens adresse kan derfor ikke begrunde en ladekontrakt på dette målepunkt.",
                        "Uafklaret - udeladt"));
        }
        var lines = new[] { string.Join(';', Headers.Select(Csv)) }
            .Concat(entries.Select(e => e.ToCsv()));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllLinesAsync(output, lines, new UTF8Encoding(true), ct);
    }

    private static DateOnly ImportDate(IXLCell cell)
    {
        if (cell.DataType != XLDataType.DateTime || cell.Style.DateFormat.Format != "yyyy-mm-dd"
            || cell.GetDateTime().TimeOfDay != TimeSpan.Zero)
            throw new InvalidOperationException($"Import date at {cell.Address} must be a native Excel date displayed as yyyy-mm-dd.");
        return DateOnly.FromDateTime(cell.GetDateTime());
    }

    private static string ImportValue(IXLCell cell) => cell.Address.ColumnNumber is 6 or 7 && !cell.IsEmpty()
        ? ImportDate(cell).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : cell.GetString();

    private static string Row(params string[] values) => string.Join('\u001F', values);
    private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string PartType(string type) => type switch
    {
        "Electricity" => "Elprodukt",
        "Discount" => "El-rabat",
        "Charger" => "Laderabonnement",
        _ => type
    };
    private static string End(DateOnly value) => value == DateOnly.MaxValue ? "Åben"
        : value.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static (DateOnly Start, DateOnly End) LinkPeriod(SettlContractLinkRow link) =>
        (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(link.Start,
            TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time")).Date),
         link.End.Year >= 9999 ? DateOnly.MaxValue
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(link.End,
                TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time")).Date).AddDays(-1));
}
