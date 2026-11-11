using System.Text.Json;
using System.Text.Json.Nodes;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Cuts one raw export-active-customers customer (the Settl responses as Settl returned them) down to
/// the fields of the approved customer examples (private Customer_405013.xlsx, approved 2026-11-06;
/// business Customer_397697, 2026-11-06): customer, metering points, contracts on them, payers,
/// customer price lists and extra invoice lines.
///
/// <para>Settl's own ids (GUIDs for segment, grid area, price area, supplier, product element) are
/// left out; where the runner resolved a name for one (under <c>names</c>), the name is used. Every null, empty string, empty list and empty object is left out, so a field that is
/// absent means Settl had no value for it. The customer's own numbers stay: customer number, payer
/// number, metering point id, installation number and the DAR address id.</para>
/// </summary>
public static class CustomerExportProjection
{
    public static JsonObject Project(JsonObject raw)
    {
        var customer = raw["customer"];
        var customerV2 = raw["customerV2"];
        var address = customer?["Address"];
        var postal = address?["PostalAddress"];
        var meteringPoints = raw["meteringPoints"] as JsonArray ?? [];
        var contracts = raw["contracts"] as JsonArray ?? [];

        var identificationBySettlId = meteringPoints
            .Where(m => m?["meteringPointId"] != null)
            .GroupBy(m => Str(m!["meteringPointId"])!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Str(g.First()!["billing"]?["MeteringPointIdentification"]), StringComparer.OrdinalIgnoreCase);

        var result = new JsonObject
        {
            ["customerNumber"] = Value(customer?["CustomerNumber"]),
            ["customerType"] = Value(customer?["CustomerType"]),
            ["cpr"] = Value(customerV2?["NationalIdentificationNumber"]),
            ["cvr"] = Value(customer?["VatIdentificationNumber"]),
            ["name"] = Value(postal?["Name1"]?["FullName"]),
            ["supplementaryName"] = Value(postal?["Name2"]?["FullName"]),
            ["phone"] = Value(address?["Phone"]),
            ["cellphone"] = Value(address?["Cellphone"]),
            ["email"] = Value(address?["Email"]),
            ["customerGroup"] = Value(customer?["CrmData"]?["CustomerGroupName"]),
            ["customerSegment"] = Value(raw["names"]?["customerSegment"]),
            ["manualApprovalRequired"] = Value(customer?["CreditInfo"]?["ManualApprovalRequired"]),
            ["address"] = Address(postal?["PhysicalAddress"]),
            ["meteringPoints"] = new JsonArray(meteringPoints.Select(m => (JsonNode?)MeteringPoint(m, contracts)).ToArray()),
            ["payers"] = new JsonArray((raw["payers"] as JsonArray ?? [])
                .Select(p => (JsonNode?)Payer(p, identificationBySettlId)).ToArray()),
            ["contracts"] = new JsonArray(contracts.Select(c => (JsonNode?)Contract(c)).ToArray()),
            ["priceLists"] = new JsonArray((raw["priceLists"]?["CustomerPriceListAssociations"] as JsonArray ?? [])
                .Select(a => (JsonNode?)PriceList(a, contracts)).ToArray()),
            ["paymentAgreements"] = new JsonArray((raw["paymentAgreements"] as JsonArray ?? [])
                .Select(a => (JsonNode?)new JsonObject
                {
                    ["meteringPoint"] = Value(a?["MeteringPointIdentification"]),
                    ["jointInvoiceNumber"] = Value(a?["JointInvoiceNumber"]),
                    ["externalCustomerNumber"] = Value(a?["ExternalCustomerNumber"]),
                    ["paymentMethod"] = Value(a?["PaymentMethod"]),
                    ["agreementNumber"] = Value(a?["PaymentAgreementNumber"]),
                    ["agreementType"] = Value(a?["AgreementType"]),
                    ["lastUpdated"] = Value(a?["LastUpdated"]),
                }).ToArray()),
            ["extraInvoiceLines"] = new JsonArray((raw["extraInvoiceLines"] as JsonArray ?? [])
                .Select(l => (JsonNode?)ExtraInvoiceLine(l)).ToArray()),
            ["errors"] = raw["errors"]?.DeepClone(),
        };
        return (JsonObject)Prune(result)!;
    }

    private static JsonObject MeteringPoint(JsonNode? m, JsonArray contracts)
    {
        var market = m?["market"];
        var billing = m?["billing"];
        var consumer = market?["Consumer"];
        var delivery = market?["DeliveryStatus"];
        var period = delivery?["CalculatedDeliveryPeriod"];
        var characteristics = market?["MeterCharacteristics"];
        var nationalIds = m?["consumerNationalIds"];
        var settlId = Str(m?["meteringPointId"]);

        return new JsonObject
        {
            ["meteringPointId"] = Value(billing?["MeteringPointIdentification"] ?? market?["Identification"]?["MeteringPointIdentification"]),
            ["installationNumber"] = Value(billing?["InstallationNumber"] ?? market?["Identification"]?["InstallationNumber"]),
            ["installationAddress"] = Address(market?["InstallationAddress"]?["Address"]),
            ["consumer"] = new JsonObject
            {
                ["name"] = Value(consumer?["FirstConsumerParty"]?["Name"]?["FullName"]),
                ["cpr"] = Value(nationalIds?["FirstConsumerPartyNationalIdentificationNumber"]),
                ["cvr"] = Value(consumer?["FirstConsumerParty"]?["VatIdentificationNumber"]),
                ["secondName"] = Value(consumer?["SecondConsumerParty"]?["Name"]?["FullName"]),
                ["secondCpr"] = Value(nationalIds?["SecondConsumerPartyNationalIdentificationNumber"]),
                ["supplyStart"] = Date(consumer?["SupplyStart"]),
                ["category"] = Value(consumer?["ConsumerCategory"]),
                ["electricalHeating"] = Value(consumer?["ElectricalHeating"]),
            },
            ["deliveryStatus"] = new JsonObject
            {
                ["inDelivery"] = Value(delivery?["InDelivery"]),
                ["start"] = Date(period?["Start"]),
                ["stop"] = OpenEnd(period?["Stop"]),
                ["startReason"] = Value(period?["StartReason"]),
                ["stopReason"] = Value(period?["StopReason"]),
            },
            ["connectionStatus"] = Value(market?["ConnectionInfo"]?["ConnectionStatus"]),
            ["remark"] = Value(market?["Notes"]?["Remark"]),
            ["priceArea"] = Value(m?["names"]?["priceArea"]),
            ["gridArea"] = Value(m?["names"]?["gridArea"]),
            ["settlementMethod"] = Value(market?["SettlementMethod"]?["SettlementMethod"]),
            ["type"] = Value(characteristics?["MeteringPointType"]),
            ["subType"] = Value(characteristics?["MeteringPointSubType"]),
            ["meters"] = new JsonArray((characteristics?["Meters"] as JsonArray ?? [])
                .Select(x => (JsonNode?)new JsonObject
                {
                    ["meterNumber"] = Value(x?["MeterIdentifier"]),
                    ["meterType"] = Value(x?["MeterType"]),
                    ["reading"] = Value(x?["MeterReadingOccurrence"]),
                }).ToArray()),
            ["estimatedAnnualConsumption"] = new JsonObject
            {
                ["value"] = Value(characteristics?["EstimatedAnnualVolume"]?["Value"]),
                ["unit"] = Value(characteristics?["EstimatedAnnualVolume"]?["Unit"]),
            },
            ["contracts"] = new JsonArray(contracts
                .SelectMany(k => (k?["MeteringPoints"] as JsonArray ?? [])
                    .Where(l => string.Equals(Str(l?["MeteringPointId"]), settlId, StringComparison.OrdinalIgnoreCase))
                    .Select(l => (JsonNode?)new JsonObject
                    {
                        ["name"] = Value(k?["ContractName"]),
                        ["description"] = Value(k?["ContractDescription"]),
                        ["onMeteringPointFrom"] = Date(l?["Start"]),
                        ["onMeteringPointTo"] = OpenEnd(l?["End"]),
                        ["settlementFrequency"] = Value(k?["FinalSettlementInfo"]?["SettlementFrequency"]),
                        ["onAccountFrequency"] = Value(k?["OnAccountSettlementInfo"]?["SettlementFrequency"]),
                    }))
                .ToArray()),
            ["marketPriceLists"] = new JsonArray((m?["marketPriceLists"] as JsonArray ?? [])
                .Select(x => (JsonNode?)MarketPriceList(x)).ToArray()),
            ["settlementGroups"] = new JsonArray((m?["settlementGroups"] as JsonArray ?? [])
                .Select(g => Value(g?["GroupName"])).ToArray()),
            ["billing"] = new JsonObject
            {
                ["currency"] = Value(billing?["BillingConfiguration"]?["Currency"]),
                ["jointInvoice"] = Value(billing?["BillingConfiguration"]?["JointInvoice"]),
                ["jointInvoiceNumber"] = Value(billing?["BillingConfiguration"]?["JointInvoiceNumber"]),
                ["ignoreInBilling"] = Value(billing?["SettlementInfo"]?["IgnoreInBilling"]),
                ["exemptFromVat"] = Value(billing?["SettlementInfo"]?["ExemptFromVat"]),
                ["exemptions"] = new JsonArray((billing?["Exemptions"] as JsonArray ?? [])
                    .Select(e => (JsonNode?)new JsonObject
                    {
                        ["type"] = Value(e?["ExemptionType"]),
                        ["percentage"] = Value(e?["ExemptionPercentage"]),
                    }).ToArray()),
            },
        };
    }

    private static JsonObject Payer(JsonNode? p, Dictionary<string, string?> identificationBySettlId)
    {
        var payer = p?["payer"];
        var billing = payer?["PayerBilling"];
        var info = payer?["PayerInformation"];
        var postal = info?["PostalAddress"];

        return new JsonObject
        {
            ["payerId"] = Value(info?["PayerIdentifier"]),
            ["payerType"] = Value(billing?["PayerType"]),
            ["primary"] = Value(payer?["IsPrimaryCustomerPayer"]),
            ["automaticallyApproveInvoices"] = Value(payer?["AutomaticallyApproveInvoices"]),
            ["cpr"] = Value(billing?["NationalIdentificationNumber"]),
            ["cvr"] = Value(billing?["VatIdentificationNumber"] ?? billing?["OrganizationIdentifier"]),
            ["name"] = Value(postal?["Name1"]?["FullName"]),
            ["supplementaryName"] = Value(postal?["Name2"]?["FullName"]),
            ["address"] = Address(postal?["PhysicalAddress"]),
            ["email"] = Value(billing?["Email"]),
            ["invoiceChannel"] = Value(billing?["InvoiceChannel"]),
            ["paymentChannel"] = Value(billing?["PaymentChannel"]),
            ["paymentAgreement"] = Value(billing?["AgreementIdentifier"]),
            ["ean"] = Value(billing?["ElectronicNumber"]),
            ["regNumber"] = Value(billing?["RegNumber"]),
            ["accountNumber"] = Value(billing?["AccountNumber"]),
            ["meteringPoints"] = new JsonArray((p?["meteringPointLinks"] as JsonArray ?? [])
                .Select(l => Str(l?["MeteringPointId"]) is { } id && identificationBySettlId.TryGetValue(id, out var ident) && ident != null
                    ? (JsonNode?)JsonValue.Create(ident)
                    : null)
                .Where(n => n != null)
                .ToArray()),
        };
    }

    /// <summary>A contract (the product on the customer's metering points) as in the business example's
    /// Contract sheet: master data, settlement terms and the contract lines that have not ended.</summary>
    private static JsonObject Contract(JsonNode? c)
    {
        var detail = c?["contract"];
        return new JsonObject
        {
            ["name"] = Value(detail?["ContractName"] ?? c?["ContractName"]),
            ["orderNumber"] = Value(detail?["OrderNumber"]),
            ["category"] = Value(detail?["ContractCategory"]),
            ["country"] = Value(detail?["CountryCode"]),
            ["typeOfContractElements"] = Value(detail?["TypeOfContractElements"]),
            ["commodityType"] = Value(detail?["CommodityType"]),
            ["description"] = Value(detail?["Description"] ?? c?["ContractDescription"]),
            ["quantityDistributionType"] = Value(detail?["QuantityDistributionType"]),
            ["finalSettlement"] = Settlement(detail?["FinalSettlementInfo"] ?? c?["FinalSettlementInfo"]),
            ["onAccountSettlement"] = Settlement(detail?["OnAccountSettlementInfo"] ?? c?["OnAccountSettlementInfo"]),
            ["lines"] = new JsonArray((c?["contractLines"] as JsonArray ?? [])
                .Select(l => (JsonNode?)new JsonObject
                {
                    ["start"] = DanishDate(l?["Start"]),
                    ["end"] = DanishOpenEnd(l?["End"]),
                    ["text"] = Value(l?["Text"]),
                    ["productElement"] = Value(l?["ProductElementCode"]),
                    ["product"] = Value(l?["ProductName"]),
                    ["unitPrice"] = Value(l?["UnitPrice"]?["Value"]),
                    ["unitPriceCurrency"] = Value(l?["UnitPrice"]?["CurrencyUnit"]),
                    ["unitPriceQuantityUnit"] = Value(l?["UnitPrice"]?["QuantityUnit"]),
                    ["coverage"] = Value(l?["Coverage"]),
                    ["category"] = Value(l?["Category"]),
                    ["profitSplit"] = Value(l?["ProfitSplit"]),
                    ["volumeFlexibility"] = Value(l?["VolumeFlexibility"]),
                    ["lastUpdated"] = DanishDate(l?["LastUpdated"]),
                }).ToArray()),
        };
    }

    private static JsonObject Settlement(JsonNode? info) => new()
    {
        ["frequency"] = Value(info?["SettlementFrequency"]),
        ["paymentTerms"] = Value(info?["PaymentTerms"]),
        ["paymentTermsOffset"] = Value(info?["PaymentTermsOffset"]),
    };

    /// <summary>A market price list on a metering point: the charge (id, type, charge owner GLN), its
    /// period on the metering point as Danish dates, and the price list's name and description.</summary>
    private static JsonObject MarketPriceList(JsonNode? x)
    {
        var association = x?["MeteringPointMarketPriceListAssociation"];
        var info = x?["marketPriceList"]?["PriceInformation"];
        return new JsonObject
        {
            ["priceListId"] = Value(x?["PriceIdentification"]),
            ["type"] = Value(x?["MarketPriceListType"]),
            ["chargeOwner"] = Value(x?["PriceChargeOwner"]?["Identification"]),
            ["start"] = DanishDate(association?["Start"]),
            ["end"] = DanishOpenEnd(association?["End"]),
            ["chargeOccurrences"] = Value(association?["ChargeOccurrences"]),
            ["name"] = Value(info?["Name"]),
            ["description"] = Value(info?["Description"]),
        };
    }

    /// <summary>A customer price list association: price list and product element by their codes, the
    /// contract by name when it is one of the exported contracts, and the period.</summary>
    private static JsonObject PriceList(JsonNode? a, JsonArray contracts)
    {
        var contractId = Str(a?["ContractId"]);
        var contract = contracts.FirstOrDefault(c => string.Equals(Str(c?["Id"]), contractId, StringComparison.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["priceList"] = Value(a?["names"]?["priceList"]),
            ["productElement"] = Value(a?["names"]?["productElement"]),
            ["contract"] = Value(contract?["ContractName"]),
            ["priceArea"] = Value(a?["names"]?["priceArea"]),
            ["start"] = DanishDate(a?["AssociationPeriod"]?["Start"]),
            ["end"] = DanishOpenEnd(a?["AssociationPeriod"]?["End"]),
        };
    }

    private static JsonObject ExtraInvoiceLine(JsonNode? l)
    {
        var data = l?["Data"];
        return new JsonObject
        {
            ["start"] = Date(data?["InvoicePeriod"]?["Start"]),
            ["end"] = Date(data?["InvoicePeriod"]?["End"]),
            ["text"] = Value(data?["Text"]),
            ["productElement"] = Value(data?["ProductElementCode"]),
            ["quantity"] = Value(data?["Quantity"]?["Value"]),
            ["unit"] = Value(data?["Quantity"]?["Unit"]),
            ["price"] = Value(data?["Price"]?["Value"]),
            ["vat"] = Value(data?["VatPercentage"]),
            ["status"] = Value(l?["Status"]),
            ["origin"] = Value(data?["Origin"]),
        };
    }

    private static JsonObject? Address(JsonNode? a) => a == null ? null : new JsonObject
    {
        ["street"] = Value(a["StreetName"]),
        ["houseNumber"] = Value(a["BuildingNumber"]),
        ["floor"] = Value(a["BuildingFloor"]),
        ["room"] = Value(a["RoomIdentification"]),
        ["postalCode"] = Value(a["PostalCode"]),
        ["city"] = Value(a["City"]),
        ["countryCode"] = Value(a["CountryCode"]),
        ["municipalityCode"] = Value(a["MunicipalityCode"]),
        ["streetCode"] = Value(a["StreetCode"]),
        ["darId"] = Value(a["AddressIdentifier"]?["Identification"]),
    };

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?.ToJsonString();

    /// <summary>A copy of a leaf value; empty strings count as no value.</summary>
    private static JsonNode? Value(JsonNode? node) =>
        node is JsonValue v && (v.GetValueKind() != JsonValueKind.String || !string.IsNullOrWhiteSpace(v.GetValue<string>()))
            ? v.DeepClone()
            : null;

    private static JsonNode? Date(JsonNode? node) =>
        Str(Value(node)) is { Length: >= 10 } s ? JsonValue.Create(s[..10]) : null;

    private static readonly TimeZoneInfo Danish = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    /// <summary>The Danish calendar date of a Settl instant: <c>2022-12-31T23:00:00Z</c> is 2023-01-01.
    /// A value without zone or offset keeps its own date.</summary>
    private static JsonNode? DanishDate(JsonNode? node)
    {
        if (Str(Value(node)) is not { Length: >= 10 } s) return null;
        if (!DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var instant)
            || s.Length <= 10 || !(s.EndsWith('Z') || s.LastIndexOfAny(['+', '-']) > 10))
            return JsonValue.Create(s[..10]);
        return JsonValue.Create(TimeZoneInfo.ConvertTime(instant, Danish).ToString("yyyy-MM-dd"));
    }

    private static JsonNode? DanishOpenEnd(JsonNode? node) =>
        Str(Value(node)) is { } s && s.StartsWith("9999", StringComparison.Ordinal) ? null : DanishDate(node);

    /// <summary>Settl writes "no end" as year 9999; that is no value.</summary>
    private static JsonNode? OpenEnd(JsonNode? node) =>
        Str(Value(node)) is { } s && s.StartsWith("9999", StringComparison.Ordinal) ? null : Date(node);

    /// <summary>Drops nulls, empty lists and objects left empty, all the way down.</summary>
    private static JsonNode? Prune(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var kept = new JsonObject();
                foreach (var (name, child) in obj.ToList())
                {
                    obj.Remove(name);
                    var pruned = Prune(child);
                    if (pruned is null or JsonArray { Count: 0 } or JsonObject { Count: 0 }) continue;
                    kept[name] = pruned;
                }
                return kept;
            case JsonArray array:
                var items = array.ToList();
                array.Clear();
                return new JsonArray(items.Select(Prune).Where(i => i is not (null or JsonObject { Count: 0 })).ToArray());
            default:
                return node;
        }
    }
}
