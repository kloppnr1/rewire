using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Runners;
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
public sealed class StubFixConsumer2DuplicateMeteringPointClient : ISettlMeteringPointClient, IConsumerVersionCleanup
{
    /// <summary>Queued <see cref="GetByIdV4Async"/> responses, one per call in order (the "detect"
    /// read, then the runner's post-write "verify" read). The last item repeats once the queue is
    /// drained, so single-read tests only need one entry.</summary>
    public Queue<MeteringPointV4Dto?> Reads { get; } = new();

    private MeteringPointV4Dto? _lastRead;
    private readonly object _readLock = new();
    public Dictionary<(Guid Id, DateTime Date), MeteringPointV4Dto?> DatedConsumerPoints { get; } = [];
    public Dictionary<Guid, (MeteringPointV3HistoryResponse? History, string? Error)> ConsumerHistories { get; } = [];
    public List<(Guid Id, DateTime? Date)> ConsumerRequests { get; } = [];
    public Dictionary<Guid, MeteringPointV4Dto?> ConsumerPoints { get; } = [];
    public const string PrefixHistory = """
        {"deliveryStatuses":[
          {"inDelivery":false,"period":{"start":"2025-12-01T00:00:00Z","end":"2026-01-01T00:00:00Z"},"startReason":"Na","stopReason":"Na"},
          {"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"ChangeOfSupplier","stopReason":"Na"}]}
        """;
    public List<Guid> DeliveryRequests { get; } = [];
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
            ConsumerRequests.Add((meteringPointId, effectiveDate));
            if (effectiveDate is { } date && DatedConsumerPoints.TryGetValue((meteringPointId, date), out var dated)) return (dated, dated is null ? "Fixture failure" : null);
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

    public List<(Guid Id, DateTime Start)> CleanedVersions { get; } = [];
    /// <summary>When set, <see cref="BlankAsync"/> refuses before writing (a pre-write check failed).</summary>
    public string? RefuseBeforeWrite { get; set; }
    /// <summary>Consumer 2's national id per point, as Settl's separate national-id read returns it.</summary>
    public Dictionary<Guid, string?> SecondNationalIds { get; } = [];
    /// <summary>Points whose national-id read fails.</summary>
    public HashSet<Guid> NationalIdReadFailures { get; } = [];
    /// <summary>When true, a successful write leaves consumer 2's national id in place.</summary>
    public bool KeepNationalIdAfterWrite { get; set; }
    public int NationalIdReads { get; private set; }

    public Task<(MeteringPointConsumerNationalIdsDto? Ids, string? Error)> GetConsumerNationalIdsAsync(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        lock (_readLock)
        {
            NationalIdReads++;
            if (NationalIdReadFailures.Contains(meteringPointId))
                return Task.FromResult<(MeteringPointConsumerNationalIdsDto?, string?)>((null, "Fixture national id failure"));
            return Task.FromResult<(MeteringPointConsumerNationalIdsDto?, string?)>((new MeteringPointConsumerNationalIdsDto
            {
                SecondConsumerPartyNationalIdentificationNumber = SecondNationalIds.GetValueOrDefault(meteringPointId)
            }, null));
        }
    }

    public async Task<(ConsumerCleanupOutcome Outcome, string? Error)> BlankAsync(Guid id, DateTime start, CancellationToken ct)
    {
        BlankCalls++;
        CleanedVersions.Add((id, start));
        if (RefuseBeforeWrite is not null) return (ConsumerCleanupOutcome.NotSent, RefuseBeforeWrite);
        if (!BlankResult.Success) return (ConsumerCleanupOutcome.Rejected, BlankResult.Error);
        if (!KeepNationalIdAfterWrite) SecondNationalIds.Remove(id);
        var (point, error) = await GetByIdV4Async(id, start, ct);
        var hasId = !string.IsNullOrWhiteSpace(SecondNationalIds.GetValueOrDefault(id));
        return point is not null && point.Consumer?.Start == start && FixConsumer2DuplicateRunner.Consumer2Reason(point, hasId) is null
            ? (ConsumerCleanupOutcome.Fixed, null) : (ConsumerCleanupOutcome.Unverified, "still shows duplicate or verification failed: " + error);
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
    public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        lock (_readLock) DeliveryRequests.Add(meteringPointId);
        return Task.FromResult(DeliveryHistories.GetValueOrDefault(meteringPointId, (PrefixHistory, null)));
    }
    public Task<(bool Success, string? Error)> UpdateAsync(Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default) => Unused<Task<(bool, string?)>>();
    public Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointInfoDto?, string?)>>();
    public Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(Guid billingMeteringPointId, CancellationToken cancellationToken = default) => Unused<Task<(BillingMeteringPointHistoryResponse?, string?)>>();
    public Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => Task.FromResult(ConsumerHistories.GetValueOrDefault(meteringPointId, (new MeteringPointV3HistoryResponse { ConsumerDates = [new DateTime(2026, 1, 1)] }, null)));
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
