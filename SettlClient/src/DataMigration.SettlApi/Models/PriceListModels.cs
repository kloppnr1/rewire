namespace DataMigration.SettlApi.Models;

/// <summary>
/// PriceList catalog item from GET /api/v1/priceLists. <see cref="Id"/> is the Settl GUID that
/// customer associations reference as <c>PriceListId</c>; <see cref="Name"/> (e.g. "ST 0500") is
/// what the migration matches a Xellent rate code against. <see cref="PriceListIdentifier"/> is a
/// separate human code ("1000") and is NOT the association key — match on Name → Id.
/// </summary>
public class PriceListDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public string? PriceListIdentifier { get; set; }
    public string? CountryCode { get; set; }
    public string? CommodityType { get; set; }
    public string? Name { get; set; }
}

/// <summary>
/// ProductElement catalog item from GET /api/v1/productElements. <see cref="Id"/> is the Settl GUID
/// that customer associations reference as <c>ProductElementId</c>; <see cref="ProductElementCode"/>
/// is the code the migration resolves products to. <see cref="Description"/> + <see cref="BillingModel"/>
/// + <see cref="AverageUnitPriceType"/> er varens <b>standard-definition</b> — den kanoniske
/// opsætning en kontraktlinje med samme kode kan holdes op imod.
/// </summary>
public class ProductElementDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public string? ProductElementCode { get; set; }
    public string? CountryCode { get; set; }
    public string? Description { get; set; }
    public string? ExternalIdentifier { get; set; }
    /// <summary>Gennemsnits-stykpris-type (fx "Subscription") — enum on the wire, som streng.</summary>
    public string? AverageUnitPriceType { get; set; }
    /// <summary>Varens primære billing-model (pris-/mængde-/beregnings-modeller).</summary>
    public ProductElementBillingModel? BillingModel { get; set; }
}

/// <summary>Standard billing-model for en vare. Modellerings-typerne leveres som enum-strenge
/// (fx <see cref="PriceModelType"/> "Fixed", <see cref="QuantityModelType"/>
/// "FixedNumberAdjustedPerMonth").</summary>
public class ProductElementBillingModel
{
    public string? QuantityModelType { get; set; }
    public string? PriceModelType { get; set; }
    public string? CalculationModelType { get; set; }
    public string? RoundingModelType { get; set; }
    public string? VatModelType { get; set; }
}

/// <summary>
/// One customer↔pricelist association from GET /api/v2/customers/{customerId}/pricelists.
/// <see cref="PriceListId"/> / <see cref="ProductElementId"/> reference the catalog GUIDs.
/// <see cref="ContractId"/> / <see cref="MarketBalanceAreaId"/> are empty-GUID in practice.
/// <see cref="AssociationPeriod"/> carries the validity window — the live API returns it even though
/// the published swagger (docs/billing_internal_api.json) omits it.
/// </summary>
public class CustomerPriceListDto
{
    public Guid Id { get; set; }
    public string? Uri { get; set; }
    public Guid PriceListId { get; set; }
    public Guid ProductElementId { get; set; }
    public Guid ContractId { get; set; }
    public Guid MarketBalanceAreaId { get; set; }
    public AssociationPeriodDto? AssociationPeriod { get; set; }
}

/// <summary>Validity window of a customer↔pricelist association. UTC instants (e.g. Danish midnight
/// 2023-01-01 as "2022-12-31T23:00:00Z"); convert to the Danish calendar date in the consumer.</summary>
public class AssociationPeriodDto
{
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
}
