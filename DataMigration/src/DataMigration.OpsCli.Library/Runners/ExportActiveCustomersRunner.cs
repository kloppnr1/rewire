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
/// points skipped, <c>InDelivery</c> true), then the customer link at today's date; with a limit the
/// scan stops once enough customers are found. Per customer the same rule is checked again on each
/// linked metering point's own market data. Only those
/// metering points are linked in the export: customer, payer and contract links to anything else
/// (moved-out, water, not in delivery) are left out, and so are contracts with no active link.</para>
///
/// <para>Per customer: customer (v2 + v3), metering point links, payers with their metering point
/// links, each electricity metering point (market, billing, delivery statuses, consumer national
/// ids, settlement groups), contracts, price list associations and extra invoice lines. A failed read is recorded under <c>errors</c> on the customer; the run
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
        var customerIds = options.CustomerNumbers.Length > 0
            ? await ResolveCustomerNumbersAsync(options.CustomerNumbers, ct)
            : await SelectActiveCustomersAsync(options.Limit, ct);
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
                JsonObject customer;
                try
                {
                    customer = await ExportCustomerAsync(customerId, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    // Last line of defence: one customer that cannot be read is written with its error,
                    // the rest of the run goes on.
                    customer = new JsonObject
                    {
                        ["customerId"] = customerId.ToString(),
                        ["errors"] = new JsonArray(new JsonObject { ["section"] = "customer", ["error"] = $"{ex.GetType().Name}: {ex.Message}" }),
                    };
                }
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
    /// Customers linked today to an electricity metering point in delivery (market v3, children
    /// skipped). Pages through the metering points and looks up their customers page by page, so a
    /// <paramref name="limit"/> stops the scan as soon as enough customers are found.
    /// </summary>
    private async Task<List<Guid>?> SelectActiveCustomersAsync(int limit, CancellationToken ct)
    {
        _log(limit > 0
            ? $"Selecting the first {limit} customer(s) with an electricity metering point in delivery..."
            : "Selecting customers with an electricity metering point in delivery (market v3)...");

        var customers = new List<Guid>();
        var seenCustomers = new HashSet<Guid>();
        var today = DateTime.UtcNow.Date;
        int skip = 0, total = 0, scanned = 0, selected = 0, unlinked = 0;

        while (limit <= 0 || customers.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var pageSkip = skip;
            var (items, count, err) = await RetryOnNetworkErrorAsync(
                () => _meteringPointClient.FindMeteringPointsAsync(PageSize, pageSkip, doCount: pageSkip == 0, ct), ct);
            if (err != null)
            {
                _log($"ERROR: Settl FindMeteringPoints failed at skip {skip}: {err}");
                return null;
            }
            if (skip == 0 && count > 0) total = count;
            if (items.Count == 0) break;
            scanned += items.Count;

            var inDelivery = items
                .Where(mp => mp.InDelivery == true
                             && (mp.ParentMeteringPoint?.Id is not { } parent || parent == Guid.Empty))
                .Select(mp => mp.Id)
                .ToList();
            selected += inDelivery.Count;

            foreach (var batch in inDelivery.Chunk(LinkLookupBatchSize))
            {
                var (entries, error) = await RetryOnNetworkErrorAsync(
                    () => _linkLookup.FindCustomersForMeteringPointsAsync(batch, today, ct), ct);
                if (error != null || entries == null)
                {
                    _log($"ERROR: customer link lookup failed: {error}");
                    return null;
                }
                var linked = new HashSet<Guid>();
                foreach (var entry in entries)
                {
                    if (entry.Customer is not { } customer) continue;
                    if (seenCustomers.Add(customer.Id)) customers.Add(customer.Id);
                    foreach (var mp in entry.MeteringPoints ?? []) linked.Add(mp.MeteringPointId);
                }
                unlinked += batch.Count(id => !linked.Contains(id));
            }

            Console.Error.Write($"\r  Scanned {scanned}{(total > 0 ? $"/{total}" : "")} metering point(s), {customers.Count} customer(s)");
            skip += PageSize;
            if (items.Count < PageSize || (total > 0 && skip >= total)) break;
        }
        Console.Error.WriteLine();

        if (limit > 0 && customers.Count > limit) customers = customers.Take(limit).ToList();
        _log($"  {selected} in delivery of {scanned} scanned; {customers.Count} customer(s); " +
             $"{unlinked} metering point(s) in delivery have no customer link today");
        return customers;
    }

    /// <summary>The selection scan's calls go through the typed clients, which throw on a dropped
    /// connection. Retry those a few times before giving up the run.</summary>
    private static async Task<T> RetryOnNetworkErrorAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Exception ex) when (attempt < 6 && !ct.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    /// <summary>
    /// True when a link (customer, payer or contract → metering point) points at one of the customer's
    /// active metering points and is open today. Settl writes open-ended links as year 9999.
    /// </summary>
    private static bool IsActiveLink(JsonNode? link, DateTime today, HashSet<Guid> active) =>
        Guid.TryParse(link?["MeteringPointId"]?.GetValue<string>(), out var mpId)
        && active.Contains(mpId)
        && IsOpen(link, today);

    private static bool IsOpen(JsonNode? link, DateTime today)
    {
        var start = ParseDate(link?["Start"]);
        var end = ParseDate(link?["End"]);
        return (start == null || start <= today) && (end == null || end > today);
    }

    /// <summary>In delivery and not a child, read from the metering point's own market data — the same
    /// two facts the scan filters the market list on.</summary>
    private static bool IsActiveMeteringPoint(JsonNode? market)
    {
        if (market?["DeliveryStatus"]?["InDelivery"]?.GetValue<bool>() != true) return false;
        var parent = market["Relation"]?["ParentMeteringPointId"]?.GetValue<string>();
        return !Guid.TryParse(parent, out var parentId) || parentId == Guid.Empty;
    }

    private static DateTime? ParseDate(JsonNode? node) =>
        node?.GetValue<string>() is { } s
        && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

    private static JsonArray ActiveLinks(JsonArray links, DateTime today, HashSet<Guid> active) =>
        new(links.Where(l => IsActiveLink(l, today, active)).Select(l => l!.DeepClone()).ToArray());

    private async Task<JsonObject> ExportCustomerAsync(Guid customerId, CancellationToken ct)
    {
        var billing = _reader.Settings.BillingBaseUrl;
        var market = _reader.Settings.MarketBaseUrl;
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
        var allLinks = await GetAll("meteringPointLinks", $"{billing}/api/v2/customers/{customerId}/meteringPoints");

        // Active = an electricity metering point the customer is linked to today that is in delivery
        // and not a child. Decided per metering point from its own market data, so no env-wide scan
        // is needed; every link list below is cut down to this set.
        var active = new HashSet<Guid>();
        var meteringPoints = new JsonArray();
        var candidates = allLinks
            .Where(l => string.Equals(l?["CommodityType"]?.GetValue<string>(), "Electricity", StringComparison.OrdinalIgnoreCase)
                        && IsOpen(l, today))
            .Select(l => Guid.TryParse(l?["MeteringPointId"]?.GetValue<string>(), out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct();
        foreach (var mpId in candidates)
        {
            var marketData = await Get($"mp {mpId} market", $"{market}/api/v3/meteringPoints/{mpId}");
            if (!IsActiveMeteringPoint(marketData)) continue;
            active.Add(mpId);
            meteringPoints.Add(new JsonObject
            {
                ["meteringPointId"] = mpId.ToString(),
                ["market"] = marketData,
                ["consumerNationalIds"] = await Get($"mp {mpId} consumerNationalIds",
                    $"{market}/api/v4/meteringPoints/{mpId}/nationalIdentificationNumber"),
                ["deliveryStatuses"] = await Get($"mp {mpId} deliveryStatuses",
                    $"{market}/api/v3/meteringPoints/{mpId}/deliveryStatuses"),
                ["billing"] = await Get($"mp {mpId} billing", $"{billing}/api/v1/meteringPoints/{mpId}"),
                ["settlementGroups"] = await GetAll($"mp {mpId} settlementGroups",
                    $"{billing}/api/v1/settlementGroups?MeteringPointId={mpId}"),
            });
        }

        var mpLinks = ActiveLinks(allLinks, today, active);

        var payers = new JsonArray();
        foreach (var payerInfo in await GetAll("payers", $"{billing}/api/v1/Payers?CustomerId={customerId}&CountryCode=DK"))
        {
            var payerId = payerInfo?["Id"]?.GetValue<string>();
            if (payerId == null) continue;
            payers.Add(new JsonObject
            {
                ["payer"] = await Get($"payer {payerId}", $"{billing}/api/v1/Payers/{payerId}"),
                ["meteringPointLinks"] = ActiveLinks(await GetAll($"payer {payerId} meteringPointLinks",
                    $"{billing}/api/v1/Payers/{payerId}/MeteringPoints"), today, active),
            });
        }

        // A contract here is the contract on a metering point, so one left with no active metering
        // point is dropped.
        var contracts = new JsonArray();
        foreach (var contract in await GetAll("contracts",
                     $"{billing}/api/v1/contracts/customer/{customerId}/metering-point-contracts?CommodityTypes=Electricity"))
        {
            if (contract is not JsonObject c || c["MeteringPoints"] is not JsonArray contractMps) continue;
            var activeMps = ActiveLinks(contractMps, today, active);
            if (activeMps.Count == 0) continue;
            var copy = (JsonObject)c.DeepClone();
            copy["MeteringPoints"] = activeMps;
            contracts.Add(copy);
        }

        var (priceLists, priceListError) = await _reader.PostSearchAsync(
            $"{billing}/api/v2/customers/{customerId}/pricelists/search", new { }, ct);
        if (priceListError != null) errors.Add(new JsonObject { ["section"] = "priceLists", ["error"] = priceListError });
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
            ["extraInvoiceLines"] = extraInvoiceLines,
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode)e).ToArray()),
        };
    }
}
