using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlMeteringPointClient
{
    void SetDeliveryCategory(string deliveryCategory);
    /// <summary>
    /// Legacy electricity create (POST /api/v3/meteringPoints). Settl removed the route — it answers
    /// 404. Use <see cref="CreateV4Async"/>.
    /// </summary>
    Task<(bool Success, string? Error)> CreateAsync(
        CreateMeteringPointsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates electricity metering points via POST /api/v4/meteringPoints (201, no body).
    /// Verified against featuretest 2026-08-12.
    /// This is the live create path; the address section carries Settl's DAR flags
    /// (<c>EnrichAddress</c> / <c>SkipAddressValidation</c>).
    /// </summary>
    Task<(bool Success, string? Error)> CreateV4Async(
        CreateMeteringPointsV4Request request, CancellationToken cancellationToken = default);

    Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> CreateWaterAsync(
        CreateWaterMeteringPointRequest request, CancellationToken cancellationToken = default);

    Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default);

    Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(
        Guid billingMeteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> UpdateBillingAsync(
        Guid billingMeteringPointId, UpdateBillingMeteringPointRequest request, CancellationToken cancellationToken = default);

    Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default);

    Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    Task<(string? Json, string? Error)> GetByIdRawAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> UpdateAsync(
        Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default);

    Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(
        string meteringPointIdentification, CancellationToken cancellationToken = default);

    Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(
        Guid billingMeteringPointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Per-sektion versionshistorik fra GET /api/v3/meteringPoints/{id}/history (market v3).
    /// Returnerer Start-datoerne for hver sektion uafhængigt — <c>MeterCharacteristicsDates</c>,
    /// <c>ConsumerDates</c>, <c>GridDates</c> etc. Hent en bestemt version af en sektion med
    /// <see cref="GetByIdAsync(Guid, DateTime?, CancellationToken)"/>.
    /// <para><c>ChangeDates</c> er union af alle sektionsdatoer (det fulde MP-ændringstimeline).</para>
    /// </summary>
    Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter fuldt MP-detail fra market v4 ved givet tidspunkt (GET /api/v4/meteringPoints/{id}).
    /// V4 returnerer alle sektioner med indhold + <c>Start</c>/<c>End</c>/<c>LifeTime</c> per sektion,
    /// modsat v3 der kun returnerer sektionernes Start-dato. Udelad <paramref name="effectiveDate"/>
    /// for aktuel version.
    /// </summary>
    Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(
        Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default);

    Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(
        CancellationToken cancellationToken = default);

    Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(
        CancellationToken cancellationToken = default);

    Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(
        int limit = 100, int skip = 0, CancellationToken cancellationToken = default);

    Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(
        int limit = 100, int skip = 0, bool doCount = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returnerer alle vandmålepunkter fra commodity-API'et (WaterMeteringPointsUrl).
    /// Internt befoldes cachen via <c>SearchWaterByIdentificationAsync</c> på første kald —
    /// efterfølgende kald returnerer straks fra cachen.
    /// </summary>
    Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(
        Guid meteringPointId, double annualVolumeKwh, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the master <c>MeterCharacteristics.EstimatedAnnualVolume</c> (the field the v3 master
    /// read surfaces and create writes) via a full-object read-modify-write master PUT. This is NOT
    /// <see cref="UpdateAnnualConsumptionsAsync"/> — that endpoint writes <c>HistoricAnnualConsumption</c>,
    /// a separate field nothing in reconcile reads. EstimatedAnnualVolume is periodised by
    /// <c>MeterCharacteristics.Start</c>; <paramref name="effectiveStart"/> must be ≤ today for the new
    /// value to be the current reading.
    /// </summary>
    Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(
        Guid meteringPointId, double annualVolumeKwh, DateOnly effectiveStart, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> AddExemptionsAsync(
        Guid billingMeteringPointId, AddMeteringPointExemptionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Full-object read-modify-write master PUT (same pattern as
    /// <see cref="UpdateEstimatedAnnualVolumeAsync"/>): if <c>Consumer.SecondConsumerParty.Name</c>
    /// deep-equals <c>Consumer.FirstConsumerParty.Name</c> (all of <c>ConsumerNameDto</c> —
    /// FullName/FirstName/LastName), blanks the second party's <c>Name</c> and
    /// <c>NationalIdentificationNumber</c> (CPR) and echoes the rest of the master verbatim.
    /// <c>VatIdentificationNumber</c> is left untouched. Safe/idempotent to call unconditionally —
    /// if the names don't match, or there is no second party, this is a no-op that still returns
    /// success.
    /// </summary>
    Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(
        Guid meteringPointId, CancellationToken cancellationToken = default);
}
