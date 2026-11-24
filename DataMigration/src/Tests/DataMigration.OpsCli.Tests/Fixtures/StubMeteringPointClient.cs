using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Fixtures;

/// <summary>
/// A metering point client that answers one canned page from <c>FindMeteringPointsAsync</c> and throws
/// on everything else. Enough to drive <c>SettlMeteringPointCosLoader</c> through its electricity path
/// without a Settl environment; anything the loader does not call stays unimplemented on purpose, so a
/// test that starts depending on a new call fails loudly rather than silently on a default.
/// </summary>
public sealed class StubMeteringPointClient(IReadOnlyList<MeteringPointInfoDto> meteringPoints) : ISettlMeteringPointClient
{
    public Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(
        int limit = 100, int skip = 0, bool doCount = false, CancellationToken cancellationToken = default)
    {
        var page = meteringPoints.Skip(skip).Take(limit).ToList();
        return Task.FromResult((page, meteringPoints.Count, (string?)null));
    }

    private static T Unused<T>() => throw new NotImplementedException("Not used by these tests");

    public void SetDeliveryCategory(string deliveryCategory) => throw new NotImplementedException();
    public Task<(bool Success, string? Error)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointInfoDto?, string?)>>();
    public Task<(bool Success, string? Error)> CreateWaterAsync(CreateWaterMeteringPointRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointInfoDto?, string?)>>();
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointInfoDto?, string?)>>();
    public Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(Guid billingMeteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointCurrentDto?, string?)>>();
    public Task<(bool Success, string? Error)> UpdateBillingAsync(Guid billingMeteringPointId, UpdateBillingMeteringPointRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointCurrentDto?, string?)>>();
    public Task<(string? Json, string? Error)> GetByIdRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(string?, string?)>>();
    public Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(GetDeliveryStatusesResponse?, string?)>>();
    public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(string?, string?)>>();
    public Task<(bool Success, string? Error)> UpdateAsync(Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointInfoDto?, string?)>>();
    public Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(Guid billingMeteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointHistoryResponse?, string?)>>();
    public Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointV3HistoryResponse?, string?)>>();
    public Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointV4Dto?, string?)>>();
    public Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(CancellationToken cancellationToken = default) => Unused<Task<(List<MarketBalanceAreaInfoDto>?, string?)>>();
    public Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(CancellationToken cancellationToken = default) => Unused<Task<(List<MeteringGridAreaInfoDto>?, string?)>>();
    public Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken cancellationToken = default) => Unused<Task<(List<BillingMeteringPointInfoDto>, int, string?)>>();
    public Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(CancellationToken cancellationToken = default) => Unused<Task<(List<MeteringPointInfoDto>, string?)>>();
    public Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(Guid meteringPointId, double annualVolumeKwh, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(Guid meteringPointId, double annualVolumeKwh, DateOnly effectiveStart, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> AddExemptionsAsync(Guid billingMeteringPointId, AddMeteringPointExemptionsRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
}
