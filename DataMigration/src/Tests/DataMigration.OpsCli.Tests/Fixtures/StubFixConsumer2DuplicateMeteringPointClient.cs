using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Fixtures;

/// <summary>
/// Drives <c>FixConsumer2DuplicateRunner</c> without a Settl environment. <see cref="GetByIdV4Async"/>
/// answers from <see cref="NextRead"/> each call — tests set this between calls to simulate a
/// post-write re-read that either confirms the fix or (the regression this stub exists to catch)
/// still shows the duplicate. Anything the runner does not call for this command stays unimplemented
/// on purpose, so a test that starts depending on a new call fails loudly rather than silently.
/// </summary>
public sealed class StubFixConsumer2DuplicateMeteringPointClient : ISettlMeteringPointClient
{
    /// <summary>Queued <see cref="GetByIdV4Async"/> responses, one per call in order (the "detect"
    /// read, then the runner's post-write "verify" read). The last item repeats once the queue is
    /// drained, so single-read tests only need one entry.</summary>
    public Queue<MeteringPointV4Dto?> Reads { get; } = new();

    private MeteringPointV4Dto? _lastRead;
    private readonly object _readLock = new();
    public Dictionary<Guid, MeteringPointV4Dto?> ConsumerPoints { get; } = [];
    public Dictionary<Guid, (string? Json, string? Error)> DeliveryHistories { get; } = [];

    /// <summary>What <see cref="BlankDuplicateSecondConsumerNameAsync"/> reports. Defaults to success.</summary>
    public (bool Success, string? Error) BlankResult { get; set; } = (true, null);

    public int GetByIdV4Calls { get; private set; }
    public int BlankCalls { get; private set; }
    public List<MeteringPointInfoDto> MarketPoints { get; } = [];
    public Func<CancellationToken, Task>? BeforeConsumerRead { get; set; }
    public Func<CancellationToken, Task>? BeforeMarketRead { get; set; }
    public Func<CancellationToken, Task>? BeforeLookup { get; set; }

    public async Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        if (BeforeConsumerRead is not null) await BeforeConsumerRead(cancellationToken);
        lock (_readLock)
        {
            GetByIdV4Calls++;
            if (ConsumerPoints.TryGetValue(meteringPointId, out var point)) return (point, null);
            if (Reads.Count > 0) _lastRead = Reads.Dequeue();
            return (_lastRead, null);
        }
    }

    public Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        BlankCalls++;
        return Task.FromResult(BlankResult);
    }

    private static T Unused<T>() => throw new NotImplementedException("Not used by these tests");

    public void SetDeliveryCategory(string deliveryCategory) => throw new NotImplementedException();
    public Task<(bool Success, string? Error)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public async Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default)
    {
        if (BeforeLookup is not null) await BeforeLookup(cancellationToken);
        return (MarketPoints.FirstOrDefault(mp => mp.MeteringPointIdentification == meteringPointIdentification), null);
    }
    public Task<(bool Success, string? Error)> CreateWaterAsync(CreateWaterMeteringPointRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointInfoDto?, string?)>>();
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointInfoDto?, string?)>>();
    public Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(Guid billingMeteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointCurrentDto?, string?)>>();
    public Task<(bool Success, string? Error)> UpdateBillingAsync(Guid billingMeteringPointId, UpdateBillingMeteringPointRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointCurrentDto?, string?)>>();
    public Task<(string? Json, string? Error)> GetByIdRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(string?, string?)>>();
    public Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(GetDeliveryStatusesResponse?, string?)>>();
    public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Task.FromResult(DeliveryHistories.GetValueOrDefault(meteringPointId, (null, "Missing fixture")));
    public Task<(bool Success, string? Error)> UpdateAsync(Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointInfoDto?, string?)>>();
    public Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(Guid billingMeteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointHistoryResponse?, string?)>>();
    public Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(MeteringPointV3HistoryResponse?, string?)>>();
    public Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(CancellationToken cancellationToken = default) => Unused<Task<(List<MarketBalanceAreaInfoDto>?, string?)>>();
    public Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(CancellationToken cancellationToken = default) => Unused<Task<(List<MeteringGridAreaInfoDto>?, string?)>>();
    public Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken cancellationToken = default) => Unused<Task<(List<BillingMeteringPointInfoDto>, int, string?)>>();
    public async Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken cancellationToken = default)
    {
        if (BeforeMarketRead is not null) await BeforeMarketRead(cancellationToken);
        return (MarketPoints.Skip(skip).Take(limit).ToList(), MarketPoints.Count, null);
    }
    public Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(CancellationToken cancellationToken = default) => Unused<Task<(List<MeteringPointInfoDto>, string?)>>();
    public Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(Guid meteringPointId, double annualVolumeKwh, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(Guid meteringPointId, double annualVolumeKwh, DateOnly effectiveStart, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(bool Success, string? Error)> AddExemptionsAsync(Guid billingMeteringPointId, AddMeteringPointExemptionsRequest request, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
}
