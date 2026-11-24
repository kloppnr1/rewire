using DataMigration.Domain.Xellent.Validation;

namespace DataMigration.Domain.Xellent.Tests.Validation;

public class AddressCompletenessValidatorTests
{
    private readonly AddressCompletenessValidator _validator = new();

    [Fact]
    public void All_present_emits_no_errors()
    {
        var customer = TestCustomer.Build(streetName: "Fiktivvejhp", postalCode: "8900", city: "Randers");

        Assert.Empty(_validator.Validate(customer));
    }

    [Fact]
    public void Missing_StreetName_emits_one_error()
    {
        var customer = TestCustomer.Build(streetName: "", postalCode: "8900", city: "Randers");

        var entries = _validator.Validate(customer).ToList();

        Assert.Single(entries);
        Assert.Equal("StreetName", entries[0].SettlField);
    }

    [Fact]
    public void All_missing_emits_three_errors()
    {
        var customer = TestCustomer.Build(streetName: "", postalCode: "", city: "");

        var fields = _validator.Validate(customer).Select(e => e.SettlField).ToList();

        Assert.Equal(3, fields.Count);
        Assert.Contains("StreetName", fields);
        Assert.Contains("PostalCode", fields);
        Assert.Contains("City", fields);
    }

    [Fact]
    public void Whitespace_only_treated_as_missing()
    {
        var customer = TestCustomer.Build(streetName: "Fiktivvejhp", postalCode: "   ", city: "Randers");

        var entries = _validator.Validate(customer).ToList();

        Assert.Single(entries);
        Assert.Equal("PostalCode", entries[0].SettlField);
    }
}
