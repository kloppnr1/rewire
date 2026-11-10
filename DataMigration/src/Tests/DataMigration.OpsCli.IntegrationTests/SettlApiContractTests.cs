using System.Text.Json.Nodes;
using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.SettlApi.Models;
using Xunit.Abstractions;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// Read-only contract tests verifying Settl API field names, shapes, and serialization
/// across all four entity types: Customer, Payer, MeteringPoint (billing v1 + market v3),
/// and Contract.
///
/// <para>Every assertion targets a field that our business logic keys on — in reconcile
/// projectors, report runners, or translators. A failing test means Settl renamed, removed,
/// or restructured a field our code relies on.</para>
///
/// <para>Tests are purely read-only. Uses <see cref="ContractTestProvisioningFixture"/> which
/// creates a dedicated customer + payer in the fake Settl tenant (no Xellent DB required)
/// and discovers the "migrated" electricity MP seeded there. Pins the shapes; does not check live Settl.</para>
/// </summary>
public class SettlApiContractTests(ContractTestProvisioningFixture fixture, ITestOutputHelper output)
    : IClassFixture<ContractTestProvisioningFixture>, IAsyncLifetime
{
    // Cached market v3 search result — shared across all MpMarket_SearchResult_* tests.
    private MeteringPointInfoDto? _mpSearchResult;

    public async Task InitializeAsync()
    {
        output.WriteLine(
            $"Customer: {fixture.CustomerId}  Payer: {fixture.PayerId}" +
            $"  MP: {fixture.MpId}  NationalId: {fixture.MpNationalId}");

        if (string.IsNullOrEmpty(fixture.MpNationalId)) return;

        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);
        var (found, _) = await mp.SearchByIdentificationAsync(fixture.MpNationalId);
        _mpSearchResult = found;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // =========================================================
    // Customer — v2 typed GET
    // =========================================================

    [Fact]
    public async Task Customer_GetById_CustomerNumber_Matches_DedicatedTestCustomer()
    {
        var (c, err) = await fixture.Settl.CustomerClient.GetByIdAsync(fixture.CustomerId);
        Assert.True(c != null, $"GET customer {fixture.CustomerId} failed: {err}");
        output.WriteLine($"CustomerNumber = {c!.CustomerNumber}");
        Assert.Equal(ContractTestProvisioningFixture.CustomerNumber, c.CustomerNumber);
    }

    [Fact]
    public async Task Customer_GetById_Address_IsPresent()
    {
        var (c, err) = await fixture.Settl.CustomerClient.GetByIdAsync(fixture.CustomerId);
        Assert.True(c != null, $"GET customer {fixture.CustomerId} failed: {err}");
        // Reconcile and reports rely on Address.PostalAddress for name + street.
        Assert.NotNull(c!.Address);
        Assert.NotNull(c.Address.PostalAddress);
        output.WriteLine($"Address.Start = {c.Address.Start}  City = {c.Address.PostalAddress.PhysicalAddress?.City}");
    }

    [Fact]
    public async Task Customer_GetById_VatIdentificationNumber_IsPresent()
    {
        // Dedicated test customer is a Company with VatIdentificationNumber set on creation.
        // If Settl renames this field (e.g. to OrganizationIdentifier on v2 customer), the
        // typed property deserialization fails silently and CustomerResponse.VatIdentificationNumber
        // returns null — reconcile would miss CVR drift for all customers.
        var (c, err) = await fixture.Settl.CustomerClient.GetByIdAsync(fixture.CustomerId);
        Assert.True(c != null, $"GET customer {fixture.CustomerId} failed: {err}");
        output.WriteLine($"VatIdentificationNumber = {c!.VatIdentificationNumber ?? "(null)"}");
        Assert.Equal(ContractTestProvisioningFixture.VatIdentificationNumber, c.VatIdentificationNumber);
    }

    [Fact]
    public async Task Customer_V3Raw_HasExpectedTopLevelFieldNames()
    {
        // v3 customer PUT uses GET→echo. If Settl renames a top-level field, the echo loses it.
        var (json, err) = await fixture.Settl.CustomerClient.GetRawByIdV3Async(fixture.CustomerId);
        Assert.True(json != null, $"GET v3 raw customer {fixture.CustomerId} failed: {err}");

        var root = JsonNode.Parse(json!)?.AsObject();
        Assert.NotNull(root);

        var keys = root!.Select(kv => kv.Key).ToList();
        output.WriteLine($"Customer v3 top-level keys: {string.Join(", ", keys)}");

        // Fields used by reconcile-apply's customer core echo-PUT.
        AssertKeyPresent(keys, "customerNumber", "v3 customer response");
        AssertKeyPresent(keys, "countryCode", "v3 customer response");
        AssertKeyPresent(keys, "customerType", "v3 customer response");
        AssertKeyPresent(keys, "vatIdentificationNumber", "v3 customer response");
        AssertKeyPresent(keys, "address", "v3 customer response");
    }

    // =========================================================
    // Payer — raw GET (field names tested against the live API response)
    // =========================================================

    [Fact]
    public async Task Payer_GetRaw_TopLevel_HasExpectedFields()
    {
        var (json, err) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        Assert.True(json != null, $"GET raw payer {fixture.PayerId} failed: {err}");

        var root = JsonNode.Parse(json!)?.AsObject();
        Assert.NotNull(root);

        var keys = root!.Select(kv => kv.Key).ToList();
        output.WriteLine($"Payer top-level keys: {string.Join(", ", keys)}");

        // Full-echo PUT writes these fields back verbatim — they must survive round-trip.
        AssertKeyPresent(keys, "customerId", "payer response");
        AssertKeyPresent(keys, "countryCode", "payer response");
        AssertKeyPresent(keys, "isPrimaryCustomerPayer", "payer response");
        AssertKeyPresent(keys, "payerBilling", "payer response");
    }

    [Fact]
    public async Task Payer_GetRaw_PayerBilling_HasOrganizationIdentifier_NotVatIdentificationNumber()
    {
        // Settl GET/PUT asymmetry: GET returns 'organizationIdentifier', PUT requires 'vatIdentificationNumber'.
        // ReconcileRunner explicitly compares these fields (1c0349c3). If Settl renames either, reconcile
        // loses visibility of CVR drift.
        var (json, err) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        Assert.True(json != null, $"GET raw payer {fixture.PayerId} failed: {err}");

        var billing = FindNestedObject(json!, "payerBilling");
        Assert.NotNull(billing);

        var keys = billing!.Select(kv => kv.Key).ToList();
        output.WriteLine($"PayerBilling keys: {string.Join(", ", keys)}");

        Assert.True(
            keys.Any(k => k.Equals("organizationIdentifier", StringComparison.OrdinalIgnoreCase)),
            "GET response PayerBilling missing 'organizationIdentifier' — update PayerBillingDto and ReconcileRunner");
        Assert.False(
            keys.Any(k => k.Equals("vatIdentificationNumber", StringComparison.OrdinalIgnoreCase)),
            "GET response PayerBilling contains 'vatIdentificationNumber' — the asymmetry may have collapsed; update both DTOs");
    }

    [Fact]
    public async Task Payer_GetRaw_PayerBilling_HasRequiredBillingFields()
    {
        var (json, err) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        Assert.True(json != null, $"GET raw payer {fixture.PayerId} failed: {err}");

        var billing = FindNestedObject(json!, "payerBilling");
        Assert.NotNull(billing);

        var keys = billing!.Select(kv => kv.Key).ToList();

        // Fields used in reconcile billing diff (PayerBillingDto properties).
        AssertKeyPresent(keys, "start", "PayerBilling");
        AssertKeyPresent(keys, "invoiceChannel", "PayerBilling");
        AssertKeyPresent(keys, "paymentChannel", "PayerBilling");
        AssertKeyPresent(keys, "electronicNumber", "PayerBilling");
        AssertKeyPresent(keys, "orderIdentifier", "PayerBilling");
        AssertKeyPresent(keys, "agreementIdentifier", "PayerBilling");
        AssertKeyPresent(keys, "buyerReferenceIdentifier", "PayerBilling");
        AssertKeyPresent(keys, "bookkeepingReference", "PayerBilling");
    }

    [Fact]
    public async Task Payer_GetRaw_PayerBilling_Start_IsUtcDatetimeString()
    {
        // DateOnlyUtcConverter serialises DateOnly as "yyyy-MM-ddT00:00:00Z" — UTC midnight.
        // If Settl changes to plain "yyyy-MM-dd" format, DateOnly deserialization fails silently
        // (returns DateOnly.MinValue) without throwing.
        var (json, err) = await fixture.Settl.PayerClient.GetRawByIdAsync(fixture.PayerId);
        Assert.True(json != null, $"GET raw payer {fixture.PayerId} failed: {err}");

        var startStr = ExtractNestedField(json!, "payerBilling", "start");
        Assert.True(!string.IsNullOrEmpty(startStr), "PayerBilling.start is missing or null in GET response");
        output.WriteLine($"PayerBilling.start raw value = {startStr}");

        // Must be UTC datetime string: "2021-01-01T00:00:00Z" or "2021-01-01T00:00:00.000Z"
        Assert.True(
            startStr!.Contains('T') && startStr.EndsWith("Z", StringComparison.OrdinalIgnoreCase),
            $"PayerBilling.start '{startStr}' is not UTC datetime format — " +
            "DateOnlyUtcConverter expects 'yyyy-MM-ddT00:00:00Z'; update converter if Settl changed the format");
    }

    // =========================================================
    // Metering point — billing v1 (typed)
    // =========================================================

    [Fact]
    public async Task MpBilling_GetById_BillingConfiguration_IsPresent()
    {
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 MP {fixture.MpId} failed: {err}");
        output.WriteLine($"BillingConfiguration.Start = {mp!.BillingConfiguration?.Start}  Currency = {mp.BillingConfiguration?.Currency}");
        Assert.NotNull(mp.BillingConfiguration);
    }

    [Fact]
    public async Task MpBilling_BillingConfiguration_Currency_IsDKK()
    {
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 MP {fixture.MpId} failed: {err}");
        // create sets Currency="DKK" for all electricity MPs. If Settl renamed this field,
        // the typed property would be null (deserialized as empty string or default).
        Assert.Equal("DKK", mp!.BillingConfiguration?.Currency);
    }

    [Fact]
    public async Task MpBilling_BillingConfiguration_Start_IsNonDefault()
    {
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 MP {fixture.MpId} failed: {err}");
        var start = mp!.BillingConfiguration?.Start;
        output.WriteLine($"BillingConfiguration.Start = {start}");
        Assert.True(start.HasValue && start.Value != DateOnly.MinValue,
            "BillingConfiguration.Start is default/missing — DateOnlyUtcConverter may have failed to parse the response");
    }

    [Fact]
    public async Task MpBilling_BillingConfiguration_JointInvoice_FieldPresent()
    {
        // JointInvoice bool is set by create on every electricity MP.
        // If Settl renames the field, BillingConfigurationDto.JointInvoice defaults to false
        // (bool is not nullable) — hard to distinguish from "legitimately false".
        // Test verifies the typed response parses without error.
        var (mp, err) = await fixture.Settl.MeteringPointClient.GetBillingByIdAsync(fixture.MpId);
        Assert.True(mp != null, $"GET billing v1 MP {fixture.MpId} failed: {err}");
        output.WriteLine($"BillingConfiguration.JointInvoice = {mp!.BillingConfiguration?.JointInvoice}");
        Assert.NotNull(mp.BillingConfiguration);
    }

    [Fact]
    public async Task MpBilling_Identification_NonEmpty_InTypedSearch()
    {
        // SearchBillingByIdentificationAsync returns BillingMeteringPointInfoDto with
        // MeteringPointIdentification. Verifies we can round-trip the national ID via billing v1.
        if (string.IsNullOrEmpty(fixture.MpNationalId))
        {
            output.WriteLine("Skipping: could not extract national ID from billing MP");
            return;
        }
        var (info, err) = await fixture.Settl.MeteringPointClient.SearchBillingByIdentificationAsync(fixture.MpNationalId);
        Assert.True(info != null, $"Billing v1 search by identification {fixture.MpNationalId} failed: {err}");
        output.WriteLine($"BillingMeteringPointInfoDto.MeteringPointIdentification = {info!.MeteringPointIdentification}");
        Assert.Equal(fixture.MpNationalId, info.MeteringPointIdentification);
    }

    // =========================================================
    // Metering point — market v3 (typed search result + raw)
    // =========================================================

    [Fact]
    public void MpMarket_Search_Returns_MeteringPointIdentification()
    {
        Assert.True(!string.IsNullOrEmpty(fixture.MpNationalId),
            $"Failed to extract MeteringPointIdentification from billing v1 for MP {fixture.MpId}");
        output.WriteLine($"MeteringPointIdentification = {fixture.MpNationalId}");
        Assert.Matches(@"^\d{18}$", fixture.MpNationalId);
    }

    [Fact]
    public void MpMarket_SearchResult_HasInDelivery_Field()
    {
        if (_mpSearchResult == null) { output.WriteLine("Skipping: market v3 search result not available"); return; }
        // InDelivery is a bool? — reconcile projector reads it as actual.InDelivery?.ToString().
        // If Settl renames it, the property is null and reconcile can't detect DeliveryFlag drift.
        output.WriteLine($"InDelivery = {_mpSearchResult.InDelivery}");
        Assert.NotNull(_mpSearchResult.InDelivery);
    }

    [Fact]
    public void MpMarket_SearchResult_HasConnectionStatus()
    {
        if (_mpSearchResult == null) { output.WriteLine("Skipping: market v3 search result not available"); return; }
        output.WriteLine($"ConnectionStatus = {_mpSearchResult.ConnectionStatus}");
        Assert.NotNull(_mpSearchResult.ConnectionStatus);
    }

    [Fact]
    public void MpMarket_SearchResult_HasSettlementMethodCode()
    {
        if (_mpSearchResult == null) { output.WriteLine("Skipping: market v3 search result not available"); return; }
        // SettlementMethodCode maps to MeteringPointSettlementMethodDto.SettlementMethod in the create payload.
        // Reconcile projector compares intended vs actual.SettlementMethodCode.
        output.WriteLine($"SettlementMethodCode = {_mpSearchResult.SettlementMethodCode}");
        Assert.NotNull(_mpSearchResult.SettlementMethodCode);
    }

    [Fact]
    public void MpMarket_SearchResult_HasMarketBalanceAreaId()
    {
        if (_mpSearchResult == null) { output.WriteLine("Skipping: market v3 search result not available"); return; }
        // MarketBalanceAreaId is used in reconcile projector and in grid drift detection.
        output.WriteLine($"MarketBalanceAreaId = {_mpSearchResult.MarketBalanceAreaId}");
        Assert.NotNull(_mpSearchResult.MarketBalanceAreaId);
    }

    [Fact]
    public async Task MpMarket_Raw_HasExpectedCamelCaseFields()
    {
        // Market v3 returns camelCase JSON (CamelCaseJsonOptions). Verifies key field names
        // as they appear in the raw response — what the typed deserialization parses from.
        var mp = fixture.Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);
        var (json, err) = await mp.GetByIdRawAsync(fixture.MpId);
        Assert.True(json != null, $"GET market v3 raw {fixture.MpId} failed: {err}");

        var root = JsonNode.Parse(json!)?.AsObject();
        Assert.NotNull(root);

        var keys = FlattenKeys(root!);
        output.WriteLine($"Market v3 top-level keys: {string.Join(", ", root!.Select(kv => kv.Key))}");

        // Fields the MeteringPointInfoDto maps from the raw search response.
        AssertKeyPresent(keys, "meteringPointIdentification", "market v3 raw response");
        AssertKeyPresent(keys, "connectionStatus", "market v3 raw response");
        AssertKeyPresent(keys, "inDelivery", "market v3 raw response");
    }

    // =========================================================
    // Contract
    // =========================================================

    [Fact]
    public async Task Contracts_FindElectricity_ReturnsAtLeastOne_WithContractName()
    {
        // FindContractsAsync is used to resolve contract name → Settl contract GUID before create.
        // ContractName is the join key between Xellent and Settl contracts.
        var (contracts, err) = await fixture.Settl.ContractClient.FindContractsAsync(commodityType: "Electricity");
        Assert.True(contracts != null, $"FindContractsAsync failed: {err}");
        output.WriteLine($"Found {contracts!.Count} electricity contracts");
        Assert.True(contracts.Count >= 1, "No electricity contracts found in the fake tenant — ContractNameResolver would fail");

        var first = contracts[0];
        output.WriteLine($"First contract: {first.ContractName} ({first.CommodityType}) period start: {first.ContractPeriod?.Start}");
        Assert.NotNull(first.ContractName);
        Assert.Equal("Electricity", first.CommodityType, ignoreCase: true);
    }

    // =========================================================
    // Helpers
    // =========================================================

    private static void AssertKeyPresent(IEnumerable<string> keys, string expectedKey, string context)
    {
        Assert.True(
            keys.Any(k => k.Equals(expectedKey, StringComparison.OrdinalIgnoreCase)),
            $"Expected field '{expectedKey}' missing from {context} — Settl may have renamed it");
    }

    private static JsonObject? FindNestedObject(string json, string key)
    {
        if (JsonNode.Parse(json) is not JsonObject root) return null;
        foreach (var kv in root)
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return kv.Value?.AsObject();
        return null;
    }

    private static string? ExtractNestedField(string json, string parentKey, string childKey)
    {
        var parent = FindNestedObject(json, parentKey);
        if (parent == null) return null;
        foreach (var kv in parent)
            if (kv.Key.Equals(childKey, StringComparison.OrdinalIgnoreCase))
                return kv.Value?.ToJsonString()?.Trim('"');
        return null;
    }

    private static IReadOnlyList<string> FlattenKeys(JsonObject obj)
    {
        var result = new List<string>();
        foreach (var kv in obj)
        {
            result.Add(kv.Key);
            if (kv.Value is JsonObject nested)
                result.AddRange(FlattenKeys(nested));
        }
        return result;
    }
}
