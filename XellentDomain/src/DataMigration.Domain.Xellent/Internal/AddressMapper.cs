using DataMigration.Core;
using DataMigration.Domain.Common;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Inputs for building a customer/payer address. The primary fields come from
/// <c>CustTable</c> + <c>Address</c> (party-address chain). When <see cref="LegalFallback"/>
/// is non-null it always wins — the customer's own primary address is only used when no
/// legal contact is on file.
/// </summary>
public sealed record CustomerAddressInput(
    string? Street,
    string? Zipcode,
    string? City,
    string? CountryCode,
    string? DarIdentifier,
    LegalAddressFallback? LegalFallback,
    string? Floor = null,
    string? Apartment = null);

/// <summary>
/// Inputs for building an electricity-MP installation address. Comes from <c>ExuDelpoint</c>
/// joined with <c>ExuStreet</c> and <c>AddressZipCode</c>.
/// </summary>
public sealed record DelpointAddressInput(
    string? StreetName,
    int HouseNumberStart,
    string? HouseLetterStart,
    string? Floor,
    string? Apartment,
    string? Zipcode,
    string? City,
    string? StreetCode,
    string? CitySubDivision,
    string? MunicipalityCode,
    string? DarIdentifier);

/// <summary>
/// Inputs for an installation-MP address (water/heat). The MP itself has no address; the
/// customer's postal address is used and split.
/// </summary>
public sealed record InstallationAddressInput(
    string? CustomerStreet,
    string? Zipcode,
    string? City,
    string? CountryCode,
    string? DarIdentifier);

/// <summary>
/// Builds <see cref="Address"/> instances from various Xellent input shapes. Reports
/// data-quality issues (unparseable DAR id) through <see cref="IWarningCollector"/>.
/// </summary>
public static class AddressMapper
{
    private const string DefaultCountryCode = "DK";

    public static Address ResolveCustomerAddress(
        CustomerAddressInput input, IWarningCollector warnings, string entityContext)
    {
        if (input.LegalFallback != null)
        {
            var lf = input.LegalFallback!;
            return new Address(
                StreetName: lf.Streetname,
                BuildingNumber: NullIfEmpty(lf.Housenumber),
                Floor: NullIfEmpty(lf.Floor),
                Apartment: NullIfEmpty(lf.Apartment),
                PostalCode: lf.Zipcode,
                City: lf.City,
                StreetCode: null,
                CitySubDivision: null,
                MunicipalityCode: null,
                CountryCode: lf.CountryCode ?? DefaultCountryCode,
                Dar: ParseDar(lf.Addressid, warnings, entityContext));
        }

        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input.Street ?? string.Empty);
        return new Address(
            StreetName: streetName,
            BuildingNumber: NullIfEmpty(buildingNumber),
            Floor: NullIfEmpty(input.Floor),
            Apartment: NullIfEmpty(input.Apartment),
            PostalCode: input.Zipcode ?? string.Empty,
            City: input.City ?? string.Empty,
            StreetCode: null,
            CitySubDivision: null,
            MunicipalityCode: null,
            CountryCode: input.CountryCode ?? DefaultCountryCode,
            Dar: ParseDar(input.DarIdentifier, warnings, entityContext));
    }

    public static Address ResolveDelpointAddress(
        DelpointAddressInput input, IWarningCollector warnings, string entityContext)
    {
        var buildingNumber = input.HouseNumberStart > 0
            ? input.HouseNumberStart.ToString() + (input.HouseLetterStart ?? string.Empty)
            : null;

        return new Address(
            StreetName: input.StreetName ?? string.Empty,
            BuildingNumber: buildingNumber,
            Floor: NullIfEmpty(input.Floor),
            Apartment: NullIfEmpty(input.Apartment),
            PostalCode: input.Zipcode ?? string.Empty,
            City: input.City ?? string.Empty,
            StreetCode: NullIfEmpty(input.StreetCode),
            CitySubDivision: NullIfEmpty(input.CitySubDivision),
            MunicipalityCode: NullIfEmpty(input.MunicipalityCode),
            CountryCode: DefaultCountryCode,
            Dar: ParseDar(input.DarIdentifier, warnings, entityContext));
    }

    public static Address ResolveInstallationAddress(
        InstallationAddressInput input, IWarningCollector warnings, string entityContext)
    {
        var (streetName, buildingNumber) = AddressParser.ParseStreetAddress(input.CustomerStreet ?? string.Empty);
        return new Address(
            StreetName: streetName,
            BuildingNumber: NullIfEmpty(buildingNumber),
            Floor: null,
            Apartment: null,
            PostalCode: input.Zipcode ?? string.Empty,
            City: input.City ?? string.Empty,
            StreetCode: null,
            CitySubDivision: null,
            MunicipalityCode: null,
            CountryCode: input.CountryCode ?? DefaultCountryCode,
            Dar: ParseDar(input.DarIdentifier, warnings, entityContext));
    }

    private static DarAddress? ParseDar(string? raw, IWarningCollector warnings, string entityContext)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (Guid.TryParse(raw, out var id) && id != Guid.Empty)
            return new DarAddress(id);

        warnings.AddError(
            $"{entityContext}: invalid DAR identifier '{raw}' — not a valid Guid [Xellent: Addressid / ExuAddressid]");
        warnings.AddValidationEntry(new ValidationEntry(
            "Error", "Address", entityContext,
            "invalid DAR identifier (not a Guid)",
            "Address.Dar", "Addressid / ExuAddressid", raw));
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
