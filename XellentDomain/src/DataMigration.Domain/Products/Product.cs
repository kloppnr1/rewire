using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Products;

/// <summary>
/// A product offered to customers (e.g. "V Kvartal", "VGGSpot6"). <see cref="Code"/> is
/// the stable identifier; <see cref="Name"/> is the display name. <see cref="Commodity"/>
/// indicates which utility the product applies to.
/// </summary>
public sealed record Product(
    string Code,
    string Name,
    Commodity Commodity);
