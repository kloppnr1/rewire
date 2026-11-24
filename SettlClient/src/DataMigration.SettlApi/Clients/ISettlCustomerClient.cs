using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlCustomerClient
{
    Task<(bool Success, string? Error)> CreateAsync(
        CreateCustomersRequest request, CancellationToken cancellationToken = default);

    Task<(CustomerInfoDto? Customer, string? Error)> SearchByCustomerNumberAsync(
        string customerNumber, string countryCode = "DK", CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> AddMeteringPointsAsync(
        Guid customerId, AddCustomerMeteringPointsRequest request, CancellationToken cancellationToken = default);

    Task<(CustomerResponse? Customer, string? Error)> GetByIdAsync(
        Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// GETs the raw v3 customer master JSON (the shape the v3 update endpoint round-trips). The
    /// customer UPDATE lives on v3 (<c>PUT /api/v3/customers/{id}</c>), NOT the v2 endpoints this
    /// client otherwise uses — so a read-modify-write core update reads v3 here and PUTs via
    /// <see cref="UpdateV3RawAsync"/>. Pairs with <see cref="ISettlPayerClient.GetRawByIdAsync"/>.
    /// </summary>
    Task<(string? Json, string? Error)> GetRawByIdV3Async(
        Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// PUTs a pre-serialised v3 customer update body (<c>{ Customer: {...}, TransactionId }</c> —
    /// wrapped, full-echo) to <c>PUT /api/v3/customers/{id}</c>. Counterpart to
    /// <see cref="GetRawByIdV3Async"/>; the caller owns building the echo body.
    /// </summary>
    Task<(bool Success, string? Error)> UpdateV3RawAsync(
        Guid customerId, string updateRequestJson, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the customer's national identification number (CPR) via its dedicated v3 sub-resource
    /// <c>PUT /api/v3/customers/{id}/national-identification-number</c>. v3 moved this field out of the
    /// main customer body (the wrapped <see cref="UpdateV3RawAsync"/> PUT cannot carry it), so a
    /// national-id drift is written here, separately. The endpoint requires a non-empty value — it
    /// cannot clear an existing number.
    /// </summary>
    Task<(bool Success, string? Error)> UpdateNationalIdentificationNumberV3Async(
        Guid customerId, string nationalIdentificationNumber, CancellationToken cancellationToken = default);

    Task<(List<CustomerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
        Guid customerId, CancellationToken cancellationToken = default);

    Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> FindCustomersAsync(
        int limit = 1000, int skip = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches customers on the v2 customers endpoint using any combination of free-text
    /// (<paramref name="fullText"/> — matches name, address, etc.), <paramref name="name"/>, or
    /// <paramref name="vatIdentificationNumber"/> (CVR). Returns the matching <see cref="CustomerInfoDto"/>
    /// list (capped by <paramref name="limit"/>) plus the total match count.
    /// </summary>
    Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> SearchCustomersAsync(
        string? fullText = null,
        string? name = null,
        string? vatIdentificationNumber = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all metering points across the customer's organisation (the customer + its
    /// subsidiaries). For an owner/top-level customer this is the only way to reach the MPs,
    /// which are linked to the subsidiary customers rather than the owner itself.
    /// </summary>
    Task<(List<CustomerOrganisationMeteringPointDto>? Items, string? Error)> GetOrganisationMeteringPointsAsync(
        Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Customer version history from GET /api/v2/customers/{id}/history.
    /// <c>ChangeDates</c> lists the UTC timestamps at which the customer was versioned
    /// (Address.Start, Owner.Start changes). Use with <see cref="GetByIdAsync"/> + effectiveDate
    /// when you need to inspect a specific version.
    /// </summary>
    Task<(CustomerHistoryResponse? History, string? Error)> GetHistoryAsync(
        Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all customer segment definitions from GET /api/v1/customerSegments. Used to resolve
    /// a customer's <c>CustomerSegmentId</c> GUID to its name, billing settings, and supported
    /// customer type.
    /// </summary>
    Task<(List<CustomerSegmentDto>? Segments, string? Error)> FindCustomerSegmentsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the customer's active pricelist associations via POST
    /// /api/v2/customers/{id}/pricelists/search. An empty request body returns all associations;
    /// filter by passing specific <c>CustomerPriceListAssociations</c> IDs.
    /// </summary>
    Task<(SearchCustomerPriceListResponse? Result, string? Error)> SearchPriceListsAsync(
        Guid customerId, SearchCustomerPriceListRequest? request = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns extra invoice lines for the customer from GET /api/v2/customers/{id}/extraInvoiceLines.
    /// </summary>
    Task<(List<ExtraInvoiceLineDto> Items, int TotalCount, string? Error)> GetExtraInvoiceLinesAsync(
        Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default);
}
