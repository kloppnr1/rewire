using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Tests.Validation;

internal static class TestCustomer
{
    public static Customer Build(
        string accountNumber = "10001",
        string name = "Acme Holdings ApS",
        CustomerType type = CustomerType.Company,
        string? cvr = "36665030",
        string? cpr = null,
        string streetName = "Fiktivvejhp",
        string postalCode = "8900",
        string city = "Randers",
        string? email = null)
    {
        return new Customer(
            AccountNumber: accountNumber,
            Name: name,
            AdditionalName: null,
            Type: type,
            Cvr: cvr,
            Cpr: cpr,
            Address: new Address(
                StreetName: streetName,
                BuildingNumber: "12",
                Floor: null,
                Apartment: null,
                PostalCode: postalCode,
                City: city,
                StreetCode: null,
                CitySubDivision: null,
                MunicipalityCode: null,
                CountryCode: "DK",
                Dar: null),
            Contact: new ContactInfo(Email: email, Phone: null, Cellphone: null, Fax: null),
            Billing: new CustomerBilling(DefaultEan: null, Pbs: null, MobilePay: null),
            Contracts: Array.Empty<Contract>());
    }
}
