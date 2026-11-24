using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using DataMigration.OpsCli.Reporting;

namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

/// <summary>Read payment exports with or without an agreement number; matches use MP or joint-invoice keys.</summary>
public static class SettlPaymentCsvParser
{
    public static List<SettlCsvRecord> ParseContent(string content)
    {
        content = content.TrimStart('\uFEFF', '\r', '\n');
        var firstLine = content.Split('\n', 2)[0];
        var delimiter = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ";" : ",";
        using var reader = new StringReader(content);
        using var csv = new CsvReader(reader, Configuration(delimiter, true));
        if (!csv.Read()) throw new InvalidOperationException("Settl-CSV mangler kolonneoverskrifter.");
        csv.ReadHeader();
        var headers = csv.HeaderRecord!;
        int Column(string name, bool required = true)
        {
            var index = Array.FindIndex(headers, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && required)
                throw new InvalidOperationException($"Could not find required column '{name}' in CSV header.");
            return index;
        }
        var number = Column("payment agreement number", required: false);
        var method = Column("payment method");
        var customer = Column("external customer number");
        var name = Column("customer name");
        var payer = Column("payer name");
        var type = Column("agreement type");
        var mp = Column("metering point id");
        var joint = Column("joint invoice number");
        var updated = Column("last updated", required: false);
        var result = new List<SettlCsvRecord>();
        while (csv.Read())
        {
            var row = csv.Parser.Record!;
            // Some historical exports wrap a complete CSV row in an additional pair of quotes.
            if (row.Length == 1 && headers.Length > 1)
            {
                using var innerText = new StringReader(row[0]);
                using var inner = new CsvReader(innerText, Configuration(delimiter, false));
                if (inner.Read()) row = inner.Parser.Record!;
            }
            // Older exports left the comma between the update date and time unquoted.
            if (updated == 0 && row.Length == headers.Length + 1
                && DateTime.TryParseExact(row[0] + " " + row[1], "dd.MM.yyyy HH.mm.ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _))
                row = [row[0] + ", " + row[1], .. row.Skip(2)];
            if (row.Length != headers.Length)
                throw new InvalidOperationException($"Settl-CSV række {csv.Parser.Row} har {row.Length} felter; forventede {headers.Length}.");
            string Value(int index) => index < 0 ? "" : row[index].Trim();
            var externalCustomer = Value(customer);
            if (externalCustomer.Length == 0) continue;
            var meteringPoint = Value(mp);
            // Settl identifiers are opaque: mixed-utility exports also contain GUIDs and labels.
            // Preserve them verbatim; electricity matching only selects the supplied 18-digit IDs.
            result.Add(new SettlCsvRecord
            {
                PaymentAgreementNumber = Value(number), PaymentMethod = Value(method),
                ExternalCustomerNumber = externalCustomer, CustomerName = Value(name),
                PayerName = Value(payer), AgreementType = Value(type),
                MeteringpointId = meteringPoint.Length == 0 ? null : meteringPoint,
                JointInvoiceNumber = Value(joint)
            });
        }
        return result;
    }

    private static CsvConfiguration Configuration(string delimiter, bool headers) => new(CultureInfo.InvariantCulture)
    {
        Delimiter = delimiter, HasHeaderRecord = headers, TrimOptions = TrimOptions.Trim,
        ExceptionMessagesContainRawData = false
    };
}
