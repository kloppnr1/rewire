using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Services.Settl;

/// <summary>
/// Thread-safe counters shared across the bulk-caching Settl read decorators. Surfaced in the
/// run-summary so a dry-run can prove how many per-entity round-trips were avoided.
/// </summary>
public sealed class BulkReadCacheStats
{
    private int _bulkPageCalls;
    private int _customersLoaded, _payersLoaded, _mpsLoaded;
    private int _cacheHits, _missNotFound, _fallThroughs, _linkGetsSkipped;

    public void AddPageCall() => Interlocked.Increment(ref _bulkPageCalls);
    public void SetCustomersLoaded(int n) => Interlocked.Exchange(ref _customersLoaded, n);
    public void SetPayersLoaded(int n) => Interlocked.Exchange(ref _payersLoaded, n);
    public void SetMpsLoaded(int n) => Interlocked.Exchange(ref _mpsLoaded, n);
    public void Hit() => Interlocked.Increment(ref _cacheHits);
    public void MissNotFound() => Interlocked.Increment(ref _missNotFound);
    public void FallThrough() => Interlocked.Increment(ref _fallThroughs);
    public void LinkGetSkipped() => Interlocked.Increment(ref _linkGetsSkipped);

    public int CustomersLoaded => _customersLoaded;
    public int PayersLoaded => _payersLoaded;
    public int MpsLoaded => _mpsLoaded;

    /// <summary>One-line summary for the run log. Null/empty when nothing was bulk-loaded.</summary>
    public string Summary() =>
        $"Settl reads: bulk-loaded {_customersLoaded} customers / {_payersLoaded} payers / {_mpsLoaded} MPs " +
        $"in {_bulkPageCalls} page calls; {_cacheHits} lookups from cache, {_missNotFound} not-found-from-cache, " +
        $"{_fallThroughs} fall-throughs, {_linkGetsSkipped} link-GETs skipped";
}

/// <summary>
/// Sequential paginate-until-empty helper. Terminates on an empty page (the only reliable
/// end-of-data signal — the server may cap page size below the requested limit, so we increment
/// <c>skip</c> by the actual item count rather than the requested limit). A TotalCount guard and
/// a hard iteration cap protect against a server that ignores <c>skip</c>.
/// </summary>
internal static class BulkPaginator
{
    public const int PageSize = 1000;
    private const int SafetyPageCap = 100_000;

    public static async Task<List<T>> LoadAllAsync<T>(
        Func<int, int, CancellationToken, Task<(List<T> Items, int TotalCount, string? Error)>> fetchPage,
        BulkReadCacheStats stats,
        CancellationToken ct)
    {
        var all = new List<T>();
        var skip = 0;
        var pages = 0;
        while (true)
        {
            var (items, totalCount, error) = await fetchPage(PageSize, skip, ct);
            stats.AddPageCall();
            if (error != null)
                throw new InvalidOperationException(error);
            if (items.Count == 0)
                break;
            all.AddRange(items);
            skip += items.Count;
            if (totalCount > 0 && all.Count >= totalCount)
                break;
            if (++pages > SafetyPageCap)
                throw new InvalidOperationException($"bulk pagination exceeded {SafetyPageCap} pages");
        }
        return all;
    }
}

/// <summary>
/// Shared state for the three read-cache decorators. A decorator is a pure pass-through until
/// <c>PreloadAsync</c> succeeds (<see cref="CacheState.Ready"/>); if the bulk load fails it stays
/// in <see cref="CacheState.Failed"/> and degrades to pass-through — an incomplete snapshot is
/// never served as authoritative.
/// </summary>
internal abstract class BulkCachingClientBase
{
    protected enum CacheState { Disabled, Ready, Failed }

    protected readonly bool DryRun;
    protected readonly BulkReadCacheStats Stats;
    protected readonly Action<string>? Log;

    /// <summary>Settl Ids of entities that existed at snapshot time. Used to short-circuit
    /// link-GETs: a Guid not in this set is either a fabricated dry-run Guid or a just-created
    /// entity — neither has existing links, so we can answer "empty" without a round-trip.</summary>
    protected HashSet<Guid> ExistingIds = [];
    protected CacheState State = CacheState.Disabled;

    protected BulkCachingClientBase(bool dryRun, BulkReadCacheStats stats, Action<string>? log)
    {
        DryRun = dryRun;
        Stats = stats;
        Log = log;
    }

    protected void MarkFailed(string entity, Exception ex)
    {
        State = CacheState.Failed;
        Log?.Invoke($"  WARNING: Settl bulk-load for {entity} failed ({ex.Message}); falling back to per-entity lookups");
    }
}

/// <summary>Read-through bulk cache for <see cref="ISettlCustomerClient"/>.</summary>
internal sealed class BulkCachingCustomerClient : BulkCachingClientBase, ISettlCustomerClient
{
    private readonly ISettlCustomerClient _inner;
    private Dictionary<string, CustomerInfoDto> _byNumber = new(StringComparer.Ordinal);

    public BulkCachingCustomerClient(ISettlCustomerClient inner, bool dryRun, BulkReadCacheStats stats, Action<string>? log)
        : base(dryRun, stats, log) => _inner = inner;

    public async Task PreloadAsync(CancellationToken ct)
    {
        try
        {
            var all = await BulkPaginator.LoadAllAsync<CustomerInfoDto>(
                (limit, skip, c) => _inner.FindCustomersAsync(limit, skip, c), Stats, ct);
            var byNumber = new Dictionary<string, CustomerInfoDto>(StringComparer.Ordinal);
            var ids = new HashSet<Guid>();
            foreach (var dto in all)
            {
                if (!string.IsNullOrEmpty(dto.CustomerNumber))
                    byNumber[dto.CustomerNumber] = dto;
                ids.Add(dto.Id);
            }
            _byNumber = byNumber;
            ExistingIds = ids;
            Stats.SetCustomersLoaded(all.Count);
            State = CacheState.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkFailed("customers", ex);
        }
    }

    public Task<(CustomerInfoDto? Customer, string? Error)> SearchByCustomerNumberAsync(
        string customerNumber, string countryCode = "DK", CancellationToken cancellationToken = default)
    {
        if (State == CacheState.Ready)
        {
            if (_byNumber.TryGetValue(customerNumber, out var dto))
            {
                Stats.Hit();
                return Task.FromResult<(CustomerInfoDto?, string?)>((dto, null));
            }
            if (DryRun)
            {
                Stats.MissNotFound();
                return Task.FromResult<(CustomerInfoDto?, string?)>((null, null));
            }
            Stats.FallThrough();
        }
        return _inner.SearchByCustomerNumberAsync(customerNumber, countryCode, cancellationToken);
    }

    public Task<(List<CustomerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        if (State == CacheState.Ready && !ExistingIds.Contains(customerId))
        {
            Stats.LinkGetSkipped();
            return Task.FromResult<(List<CustomerMeteringPointDto>?, string?)>((new List<CustomerMeteringPointDto>(), null));
        }
        return _inner.GetMeteringPointsAsync(customerId, cancellationToken);
    }

    // --- pure pass-through ---
    public Task<(bool Success, string? Error)> CreateAsync(CreateCustomersRequest request, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(request, cancellationToken);
    public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid customerId, AddCustomerMeteringPointsRequest request, CancellationToken cancellationToken = default)
        => _inner.AddMeteringPointsAsync(customerId, request, cancellationToken);
    public Task<(CustomerResponse? Customer, string? Error)> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default)
        => _inner.GetByIdAsync(customerId, cancellationToken);
    public Task<(string? Json, string? Error)> GetRawByIdV3Async(Guid customerId, CancellationToken cancellationToken = default)
        => _inner.GetRawByIdV3Async(customerId, cancellationToken);
    public Task<(bool Success, string? Error)> UpdateV3RawAsync(Guid customerId, string updateRequestJson, CancellationToken cancellationToken = default)
        => _inner.UpdateV3RawAsync(customerId, updateRequestJson, cancellationToken);
    public Task<(bool Success, string? Error)> UpdateNationalIdentificationNumberV3Async(Guid customerId, string nationalIdentificationNumber, CancellationToken cancellationToken = default)
        => _inner.UpdateNationalIdentificationNumberV3Async(customerId, nationalIdentificationNumber, cancellationToken);
    public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken cancellationToken = default)
        => _inner.FindCustomersAsync(limit, skip, cancellationToken);
    public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> SearchCustomersAsync(string? fullText = null, string? name = null, string? vatIdentificationNumber = null, int limit = 50, CancellationToken cancellationToken = default)
        => _inner.SearchCustomersAsync(fullText, name, vatIdentificationNumber, limit, cancellationToken);
    public Task<(List<CustomerOrganisationMeteringPointDto>? Items, string? Error)> GetOrganisationMeteringPointsAsync(Guid customerId, CancellationToken cancellationToken = default)
        => _inner.GetOrganisationMeteringPointsAsync(customerId, cancellationToken);
    public Task<(CustomerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid customerId, CancellationToken cancellationToken = default)
        => _inner.GetHistoryAsync(customerId, cancellationToken);
    public Task<(List<CustomerSegmentDto>? Segments, string? Error)> FindCustomerSegmentsAsync(CancellationToken cancellationToken = default)
        => _inner.FindCustomerSegmentsAsync(cancellationToken);
    public Task<(SearchCustomerPriceListResponse? Result, string? Error)> SearchPriceListsAsync(Guid customerId, SearchCustomerPriceListRequest? request = null, CancellationToken cancellationToken = default)
        => _inner.SearchPriceListsAsync(customerId, request, cancellationToken);
    public Task<(List<ExtraInvoiceLineDto> Items, int TotalCount, string? Error)> GetExtraInvoiceLinesAsync(Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default)
        => _inner.GetExtraInvoiceLinesAsync(customerId, limit, skip, cancellationToken);
}

/// <summary>Read-through bulk cache for <see cref="ISettlPayerClient"/>.</summary>
internal sealed class BulkCachingPayerClient : BulkCachingClientBase, ISettlPayerClient
{
    private readonly ISettlPayerClient _inner;
    private Dictionary<string, PayerInfoDto> _byIdentification = new(StringComparer.Ordinal);

    public BulkCachingPayerClient(ISettlPayerClient inner, bool dryRun, BulkReadCacheStats stats, Action<string>? log)
        : base(dryRun, stats, log) => _inner = inner;

    public async Task PreloadAsync(CancellationToken ct)
    {
        try
        {
            var all = await BulkPaginator.LoadAllAsync<PayerInfoDto>(
                (limit, skip, c) => _inner.FindPayersAsync(limit, skip, c), Stats, ct);
            var byId = new Dictionary<string, PayerInfoDto>(StringComparer.Ordinal);
            var ids = new HashSet<Guid>();
            foreach (var dto in all)
            {
                if (!string.IsNullOrEmpty(dto.PayerIdentification))
                    byId[dto.PayerIdentification] = dto;
                ids.Add(dto.Id);
            }
            _byIdentification = byId;
            ExistingIds = ids;
            Stats.SetPayersLoaded(all.Count);
            State = CacheState.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkFailed("payers", ex);
        }
    }

    public Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(
        string payerIdentification, string countryCode = "DK", CancellationToken cancellationToken = default)
    {
        if (State == CacheState.Ready)
        {
            if (_byIdentification.TryGetValue(payerIdentification, out var dto))
            {
                Stats.Hit();
                return Task.FromResult<(PayerInfoDto?, string?)>((dto, null));
            }
            if (DryRun)
            {
                Stats.MissNotFound();
                return Task.FromResult<(PayerInfoDto?, string?)>((null, null));
            }
            Stats.FallThrough();
        }
        return _inner.SearchByPayerIdentificationAsync(payerIdentification, countryCode, cancellationToken);
    }

    public Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid payerId, CancellationToken cancellationToken = default)
    {
        if (State == CacheState.Ready && !ExistingIds.Contains(payerId))
        {
            Stats.LinkGetSkipped();
            return Task.FromResult<(List<PayerMeteringPointDto>?, string?)>((new List<PayerMeteringPointDto>(), null));
        }
        return _inner.GetMeteringPointsAsync(payerId, cancellationToken);
    }

    // --- pure pass-through ---
    public Task<(bool Success, string? Error)> CreateAsync(CreatePayersRequest request, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(request, cancellationToken);
    public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken cancellationToken = default)
        => _inner.AddMeteringPointsAsync(payerId, request, cancellationToken);
    public Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(Guid payerId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
        => _inner.GetByIdAsync(payerId, effectiveDate, cancellationToken);
    public Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid payerId, CancellationToken cancellationToken = default)
        => _inner.GetHistoryAsync(payerId, cancellationToken);
    public Task<(string? Json, string? Error)> GetRawByIdAsync(Guid payerId, CancellationToken cancellationToken = default)
        => _inner.GetRawByIdAsync(payerId, cancellationToken);
    public Task<(bool Success, string? Error)> UpdatePayerBillingAsync(Guid payerId, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> billingPatches, CancellationToken cancellationToken = default)
        => _inner.UpdatePayerBillingAsync(payerId, billingPatches, cancellationToken);
    public Task<(bool Success, string? Error)> UpdateRawAsync(Guid payerId, string updateRequestJson, CancellationToken cancellationToken = default)
        => _inner.UpdateRawAsync(payerId, updateRequestJson, cancellationToken);
    public Task<(bool Success, string? Error)> DeleteAsync(Guid payerId, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(payerId, cancellationToken);
    public Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken cancellationToken = default)
        => _inner.FindPayersAsync(limit, skip, cancellationToken);
    public Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(Guid customerId, string countryCode = "DK", CancellationToken cancellationToken = default)
        => _inner.FindPayersByCustomerAsync(customerId, countryCode, cancellationToken);
    public Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken cancellationToken = default)
        => _inner.FindPayersForMeteringPointsAsync(meteringPointIds, searchDate, cancellationToken);
}

/// <summary>
/// Read-through bulk cache for <see cref="ISettlMeteringPointClient"/>. Caches only the
/// electricity (market v3) <c>SearchByIdentificationAsync</c> path — water/heat MPs use a
/// separate endpoint (<c>SearchWaterByIdentificationAsync</c>) and are left as pass-through.
/// </summary>
internal sealed class BulkCachingMeteringPointClient : BulkCachingClientBase, ISettlMeteringPointClient
{
    private readonly ISettlMeteringPointClient _inner;
    private Dictionary<string, MeteringPointInfoDto> _byIdentification = new(StringComparer.Ordinal);

    public BulkCachingMeteringPointClient(ISettlMeteringPointClient inner, bool dryRun, BulkReadCacheStats stats, Action<string>? log)
        : base(dryRun, stats, log) => _inner = inner;

    /// <param name="deliveryCategory">Set on the inner client before paginating so the market-v3
    /// enumeration targets the correct commodity endpoint.</param>
    public async Task PreloadAsync(string deliveryCategory, CancellationToken ct)
    {
        try
        {
            _inner.SetDeliveryCategory(deliveryCategory);
            var all = await BulkPaginator.LoadAllAsync<MeteringPointInfoDto>(
                // Request the count only on the first page — cheaper, and all we need for the guard.
                (limit, skip, c) => _inner.FindMeteringPointsAsync(limit, skip, doCount: skip == 0, c), Stats, ct);
            var byId = new Dictionary<string, MeteringPointInfoDto>(StringComparer.Ordinal);
            var ids = new HashSet<Guid>();
            foreach (var dto in all)
            {
                if (!string.IsNullOrEmpty(dto.MeteringPointIdentification))
                    byId[dto.MeteringPointIdentification] = dto;
                ids.Add(dto.Id);
            }
            _byIdentification = byId;
            ExistingIds = ids;
            Stats.SetMpsLoaded(all.Count);
            State = CacheState.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkFailed("metering points", ex);
        }
    }

    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default)
    {
        if (State == CacheState.Ready)
        {
            if (_byIdentification.TryGetValue(meteringPointIdentification, out var dto))
            {
                Stats.Hit();
                return Task.FromResult<(MeteringPointInfoDto?, string?)>((dto, null));
            }
            if (DryRun)
            {
                Stats.MissNotFound();
                return Task.FromResult<(MeteringPointInfoDto?, string?)>((null, null));
            }
            Stats.FallThrough();
        }
        return _inner.SearchByIdentificationAsync(meteringPointIdentification, cancellationToken);
    }

    // --- pure pass-through (incl. water search, billing, grid maps, mutations) ---
    public void SetDeliveryCategory(string deliveryCategory) => _inner.SetDeliveryCategory(deliveryCategory);
    public Task<(bool Success, string? Error)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(request, cancellationToken);
    public Task<(bool Success, string? Error)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken cancellationToken = default)
        => _inner.CreateV4Async(request, cancellationToken);
    public Task<(bool Success, string? Error)> CreateWaterAsync(CreateWaterMeteringPointRequest request, CancellationToken cancellationToken = default)
        => _inner.CreateWaterAsync(request, cancellationToken);
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default)
        => _inner.SearchWaterByIdentificationAsync(meteringPointIdentification, cancellationToken);
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
        => _inner.GetWaterByIdAsync(meteringPointId, cancellationToken);
    public Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(Guid billingMeteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
        => _inner.GetBillingByIdAsync(billingMeteringPointId, effectiveDate, cancellationToken);
    public Task<(bool Success, string? Error)> UpdateBillingAsync(Guid billingMeteringPointId, UpdateBillingMeteringPointRequest request, CancellationToken cancellationToken = default)
        => _inner.UpdateBillingAsync(billingMeteringPointId, request, cancellationToken);
    public Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
        => _inner.GetByIdAsync(meteringPointId, effectiveDate, cancellationToken);
    public Task<(string? Json, string? Error)> GetByIdRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
        => _inner.GetByIdRawAsync(meteringPointId, cancellationToken);
    public Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
        => _inner.GetDeliveryStatusesAsync(meteringPointId, cancellationToken);
    public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
        => _inner.GetDeliveryStatusesRawAsync(meteringPointId, cancellationToken);
    public Task<(bool Success, string? Error)> UpdateAsync(Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default)
        => _inner.UpdateAsync(meteringPointId, dto, cancellationToken);
    public Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default)
        => _inner.SearchBillingByIdentificationAsync(meteringPointIdentification, cancellationToken);
    public Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(Guid billingMeteringPointId, CancellationToken cancellationToken = default)
        => _inner.GetBillingHistoryAsync(billingMeteringPointId, cancellationToken);
    public Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(CancellationToken cancellationToken = default)
        => _inner.FindMarketBalanceAreasAsync(cancellationToken);
    public Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(CancellationToken cancellationToken = default)
        => _inner.FindMeteringGridAreasAsync(cancellationToken);
    public Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken cancellationToken = default)
        => _inner.FindBillingMeteringPointsAsync(limit, skip, cancellationToken);
    public Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken cancellationToken = default)
        => _inner.FindMeteringPointsAsync(limit, skip, doCount, cancellationToken);
    public Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(CancellationToken cancellationToken = default)
        => _inner.FindWaterMeteringPointsAsync(cancellationToken);
    public Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(Guid meteringPointId, double annualVolumeKwh, CancellationToken cancellationToken = default)
        => _inner.UpdateAnnualConsumptionsAsync(meteringPointId, annualVolumeKwh, cancellationToken);
    public Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(Guid meteringPointId, double annualVolumeKwh, DateOnly effectiveStart, CancellationToken cancellationToken = default)
        => _inner.UpdateEstimatedAnnualVolumeAsync(meteringPointId, annualVolumeKwh, effectiveStart, cancellationToken);
    public Task<(bool Success, string? Error)> AddExemptionsAsync(Guid billingMeteringPointId, AddMeteringPointExemptionsRequest request, CancellationToken cancellationToken = default)
        => _inner.AddExemptionsAsync(billingMeteringPointId, request, cancellationToken);
    public Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
        => _inner.GetMarketHistoryAsync(meteringPointId, cancellationToken);
    public Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
        => _inner.GetByIdV4Async(meteringPointId, effectiveDate, cancellationToken);
    public Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
        => _inner.BlankDuplicateSecondConsumerNameAsync(meteringPointId, cancellationToken);
}
