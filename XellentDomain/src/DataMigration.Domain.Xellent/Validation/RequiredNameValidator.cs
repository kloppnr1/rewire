using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// <see cref="Customer.Name"/> must be a non-empty, non-whitespace string.
/// Replaces <c>RequiredField(customerName, ...)</c> in <c>SettlExtractionService.Customers.cs</c>.
/// </summary>
public sealed class RequiredNameValidator : ICustomerValidator
{
    public IEnumerable<ValidationEntry> Validate(Customer customer)
    {
        if (!string.IsNullOrWhiteSpace(customer.Name)) yield break;

        yield return new ValidationEntry(
            Severity: "Error",
            EntityType: "Customer",
            EntityId: customer.AccountNumber,
            Pattern: "missing Name",
            SettlField: "Name",
            XellentField: XellentFieldNames.For("Name"),
            XellentValue: null);
    }
}
