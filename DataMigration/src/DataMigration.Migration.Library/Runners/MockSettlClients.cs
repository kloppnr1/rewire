using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Runners;

/// <summary>Mock Settl API clients for DryRun/seed-based pipeline runs.</summary>
public class MockCustomerClient : ISettlCustomerClient
{
    public Task<(bool, string?)> CreateAsync(CreateCustomersRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(CustomerInfoDto?, string?)> SearchByCustomerNumberAsync(string n, string c = "DK", CancellationToken ct = default) => Task.FromResult(((CustomerInfoDto?)null, (string?)null));
    public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddCustomerMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<CustomerMeteringPointDto>?, string?)> GetMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((List<CustomerMeteringPointDto>?)null, (string?)null));
    public Task<(CustomerResponse?, string?)> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((CustomerResponse?)null, (string?)null));
    public Task<(string?, string?)> GetRawByIdV3Async(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(bool, string?)> UpdateV3RawAsync(Guid id, string json, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> UpdateNationalIdentificationNumberV3Async(Guid id, string nin, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<CustomerInfoDto>, int, string?)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<CustomerInfoDto>(), 0, (string?)null));
    public Task<(List<CustomerInfoDto>, int, string?)> SearchCustomersAsync(string? fullText = null, string? name = null, string? vatIdentificationNumber = null, int limit = 50, CancellationToken ct = default) => Task.FromResult((new List<CustomerInfoDto>(), 0, (string?)null));
    public Task<(List<CustomerOrganisationMeteringPointDto>?, string?)> GetOrganisationMeteringPointsAsync(Guid customerId, CancellationToken ct = default) => Task.FromResult(((List<CustomerOrganisationMeteringPointDto>?)null, (string?)null));
    public Task<(CustomerHistoryResponse?, string?)> GetHistoryAsync(Guid customerId, CancellationToken ct = default) => Task.FromResult(((CustomerHistoryResponse?)null, (string?)null));
    public Task<(List<CustomerSegmentDto>?, string?)> FindCustomerSegmentsAsync(CancellationToken ct = default) => Task.FromResult(((List<CustomerSegmentDto>?)null, (string?)null));
    public Task<(SearchCustomerPriceListResponse?, string?)> SearchPriceListsAsync(Guid customerId, SearchCustomerPriceListRequest? request = null, CancellationToken ct = default) => Task.FromResult(((SearchCustomerPriceListResponse?)null, (string?)null));
    public Task<(List<ExtraInvoiceLineDto>, int, string?)> GetExtraInvoiceLinesAsync(Guid customerId, int limit = 50, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<ExtraInvoiceLineDto>(), 0, (string?)null));
}

public class MockPayerClient : ISettlPayerClient
{
    public Task<(bool, string?)> CreateAsync(CreatePayersRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(PayerInfoDto?, string?)> SearchByPayerIdentificationAsync(string p, string c = "DK", CancellationToken ct = default) => Task.FromResult(((PayerInfoDto?)null, (string?)null));
    public Task<(bool, string?)> AddMeteringPointsAsync(Guid id, AddPayerMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<PayerMeteringPointDto>?, string?)> GetMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((List<PayerMeteringPointDto>?)null, (string?)null));
    public Task<(PayerResponse?, string?)> GetByIdAsync(Guid id, DateTime? effectiveDate = null, CancellationToken ct = default) => Task.FromResult(((PayerResponse?)null, (string?)null));
    public Task<(PayerHistoryResponse?, string?)> GetHistoryAsync(Guid payerId, CancellationToken ct = default) => Task.FromResult(((PayerHistoryResponse?)null, (string?)null));
    public Task<(string?, string?)> GetRawByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(bool, string?)> UpdatePayerBillingAsync(Guid id, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> p, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> UpdateRawAsync(Guid id, string j, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<PayerInfoDto>, int, string?)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<PayerInfoDto>(), 0, (string?)null));
    public Task<(List<PayerInfoDto>?, string?)> FindPayersByCustomerAsync(Guid customerId, string countryCode = "DK", CancellationToken ct = default) => Task.FromResult(((List<PayerInfoDto>?)null, (string?)null));
    public Task<(List<PayerForMeteringPointsDto>?, string?)> FindPayersForMeteringPointsAsync(IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken ct = default) => Task.FromResult(((List<PayerForMeteringPointsDto>?)null, (string?)null));
}

public class MockMeteringPointClient : ISettlMeteringPointClient
{
    public void SetDeliveryCategory(string d) { }
    public Task<(bool, string?)> CreateAsync(CreateMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> CreateV4Async(CreateMeteringPointsV4Request r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(MeteringPointInfoDto?, string?)> SearchByIdentificationAsync(string m, CancellationToken ct = default) => Task.FromResult(((MeteringPointInfoDto?)null, (string?)null));
    public Task<(bool, string?)> CreateWaterAsync(CreateWaterMeteringPointRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(MeteringPointInfoDto?, string?)> SearchWaterByIdentificationAsync(string m, CancellationToken ct = default) => Task.FromResult(((MeteringPointInfoDto?)null, (string?)null));
    public Task<(MeteringPointInfoDto?, string?)> GetWaterByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((MeteringPointInfoDto?)null, (string?)null));
    public Task<(BillingMeteringPointCurrentDto?, string?)> GetBillingByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default) => Task.FromResult(((BillingMeteringPointCurrentDto?)null, (string?)null));
    public Task<(bool, string?)> UpdateBillingAsync(Guid id, UpdateBillingMeteringPointRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<MarketBalanceAreaInfoDto>?, string?)> FindMarketBalanceAreasAsync(CancellationToken ct = default) => Task.FromResult(((List<MarketBalanceAreaInfoDto>?)null, (string?)null));
    public Task<(List<MeteringGridAreaInfoDto>?, string?)> FindMeteringGridAreasAsync(CancellationToken ct = default) => Task.FromResult(((List<MeteringGridAreaInfoDto>?)null, (string?)null));
    public Task<(MeteringPointCurrentDto?, string?)> GetByIdAsync(Guid id, DateTime? effectiveDate = null, CancellationToken ct = default) => Task.FromResult(((MeteringPointCurrentDto?)null, (string?)null));
    public Task<(string?, string?)> GetByIdRawAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(GetDeliveryStatusesResponse?, string?)> GetDeliveryStatusesAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((GetDeliveryStatusesResponse?)null, (string?)null));
    public Task<(string?, string?)> GetDeliveryStatusesRawAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(bool, string?)> UpdateAsync(Guid id, MeteringPointDto dto, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(BillingMeteringPointInfoDto?, string?)> SearchBillingByIdentificationAsync(string m, CancellationToken ct = default) => Task.FromResult(((BillingMeteringPointInfoDto?)null, (string?)null));
    public Task<(BillingMeteringPointHistoryResponse?, string?)> GetBillingHistoryAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((BillingMeteringPointHistoryResponse?)null, (string?)null));
    public Task<(List<BillingMeteringPointInfoDto>, int, string?)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<BillingMeteringPointInfoDto>(), 0, (string?)null));
    public Task<(List<MeteringPointInfoDto>, int, string?)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken ct = default) => Task.FromResult((new List<MeteringPointInfoDto>(), 0, (string?)null));
    public Task<(List<MeteringPointInfoDto>, string?)> FindWaterMeteringPointsAsync(CancellationToken ct = default) => Task.FromResult((new List<MeteringPointInfoDto>(), (string?)null));
    public Task<(bool, string?)> UpdateAnnualConsumptionsAsync(Guid id, double kwh, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> UpdateEstimatedAnnualVolumeAsync(Guid id, double kwh, DateOnly effectiveStart, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> AddExemptionsAsync(Guid id, AddMeteringPointExemptionsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(MeteringPointV3HistoryResponse?, string?)> GetMarketHistoryAsync(Guid meteringPointId, CancellationToken ct = default) => Task.FromResult(((MeteringPointV3HistoryResponse?)null, (string?)null));
    public Task<(MeteringPointV4Dto?, string?)> GetByIdV4Async(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken ct = default) => Task.FromResult(((MeteringPointV4Dto?)null, (string?)null));
    public Task<(bool, string?)> BlankDuplicateSecondConsumerNameAsync(Guid meteringPointId, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
}

public class MockContractClient : ISettlContractClient
{
    public Task<(List<ContractInfoDto>?, string?)> FindContractsAsync(string? commodityType = null, CancellationToken ct = default) => Task.FromResult(((List<ContractInfoDto>?)null, (string?)null));
    public Task<(List<ContractInfoDto>?, string?)> FindContractsByMeteringPointAsync(Guid meteringPointId, CancellationToken ct = default) => Task.FromResult(((List<ContractInfoDto>?)null, (string?)null));
    public Task<(List<ContractLineInfoDto>?, string?)> GetContractLinesAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((List<ContractLineInfoDto>?)null, (string?)null));
    public Task<(List<ContractMeteringPointInfoDto>?, string?)> GetContractMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((List<ContractMeteringPointInfoDto>?)null, (string?)null));
    public Task<(List<CustomerContractWithMeteringPointsDto>?, string?)> GetCustomerMeteringPointContractsAsync(Guid customerId, CancellationToken ct = default) => Task.FromResult(((List<CustomerContractWithMeteringPointsDto>?)null, (string?)null));
    public Task<(bool, string?)> AddContractMeteringPointsAsync(Guid id, AddContractMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool, string?)> RemoveContractMeteringPointAsync(Guid contractId, Guid linkId, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
}

public class MockSettlementGroupClient : ISettlSettlementGroupClient
{
    public Task<(SettlementGroupInfoDto?, string?)> FindByNameAsync(string countryCode, string groupName, CancellationToken ct = default) => Task.FromResult(((SettlementGroupInfoDto?)null, (string?)null));
    public Task<(List<SettlementGroupInfoDto>?, string?)> FindByMeteringPointAsync(Guid mp, CancellationToken ct = default) => Task.FromResult(((List<SettlementGroupInfoDto>?)null, (string?)null));
    public Task<(List<SettlementGroupInfoDto>?, string?)> ListAllAsync(string countryCode, CancellationToken ct = default) => Task.FromResult(((List<SettlementGroupInfoDto>?)new List<SettlementGroupInfoDto>(), (string?)null));
    public Task<(HashSet<Guid>?, string?)> ListMeteringPointIdsAsync(Guid g, CancellationToken ct = default) => Task.FromResult(((HashSet<Guid>?)new HashSet<Guid>(), (string?)null));
    public Task<(bool, string?)> AddMeteringPointsAsync(Guid g, AddSettlementGroupMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(HashSet<Guid>?, string?)> ListCustomerIdsAsync(Guid g, CancellationToken ct = default) => Task.FromResult(((HashSet<Guid>?)new HashSet<Guid>(), (string?)null));
    public Task<(bool, string?)> AddCustomersAsync(Guid g, AddSettlementGroupCustomersRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
}
