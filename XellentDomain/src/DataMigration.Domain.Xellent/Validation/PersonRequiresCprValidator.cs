using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// Person customers must have a valid CPR. Replaces the
/// "Person customer missing valid CPR"-error in
/// <c>SettlExtractionService.Customers.cs</c>.
/// </summary>
public sealed class PersonRequiresCprValidator : ICustomerValidator
{
    public IEnumerable<ValidationEntry> Validate(Customer customer)
    {
        if (customer.Type != CustomerType.Person) yield break;
        if (!string.IsNullOrEmpty(customer.Cpr)) yield break;

        yield return new ValidationEntry(
            Severity: "Error",
            EntityType: "Customer",
            EntityId: customer.AccountNumber,
            Pattern: "Person customer missing valid CPR",
            SettlField: "NationalIdentificationNumber",
            XellentField: XellentFieldNames.For("NationalIdentificationNumber"),
            XellentValue: null);
    }
}
