using DataMigration.Domain.Xellent.Validation;

namespace DataMigration.Domain.Xellent.Tests.Validation;

public class RequiredNameValidatorTests
{
    private readonly RequiredNameValidator _validator = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Empty_or_whitespace_name_emits_error(string name)
    {
        var customer = TestCustomer.Build(name: name);

        var entries = _validator.Validate(customer).ToList();

        Assert.Single(entries);
        Assert.Equal("Error", entries[0].Severity);
        Assert.Equal("Name", entries[0].SettlField);
    }

    [Fact]
    public void Non_empty_name_emits_no_errors()
    {
        var customer = TestCustomer.Build(name: "Acme");

        Assert.Empty(_validator.Validate(customer));
    }
}
