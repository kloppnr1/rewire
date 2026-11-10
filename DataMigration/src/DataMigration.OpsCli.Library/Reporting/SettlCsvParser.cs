using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;

namespace DataMigration.OpsCli.Reporting;

/// <summary>
/// Parses manually exported Settl payment agreement CSV files.
/// Column names must match exactly (case-insensitive) — no aliases.
/// If Settl renames a column, this parser throws so the change is caught immediately.
/// </summary>
public class SettlCsvParser
{
    private const string ColPaymentAgreementNumber = "payment agreement number";
    private const string ColPaymentMethod          = "payment method";
    private const string ColExternalCustomerNumber = "external customer number";
    private const string ColCustomerName           = "customer name";
    private const string ColPayerName              = "payer name";
    private const string ColAgreementType          = "agreement type";
    private const string ColMeteringpointId        = "metering point id";
    private const string ColJointInvoiceNumber     = "joint invoice number";

    public List<SettlCsvRecord> Parse(string filePath)
    {
        var content = File.ReadAllText(filePath);
        return ParseContent(content);
    }

    public List<SettlCsvRecord> ParseContent(string csvContent)
    {
        csvContent = SanitizeCsvContent(csvContent);
        var delimiter = DetectDelimiter(csvContent);

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim
        };

        using var reader = new StringReader(csvContent);
        using var csv = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();

        var headers = csv.HeaderRecord ?? throw new InvalidOperationException("CSV file has no headers");

        var paymentAgreementIdx = RequireColumn(headers, ColPaymentAgreementNumber);
        var externalCustomerIdx = RequireColumn(headers, ColExternalCustomerNumber);
        var paymentMethodIdx    = RequireColumn(headers, ColPaymentMethod);
        var customerNameIdx     = RequireColumn(headers, ColCustomerName);
        var agreementTypeIdx    = RequireColumn(headers, ColAgreementType);
        var meteringpointIdx    = RequireColumn(headers, ColMeteringpointId);
        var payerNameIdx        = RequireColumn(headers, ColPayerName);
        var jointInvoiceIdx     = RequireColumn(headers, ColJointInvoiceNumber);

        var records = new List<SettlCsvRecord>();
        while (csv.Read())
        {
            var paymentAgreement = csv.GetField(paymentAgreementIdx)?.Trim();
            var externalCustomer = csv.GetField(externalCustomerIdx)?.Trim();

            if (string.IsNullOrEmpty(paymentAgreement) || string.IsNullOrEmpty(externalCustomer))
                continue;

            var meteringpoint = csv.GetField(meteringpointIdx)?.Trim();
            if (string.IsNullOrEmpty(meteringpoint))
                meteringpoint = null;
            else if (!meteringpoint.All(char.IsLetterOrDigit))
                throw new InvalidOperationException(
                    $"Unexpected value in Meteringpoint Id column: '{meteringpoint}'. " +
                    "Expected a numeric or alphanumeric metering point ID (e.g. water metering points prefixed with a letter) or an empty field. " +
                    "The CSV column layout may have shifted (e.g. a currency column was not stripped).");

            records.Add(new SettlCsvRecord
            {
                PaymentAgreementNumber = paymentAgreement,
                PaymentMethod = csv.GetField(paymentMethodIdx)?.Trim() ?? "",
                ExternalCustomerNumber = externalCustomer,
                CustomerName = csv.GetField(customerNameIdx)?.Trim() ?? "",
                PayerName = csv.GetField(payerNameIdx)?.Trim(),
                AgreementType = csv.GetField(agreementTypeIdx)?.Trim() ?? "",
                MeteringpointId = meteringpoint,
                JointInvoiceNumber = csv.GetField(jointInvoiceIdx)?.Trim()
            });
        }

        return records;
    }

    private static string DetectDelimiter(string content)
    {
        var firstLine = content.Split('\n', 2)[0];
        var semicolonCount = firstLine.Count(c => c == ';');
        var commaCount = firstLine.Count(c => c == ',');
        return semicolonCount >= commaCount ? ";" : ",";
    }

    private static readonly Regex DatePrefix = new(
        @"^""?\d{2}\.\d{2}\.\d{4},\s*\d{2}\.\d{2}\.\d{2}""?,\s*",
        RegexOptions.Compiled);

    private static string SanitizeCsvContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length == 0) return content;

        var sb = new System.Text.StringBuilder();

        var header = lines[0].TrimEnd('\r');
        var hasLastUpdated = header.StartsWith("Last Updated", StringComparison.OrdinalIgnoreCase);
        if (hasLastUpdated)
            header = Regex.Replace(header, @"^Last Updated\s*,\s*", "", RegexOptions.IgnoreCase);
        header = Regex.Replace(header, @",Currency(?=,|$)", "", RegexOptions.IgnoreCase);
        sb.AppendLine(header);

        if (!hasLastUpdated)
        {
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (string.IsNullOrEmpty(line)) continue;
                line = Regex.Replace(line, @",([A-Z]{3}),(?=(?:Meter(?:ing)? Point|Joint) )", ",");
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith('"') && line.EndsWith('"'))
            {
                line = line[1..^1];
                line = line.Replace("\"\"", "\"");
            }

            line = DatePrefix.Replace(line, "", 1);
            line = Regex.Replace(line, @",([A-Z]{3}),(?=(?:Meter(?:ing)? Point|Joint) )", ",");

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static int RequireColumn(string[] headers, string columnName)
    {
        for (int i = 0; i < headers.Length; i++)
        {
            if (string.Equals(headers[i].Trim(), columnName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new InvalidOperationException(
            $"Could not find required column '{columnName}' in CSV header.");
    }
}
