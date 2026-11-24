using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// <see cref="Customer.Address"/> must have non-empty <c>StreetName</c>, <c>PostalCode</c>,
/// and <c>City</c>. Emits one <see cref="ValidationEntry"/> per missing field. Replaces the
/// three <c>RequiredField</c>-checks for street/postal/city in
/// <c>SettlExtractionService.Customers.cs</c>.
/// </summary>
public sealed class AddressCompletenessValidator : ICustomerValidator
{
    public IEnumerable<ValidationEntry> Validate(Customer customer)
    {
        var address = customer.Address;
        if (string.IsNullOrWhiteSpace(address.StreetName))
            yield return Missing(customer.AccountNumber, "StreetName");
        if (string.IsNullOrWhiteSpace(address.PostalCode))
            yield return Missing(customer.AccountNumber, "PostalCode");
        if (string.IsNullOrWhiteSpace(address.City))
            yield return Missing(customer.AccountNumber, "City");
    }

    private static ValidationEntry Missing(string accountNumber, string settlField) =>
        new(Severity: "Error",
            EntityType: "Customer",
            EntityId: accountNumber,
            Pattern: $"missing {settlField}",
            SettlField: settlField,
            XellentField: XellentFieldNames.For(settlField),
            XellentValue: null);
}
