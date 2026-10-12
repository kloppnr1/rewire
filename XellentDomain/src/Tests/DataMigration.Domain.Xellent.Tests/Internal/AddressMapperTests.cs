using DataMigration.Core;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

public class AddressMapperTests
{
    private readonly WarningCollector _warnings = new();

    [Fact]
    public void Customer_PrimaryAddressWithDar_ParsesEverything()
    {
        var darId = Guid.NewGuid();
        var input = new CustomerAddressInput(
            Street: "Tonne Fiktivgader 65",
            Zipcode: "7000",
            City: "Fredericia",
            CountryCode: "DK",
            DarIdentifier: darId.ToString(),
            LegalFallback: null);

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 510438");

        Assert.Equal("Tonne Fiktivgader", addr.StreetName);
        Assert.Equal("65", addr.BuildingNumber);
        Assert.Equal("7000", addr.PostalCode);
        Assert.Equal("Fredericia", addr.City);
        Assert.Equal(darId, addr.Dar?.Identifier);
        Assert.Equal(0, _warnings.ErrorCount);
    }

    [Fact]
    public void Customer_PrimaryDarAndLegalFallback_LegalWins()
    {
        // DArID hører altid hjemme på legal contact (Contacttype=4) — også når CustTable's
        // primary address har sin egen DAR. Primary-adressen vinder kun hvis legal-contact
        // mangler. Bekræftet adfærd i SettlExtractionService' AddressResolver.
        var primaryDar = Guid.NewGuid();
        var legalDar = Guid.NewGuid();
        var input = new CustomerAddressInput(
            Street: "Fiktivgades 3, 3.",
            Zipcode: "1613", City: "København V", CountryCode: "DK",
            DarIdentifier: primaryDar.ToString(),
            LegalFallback: new LegalAddressFallback(
                "Fiktivvejgu", "24", "st", "", "4550", "Asnæs", legalDar.ToString()));

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 446413");

        Assert.Equal("Fiktivvejgu", addr.StreetName);
        Assert.Equal("24", addr.BuildingNumber);
        Assert.Equal("4550", addr.PostalCode);
        Assert.Equal("Asnæs", addr.City);
        Assert.Equal(legalDar, addr.Dar?.Identifier);
    }

    [Fact]
    public void Customer_LegalFallbackCrossBorder_UsesFallbacksOwnCountryNotPrimarys()
    {
        // Account 46747: primary CustTable address is DK, legal-contact fallback is in
        // Winnemark, DE. CountryCode must follow the fallback's own address (via
        // EXU_DELPOINTCONTACTINFOTABLE.ADDRESSID -> ADDRESS.EXU_ADDRESSID), not the
        // primary's — otherwise Settl rejects "24398 / Winnemark" tagged as DK.
        var input = new CustomerAddressInput(
            Street: "Primary Street 1",
            Zipcode: "1000", City: "København", CountryCode: "DK",
            DarIdentifier: null,
            LegalFallback: new LegalAddressFallback(
                "Hauptstrasse", "5", "", "", "24398", "Winnemark", "some-addr-id",
                CountryCode: "DE"));

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 46747");

        Assert.Equal("Hauptstrasse", addr.StreetName);
        Assert.Equal("24398", addr.PostalCode);
        Assert.Equal("Winnemark", addr.City);
        Assert.Equal("DE", addr.CountryCode);
    }

    [Fact]
    public void Customer_LegalFallbackWithoutOwnCountry_FallsBackToDefault()
    {
        // When the legal-contact address has no resolvable country (join miss), fall back to
        // the module default rather than silently reusing the primary's country.
        var input = new CustomerAddressInput(
            Street: "Primary Street 1",
            Zipcode: "1000", City: "København", CountryCode: "DE",
            DarIdentifier: null,
            LegalFallback: new LegalAddressFallback(
                "Fiktivvejos", "12", "", "", "8200", "Aarhus N", "addr-id", CountryCode: null));

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 1");

        Assert.Equal("DK", addr.CountryCode);
    }

    [Fact]
    public void Customer_NoDarButLegalFallback_UsesLegalFields()
    {
        var darId = Guid.NewGuid();
        var input = new CustomerAddressInput(
            Street: "ignored", Zipcode: "0000", City: "ignored", CountryCode: "DK",
            DarIdentifier: null,
            LegalFallback: new LegalAddressFallback(
                "Fiktivvejos", "12", "3", "tv", "8200", "Aarhus N", darId.ToString()));

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 1");

        Assert.Equal("Fiktivvejos", addr.StreetName);
        Assert.Equal("12", addr.BuildingNumber);
        Assert.Equal("3", addr.Floor);
        Assert.Equal("tv", addr.Apartment);
        Assert.Equal("8200", addr.PostalCode);
        Assert.Equal(darId, addr.Dar?.Identifier);
    }

    [Fact]
    public void Customer_InvalidDarString_ReportsErrorReturnsNullDar()
    {
        var input = new CustomerAddressInput(
            Street: "Some Street 1", Zipcode: "1000", City: "City", CountryCode: "DK",
            DarIdentifier: "not-a-guid",
            LegalFallback: null);

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 510438");

        Assert.Null(addr.Dar);
        Assert.Equal(1, _warnings.ErrorCount);
        Assert.Contains(_warnings.GetErrors(),
            e => e.Contains("invalid DAR identifier") && e.Contains("Customer 510438"));
    }

    [Fact]
    public void Customer_EmptyDarAndNoFallback_ReturnsNullDarNoError()
    {
        var input = new CustomerAddressInput(
            Street: "X 1", Zipcode: "1", City: "X", CountryCode: "DK",
            DarIdentifier: null, LegalFallback: null);

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 1");

        Assert.Null(addr.Dar);
        Assert.Equal(0, _warnings.ErrorCount);
    }

    [Fact]
    public void Customer_GuidEmpty_ReportsError()
    {
        var input = new CustomerAddressInput(
            Street: "X 1", Zipcode: "1", City: "X", CountryCode: "DK",
            DarIdentifier: Guid.Empty.ToString(), LegalFallback: null);

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "Customer 1");

        Assert.Null(addr.Dar);
        Assert.Equal(1, _warnings.ErrorCount);
    }

    [Fact]
    public void Customer_NoFallback_FloorAndApartmentPassedThrough()
    {
        var input = new CustomerAddressInput(
            Street: "Fiktivvejgu 4",
            Zipcode: "8000", City: "Aarhus C", CountryCode: "DK",
            DarIdentifier: null,
            LegalFallback: null,
            Floor: "2",
            Apartment: "th");

        var addr = AddressMapper.ResolveCustomerAddress(input, _warnings, "MP 571313 contact");

        Assert.Equal("2", addr.Floor);
        Assert.Equal("th", addr.Apartment);
        Assert.Equal(0, _warnings.ErrorCount);
    }

    [Fact]
    public void Delpoint_BuildsBuildingNumberFromHouseNumberAndLetter()
    {
        var input = new DelpointAddressInput(
            StreetName: "Tonne Fiktivgader",
            HouseNumberStart: 65,
            HouseLetterStart: "A",
            Floor: null, Apartment: null,
            Zipcode: "7000", City: "Fredericia",
            StreetCode: "1234", CitySubDivision: null, MunicipalityCode: "615",
            DarIdentifier: Guid.NewGuid().ToString());

        var addr = AddressMapper.ResolveDelpointAddress(input, _warnings, "MP 571");

        Assert.Equal("65A", addr.BuildingNumber);
        Assert.Equal("1234", addr.StreetCode);
        Assert.Equal("615", addr.MunicipalityCode);
    }

    [Fact]
    public void Delpoint_HouseNumberZero_LeavesBuildingNumberNull()
    {
        var input = new DelpointAddressInput(
            "Street", 0, null, null, null, "1000", "City",
            null, null, null, null);

        var addr = AddressMapper.ResolveDelpointAddress(input, _warnings, "MP X");

        Assert.Null(addr.BuildingNumber);
    }

    [Fact]
    public void Installation_UsesCustomerStreetSplit()
    {
        var input = new InstallationAddressInput(
            CustomerStreet: "Fiktivvejhp 5, 2 mf",
            Zipcode: "2100", City: "København Ø",
            CountryCode: "DK",
            DarIdentifier: null);

        var addr = AddressMapper.ResolveInstallationAddress(input, _warnings, "MP V100");

        Assert.Equal("Fiktivvejhp", addr.StreetName);
        Assert.Equal("5", addr.BuildingNumber);
        Assert.Equal("2100", addr.PostalCode);
    }
}
