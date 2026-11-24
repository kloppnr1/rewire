namespace DataMigration.Domain.Common;

/// <summary>
/// A postal address. <see cref="Dar"/> is set when the address has a registration in
/// Danmarks Adresseregister.
/// </summary>
public sealed record Address(
    string StreetName,
    string? BuildingNumber,
    string? Floor,
    string? Apartment,
    string PostalCode,
    string City,
    string? StreetCode,
    string? CitySubDivision,
    string? MunicipalityCode,
    string CountryCode,
    DarAddress? Dar);
