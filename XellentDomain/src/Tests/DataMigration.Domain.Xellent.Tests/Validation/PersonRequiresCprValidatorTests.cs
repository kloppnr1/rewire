using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Validation;

namespace DataMigration.Domain.Xellent.Tests.Validation;

public class PersonRequiresCprValidatorTests
{
    private readonly PersonRequiresCprValidator _validator = new();

    [Fact]
    public void Person_with_null_Cpr_emits_one_error()
    {
        var customer = TestCustomer.Build(type: CustomerType.Person, cvr: null, cpr: null);

        var entries = _validator.Validate(customer).ToList();

        Assert.Single(entries);
        Assert.Equal("Error", entries[0].Severity);
        Assert.Equal("Customer", entries[0].EntityType);
        Assert.Equal(customer.AccountNumber, entries[0].EntityId);
        Assert.Equal("NationalIdentificationNumber", entries[0].SettlField);
    }

    [Fact]
    public void Person_with_Cpr_emits_no_errors()
    {
        var customer = TestCustomer.Build(type: CustomerType.Person, cvr: null, cpr: "1234567890");

        Assert.Empty(_validator.Validate(customer));
    }

    [Fact]
    public void Company_emits_no_errors_even_when_Cpr_is_null()
    {
        var customer = TestCustomer.Build(type: CustomerType.Company, cvr: "36665030", cpr: null);

        Assert.Empty(_validator.Validate(customer));
    }
}
