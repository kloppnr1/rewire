using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using TimeSeries.Core.Models;

namespace TimeSeries.Cli.Tests.Fixtures;

/// <summary>
/// Records all calls and lets the test program responses. Hand-rolled instead of a mock framework.
/// The runner calls these from parallel workers (done checks, presence scans), so every recording
/// takes a lock: an unlocked List.Add from two threads can drop one of the calls.
/// </summary>
public sealed class RecordingTimeSeriesClient : ISettlTimeSeriesClient
{
    public List<(Guid TsGuid, SettlTimeSeriesRequestDto Payload)> PostCalls { get; } = new();
    public Func<Guid, SettlTimeSeriesRequestDto, (bool Success, string? Error)> PostResponse { get; set; }
        = (_, _) => (true, null);

    public List<(Guid TsGuid, DateOnly Start, DateOnly End)> GetPointsCalls { get; } = new();
    public Func<Guid, DateOnly, DateOnly, (SettlPointsSummaryDto? Summary, string? Error)> GetPointsResponse { get; set; }
        = (_, _, _) => (new SettlPointsSummaryDto { Count = 0 }, null);

    public Task<(bool Success, string? Error)> PostPointsAsync(
        Guid timeSeriesGuid, SettlTimeSeriesRequestDto payload, CancellationToken cancellationToken = default)
    {
        lock (PostCalls) PostCalls.Add((timeSeriesGuid, payload));
        return Task.FromResult(PostResponse(timeSeriesGuid, payload));
    }

    public Task<(SettlPointsSummaryDto? Summary, string? Error)> GetPointsSummaryAsync(
        Guid timeSeriesGuid, DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
    {
        lock (GetPointsCalls) GetPointsCalls.Add((timeSeriesGuid, start, end));
        // Mirrors Settl: a summary period longer than one year is rejected with HTTP 400.
        if (end > start.AddYears(1))
            return Task.FromResult(((SettlPointsSummaryDto?)null, (string?)"HTTP 400: \"The requested period must not exceed 1 year.\""));
        return Task.FromResult(GetPointsResponse(timeSeriesGuid, start, end));
    }

    // Unused — return defaults so the interface compiles.
    public Task<(SettlTimeSeriesPointsResponseDto? Points, string? Error)> GetPointsAsync(Guid id, DateOnly start, DateOnly end, CancellationToken ct = default) => Task.FromResult(((SettlTimeSeriesPointsResponseDto?)null, (string?)null));
}

public sealed class RecordingMeteringPointClient : ISettlTimeSeriesMeteringPointClient
{
    public List<string> SearchByIdentificationCalls { get; } = new();
    public List<Guid> GetByIdCalls { get; } = new();
    public List<Guid> GetDeliveryStatusesCalls { get; } = new();
    public List<Guid> GetTimeSeriesCalls { get; } = new();
    public List<(Guid MpGuid, AddExternallyInvoicedPeriodsRequest Request)> AddEipCalls { get; } = new();
    public string? LastDeliveryCategory { get; private set; }

    public Func<string, MeteringPointInfoDto?> SearchByIdentificationResponse { get; set; }
        = _ => new MeteringPointInfoDto { Id = Guid.NewGuid() };
    public Func<Guid, MeteringPointCurrentDto?> GetByIdResponse { get; set; }
        = _ => null;
    public Func<Guid, (GetDeliveryStatusesResponse? Response, string? Error)> GetDeliveryStatusesResponse { get; set; }
        = _ => (
            new GetDeliveryStatusesResponse
            {
                DeliveryStatuses =
                [
                    new DeliveryStatusDto
                    {
                        InDelivery = true,
                        StartReason = DeliveryStartReason.ChangeOfSupplier,
                        Period = new TimePeriodDto
                        {
                            Start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        }
                    }
                ]
            },
            null);
    // Default: én Hour-resolution TS — matcher SeedHourlyTimeSeries-fixture i de fleste eksisterende tests.
    // Tests der seed'er andre resolutioner skal sætte GetTimeSeriesResponse eksplicit.
    public Func<Guid, List<TimeSeriesInfoDto>?> GetTimeSeriesResponse { get; set; }
        = _ => new List<TimeSeriesInfoDto>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TimeResolution = new TimeResolutionInfoDto { Length = 1, Unit = "Hour" }
            }
        };
    // Default mirrors Settl: a period whose start is after its end is rejected with HTTP 400.
    public Func<Guid, AddExternallyInvoicedPeriodsRequest, (bool Success, string? Error)> AddEipResponse { get; set; }
        = (_, request) => request.Periods.FirstOrDefault(p => p.Period != null && p.Period.Start > p.Period.End) is { } bad
            ? (false, $"HTTP 400: Period is invalid: Start {bad.Period!.Start:yyyy-MM-dd} MUST be before or equal to end {bad.Period.End:yyyy-MM-dd}")
            : (true, null);

    public void SetDeliveryCategory(string deliveryCategory) => LastDeliveryCategory = deliveryCategory;

    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default)
    {
        lock (SearchByIdentificationCalls) SearchByIdentificationCalls.Add(meteringPointIdentification);
        var result = SearchByIdentificationResponse(meteringPointIdentification);
        return Task.FromResult((result, (string?)null));
    }

    public Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
    {
        lock (GetByIdCalls) GetByIdCalls.Add(meteringPointId);
        var result = GetByIdResponse(meteringPointId);
        return Task.FromResult((result, (string?)null));
    }

    public Task<(List<TimeSeriesInfoDto>? TimeSeries, string? Error)> GetTimeSeriesAsync(
        Guid meteringPointGuid, CancellationToken cancellationToken = default)
    {
        lock (GetTimeSeriesCalls) GetTimeSeriesCalls.Add(meteringPointGuid);
        var result = GetTimeSeriesResponse(meteringPointGuid);
        return Task.FromResult((result, (string?)null));
    }

    public Task<(bool Success, string? Error)> AddExternallyInvoicedPeriodsAsync(
        Guid meteringPointId, AddExternallyInvoicedPeriodsRequest request, CancellationToken cancellationToken = default)
    {
        lock (AddEipCalls) AddEipCalls.Add((meteringPointId, request));
        return Task.FromResult(AddEipResponse(meteringPointId, request));
    }

    // Unused — return defaults so the interface compiles.
    public Task<(bool Success, string? Error)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(string? Json, string? Error)> GetByIdRawAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(bool Success, string? Error)> UpdateAsync(Guid id, MeteringPointDto dto, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> CreateWaterAsync(CreateWaterMeteringPointRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(string m, CancellationToken ct = default) => Task.FromResult(((MeteringPointInfoDto?)null, (string?)null));
    public Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(string m, CancellationToken ct = default) => Task.FromResult(((BillingMeteringPointInfoDto?)null, (string?)null));
    public Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default) => Task.FromResult(((BillingMeteringPointCurrentDto?)null, (string?)null));
    public Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((BillingMeteringPointHistoryResponse?)null, (string?)null));
    public Task<(bool Success, string? Error)> UpdateBillingAsync(Guid id, UpdateBillingMeteringPointRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(CancellationToken ct = default) => Task.FromResult(((List<MarketBalanceAreaInfoDto>?)null, (string?)null));
    public Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(CancellationToken ct = default) => Task.FromResult(((List<MeteringGridAreaInfoDto>?)null, (string?)null));
    public Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<BillingMeteringPointInfoDto>(), 0, (string?)null));
    // The environment's metering point list, served in pages like Settl does.
    public List<MeteringPointInfoDto> MeteringPoints { get; set; } = new();
    public int FindMeteringPointsCalls { get; private set; }
    public Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken ct = default)
    {
        FindMeteringPointsCalls++;
        return Task.FromResult((MeteringPoints.Skip(skip).Take(limit).ToList(), doCount ? MeteringPoints.Count : 0, (string?)null));
    }
    public Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(Guid id, double kwh, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> AddExemptionsAsync(Guid id, AddMeteringPointExemptionsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((MeteringPointInfoDto?)null, (string?)null));
    public Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(CancellationToken ct = default) => Task.FromResult((new List<MeteringPointInfoDto>(), (string?)null));
    public Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((MeteringPointV3HistoryResponse?)null, (string?)null));
    // The metering point's current delivery status. Default mirrors a consistent Settl: it starts where
    // the InDelivery=true status from GetDeliveryStatusesResponse starts (null when there is none).
    public Func<Guid, MeteringPointV4Dto?> GetByIdV4Response { get; set; }

    public RecordingMeteringPointClient()
    {
        GetByIdV4Response = id =>
            GetDeliveryStatusesResponse(id).Response?.DeliveryStatuses?.FirstOrDefault(s => s.InDelivery)?.Period is { } period
                ? new MeteringPointV4Dto
                {
                    DeliveryStatus = new MeteringPointV4DeliveryStatusDto { Start = period.Start.UtcDateTime, InDelivery = true }
                }
                : null;
    }
    public List<Guid> GetByIdV4Calls { get; } = new();
    public Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(Guid id, DateTime? d = null, CancellationToken ct = default)
    {
        lock (GetByIdV4Calls) GetByIdV4Calls.Add(id);
        return Task.FromResult((GetByIdV4Response(id), (string?)null));
    }
    public Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(Guid id, double volume, DateOnly validFrom, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(Guid id, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));

    public Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default)
    {
        lock (GetDeliveryStatusesCalls) GetDeliveryStatusesCalls.Add(meteringPointId);
        return Task.FromResult(GetDeliveryStatusesResponse(meteringPointId));
    }
}

public sealed class RecordingPayerClient : ISettlPayerClient
{
    /// <summary>Current payer identification per metering point GUID; metering points not listed have no payer.</summary>
    public Dictionary<Guid, string> PayerOf { get; } = new();
    public int FindPayersForMeteringPointsCalls { get; private set; }

    public Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(
        IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken cancellationToken = default)
    {
        FindPayersForMeteringPointsCalls++;
        var items = meteringPointIds
            .Where(PayerOf.ContainsKey)
            .GroupBy(id => PayerOf[id])
            .Select(g => new PayerForMeteringPointsDto
            {
                Payer = new PayerResponse { PayerInformation = new PayerInformationDto { PayerIdentifier = g.Key } },
                MeteringPoints = g.Select(id => new PayerMeteringPointDto { MeteringPointId = id }).ToList(),
            })
            .ToList();
        return Task.FromResult(((List<PayerForMeteringPointsDto>?)items, (string?)null));
    }

    // Unused — return defaults so the interface compiles.
    public Task<(bool Success, string? Error)> CreateAsync(CreatePayersRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(string id, string countryCode = "DK", CancellationToken ct = default) => Task.FromResult(((PayerInfoDto?)null, (string?)null));
    public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid id, AddPayerMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(Guid id, DateTime? d = null, CancellationToken ct = default) => Task.FromResult(((PayerResponse?)null, (string?)null));
    public Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((PayerHistoryResponse?)null, (string?)null));
    public Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((List<PayerMeteringPointDto>?)null, (string?)null));
    public Task<(string? Json, string? Error)> GetRawByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(bool Success, string? Error)> UpdateRawAsync(Guid id, string json, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> UpdatePayerBillingAsync(Guid id, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> p, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<PayerInfoDto>(), 0, (string?)null));
    public Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(Guid id, string countryCode = "DK", CancellationToken ct = default) => Task.FromResult(((List<PayerInfoDto>?)null, (string?)null));
}

public sealed class RecordingCustomerClient : ISettlCustomerClient
{
    public List<string> SearchByCustomerNumberCalls { get; } = new();
    public List<Guid> GetMeteringPointsCalls { get; } = new();

    public Func<string, CustomerInfoDto?> SearchByCustomerNumberResponse { get; set; }
        = num => new CustomerInfoDto { Id = Guid.NewGuid(), CustomerNumber = num };
    public Func<Guid, List<CustomerMeteringPointDto>?> GetMeteringPointsResponse { get; set; }
        = _ => new List<CustomerMeteringPointDto>();

    public Task<(CustomerInfoDto? Customer, string? Error)> SearchByCustomerNumberAsync(
        string customerNumber, string countryCode = "DK", CancellationToken cancellationToken = default)
    {
        lock (SearchByCustomerNumberCalls) SearchByCustomerNumberCalls.Add(customerNumber);
        return Task.FromResult((SearchByCustomerNumberResponse(customerNumber), (string?)null));
    }

    public Task<(List<CustomerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid customerId, CancellationToken cancellationToken = default)
    {
        lock (GetMeteringPointsCalls) GetMeteringPointsCalls.Add(customerId);
        return Task.FromResult((GetMeteringPointsResponse(customerId), (string?)null));
    }

    public Task<(bool Success, string? Error)> CreateAsync(CreateCustomersRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid id, AddCustomerMeteringPointsRequest r, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(CustomerResponse? Customer, string? Error)> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((CustomerResponse?)null, (string?)null));
    public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<CustomerInfoDto>(), 0, (string?)null));
    public Task<(string? Json, string? Error)> GetRawByIdV3Async(Guid id, CancellationToken ct = default) => Task.FromResult(((string?)null, (string?)null));
    public Task<(bool Success, string? Error)> UpdateV3RawAsync(Guid id, string json, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(bool Success, string? Error)> UpdateNationalIdentificationNumberV3Async(Guid id, string number, CancellationToken ct = default) => Task.FromResult((true, (string?)null));
    public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> SearchCustomersAsync(string? name = null, string? cvr = null, string? cpr = null, int limit = 100, CancellationToken ct = default) => Task.FromResult((new List<CustomerInfoDto>(), 0, (string?)null));
    public Task<(List<CustomerOrganisationMeteringPointDto>? Items, string? Error)> GetOrganisationMeteringPointsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((List<CustomerOrganisationMeteringPointDto>?)null, (string?)null));
    public Task<(CustomerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid id, CancellationToken ct = default) => Task.FromResult(((CustomerHistoryResponse?)null, (string?)null));
    public Task<(List<CustomerSegmentDto>? Segments, string? Error)> FindCustomerSegmentsAsync(CancellationToken ct = default) => Task.FromResult(((List<CustomerSegmentDto>?)null, (string?)null));
    public Task<(SearchCustomerPriceListResponse? Result, string? Error)> SearchPriceListsAsync(Guid id, SearchCustomerPriceListRequest? request = null, CancellationToken ct = default) => Task.FromResult(((SearchCustomerPriceListResponse?)null, (string?)null));
    public Task<(List<ExtraInvoiceLineDto> Items, int TotalCount, string? Error)> GetExtraInvoiceLinesAsync(Guid id, int limit = 100, int skip = 0, CancellationToken ct = default) => Task.FromResult((new List<ExtraInvoiceLineDto>(), 0, (string?)null));
}
