using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Tests;

/// <summary>
/// Golden-wire parity: in an all-fresh scenario (nothing exists in Settl yet) the payload
/// <see cref="SettlClient.CapturePlanAsync"/> returns must equal — property-for-property,
/// GUIDs aside — what <see cref="SettlClient.MigrateAsync"/> actually POSTs. Both run the same
/// shared builder helpers, so this guards the capture-assembly logic against drift from create.
/// </summary>
public class CapturePlanParityTests
{
    // Property names that hold server/random GUIDs or transaction ids — excluded from the diff
    // (same exclusion semantics as the reconcile command).
    private static readonly HashSet<string> VolatileKeys = new(StringComparer.Ordinal)
    {
        "Id", "TransactionId", "MeteringPointId", "CustomerId", "OwnerId", "ProductElementId", "Uri"
    };

    [Fact]
    public async Task CapturePlan_payload_equals_MigrateAsync_wire_for_fresh_bundle()
    {
        var customer = MakeCustomer();
        var mps = new Dictionary<string, MeteringPoint> { [customer.Contracts[0].MeteringPoints[0].MeteringPointId] = MakeElectricityMp() };
        var context = MakeContext();

        // --- create path (records what would be POSTed) ---
        var rec = new RecordingClients();
        var createClient = NewClient(rec);
        await createClient.MigrateAsync(customer, mps, context, default);

        // --- capture path (fresh instance, fresh fakes — independent of Settl state) ---
        var capClient = NewClient(new RecordingClients());
        var plan = await capClient.CapturePlanAsync(customer, mps, context, default);

        // Customer
        AssertScrubbedEqual(rec.CreatedCustomers.Single(), plan.Customer, "customer");

        // Payers (match on PayerIdentifier)
        Assert.Equal(rec.CreatedPayers.Count, plan.Payers.Count);
        foreach (var planned in plan.Payers)
        {
            var posted = rec.CreatedPayers.Single(p => p.PayerInformation!.PayerIdentifier == planned.PayerInformation!.PayerIdentifier);
            AssertScrubbedEqual(posted, planned, $"payer {planned.PayerInformation!.PayerIdentifier}");
        }

        // MP master (match on MeteringPointIdentification)
        Assert.Equal(rec.CreatedMps.Count, plan.MeteringPoints.Count);
        foreach (var planned in plan.MeteringPoints)
        {
            var posted = rec.CreatedMps.Single(m => m.Identification!.MeteringPointIdentification == planned.Identification);
            // The plan keeps the v3 master shape (that's what reconcile diffs against Settl), while
            // the wire is the v4 create object. Reshape the plan the same way MigrateAsync does, so
            // the comparison stays about the section content and not about the address wrapper.
            var plannedWire = MeteringPointCreateObjectV4Dto.FromMeteringPoint(
                planned.Master, context.EnrichAddress, context.SkipAddressValidation);
            AssertScrubbedEqual(posted, plannedWire, $"mp-master {planned.Identification}");

            // Billing (one update per fresh electricity MP)
            var postedBilling = rec.BillingUpdates[posted.Id];
            AssertScrubbedEqual(postedBilling, planned.Billing, $"mp-billing {planned.Identification}");
        }

        // Customer→MP links
        AssertLinksEqual(rec.CustomerLinks, plan.CustomerMpLinks, l => l.ExternalIdentifier!, "customer-link");
        // Payer→MP links
        AssertLinksEqual(rec.PayerLinks, plan.PayerMpLinks.Select(p => p.Link).ToList(), l => l.ExternalIdentifier!, "payer-link");
        // Contract→MP links
        AssertLinksEqual(rec.ContractLinks, plan.ContractMpLinks.Select(c => c.Link).ToList(), l => l.MeteringPointIdentification!, "contract-link");
    }

    private static void AssertLinksEqual<T>(IReadOnlyList<T> posted, IReadOnlyList<T> planned, Func<T, string> key, string label)
    {
        Assert.Equal(posted.Count, planned.Count);
        foreach (var p in planned)
            AssertScrubbedEqual(posted.Single(x => key(x) == key(p)), p, $"{label} {key(p)}");
    }

    private static void AssertScrubbedEqual(object? expected, object? actual, string label)
    {
        Assert.Equal(Canonical(expected), Canonical(actual));
        return;

        static string Canonical(object? o)
        {
            if (o == null) return "null";
            var node = JsonSerializer.SerializeToNode(o, o.GetType());
            Scrub(node);
            return node!.ToJsonString();
        }
    }

    private static void Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var k in obj.Select(kv => kv.Key).Where(VolatileKeys.Contains).ToList())
                    obj.Remove(k);
                foreach (var kv in obj) Scrub(kv.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) Scrub(item);
                break;
        }
    }

    private static SettlClient NewClient(RecordingClients r) =>
        new(r, r, r, r, r, deliveryCategory: "El-ekstern", logLine: _ => { });

    private static SettlMigrationContext MakeContext() => new(
        DryRun: false,
        ChangeOfSupplierDate: new DateTime(2024, 1, 1),
        MpContractLinks:
        [
            new SettlMpContractLink("571313100000000017", Guid.NewGuid(),
                new DateTime(2024, 1, 1), MigrationConstants.EndDateNull, IsJoined: false)
        ],
        ExemptionContext: null,
        SettlementGroupMap: null,
        OwnerByCustomerNumber: null);

    private static Customer MakeCustomer()
    {
        var address = new Address("Rådhusvej", "3", "", "", "3200", "Helsinge", "", "", "", "DK", Dar: null!);
        var contact = new ContactInfo("test@example.com", "", "", "");

        var assignment = new MeteringPointAssignment(
            MeteringPointId: "571313100000000017",
            Product: null!,
            Start: new DateOnly(2024, 1, 1),
            EndDate: DateOnly.FromDateTime(MigrationConstants.EndDateNull),
            AmrCode: "");

        var contract = new Contract(
            Number: "C001", Description: "Test", Start: new DateOnly(2024, 1, 1),
            Commodity: Commodity.Electricity, CustomerNumber: "352639",
            Billing: new BillingSchedule(BillingFrequency.Monthly, "MND"),
            MeteringPoints: [assignment], PaymentTerm: null,
            BuyerInvoicing: new BuyerInvoicingReferences("5798008161063", null, null, null));

        return new Customer(
            AccountNumber: "352639", Name: "Gribskov Kommune", AdditionalName: "",
            Type: CustomerType.Company, Cvr: "29188440", Cpr: "",
            Address: address, Contact: contact,
            Billing: new CustomerBilling(DefaultEan: "5798008161063", Pbs: null, MobilePay: null),
            Contracts: [contract])
        {
            Payers =
            [
                new Payer(
                    Identifier: "352639",
                    IsPrimary: true,
                    ContractNumbers: ["C001"],
                    BuyerInvoicing: new BuyerInvoicingReferences("5798008161063", null, null, null),
                    InvoiceChannel: InvoiceChannel.OioUbl,
                    PaymentChannel: PaymentChannel.EInvoiceFik)
            ]
        };
    }

    private static MeteringPoint MakeElectricityMp() => new(
        Identification: "571313100000000017",
        InstallationAddress: new Address("Rådhusvej", "3", null, null, "3200", "Helsinge", "0001", "Helsinge", "270", "DK", Dar: null!),
        Commodity: Commodity.Electricity,
        Meter: new Meter("M-1", 1m, ReadingInterval: null),
        ContactPoints: [],
        JointInvoiceGroupId: "JIG-1",
        ConnectionStatus: ConnectionStatus.Connected,
        InDelivery: true,
        Grid: new GridConnection(PriceArea.Dk2, "031", SettlementMethod.Flex, MeteringPointKind.Consumption, ""),
        Start: new DateOnly(2024, 1, 1),
        EstimatedAnnualVolumeKwh: 5000,
        BillingName: "Gribskov Kommune",
        BuyerInvoicing: new BuyerInvoicingReferences("5798008161063", "ORD-1", null, null));

    /// <summary>
    /// One object implementing all five Settl client interfaces. Search-methods return null
    /// (nothing exists), create-methods record the payload; MP search returns the created MP
    /// after create so RetrySearch resolves a GUID.
    /// </summary>
    private sealed class RecordingClients
        : ISettlCustomerClient, ISettlPayerClient, ISettlMeteringPointClient, ISettlContractClient, ISettlSettlementGroupClient
    {
        public List<CreateCustomerDto> CreatedCustomers { get; } = [];
        public List<CreatePayerDto> CreatedPayers { get; } = [];
        public List<MeteringPointCreateObjectV4Dto> CreatedMps { get; } = [];
        public Dictionary<Guid, UpdateBillingMeteringPointRequest> BillingUpdates { get; } = [];
        public List<CustomerMeteringPointDto> CustomerLinks { get; } = [];
        public List<PayerMeteringPointDto> PayerLinks { get; } = [];
        public List<ContractMeteringPointDto> ContractLinks { get; } = [];

        private readonly Dictionary<string, Guid> _mpIdByIdentification = new(StringComparer.Ordinal);

        // --- Customer ---
        public Task<(bool, string?)> CreateAsync(CreateCustomersRequest request, CancellationToken ct = default)
        { CreatedCustomers.AddRange(request.Customers); return Ok(); }
        public Task<(CustomerInfoDto?, string?)> SearchByCustomerNumberAsync(string n, string c = "DK", CancellationToken ct = default) => Null<CustomerInfoDto>();
        public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddCustomerMeteringPointsRequest request, CancellationToken ct = default)
        { CustomerLinks.AddRange(request.CustomerMeteringPoints); return Ok(); }
        public Task<(CustomerResponse?, string?)> GetByIdAsync(Guid id, CancellationToken ct = default) => Null<CustomerResponse>();
        public Task<(string?, string?)> GetRawByIdV3Async(Guid id, CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<(bool, string?)> UpdateV3RawAsync(Guid id, string json, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> UpdateNationalIdentificationNumberV3Async(Guid id, string nin, CancellationToken ct = default) => Ok();
        public Task<(List<CustomerMeteringPointDto>?, string?)> GetMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<CustomerMeteringPointDto>?, string?)>(([], null));
        public Task<(List<CustomerInfoDto>, int, string?)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<CustomerInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<CustomerInfoDto>, int, string?)> SearchCustomersAsync(string? f = null, string? n = null, string? v = null, int limit = 50, CancellationToken ct = default) => Task.FromResult<(List<CustomerInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<CustomerOrganisationMeteringPointDto>?, string?)> GetOrganisationMeteringPointsAsync(Guid id, CancellationToken ct = default) => Null<List<CustomerOrganisationMeteringPointDto>>();
        public Task<(CustomerHistoryResponse?, string?)> GetHistoryAsync(Guid id, CancellationToken ct = default) => Null<CustomerHistoryResponse>();
        public Task<(List<CustomerSegmentDto>?, string?)> FindCustomerSegmentsAsync(CancellationToken ct = default) => Null<List<CustomerSegmentDto>>();
        public Task<(SearchCustomerPriceListResponse?, string?)> SearchPriceListsAsync(Guid id, SearchCustomerPriceListRequest? req = null, CancellationToken ct = default) => Null<SearchCustomerPriceListResponse>();
        public Task<(List<ExtraInvoiceLineDto>, int, string?)> GetExtraInvoiceLinesAsync(Guid id, int limit = 50, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<ExtraInvoiceLineDto>, int, string?)>(([], 0, null));

        // --- Payer ---
        public Task<(bool, string?)> CreateAsync(CreatePayersRequest request, CancellationToken ct = default)
        { CreatedPayers.AddRange(request.Payers); return Ok(); }
        public Task<(PayerInfoDto?, string?)> SearchByPayerIdentificationAsync(string id, string c = "DK", CancellationToken ct = default) => Null<PayerInfoDto>();
        public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddPayerMeteringPointsRequest request, CancellationToken ct = default)
        { PayerLinks.AddRange(request.MeteringPoints); return Ok(); }
        Task<(PayerResponse?, string?)> ISettlPayerClient.GetByIdAsync(Guid id, DateTime? effectiveDate, CancellationToken ct) => Null<PayerResponse>();
        Task<(PayerHistoryResponse?, string?)> ISettlPayerClient.GetHistoryAsync(Guid id, CancellationToken ct) => Null<PayerHistoryResponse>();
        Task<(List<PayerMeteringPointDto>?, string?)> ISettlPayerClient.GetMeteringPointsAsync(Guid id, CancellationToken ct) => Task.FromResult<(List<PayerMeteringPointDto>?, string?)>(([], null));
        public Task<(string?, string?)> GetRawByIdAsync(Guid id, CancellationToken ct = default) => Null<string>();
        public Task<(bool, string?)> UpdateRawAsync(Guid id, string json, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> UpdatePayerBillingAsync(Guid id, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> patches, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> DeleteAsync(Guid id, CancellationToken ct = default) => Ok();
        public Task<(List<PayerInfoDto>, int, string?)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<PayerInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<PayerInfoDto>?, string?)> FindPayersByCustomerAsync(Guid id, string c = "DK", CancellationToken ct = default) => Null<List<PayerInfoDto>>();
        public Task<(List<PayerForMeteringPointsDto>?, string?)> FindPayersForMeteringPointsAsync(IReadOnlyList<Guid> ids, DateTime? d = null, CancellationToken ct = default) => Null<List<PayerForMeteringPointsDto>>();

        // --- Metering point ---
        public void SetDeliveryCategory(string deliveryCategory) { }
        public Task<(bool, string?)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken ct = default)
            => throw new NotSupportedException("v3 create is retired — MigrateAsync must POST v4");
        public Task<(bool, string?)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken ct = default)
        {
            foreach (var mp in request.MeteringPointCreateObjectDtos)
            {
                CreatedMps.Add(mp);
                _mpIdByIdentification[mp.Identification!.MeteringPointIdentification] = mp.Id;
            }
            return Ok();
        }
        public Task<(MeteringPointInfoDto?, string?)> SearchByIdentificationAsync(string ident, CancellationToken ct = default)
            => _mpIdByIdentification.TryGetValue(ident, out var id)
                ? Task.FromResult<(MeteringPointInfoDto?, string?)>((new MeteringPointInfoDto { Id = id, MeteringPointIdentification = ident }, null))
                : Null<MeteringPointInfoDto>();
        public Task<(bool, string?)> CreateWaterAsync(CreateWaterMeteringPointRequest request, CancellationToken ct = default) => Ok();
        public Task<(MeteringPointInfoDto?, string?)> SearchWaterByIdentificationAsync(string ident, CancellationToken ct = default) => Null<MeteringPointInfoDto>();
        public Task<(MeteringPointInfoDto?, string?)> GetWaterByIdAsync(Guid id, CancellationToken ct = default) => Null<MeteringPointInfoDto>();
        public Task<(BillingMeteringPointCurrentDto?, string?)> GetBillingByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default) => Null<BillingMeteringPointCurrentDto>();
        public Task<(bool, string?)> UpdateBillingAsync(Guid id, UpdateBillingMeteringPointRequest request, CancellationToken ct = default)
        { BillingUpdates[id] = request; return Ok(); }
        public Task<(MeteringPointCurrentDto?, string?)> GetByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default) => Null<MeteringPointCurrentDto>();
        public Task<(GetDeliveryStatusesResponse?, string?)> GetDeliveryStatusesAsync(Guid id, CancellationToken ct = default) => Null<GetDeliveryStatusesResponse>();
        public Task<(string?, string?)> GetDeliveryStatusesRawAsync(Guid id, CancellationToken ct = default) => Null<string>();
        public Task<(string?, string?)> GetByIdRawAsync(Guid id, CancellationToken ct = default) => Null<string>();
        public Task<(bool, string?)> UpdateAsync(Guid id, MeteringPointDto dto, CancellationToken ct = default) => Ok();
        public Task<(BillingMeteringPointInfoDto?, string?)> SearchBillingByIdentificationAsync(string ident, CancellationToken ct = default) => Null<BillingMeteringPointInfoDto>();
        public Task<(BillingMeteringPointHistoryResponse?, string?)> GetBillingHistoryAsync(Guid id, CancellationToken ct = default) => Null<BillingMeteringPointHistoryResponse>();
        public Task<(MeteringPointV3HistoryResponse?, string?)> GetMarketHistoryAsync(Guid id, CancellationToken ct = default) => Null<MeteringPointV3HistoryResponse>();
        public Task<(MeteringPointV4Dto?, string?)> GetByIdV4Async(Guid id, DateTime? d = null, CancellationToken ct = default) => Null<MeteringPointV4Dto>();
        public Task<(List<MarketBalanceAreaInfoDto>?, string?)> FindMarketBalanceAreasAsync(CancellationToken ct = default) => Task.FromResult<(List<MarketBalanceAreaInfoDto>?, string?)>(([], null));
        public Task<(List<MeteringGridAreaInfoDto>?, string?)> FindMeteringGridAreasAsync(CancellationToken ct = default) => Task.FromResult<(List<MeteringGridAreaInfoDto>?, string?)>(([], null));
        public Task<(List<BillingMeteringPointInfoDto>, int, string?)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult<(List<BillingMeteringPointInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<MeteringPointInfoDto>, int, string?)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken ct = default) => Task.FromResult<(List<MeteringPointInfoDto>, int, string?)>(([], 0, null));
        public Task<(List<MeteringPointInfoDto>, string?)> FindWaterMeteringPointsAsync(CancellationToken ct = default) => Task.FromResult<(List<MeteringPointInfoDto>, string?)>(([], null));
        public Task<(bool, string?)> UpdateAnnualConsumptionsAsync(Guid id, double v, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> UpdateEstimatedAnnualVolumeAsync(Guid id, double v, DateOnly effectiveStart, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> AddExemptionsAsync(Guid id, AddMeteringPointExemptionsRequest request, CancellationToken ct = default) => Ok();
        public Task<(bool, string?)> BlankDuplicateSecondConsumerNameAsync(Guid id, CancellationToken ct = default) => Ok();

        // --- Contract ---
        public Task<(List<ContractInfoDto>?, string?)> FindContractsAsync(string? commodityType = null, CancellationToken ct = default) => Null<List<ContractInfoDto>>();
        public Task<(List<ContractMeteringPointInfoDto>?, string?)> GetContractMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<ContractMeteringPointInfoDto>?, string?)>(([], null));
        public Task<(List<ContractInfoDto>?, string?)> FindContractsByMeteringPointAsync(Guid meteringPointId, CancellationToken ct = default) => Null<List<ContractInfoDto>>();
        public Task<(List<ContractLineInfoDto>?, string?)> GetContractLinesAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<ContractLineInfoDto>?, string?)>(([], null));
        public Task<(List<CustomerContractWithMeteringPointsDto>?, string?)> GetCustomerMeteringPointContractsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<(List<CustomerContractWithMeteringPointsDto>?, string?)>(([], null));
        public Task<(bool, string?)> AddContractMeteringPointsAsync(Guid id, AddContractMeteringPointsRequest request, CancellationToken ct = default)
        { ContractLinks.AddRange(request.ContractMeteringPoints); return Ok(); }
        public Task<(bool, string?)> RemoveContractMeteringPointAsync(Guid contractId, Guid linkId, CancellationToken ct = default) => Task.FromResult((true, (string?)null));

        // --- Settlement group (unused: context.SettlementGroupMap == null) ---
        public Task<(SettlementGroupInfoDto?, string?)> FindByNameAsync(string c, string n, CancellationToken ct = default) => Null<SettlementGroupInfoDto>();
        public Task<(List<SettlementGroupInfoDto>?, string?)> FindByMeteringPointAsync(Guid id, CancellationToken ct = default) => Null<List<SettlementGroupInfoDto>>();
        public Task<(List<SettlementGroupInfoDto>?, string?)> ListAllAsync(string c, CancellationToken ct = default) => Null<List<SettlementGroupInfoDto>>();
        public Task<(HashSet<Guid>?, string?)> ListMeteringPointIdsAsync(Guid id, CancellationToken ct = default) => Null<HashSet<Guid>>();
        public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddSettlementGroupMeteringPointsRequest request, CancellationToken ct = default) => Ok();
        public Task<(HashSet<Guid>?, string?)> ListCustomerIdsAsync(Guid id, CancellationToken ct = default) => Null<HashSet<Guid>>();
        public Task<(bool, string?)> AddCustomersAsync(Guid id, AddSettlementGroupCustomersRequest request, CancellationToken ct = default) => Ok();

        private static Task<(bool, string?)> Ok() => Task.FromResult<(bool, string?)>((true, null));
        private static Task<(T?, string?)> Null<T>() => Task.FromResult<(T?, string?)>((default, null));
    }
}
