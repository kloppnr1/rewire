using System.Text.Json.Nodes;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlPayerClient
{
    Task<(bool Success, string? Error)> CreateAsync(
        CreatePayersRequest request, CancellationToken cancellationToken = default);

    Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(
        string payerIdentification, string countryCode = "DK", CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> AddMeteringPointsAsync(
        Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter payer ved givet tidspunkt. Udelad <paramref name="effectiveDate"/> for aktuel version.
    /// Brug <see cref="GetHistoryAsync"/> til at finde tilgængelige versioner.
    /// </summary>
    Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(
        Guid payerId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter versionsoversigt for en payer (GET /api/v1/Payers/{id}/History).
    /// <para>Payer er versioneret: <c>PayerInformation.Start</c> og <c>PayerBilling.Start</c> markerer
    /// versionsgrænser — en PUT med anderledes <c>Start</c> opretter en ny version frem for at opdatere
    /// den eksisterende. <see cref="PayerHistoryResponse.ChangeDates"/> indeholder Start-datoen for
    /// hver historisk version; hent version <c>d</c> med <see cref="GetByIdAsync(Guid, DateTime?, CancellationToken)"/>.</para>
    /// </summary>
    Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(
        Guid payerId, CancellationToken cancellationToken = default);

    Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid payerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET af Payer som rå JSON-tekst. Bruges af konsumere der vil round-trippe
    /// PayerBilling-felter Settl returnerer men som <see cref="PayerResponse"/> ikke
    /// modellerer (<c>OrderIdentifier</c>, <c>AgreementIdentifier</c>,
    /// <c>BuyerReferenceIdentifier</c>, og 15+ andre billing-felter). PUT'en på samme
    /// endpoint er full-replacement, så typed deserialisering ville nulle alle ikke-
    /// modellerede felter.
    /// </summary>
    Task<(string? Json, string? Error)> GetRawByIdAsync(
        Guid payerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// PUT af Payer med præ-serialiseret JSON-body. Modstykke til <see cref="GetRawByIdAsync"/>;
    /// lader konsumere modificere enkeltfelter i JSON-træet og roundtrippe resten verbatim.
    /// <para><b>Body-form (verificeret mod featuretest 2026-06-13):</b> Settls runtime forventer
    /// payer-felterne <b>fladt</b> på top-niveau ved siden af <c>TransactionId</c> — IKKE wrapped i
    /// <c>{ TransactionId, Payer: {…} }</c>. En wrapped body efterlader intet top-niveau
    /// <c>CustomerId</c>, så serveren nulstiller det og sletter customer↔payer-linket. <c>PayerInformation</c>
    /// skal være <c>null</c> (round-trip af den nested adresse fejler validering; serveren bevarer
    /// eksisterende info når feltet er null).</para>
    /// </summary>
    Task<(bool Success, string? Error)> UpdateRawAsync(
        Guid payerId, string updateRequestJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Overskriver navngivne <c>PayerBilling</c>-leaves på en payer og PUT'er i Settls flade form.
    /// Indkapsler kontrakten (GET-raw → patch → flad PUT, <c>PayerInformation=null</c>) så konsumere ikke
    /// selv skal håndtere den — en forkert (wrapped) body sletter customer↔payer-linket. Caller'en læser
    /// current selv (typed <see cref="GetByIdAsync"/>) til re-validering og sender kun de drivende leaves;
    /// alt andet roundtrippes verbatim, så linket + ikke-patchede billing-felter overlever.
    /// </summary>
    Task<(bool Success, string? Error)> UpdatePayerBillingAsync(
        Guid payerId, IReadOnlyDictionary<string, JsonNode?> billingPatches, CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> DeleteAsync(
        Guid payerId, CancellationToken cancellationToken = default);

    Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(
        int limit = 100, int skip = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Henter alle betalere knyttet til én Settl-customer. Settl Billing-API'et
    /// understøtter direkte CustomerId-filter på GET /api/v1/Payers (verificeret mod
    /// featuretest), så dette er det korteste path customer→payers uden at paginere alle.
    /// </summary>
    Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(
        Guid customerId, string countryCode = "DK", CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverse lookup: given metering point Settl-GUIDs, returns the payers associated with them
    /// (at <paramref name="searchDate"/>, default = now). Only MPs that have a payer association are
    /// returned. Each <see cref="PayerForMeteringPointsDto.Payer"/> carries <c>CustomerId</c>, which
    /// is the only API path from a metering point back to its owning customer.
    /// </summary>
    Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(
        IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken cancellationToken = default);
}
