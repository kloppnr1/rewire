using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataMigration.Domain.Customers;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class ReportPayersRunOptions
{
    public bool Verbose { get; set; }
    public bool ShowAll { get; set; }
}

/// <summary>
/// Read-only scan of all payers in Settl. Two checks per payer:
/// <list type="number">
///   <item>CustomerId resolves to an existing customer (else ghost/orphan).</item>
///   <item>PayerIdentification has the canonical account-based shape
///     (<c>{account}</c> or <c>{account}-{n}</c>, all-numeric). Free-text ids — e.g.
///     <c>"Målernr. 15418197"</c> leaked from the Xellent Custpurchaseorder/Debitorrekvisition
///     field before the 2026-06-01 PayerMapper fix — fail this and are flagged
///     <c>InvalidIdentificationFormat</c>; they need re-creation with the account-based id.</item>
/// </list>
/// Emits NeedsUpdate rows for any payer failing either check.
/// </summary>
public class ReportPayersRunner
{
    private static readonly string[] CsvHeader =
    [
        "EntityType", "Identification", "SettlId", "Status", "Årsager", "Payer_CustomerId", "Customer_Number"
    ];

    // Canonical payer identification: the Xellent Accountnum (numeric, possibly env-padded to 6
    // digits), optionally with a "-{n}" fan-out suffix. Anything else is a leaked free-text value.
    private static readonly Regex ValidPayerIdentification = new(@"^\d+(-\d+)?$", RegexOptions.Compiled);

    /// <summary>True if the payer identification has the canonical account-based shape
    /// (<c>{account}</c> or <c>{account}-{n}</c>). Shared with the per-entity drift lookup.</summary>
    public static bool IsValidPayerIdentification(string payerIdentification) =>
        ValidPayerIdentification.IsMatch(payerIdentification);

    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlPayerClient _payerClient;

    public ReportPayersRunner(
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient)
    {
        _customerClient = customerClient;
        _payerClient = payerClient;
    }

    /// <summary>
    /// Structured result of the payer scan: the intrinsic <see cref="DriftRow"/>s plus the
    /// loaded Settl payers and a customer-id → customer-number (representative account) map,
    /// so the unified <c>report-drift</c> aggregator can run the fan-out check off the same
    /// single payer load without re-paginating Settl.
    /// </summary>
    public sealed record PayerScanResult(
        List<DriftRow> DriftRows,
        IReadOnlyList<PayerInfoDto> Payers,
        IReadOnlyDictionary<Guid, string?> CustomerNumberById);

    public async Task<int> RunAsync(ReportPayersRunOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var (exitCode, rows, _) = await ScanAsync(options, cancellationToken);
        if (exitCode != 0) return exitCode;

        await output.WriteLineAsync(string.Join(";", CsvHeader.Select(CsvQuote)));
        foreach (var row in rows)
            await output.WriteLineAsync(row);

        return 0;
    }

    /// <summary>
    /// Runs the same intrinsic payer scan as <see cref="RunAsync"/> but returns the structured
    /// <see cref="DriftRow"/> projection together with the loaded payers and representative map
    /// for the <c>report-drift</c> aggregator. Same source of truth as the CSV path.
    /// </summary>
    public async Task<PayerScanResult> ScanDriftAsync(ReportPayersRunOptions options, CancellationToken cancellationToken = default)
    {
        var (_, _, result) = await ScanAsync(options, cancellationToken);
        return result;
    }

    private async Task<(int ExitCode, List<string> Rows, PayerScanResult Result)> ScanAsync(
        ReportPayersRunOptions options, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine("=== report-payers (scan) ===");
        Console.Error.WriteLine();

        Console.Error.Write("  Loading payers...");
        const int pageSize = 100;
        var allPayers = new List<PayerInfoDto>();
        var empty = new PayerScanResult([], allPayers, new Dictionary<Guid, string?>());
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (page, _, pageErr) = await _payerClient.FindPayersAsync(pageSize, allPayers.Count, cancellationToken);
            if (pageErr != null)
            {
                Console.Error.WriteLine($"\n  FAILED at offset {allPayers.Count}: {pageErr}");
                return (1, [], empty);
            }
            allPayers.AddRange(page);
            Console.Error.Write($"\r  Loading payers... {allPayers.Count}");
            if (page.Count < pageSize) break;
        }
        Console.Error.WriteLine($"\r  Loaded {allPayers.Count} payers                    ");
        Console.Error.WriteLine();

        var rows = new List<string>();
        var driftRows = new List<DriftRow>();
        var customerCache = new Dictionary<Guid, (bool Exists, string? CustomerNumber)>();
        var payerOk = 0;
        var payerNeedsUpdate = 0;
        var invalidFormat = 0;

        foreach (var payer in allPayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reasons = await ProcessPayerAsync(payer, customerCache, options, rows, driftRows, cancellationToken);
            if (reasons.Count > 0)
            {
                payerNeedsUpdate++;
                if (reasons.Contains("InvalidIdentificationFormat"))
                    invalidFormat++;
            }
            else
            {
                payerOk++;
            }
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("=== Summary ===");
        Console.Error.WriteLine($"  Payers checked:           {allPayers.Count}");
        Console.Error.WriteLine($"  Payers OK:                {payerOk}");
        Console.Error.WriteLine($"  Payers NeedsUpdate:       {payerNeedsUpdate}");
        Console.Error.WriteLine($"  - InvalidIdentificationFormat: {invalidFormat}");
        Console.Error.WriteLine();

        var customerNumberById = customerCache.ToDictionary(kv => kv.Key, kv => kv.Value.CustomerNumber);
        return (0, rows, new PayerScanResult(driftRows, allPayers, customerNumberById));
    }

    /// <summary>
    /// Diffs each payer's Settl PaymentConfiguration (PayerBilling) against the Xellent-expected
    /// override — EAN/OrderId/AgreementId/BuyerReference — using the same rule as fix-payment-config
    /// Phase A (<see cref="PaymentConfigDiff"/>). EAN comes from the already-paginated
    /// <see cref="PayerInfoDto"/>; the three identifiers live only in the payer's raw JSON, so a raw
    /// GET is issued only for payers whose expectation actually carries one of them. Emits one "Payer"
    /// <see cref="DriftRow"/> per payer with billing drift (or an OK row when <paramref name="showAll"/>).
    /// Used by <c>report-drift</c> after the Xellent domain load; the intrinsic scan rows stay separate.
    /// </summary>
    public async Task<List<DriftRow>> DiffBillingAsync(
        IReadOnlyList<PayerInfoDto> payers,
        IReadOnlyDictionary<string, BuyerInvoicingReferences> expectedByIdentification,
        bool showAll,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<DriftRow>();
        foreach (var payer in payers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = payer.PayerIdentification;
            if (id == null || !expectedByIdentification.TryGetValue(id, out var expected) || expected == null)
                continue;

            var reasons = new List<string>();
            var detail = new List<string>();
            void Record(string reason, string label, string? settl, string? wanted)
            {
                var action = PaymentConfigDiff.Classify(settl, wanted);
                if (action == PaymentConfigDiffAction.None) return;
                reasons.Add(reason);
                detail.Add($"{label}='{settl}'→'{wanted}'({action})");
            }

            // EAN is on the paginated DTO — no extra fetch.
            Record("ElectronicNumber", "EAN", payer.ElectronicNumber, expected.ElectronicNumber);

            // The three identifiers live only in the raw JSON — fetch once, and only if expected.
            if (!string.IsNullOrEmpty(expected.OrderIdentifier)
                || !string.IsNullOrEmpty(expected.AgreementIdentifier)
                || !string.IsNullOrEmpty(expected.BuyerReferenceIdentifier))
            {
                var (json, _) = await _payerClient.GetRawByIdAsync(payer.Id, cancellationToken);
                var billing = json != null ? JsonNode.Parse(json)?["PayerBilling"]?.AsObject() : null;
                string? Get(string key) => billing?[key]?.GetValue<string?>();
                Record("OrderIdentifier", "OrderId", Get("OrderIdentifier"), expected.OrderIdentifier);
                Record("AgreementIdentifier", "AgreementId", Get("AgreementIdentifier"), expected.AgreementIdentifier);
                Record("BuyerReference", "BuyerRef", Get("BuyerReferenceIdentifier"), expected.BuyerReferenceIdentifier);
            }

            if (reasons.Count == 0)
            {
                if (showAll)
                    rows.Add(new DriftRow("Payer", id, payer.Id.ToString(), "OK", "", "PaymentConfig OK"));
                continue;
            }

            rows.Add(new DriftRow("Payer", id, payer.Id.ToString(), "NeedsUpdate",
                string.Join(",", reasons), string.Join(";", detail)));
        }
        return rows;
    }

    /// <summary>Returns the list of problem reasons for this payer (empty = OK).</summary>
    private async Task<IReadOnlyList<string>> ProcessPayerAsync(
        PayerInfoDto payer,
        Dictionary<Guid, (bool Exists, string? CustomerNumber)> customerCache,
        ReportPayersRunOptions options,
        List<string> rows,
        List<DriftRow> driftRows,
        CancellationToken cancellationToken)
    {
        if (!customerCache.TryGetValue(payer.CustomerId, out var customer))
        {
            var (cust, _) = await _customerClient.GetByIdAsync(payer.CustomerId, cancellationToken);
            customer = (cust != null, cust?.CustomerNumber);
            customerCache[payer.CustomerId] = customer;
        }

        var payerLabel = payer.PayerIdentification ?? payer.Id.ToString();
        var reasons = new List<string>();

        // Check 1 — customer linkage (ghost/orphan).
        if (!customer.Exists)
        {
            if (payer.PayerIdentification != null)
            {
                var (realCust, _) = await _customerClient.SearchByCustomerNumberAsync(
                    payer.PayerIdentification, cancellationToken: cancellationToken);
                reasons.Add(realCust != null ? "GhostCustomerId" : "OrphanPayer");
            }
            else
            {
                reasons.Add("OrphanPayer");
            }
        }

        // Check 2 — identification shape (catches free-text leaks like "Målernr. …").
        if (payer.PayerIdentification != null && !ValidPayerIdentification.IsMatch(payer.PayerIdentification))
            reasons.Add("InvalidIdentificationFormat");

        if (reasons.Count == 0)
        {
            Console.Error.WriteLine($"  Payer {payerLabel}  OK");
            if (options.ShowAll)
                AddPayerRow(rows, driftRows, payerLabel, payer.Id.ToString(), "OK", "", payer.CustomerId.ToString(), customer.CustomerNumber);
            return reasons;
        }

        var årsag = string.Join("+", reasons);
        Console.Error.WriteLine($"  Payer {payerLabel}  NeedsUpdate  {årsag}");
        AddPayerRow(rows, driftRows, payerLabel, payer.Id.ToString(), "NeedsUpdate", årsag, payer.CustomerId.ToString(), customer.CustomerNumber);
        return reasons;
    }

    private static void AddPayerRow(List<string> rows, List<DriftRow> driftRows,
        string identification, string settlId, string status, string årsager, string? customerId, string? customerNumber)
    {
        rows.Add(string.Join(";",
            CsvQuote("Payer"), CsvQuote(identification), CsvQuote(settlId),
            CsvQuote(status), CsvQuote(årsager), CsvQuote(customerId), CsvQuote(customerNumber)));

        var detail = new List<string>();
        if (customerId != null) detail.Add($"CustomerId={customerId}");
        if (customerNumber != null) detail.Add($"Customer_Number={customerNumber}");
        driftRows.Add(new DriftRow("Payer", identification, settlId, status, årsager, string.Join(";", detail)));
    }

    private static string CsvQuote(string? value)
    {
        var s = value ?? "";
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
