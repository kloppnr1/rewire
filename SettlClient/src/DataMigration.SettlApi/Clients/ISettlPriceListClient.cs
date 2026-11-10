using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

/// <summary>
/// Read-only access to Settl's billing price-list surface: the price-list catalog, the product-element
/// catalog, and a customer's price-list associations. Used by reconcile to diff the create-true
/// intended customer↔pricelist associations (rebuilt from Xellent) against actual Settl state.
/// </summary>
public interface ISettlPriceListClient
{
    /// <summary>
    /// Price-list catalog for a country (paginated). Build a Name→Id map once: the migration resolves
    /// a Xellent rate code to a price-list <b>Name</b> ("ST 0500"), and an association references the
    /// catalog <see cref="PriceListDto.Id"/> (GUID) — so Name→Id is the bridge.
    /// </summary>
    Task<(List<PriceListDto>? PriceLists, string? Error)> ListPriceListsAsync(
        string countryCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Product-element catalog for a country (paginated). Build a ProductElementCode→Id map: the
    /// migration produces a ProductElementCode and an association references the GUID.
    /// </summary>
    Task<(List<ProductElementDto>? ProductElements, string? Error)> ListProductElementsAsync(
        string countryCode, CancellationToken cancellationToken = default);

    /// <summary>
    /// A customer's current price-list associations (paginated). Each is
    /// <c>(PriceListId, ProductElementId, AssociationPeriod)</c>; ContractId/MarketBalanceAreaId are
    /// empty-GUID in practice.
    /// </summary>
    Task<(List<CustomerPriceListDto>? Associations, string? Error)> GetCustomerPriceListsAsync(
        Guid customerId, CancellationToken cancellationToken = default);
}
