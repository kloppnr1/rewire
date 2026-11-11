using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataMigration.OpsCli.IntegrationTests.Fakes;

/// <summary>
/// An in-memory Settl tenant behind the real typed clients. It answers the routes the OpsCli
/// runners and the create pipeline use, keeps what was written, and gives it back on the next read
/// — so a create, a reconcile scan, a mutation and a reconcile-apply see one consistent Settl.
///
/// <para>Shapes follow what the clients read from the live API: PascalCase bodies, list endpoints
/// wrapped in <c>Items</c>, and the asymmetries the code depends on (payer billing is written as
/// <c>VatIdentificationNumber</c> and read back as <c>OrganizationIdentifier</c>; a payer PUT with
/// <c>PayerInformation: null</c> leaves the payer information alone). Where this fake decides how
/// Settl behaves, it pins an assumption rather than checking the live API.</para>
/// </summary>
public sealed class FakeSettlApi : HttpMessageHandler
{
    public const string BaseUrl = "https://settl.fake";
    private const string OpenEnd = "9999-12-31T23:59:58.999Z";

    private readonly object _gate = new();

    public Dictionary<Guid, JsonObject> Customers { get; } = [];
    public Dictionary<Guid, List<JsonObject>> CustomerMpLinks { get; } = [];
    public Dictionary<Guid, List<JsonObject>> CustomerPriceLists { get; } = [];
    public Dictionary<Guid, List<JsonObject>> ExtraInvoiceLines { get; } = [];
    public Dictionary<Guid, JsonObject> Payers { get; } = [];
    public Dictionary<Guid, List<JsonObject>> PayerMpLinks { get; } = [];
    public Dictionary<Guid, FakeMeteringPoint> MeteringPoints { get; } = [];
    public List<FakeContract> Contracts { get; } = [];
    public List<FakeSettlementGroup> SettlementGroups { get; } = [];
    public List<JsonObject> CustomerSegments { get; } = [];
    public List<JsonObject> PriceLists { get; } = [];
    public List<JsonObject> ProductElements { get; } = [];
    public List<(Guid Id, string Name)> MarketBalanceAreas { get; } = [];
    public List<(Guid Id, string Code)> GridAreas { get; } = [];
    public List<(HttpMethod Method, string PathAndQuery)> Requests { get; } = [];

    /// <summary>Routes this fake does not know. A test that hits one fails loudly instead of reading an empty answer.</summary>
    public List<string> UnknownRoutes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var uri = request.RequestUri!;
        lock (_gate)
        {
            Requests.Add((request.Method, uri.PathAndQuery));
            var query = ParseQuery(uri.Query);
            var (status, json) = Route(request.Method, uri.AbsolutePath, query, body);
            if (status == HttpStatusCode.NotFound && json == null)
                UnknownRoutes.Add($"{request.Method} {uri.PathAndQuery}");
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json ?? "", Encoding.UTF8, "application/json"),
            };
        }
    }

    // ---------------------------------------------------------------- routing

    private (HttpStatusCode, string?) Route(HttpMethod method, string path, Dictionary<string, List<string>> q, string? body)
    {
        var s = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // internal-billing
        if (s.Length >= 3 && s[0] == "internal-billing")
        {
            var api = $"{s[1]}/{s[2]}".ToLowerInvariant();
            var rest = s.Skip(3).ToArray();
            return api switch
            {
                "api/v2" when rest.Length > 0 && rest[0] == "customers" => Customer(method, rest[1..], q, body),
                "api/v3" when rest.Length > 0 && rest[0] == "customers" => CustomerV3(method, rest[1..], body),
                "api/v1" when rest.Length > 0 && rest[0].Equals("customerSegments", StringComparison.OrdinalIgnoreCase)
                    => Ok(new JsonArray([.. CustomerSegments.Select(c => c.DeepClone())])),
                "api/v1" when rest.Length > 0 && rest[0].Equals("Payers", StringComparison.OrdinalIgnoreCase) => Payer(method, rest[1..], q, body),
                "api/v1" when rest.Length == 1 && rest[0] == "priceLists" && method == HttpMethod.Get
                    => Ok(Page(PriceLists.Select(x => (JsonObject)x.DeepClone()), q)),
                "api/v1" when rest.Length == 1 && rest[0] == "productElements" && method == HttpMethod.Get
                    => Ok(Page(ProductElements.Select(x => (JsonObject)x.DeepClone()), q)),
                "api/v1" when rest.Length > 0 && rest[0] == "meteringPoints" => BillingMp(method, rest[1..], q, body),
                "api/v1" when rest.Length > 0 && rest[0].Equals("contracts", StringComparison.OrdinalIgnoreCase) => Contract(method, rest[1..], q, body),
                "api/v1" when rest.Length > 0 && rest[0] == "settlementGroups" => Group(method, rest[1..], q, body),
                _ => (HttpStatusCode.NotFound, null),
            };
        }
        if (s.Length >= 3 && s[0] == "internal-market")
        {
            var version = s[2];
            var rest = s.Skip(3).ToArray();
            if (rest.Length > 0 && rest[0] == "meteringPoints") return MarketMp(method, version, rest[1..], q, body);
            return (HttpStatusCode.NotFound, null);
        }
        if (s.Length >= 4 && s[0] == "market" && s[1] == "tenant-api")
        {
            if (s[3] == "market-balance-areas")
                return Ok(Find(MarketBalanceAreas.Select(a => new JsonObject { ["Id"] = a.Id, ["Name"] = a.Name })));
            if (s[3] == "metering-grid-areas" && s.Length == 4)
                return Ok(Find(GridAreas.Select(a => new JsonObject { ["Id"] = a.Id })));
            if (s[3] == "metering-grid-areas" && s.Length == 5)
            {
                var area = GridAreas.FirstOrDefault(a => a.Id == Guid.Parse(s[4]));
                return area.Id == Guid.Empty ? NotFound()
                    : Ok(new JsonObject { ["Id"] = area.Id, ["MeteringGridAreaIdentification"] = area.Code });
            }
        }
        if (s.Length >= 4 && s[0] == "commodities" && s[^1] == "water-metering-points")
            return Ok(Find([]));
        return (HttpStatusCode.NotFound, null);
    }

    // ---------------------------------------------------------------- customers

    private (HttpStatusCode, string?) Customer(HttpMethod m, string[] r, Dictionary<string, List<string>> q, string? body)
    {
        if (r.Length == 0 && m == HttpMethod.Post)
        {
            foreach (var c in Obj(body)["Customers"]!.AsArray())
            {
                var dto = Pascal(c!).AsObject();
                var id = Guid.Parse(dto["Id"]!.GetValue<string>());
                Customers[id] = dto;
            }
            return Created();
        }
        if (r.Length == 0 && m == HttpMethod.Get)
        {
            IEnumerable<JsonObject> hits = Customers.Values;
            if (Q(q, "CustomerNumber") is { } number)
                hits = hits.Where(c => (string?)c["CustomerNumber"] == number);
            return Ok(Page(hits.Select(CustomerInfo), q));
        }
        if (r.Length == 1 && r[0] == "CustomerInfosForMeteringPoints" && m == HttpMethod.Post)
            return Ok(CustomerInfosForMeteringPoints(Obj(body)));
        var id2 = Guid.Parse(r[0]);
        if (!Customers.TryGetValue(id2, out var customer)) return NotFound();
        if (r.Length == 1 && m == HttpMethod.Get) return Ok(customer.DeepClone());
        if (r.Length == 3 && r[1] == "pricelists" && r[2] == "search" && m == HttpMethod.Post)
            return Ok(new JsonObject
            {
                ["CustomerPriceListAssociations"] = new JsonArray([.. (CustomerPriceLists.GetValueOrDefault(id2) ?? [])
                    .Select(p => p.DeepClone())]),
            });
        if (r.Length == 2 && r[1] == "extraInvoiceLines" && m == HttpMethod.Get)
            return Ok(Page((ExtraInvoiceLines.GetValueOrDefault(id2) ?? []).Select(l => (JsonObject)l.DeepClone()), q));
        if (r.Length == 2 && r[1] == "meteringPoints")
        {
            var links = CustomerMpLinks.TryGetValue(id2, out var l) ? l : CustomerMpLinks[id2] = [];
            if (m == HttpMethod.Post)
            {
                foreach (var link in Obj(body)["CustomerMeteringPoints"]!.AsArray())
                    links.Add(Pascal(link!).AsObject());
                return Created();
            }
            return Ok(Find(links.Select(x => (JsonObject)x.DeepClone())));
        }
        if (r.Length == 2 && r[1] == "history") return Ok(new JsonObject { ["ChangeDates"] = new JsonArray() });
        if (r.Length == 2 && r[1] == "meteringPointsInOrganisation") return Ok(new JsonArray());
        return (HttpStatusCode.NotFound, null);
    }

    /// <summary>
    /// The customers linked to the given metering points on <c>RelationDate</c>, one entry per
    /// customer with the asked-for metering points it holds. A metering point nobody holds that day is
    /// left out (assumption — matches what the export's selection expects of the live API).
    /// </summary>
    private JsonArray CustomerInfosForMeteringPoints(JsonObject request)
    {
        var day = DateTime.Parse((string)request["RelationDate"]!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal).Date;
        var wanted = request["MeteringPointIds"]!.AsArray().Select(x => Guid.Parse(x!.GetValue<string>())).ToHashSet();
        var result = new JsonArray();
        foreach (var (customerId, links) in CustomerMpLinks)
        {
            var held = links
                .Where(l => Guid.TryParse((string?)l["MeteringPointId"], out var mp) && wanted.Contains(mp)
                            && OpenOn(l, day))
                .Select(l => (string)l["MeteringPointId"]!)
                .Distinct()
                .ToList();
            if (held.Count == 0) continue;
            result.Add(new JsonObject
            {
                ["Customer"] = new JsonObject { ["Id"] = customerId },
                ["MeteringPoints"] = new JsonArray([.. held.Select(mp => (JsonNode)new JsonObject { ["MeteringPointId"] = mp })]),
            });
        }
        return result;
    }

    private static bool OpenOn(JsonObject link, DateTime day)
    {
        static DateTime? Date(JsonNode? n) => n?.GetValue<string>() is { } s
            ? DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
            : null;
        var start = Date(link["Start"]);
        var end = Date(link["End"]);
        return (start == null || start <= day) && (end == null || end > day);
    }

    private (HttpStatusCode, string?) CustomerV3(HttpMethod m, string[] r, string? body)
    {
        var id = Guid.Parse(r[0]);
        if (!Customers.TryGetValue(id, out var customer)) return NotFound();
        if (r.Length == 1 && m == HttpMethod.Get)
        {
            var raw = (JsonObject)customer.DeepClone();
            // v3 always carries the segment, null when none is assigned (create cannot set it).
            if (!raw.ContainsKey("CustomerSegmentId")) raw["CustomerSegmentId"] = null;
            raw["Uri"] = $"{BaseUrl}/internal-billing/api/v3/customers/{id}";
            raw["Links"] = new JsonArray();
            return Ok(raw);
        }
        if (r.Length == 1 && m == HttpMethod.Put)
        {
            // The v3 customer PUT is wrapped: { Customer: {...}, TransactionId }. A flat body is refused.
            var wrapped = Obj(body);
            if (wrapped["Customer"] is not JsonObject replacement)
                return (HttpStatusCode.BadRequest, """{"error":"Customer is required"}""");
            var stored = (JsonObject)Pascal(replacement);
            stored.Remove("Uri");
            stored.Remove("Links");
            stored["Id"] = id;
            Customers[id] = stored;
            return NoContent();
        }
        if (r.Length == 2 && r[1] == "national-identification-number" && m == HttpMethod.Put)
        {
            customer["NationalIdentificationNumber"] = (string?)Obj(body)["NationalIdentificationNumber"];
            return NoContent();
        }
        return (HttpStatusCode.NotFound, null);
    }

    private static JsonObject CustomerInfo(JsonObject c) => new()
    {
        ["Id"] = c["Id"]?.DeepClone(),
        ["CustomerNumber"] = c["CustomerNumber"]?.DeepClone(),
        ["CountryCode"] = c["CountryCode"]?.DeepClone(),
        ["CustomerType"] = c["CustomerType"]?.DeepClone(),
        ["VatIdentificationNumber"] = c["VatIdentificationNumber"]?.DeepClone(),
        ["NationalIdentificationNumber"] = c["NationalIdentificationNumber"]?.DeepClone(),
        ["FullName"] = c["Address"]?["PostalAddress"]?["Name1"]?["FullName"]?.DeepClone(),
        ["Email"] = c["Address"]?["Email"]?.DeepClone(),
        ["PostalCode"] = c["Address"]?["PostalAddress"]?["PhysicalAddress"]?["PostalCode"]?.DeepClone(),
        ["City"] = c["Address"]?["PostalAddress"]?["PhysicalAddress"]?["City"]?.DeepClone(),
    };

    // ---------------------------------------------------------------- payers

    private (HttpStatusCode, string?) Payer(HttpMethod m, string[] r, Dictionary<string, List<string>> q, string? body)
    {
        if (r.Length == 0 && m == HttpMethod.Post)
        {
            foreach (var p in Obj(body)["Payers"]!.AsArray())
            {
                var dto = Pascal(p!).AsObject();
                var id = Guid.Parse(dto["Id"]!.GetValue<string>());
                if (dto["PayerBilling"] is JsonObject billing) StoreBilling(billing);
                Payers[id] = dto;
            }
            return Created();
        }
        if (r.Length == 0 && m == HttpMethod.Get)
        {
            IEnumerable<JsonObject> hits = Payers.Values;
            if (Q(q, "PayerIdentification") is { } ident)
                hits = hits.Where(p => (string?)p["PayerInformation"]?["PayerIdentifier"] == ident);
            if (Q(q, "CustomerId") is { } customerId)
                hits = hits.Where(p => (string?)p["CustomerId"] == customerId);
            return Ok(Page(hits.Select(PayerInfo), q));
        }
        if (r.Length == 1 && r[0] == "payersForMeteringPoints" && m == HttpMethod.Post)
        {
            var wanted = Obj(body)["MeteringPointIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var result = new JsonArray();
            foreach (var (payerId, payer) in Payers)
            {
                var links = (PayerMpLinks.GetValueOrDefault(payerId) ?? [])
                    .Where(l => wanted.Contains((string)l["MeteringPointId"]!)).ToList();
                if (links.Count == 0) continue;
                result.Add(new JsonObject
                {
                    ["Payer"] = payer.DeepClone(),
                    ["MeteringPoints"] = new JsonArray([.. links.Select(l => l.DeepClone())]),
                });
            }
            return Ok(result);
        }
        var id2 = Guid.Parse(r[0]);
        if (!Payers.TryGetValue(id2, out var stored)) return NotFound();
        if (r.Length == 1 && m == HttpMethod.Get)
        {
            var raw = (JsonObject)stored.DeepClone();
            raw["Uri"] = $"{BaseUrl}/internal-billing/api/v1/Payers/{id2}";
            raw["Links"] = new JsonArray();
            return Ok(raw);
        }
        if (r.Length == 1 && m == HttpMethod.Put)
        {
            // Flat body: fields at top level + TransactionId. PayerInformation null = leave it alone.
            var flat = Obj(body);
            if (flat.ContainsKey("Payer"))
                return (HttpStatusCode.BadRequest, """{"error":"wrapped payer body deletes the customer link"}""");
            foreach (var (key, value) in flat.ToList())
            {
                if (key is "TransactionId" or "Id" or "Uri" or "Links") continue;
                if (key == "PayerInformation" && value is null) continue;
                var copy = value?.DeepClone();
                if (key == "PayerBilling" && copy is JsonObject billing) StoreBilling(billing, stored["PayerBilling"] as JsonObject);
                stored[key] = copy;
            }
            return NoContent();
        }
        if (r.Length == 2 && r[1].Equals("History", StringComparison.OrdinalIgnoreCase))
            return Ok(new JsonObject { ["ChangeDates"] = new JsonArray(stored["PayerBilling"]?["Start"]?.DeepClone()) });
        if (r.Length == 2 && r[1].Equals("MeteringPoints", StringComparison.OrdinalIgnoreCase))
        {
            var links = PayerMpLinks.TryGetValue(id2, out var l) ? l : PayerMpLinks[id2] = [];
            if (m == HttpMethod.Post)
            {
                foreach (var link in Obj(body)["MeteringPoints"]!.AsArray())
                    links.Add(Pascal(link!).AsObject());
                return Created();
            }
            return Ok(Find(links.Select(x => (JsonObject)x.DeepClone())));
        }
        return (HttpStatusCode.NotFound, null);
    }

    /// <summary>
    /// Settl's payer billing asymmetry: written as VatIdentificationNumber, read as
    /// OrganizationIdentifier. An OrganizationIdentifier in a PUT body is ignored; a body without
    /// VatIdentificationNumber keeps the stored value (assumption — the live API was not probed for it).
    /// </summary>
    private static void StoreBilling(JsonObject billing, JsonObject? existing = null)
    {
        var vat = billing.ContainsKey("VatIdentificationNumber")
            ? billing["VatIdentificationNumber"]?.DeepClone()
            : existing?["OrganizationIdentifier"]?.DeepClone();
        billing.Remove("VatIdentificationNumber");
        billing.Remove("OrganizationIdentifier");
        billing["OrganizationIdentifier"] = vat;
        foreach (var key in new[] { "ElectronicNumber", "OrderIdentifier", "AgreementIdentifier", "BuyerReferenceIdentifier", "BookkeepingReference" })
            if (!billing.ContainsKey(key)) billing[key] = null;
    }

    private static JsonObject PayerInfo(JsonObject p) => new()
    {
        ["Id"] = p["Id"]?.DeepClone(),
        ["CountryCode"] = p["CountryCode"]?.DeepClone(),
        ["CustomerId"] = p["CustomerId"]?.DeepClone(),
        ["IsPrimaryCustomerPayer"] = p["IsPrimaryCustomerPayer"]?.DeepClone(),
        ["PayerIdentification"] = p["PayerInformation"]?["PayerIdentifier"]?.DeepClone(),
        ["InvoiceChannel"] = p["PayerBilling"]?["InvoiceChannel"]?.DeepClone(),
        ["PaymentChannel"] = p["PayerBilling"]?["PaymentChannel"]?.DeepClone(),
        ["ElectronicNumber"] = p["PayerBilling"]?["ElectronicNumber"]?.DeepClone(),
        ["Email"] = p["PayerBilling"]?["Email"]?.DeepClone(),
    };

    // ---------------------------------------------------------------- metering points

    private (HttpStatusCode, string?) MarketMp(HttpMethod m, string version, string[] r, Dictionary<string, List<string>> q, string? body)
    {
        if (r.Length == 0 && m == HttpMethod.Post && version == "v4")
        {
            foreach (var mp in Obj(body)["MeteringPointCreateObjectDtos"]!.AsArray())
            {
                var dto = Pascal(mp!).AsObject();
                if (dto["InstallationAddressCreateObject"] is JsonObject address)
                {
                    dto.Remove("InstallationAddressCreateObject");
                    address.Remove("EnrichAddress");
                    address.Remove("SkipAddressValidation");
                    dto["InstallationAddress"] = address;
                }
                var id = Guid.Parse(dto["Id"]!.GetValue<string>());
                MeteringPoints[id] = new FakeMeteringPoint(id, dto);
            }
            return Created();
        }
        if (r.Length == 0 && m == HttpMethod.Get)
        {
            IEnumerable<FakeMeteringPoint> hits = MeteringPoints.Values;
            if (Q(q, "MeteringPointIdentification") is { } ident)
                hits = hits.Where(p => p.Identification == ident);
            return Ok(Page(hits.Select(p => p.Info()), q));
        }
        var id2 = Guid.Parse(r[0]);
        if (!MeteringPoints.TryGetValue(id2, out var point)) return NotFound();
        if (r.Length == 1 && m == HttpMethod.Get) return Ok(point.Master());
        if (r.Length == 1 && m == HttpMethod.Put)
        {
            var master = Obj(body)["MeteringPoint"] as JsonObject;
            if (master == null) return (HttpStatusCode.BadRequest, """{"error":"MeteringPoint is required"}""");
            point.Market = (JsonObject)Pascal(master);
            point.Market["Id"] = id2;
            return NoContent();
        }
        if (r.Length == 2 && r[1] == "deliveryStatuses") return Ok(point.DeliveryStatuses());
        if (r.Length == 2 && r[1] == "history") return Ok(new JsonObject { ["ChangeDates"] = new JsonArray() });
        if (r.Length == 2 && r[1] == "nationalIdentificationNumber") return Ok(new JsonObject());
        return (HttpStatusCode.NotFound, null);
    }

    private (HttpStatusCode, string?) BillingMp(HttpMethod m, string[] r, Dictionary<string, List<string>> q, string? body)
    {
        if (r.Length == 0 && m == HttpMethod.Get)
        {
            IEnumerable<FakeMeteringPoint> hits = MeteringPoints.Values.Where(p => p.HasBilling);
            if (Q(q, "MeteringPointIdentification") is { } ident)
                hits = MeteringPoints.Values.Where(p => p.Identification == ident);
            return Ok(Page(hits.Select(p => p.BillingInfo()), q));
        }
        var id = Guid.Parse(r[0]);
        if (!MeteringPoints.TryGetValue(id, out var point)) return NotFound();
        if (r.Length == 1 && m == HttpMethod.Get)
            return Ok(point.Billing(Q(q, "EffectiveDate") is { } at ? DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture) : null));
        if (r.Length == 2 && r[1] == "meteringPoint" && m == HttpMethod.Put)
        {
            var update = Pascal(Obj(body)).AsObject();
            if (update["BillingConfiguration"] is JsonObject billing) point.SetBilling(billing);
            if (update["PaymentConfiguration"] is JsonObject payment) point.SetPayment(payment);
            return NoContent();
        }
        if (r.Length == 2 && r[1] == "history")
            return Ok(new JsonObject
            {
                ["BillingConfigurationDates"] = new JsonArray([.. point.BillingDates.Select(d => (JsonNode?)d)]),
                ["PaymentConfigurationDates"] = new JsonArray([.. point.PaymentDates.Select(d => (JsonNode?)d)]),
            });
        if (r.Length == 2 && r[1] == "exemptions" && m == HttpMethod.Post)
        {
            foreach (var e in Obj(body)["Exemptions"]!.AsArray()) point.Exemptions.Add(Pascal(e!).AsObject());
            return Created();
        }
        return (HttpStatusCode.NotFound, null);
    }

    // ---------------------------------------------------------------- contracts

    private (HttpStatusCode, string?) Contract(HttpMethod m, string[] r, Dictionary<string, List<string>> q, string? body)
    {
        if (r.Length == 0 && m == HttpMethod.Get)
        {
            IEnumerable<FakeContract> hits = Contracts;
            if (Q(q, "MeteringPointId") is { } mpId)
                hits = hits.Where(c => c.Links.Any(l => (string?)l["MeteringPointId"] == mpId));
            else
            {
                // Settl lists only electricity when the commodity filter is left out (documented behaviour).
                var commodity = Q(q, "CommodityType") ?? "Electricity";
                hits = hits.Where(c => c.CommodityType.Equals(commodity, StringComparison.OrdinalIgnoreCase));
            }
            return Ok(Find(hits.Select(c => c.Info())));
        }
        if (r.Length >= 3 && r[0] == "customer" && r[2] == "metering-point-contracts")
        {
            var customerId = Guid.Parse(r[1]);
            var mine = (CustomerMpLinks.GetValueOrDefault(customerId) ?? [])
                .Select(l => (string)l["MeteringPointId"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = Contracts
                .Select(c => (Contract: c, Links: c.Links.Where(l => mine.Contains((string)l["MeteringPointId"]!)).ToList()))
                .Where(x => x.Links.Count > 0)
                .Select(x => new JsonObject
                {
                    ["Id"] = x.Contract.Id,
                    ["CustomerId"] = customerId,
                    ["ContractName"] = x.Contract.Name,
                    ["ContractStart"] = "2020-01-01T00:00:00Z",
                    ["ContractEnd"] = null,
                    ["MeteringPoints"] = new JsonArray([.. x.Links.Select(l => (JsonNode)new JsonObject
                    {
                        ["MeteringPointId"] = l["MeteringPointId"]?.DeepClone(),
                        ["MeteringPointIdentification"] = l["MeteringPointIdentification"]?.DeepClone(),
                        ["CommodityType"] = x.Contract.CommodityType,
                        ["Start"] = l["Start"]?.DeepClone(),
                        ["End"] = l["End"]?.DeepClone(),
                    })]),
                });
            return Ok(Find(items));
        }
        var contract = Contracts.FirstOrDefault(c => c.Id == Guid.Parse(r[0]));
        if (contract == null) return NotFound();
        if (r.Length == 2 && r[1].Equals("ContractLines", StringComparison.OrdinalIgnoreCase)) return Ok(Find([]));
        if (r.Length == 2 && r[1] == "meteringPoints")
        {
            if (m == HttpMethod.Post)
            {
                foreach (var link in Obj(body)["ContractMeteringPoints"]!.AsArray())
                {
                    var stored = Pascal(link!).AsObject();
                    if (stored["End"] is null) stored["End"] = OpenEnd;
                    contract.Links.Add(stored);
                }
                return Created();
            }
            IEnumerable<JsonObject> links = contract.Links;
            if (Q(q, "MeteringPointId") is { } mpId)
                links = links.Where(l => (string?)l["MeteringPointId"] == mpId);
            return Ok(Find(links.Select(l => (JsonObject)l.DeepClone())));
        }
        if (r.Length == 3 && r[1] == "meteringPoints" && m == HttpMethod.Delete)
        {
            if (Q(q, "TransactionId") == null) return (HttpStatusCode.BadRequest, """{"error":"TransactionId is required"}""");
            var removed = contract.Links.RemoveAll(l => (string?)l["Id"] == r[2]);
            return removed == 0 ? NotFound() : NoContent();
        }
        return (HttpStatusCode.NotFound, null);
    }

    // ---------------------------------------------------------------- settlement groups

    private (HttpStatusCode, string?) Group(HttpMethod m, string[] r, Dictionary<string, List<string>> q, string? body)
    {
        if (r.Length == 0 && m == HttpMethod.Get)
        {
            IEnumerable<FakeSettlementGroup> hits = SettlementGroups;
            if (Q(q, "GroupName") is { } name)
                hits = hits.Where(g => g.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (Q(q, "MeteringPointId") is { } mpId)
                hits = hits.Where(g => g.MeteringPoints.Contains(Guid.Parse(mpId)));
            return Ok(Find(hits.Select(g => g.Info())));
        }
        var group = SettlementGroups.FirstOrDefault(g => g.Id == Guid.Parse(r[0]));
        if (group == null) return NotFound();
        if (r.Length == 2 && r[1] == "customers")
        {
            if (m == HttpMethod.Post)
            {
                foreach (var c in Obj(body)["Customers"]!.AsArray())
                    group.Customers.Add(Guid.Parse((string)c!["CustomerId"]!));
                return Created();
            }
            return Ok(Find(group.Customers.Select(id => new JsonObject { ["Id"] = id, ["IncludeSubsidiaries"] = false })));
        }
        if (r.Length == 2 && r[1] == "meteringPoints")
        {
            if (m == HttpMethod.Post)
            {
                foreach (var id in Obj(body)["MeteringPoints"]!.AsArray()) group.MeteringPoints.Add(Guid.Parse((string)id!));
                return Created();
            }
            return Ok(Find(group.MeteringPoints.Select(id => new JsonObject { ["Id"] = id })));
        }
        return (HttpStatusCode.NotFound, null);
    }

    // ---------------------------------------------------------------- helpers

    private static (HttpStatusCode, string?) Ok(JsonNode node) => (HttpStatusCode.OK, node.ToJsonString());
    private static (HttpStatusCode, string?) Created() => (HttpStatusCode.Created, "");
    private static (HttpStatusCode, string?) NoContent() => (HttpStatusCode.NoContent, "");
    private static (HttpStatusCode, string?) NotFound() => (HttpStatusCode.NotFound, """{"error":"not found"}""");

    private static JsonObject Find(IEnumerable<JsonObject> items)
    {
        var list = items.ToList();
        return new JsonObject
        {
            ["Items"] = new JsonArray([.. list]),
            ["CountResult"] = new JsonObject { ["Count"] = list.Count },
            ["NextUri"] = null,
        };
    }

    private static JsonObject Page(IEnumerable<JsonObject> items, Dictionary<string, List<string>> q)
    {
        var all = items.ToList();
        var skip = int.TryParse(Q(q, "Skip"), out var s) ? s : 0;
        var limit = int.TryParse(Q(q, "Limit"), out var l) ? l : 100;
        return new JsonObject
        {
            ["Items"] = new JsonArray([.. all.Skip(skip).Take(limit)]),
            ["CountResult"] = new JsonObject { ["Count"] = all.Count },
            ["NextUri"] = null,
        };
    }

    private static string? Q(Dictionary<string, List<string>> q, string key)
        => q.TryGetValue(key, out var values) ? values[0] : null;

    private static Dictionary<string, List<string>> ParseQuery(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(value);
        }
        return result;
    }

    private static JsonObject Obj(string? body) => JsonNode.Parse(body ?? "{}")!.AsObject();

    /// <summary>Every key with an upper-case first letter — the market billing update is sent camelCase.</summary>
    internal static JsonNode Pascal(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(
            kv.Key.Length > 0 ? char.ToUpperInvariant(kv.Key[0]) + kv.Key[1..] : kv.Key,
            kv.Value == null ? null : Pascal(kv.Value)))),
        JsonArray a => new JsonArray([.. a.Select(x => x == null ? null : Pascal(x))]),
        _ => node.DeepClone(),
    };

    // ---------------------------------------------------------------- entity types

    public sealed class FakeMeteringPoint(Guid id, JsonObject market)
    {
        public Guid Id { get; } = id;
        public JsonObject Market { get; set; } = market;

        /// <summary>Billing versions by Start. A PUT with an existing Start updates that version in
        /// place; a new Start opens a new version (the billing-PUT rule the runners rely on).</summary>
        public SortedDictionary<DateTimeOffset, JsonObject> BillingVersions { get; } = [];
        public SortedDictionary<DateTimeOffset, JsonObject> PaymentVersions { get; } = [];
        public List<JsonObject> Exemptions { get; } = [];
        public JsonObject? SettlementInfo { get; set; }
        public JsonObject? BillingConfiguration => BillingVersions.Count == 0 ? null : BillingVersions.Last().Value;
        public JsonObject? PaymentConfiguration => PaymentVersions.Count == 0 ? null : PaymentVersions.Last().Value;
        public IEnumerable<string> BillingDates => BillingVersions.Values.Select(v => (string)v["Start"]!);
        public IEnumerable<string> PaymentDates => PaymentVersions.Values.Select(v => (string)v["Start"]!);
        public bool HasBilling => BillingVersions.Count > 0;
        public string? Identification => (string?)Market["Identification"]?["MeteringPointIdentification"];
        public bool InDelivery => Market["DeliveryStatus"]?["InDelivery"]?.GetValue<bool>() ?? false;

        public void SetBilling(JsonObject billing) => Put(BillingVersions, billing);
        public void SetPayment(JsonObject payment) => Put(PaymentVersions, payment);

        private static void Put(SortedDictionary<DateTimeOffset, JsonObject> versions, JsonObject section)
        {
            var start = DateTimeOffset.Parse((string)section["Start"]!, System.Globalization.CultureInfo.InvariantCulture);
            versions[start] = (JsonObject)section.DeepClone();
        }

        private static JsonObject? At(SortedDictionary<DateTimeOffset, JsonObject> versions, DateTimeOffset? at) =>
            at is null ? versions.Values.LastOrDefault()
                : versions.LastOrDefault(v => v.Key <= at.Value.AddHours(23)).Value ?? versions.Values.FirstOrDefault();

        public JsonObject Master()
        {
            var master = (JsonObject)Market.DeepClone();
            master["Id"] = Id;
            return master;
        }

        public JsonObject Info() => new()
        {
            ["Id"] = Id,
            ["Uri"] = $"{BaseUrl}/internal-market/api/v3/meteringPoints/{Id}",
            ["MeteringPointIdentification"] = Identification,
            ["InstallationNo"] = Market["Identification"]?["InstallationNumber"]?.DeepClone(),
            ["CountryCode"] = Market["CountryCode"]?.DeepClone(),
            ["SettlementMethodCode"] = Market["SettlementMethod"]?["SettlementMethod"]?.DeepClone(),
            ["ConnectionStatus"] = Market["ConnectionInfo"]?["ConnectionStatus"]?.DeepClone(),
            ["MeteringPointType"] = Market["MeterCharacteristics"]?["MeteringPointType"]?.DeepClone(),
            ["MeteringPointSubType"] = Market["MeterCharacteristics"]?["MeteringPointSubType"]?.DeepClone(),
            ["InDelivery"] = InDelivery,
            ["MarketBalanceAreaId"] = Market["Grid"]?["MarketBalanceAreaId"]?.DeepClone(),
            ["MeteringGridAreaId"] = Market["Grid"]?["MeteringGridAreaId"]?.DeepClone(),
            ["EstimatedAnnualVolume"] = Market["MeterCharacteristics"]?["EstimatedAnnualVolume"]?.DeepClone(),
            ["InstallationAddress"] = Market["InstallationAddress"]?["Address"]?.DeepClone(),
            ["Meters"] = new JsonArray([.. (Market["MeterCharacteristics"]?["Meters"] as JsonArray ?? [])
                .Select(meter => (JsonNode)new JsonObject { ["MeterIdentification"] = meter?["MeterIdentifier"]?.DeepClone() })]),
            ["CurrentOrNextDeliveryPeriod"] = InDelivery
                ? new JsonObject { ["Start"] = Market["DeliveryStatus"]?["Start"]?.DeepClone(), ["End"] = OpenEnd }
                : null,
            ["MostRecentCompletedDeliveryPeriod"] = null,
        };

        public JsonObject BillingInfo() => new()
        {
            ["Id"] = Id,
            ["MeteringPointIdentification"] = Identification,
            ["JointInvoice"] = BillingConfiguration?["JointInvoice"]?.DeepClone(),
            ["JointInvoiceNumber"] = BillingConfiguration?["JointInvoiceNumber"]?.DeepClone(),
        };

        public JsonObject Billing(DateTimeOffset? at = null) => new()
        {
            ["Id"] = Id,
            // The export projection reads the identification from billing (CustomerExportProjection).
            ["MeteringPointIdentification"] = Identification,
            ["BillingConfiguration"] = At(BillingVersions, at)?.DeepClone(),
            ["PaymentConfiguration"] = At(PaymentVersions, at)?.DeepClone(),
            ["Exemptions"] = new JsonArray([.. Exemptions.Select(e => e.DeepClone())]),
            ["SettlementInfo"] = SettlementInfo?.DeepClone(),
        };

        public JsonObject DeliveryStatuses() => new()
        {
            ["Id"] = Id,
            ["DeliveryStatuses"] = new JsonArray(new JsonObject
            {
                ["InDelivery"] = InDelivery,
                ["Period"] = new JsonObject { ["Start"] = Market["DeliveryStatus"]?["Start"]?.DeepClone(), ["End"] = OpenEnd },
                ["StartReason"] = InDelivery ? "ChangeOfSupplier" : "Na",
                ["StopReason"] = "Na",
            }),
        };
    }

    public sealed class FakeContract(Guid id, string name, string commodityType)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public string CommodityType { get; } = commodityType;
        public List<JsonObject> Links { get; } = [];

        public JsonObject Info() => new()
        {
            ["Id"] = Id,
            ["ContractName"] = Name,
            ["CommodityType"] = CommodityType,
            ["CountryCode"] = "DK",
            ["ContractPeriod"] = new JsonObject { ["Start"] = "2020-01-01T00:00:00Z", ["End"] = OpenEnd },
        };
    }

    public sealed class FakeSettlementGroup(Guid id, string name)
    {
        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public HashSet<Guid> Customers { get; } = [];
        public HashSet<Guid> MeteringPoints { get; } = [];

        public JsonObject Info() => new()
        {
            ["Id"] = Id,
            ["Uri"] = $"{BaseUrl}/internal-billing/api/v1/settlementGroups/{Id}",
            ["CountryCode"] = "DK",
            ["GroupName"] = Name,
            ["IsActive"] = true,
        };
    }

    internal static string Json(object value) => JsonSerializer.Serialize(value);
}
