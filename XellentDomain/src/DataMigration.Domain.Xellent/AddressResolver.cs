namespace DataMigration.Domain.Xellent;

/// <summary>
/// Xellent's legal address lookup (Contacttype 4) — used as fallback when the primary
/// CustTable/Address row has no EXU_ADDRESSID. Consumed by
/// <see cref="Internal.CustomerMapper"/> / <see cref="Internal.AddressMapper"/>.
/// </summary>
public record LegalAddressFallback(
    string Streetname, string Housenumber, string Floor, string Apartment,
    string Zipcode, string City, string? Addressid, string? CountryCode = null);
