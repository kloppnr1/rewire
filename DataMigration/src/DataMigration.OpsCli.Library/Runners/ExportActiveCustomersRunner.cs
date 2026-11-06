using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;

namespace DataMigration.OpsCli.Runners;

public class ExportActiveCustomersRunOptions
{
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Only these Settl customer numbers (skips the env-wide metering point scan). For testing.</summary>
    public string[] CustomerNumbers { get; set; } = [];

    /// <summary>Stop after this many customers. 0 = all.</summary>
    public int Limit { get; set; }

    public int Parallelism { get; set; } = 8;

    public bool Pretty { get; set; }

    /// <summary>Replace personal data with consistent fake values (sample files for outside parties).
    /// See <see cref="ExportPseudonymiser"/>.</summary>
    public bool Pseudonymise { get; set; }
}

/// <summary>
/// Read-only carve-out of every active Settl customer as JSON, one object per customer with the
/// Settl responses nested as Settl returned them (no DTO projection, so no field is dropped).
///
/// <para>Active = linked today to at least one electricity metering point that is in delivery.
/// The population comes from the market-v3 metering point list (electricity only, child metering
/// points skipped, <c>InDelivery</c> true), then the customer link at today's date. Only those
/// metering points are linked in the export: customer, payer and contract links to anything else
/// (moved-out, water, not in delivery) are left out, and so are contracts with no active link.</para>
///
/// <para>Per customer: customer (v2 + v3), metering point links, payers with their metering point
/// links, each electricity metering point (market, billing, delivery statuses, consumer national
/// ids, settlement groups), contracts, price list associations, account info, account entries and
/// extra invoice lines. A failed read is recorded under <c>errors</c> on the customer; the run
/// continues.</para>
/// </summary>
public class ExportActiveCustomersRunner
{
    private const int LinkLookupBatchSize = 400;
    private const int PageSize = 500;

    private readonly ISettlRawReader _reader;
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly ISettlCustomerLinkLookup _linkLookup;
    private readonly ISettlCustomerClient _customerClient;
    private readonly Action<string> _log;

    public ExportActiveCustomersRunner(
        ISettlRawReader reader,
        ISettlMeteringPointClient meteringPointClient,
        ISettlCustomerLinkLookup linkLookup,
        ISettlCustomerClient customerClient,
        Action<string>? log = null)
    {
        _reader = reader;
        _meteringPointClient = meteringPointClient;
        _linkLookup = linkLookup;
        _customerClient = customerClient;
        _log = log ?? Console.Error.WriteLine;
    }

    public async Task<int> RunAsync(ExportActiveCustomersRunOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            _log("ERROR: --output is required");
            return 1;
        }

        var sw = Stopwatch.StartNew();
        var active = await SelectActiveAsync(ct);
        if (active == null) return 1;
        _activeMeteringPoints = active.Value.MeteringPointIds;
        var customerIds = options.CustomerNumbers.Length > 0
            ? await ResolveCustomerNumbersAsync(options.CustomerNumbers, ct)
            : active.Value.CustomerIds;
        if (customerIds == null) return 1;

        if (options.Limit > 0 && customerIds.Count > options.Limit)
            customerIds = customerIds.Take(options.Limit).ToList();
        _log($"Exporting {customerIds.Count} customer(s) [selection: {sw.Elapsed:mm\\:ss}]");

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var writeOptions = new JsonWriterOptions { Indented = options.Pretty, Encoder = JsonOutput.HumanReadable };
        await using var stream = File.Create(options.OutputPath);
        await using var writer = new Utf8JsonWriter(stream, writeOptions);
        var writeLock = new SemaphoreSlim(1, 1);

        writer.WriteStartObject();
        writer.WriteString("generatedAt", DateTime.UtcNow);
        writer.WriteString("settlBaseUrl", _reader.Settings.BaseUrl);
        writer.WriteString("selection", (options.CustomerNumbers.Length > 0
            ? "customer numbers given on the command line"
            : "customers linked today to an electricity metering point in delivery (market v3, children skipped)")
            + "; only links to active metering points are kept");
        writer.WriteBoolean("pseudonymised", options.Pseudonymise);
        writer.WritePropertyName("customers");
        writer.WriteStartArray();

        var done = 0;
        var withErrors = 0;
        var errorsBySection = new ConcurrentDictionary<string, int>();
        // Pseudonymising needs every real value in the file before it can scrub free text, so those
        // runs hold all customers in memory and write at the end. Sample runs are small.
        var pseudonymiser = options.Pseudonymise ? new ExportPseudonymiser() : null;
        var held = new List<JsonObject>();
        sw.Restart();
        await Parallel.ForEachAsync(customerIds,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = ct },
            async (customerId, token) =>
            {
                var customer = await ExportCustomerAsync(customerId, token);
                if (customer["errors"] is JsonArray { Count: > 0 } customerErrors)
                {
                    Interlocked.Increment(ref withErrors);
                    foreach (var section in customerErrors
                                 .Select(e => SectionKind(e?["section"]?.GetValue<string>()))
                                 .Distinct())
                        errorsBySection.AddOrUpdate(section, 1, (_, n) => n + 1);
                }

                await writeLock.WaitAsync(token);
                try
                {
                    if (pseudonymiser != null) held.Add(customer);
                    else customer.WriteTo(writer);
                    var n = ++done;
                    if (n % 100 == 0) await writer.FlushAsync(token);
                    if (n % 50 == 0 || n == customerIds.Count)
                        _log($"  {n}/{customerIds.Count} customer(s) [{sw.Elapsed:hh\\:mm\\:ss}], {withErrors} with read errors");
                }
                finally
                {
                    writeLock.Release();
                }
            });

        if (pseudonymiser != null)
        {
            pseudonymiser.Apply(held);
            foreach (var customer in held) customer.WriteTo(writer);
        }

        writer.WriteEndArray();
        writer.WriteNumber("customerCount", done);
        writer.WriteNumber("customersWithReadErrors", withErrors);
        writer.WriteEndObject();
        await writer.FlushAsync(ct);

        _log($"Wrote {done} customer(s) to {options.OutputPath} ({withErrors} with read errors)");
        if (pseudonymiser != null)
            _log($"Pseudonymised: {pseudonymiser.Replaced} value(s) replaced");
        foreach (var (section, count) in errorsBySection.OrderBy(kv => kv.Key))
            _log($"  read errors in {section}: {count} customer(s)");
        return withErrors > 0 ? 2 : 0;
    }

    // "mp <guid> market" and "payer <guid>" collapse to one kind, so the summary stays short.
    private static string SectionKind(string? section)
    {
        if (string.IsNullOrEmpty(section)) return "?";
        var parts = section.Split(' ');
        return parts.Length >= 3 ? $"{parts[0]} {parts[2]}" : parts[0];
    }

    private async Task<List<Guid>?> ResolveCustomerNumbersAsync(string[] numbers, CancellationToken ct)
    {
        var ids = new List<Guid>();
        foreach (var number in numbers)
        {
            var (customer, error) = await _customerClient.SearchByCustomerNumberAsync(number, cancellationToken: ct);
            if (customer == null)
            {
                _log($"ERROR: customer {number} not found in Settl{(error != null ? $": {error}" : "")}");
                return null;
            }
            ids.Add(customer.Id);
        }
        return ids;
    }

    /// <summary>
    /// The active population: electricity metering points in delivery that a customer is linked to
    /// today, and those customers. Every link list in the export is cut down to these metering points.
    /// </summary>
    private async Task<(List<Guid> CustomerIds, HashSet<Guid> MeteringPointIds)?> SelectActiveAsync(CancellationToken ct)
    {
        _log("Selecting electricity metering points in delivery (market v3)...");
        var selection = await SettlMeteringPointScanner.SelectAsync(
            _meteringPointClient, includeChildren: false, includeNotInDelivery: false, s => Console.Error.Write(s), ct);
        Console.Error.WriteLine();
        _log($"  {selection.MeteringPointIds.Count} in delivery of {selection.Scanned} scanned " +
             $"({selection.SkippedChildren} children, {selection.SkippedNotInDelivery} not in delivery skipped)");

        var customers = new HashSet<Guid>();
        var linked = new HashSet<Guid>();
        var today = DateTime.UtcNow.Date;
        foreach (var batch in selection.MeteringPointIds.Chunk(LinkLookupBatchSize))
        {
            var (entries, error) = await _linkLookup.FindCustomersForMeteringPointsAsync(batch, today, ct);
            if (error != null || entries == null)
            {
                _log($"ERROR: customer link lookup failed: {error}");
                return null;
            }
            foreach (var entry in entries)
            {
                if (entry.Customer is not { } customer) continue;
                customers.Add(customer.Id);
                foreach (var mp in entry.MeteringPoints ?? []) linked.Add(mp.MeteringPointId);
            }
        }

        var unlinked = selection.MeteringPointIds.Count(id => !linked.Contains(id));
        _log($"  {customers.Count} customer(s); {unlinked} metering point(s) in delivery have no customer link today");
        return (customers.OrderBy(id => id).ToList(), linked);
    }

    private HashSet<Guid> _activeMeteringPoints = [];

    /// <summary>
    /// True when a link (customer, payer or contract → metering point) points at an active metering
    /// point and is open today. Settl writes open-ended links as year 9999.
    /// </summary>
    private bool IsActiveLink(JsonNode? link, DateTime today)
    {
        if (!Guid.TryParse(link?["MeteringPointId"]?.GetValue<string>(), out var mpId)
            || !_activeMeteringPoints.Contains(mpId))
            return false;
        var start = ParseDate(link?["Start"]);
        var end = ParseDate(link?["End"]);
        return (start == null || start <= today) && (end == null || end > today);
    }

    private static DateTime? ParseDate(JsonNode? node) =>
        node?.GetValue<string>() is { } s
        && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private JsonArray ActiveLinks(JsonArray links, DateTime today) =>
        new(links.Where(l => IsActiveLink(l, today)).Select(l => l!.DeepClone()).ToArray());

    private async Task<JsonObject> ExportCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var billing = _reader.Settings.BillingBaseUrl;
        var market = _reader.Settings.MarketBaseUrl;
        var debtors = _reader.Settings.DebtorsBaseUrl;
        var errors = new ConcurrentBag<JsonObject>();

        async Task<JsonNode?> Get(string section, string url)
        {
            var (node, error) = await _reader.GetAsync(url, ct);
            if (error != null) errors.Add(new JsonObject { ["section"] = section, ["error"] = error });
            return node;
        }

        async Task<JsonArray> GetAll(string section, string url)
        {
            var (items, error) = await _reader.GetAllItemsAsync(url, PageSize, ct);
            if (error != null) errors.Add(new JsonObject { ["section"] = section, ["error"] = error });
            return items;
        }

        var customerV3 = await Get("customer", $"{billing}/api/v3/customers/{customerId}");
        var customerV2 = await Get("customerV2", $"{billing}/api/v2/customers/{customerId}");
        var today = DateTime.UtcNow.Date;
        var mpLinks = ActiveLinks(
            await GetAll("meteringPointLinks", $"{billing}/api/v2/customers/{customerId}/meteringPoints"), today);

        var payers = new JsonArray();
        foreach (var payerInfo in await GetAll("payers", $"{billing}/api/v1/Payers?CustomerId={customerId}&CountryCode=DK"))
        {
            var payerId = payerInfo?["Id"]?.GetValue<string>();
            if (payerId == null) continue;
            payers.Add(new JsonObject
            {
                ["payer"] = await Get($"payer {payerId}", $"{billing}/api/v1/Payers/{payerId}"),
                ["meteringPointLinks"] = ActiveLinks(await GetAll($"payer {payerId} meteringPointLinks",
                    $"{billing}/api/v1/Payers/{payerId}/MeteringPoints"), today),
            });
        }

        var meteringPoints = new JsonArray();
        var electricityMpIds = mpLinks
            .Select(l => l?["MeteringPointId"]?.GetValue<string>())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var mpId in electricityMpIds)
        {
            meteringPoints.Add(new JsonObject
            {
                ["meteringPointId"] = mpId,
                ["market"] = await Get($"mp {mpId} market", $"{market}/api/v3/meteringPoints/{mpId}"),
                ["consumerNationalIds"] = await Get($"mp {mpId} consumerNationalIds",
                    $"{market}/api/v4/meteringPoints/{mpId}/nationalIdentificationNumber"),
                ["deliveryStatuses"] = await Get($"mp {mpId} deliveryStatuses",
                    $"{market}/api/v3/meteringPoints/{mpId}/deliveryStatuses"),
                ["billing"] = await Get($"mp {mpId} billing", $"{billing}/api/v1/meteringPoints/{mpId}"),
                ["settlementGroups"] = await GetAll($"mp {mpId} settlementGroups",
                    $"{billing}/api/v1/settlementGroups?MeteringPointId={mpId}"),
            });
        }

        // A contract here is the contract on a metering point, so one left with no active metering
        // point is dropped.
        var contracts = new JsonArray();
        foreach (var contract in await GetAll("contracts",
                     $"{billing}/api/v1/contracts/customer/{customerId}/metering-point-contracts?CommodityTypes=Electricity"))
        {
            if (contract is not JsonObject c || c["MeteringPoints"] is not JsonArray contractMps) continue;
            var activeMps = ActiveLinks(contractMps, today);
            if (activeMps.Count == 0) continue;
            var copy = (JsonObject)c.DeepClone();
            copy["MeteringPoints"] = activeMps;
            contracts.Add(copy);
        }

        var (priceLists, priceListError) = await _reader.PostSearchAsync(
            $"{billing}/api/v2/customers/{customerId}/pricelists/search", new { }, ct);
        if (priceListError != null) errors.Add(new JsonObject { ["section"] = "priceLists", ["error"] = priceListError });

        var accountInfo = await Get("accountInfo",
            $"{debtors}/api/v1/debtorAccountEntries/customerAccountInfo?CustomerId={customerId}");
        var accountEntries = await GetAll("accountEntries",
            $"{debtors}/api/v1/debtorAccountEntries/findEntriesForCustomer?CustomerId={customerId}");
        var extraInvoiceLines = await GetAll("extraInvoiceLines",
            $"{billing}/api/v2/customers/{customerId}/extraInvoiceLines?countryCode=DK");

        return new JsonObject
        {
            ["customerId"] = customerId.ToString(),
            ["customerNumber"] = customerV3?["CustomerNumber"]?.DeepClone() ?? customerV2?["CustomerNumber"]?.DeepClone(),
            ["customer"] = customerV3,
            ["customerV2"] = customerV2,
            ["meteringPointLinks"] = mpLinks,
            ["payers"] = payers,
            ["meteringPoints"] = meteringPoints,
            ["contracts"] = contracts,
            ["priceLists"] = priceLists,
            ["accountInfo"] = accountInfo,
            ["accountEntries"] = accountEntries,
            ["extraInvoiceLines"] = extraInvoiceLines,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()),
        };
    }
}
