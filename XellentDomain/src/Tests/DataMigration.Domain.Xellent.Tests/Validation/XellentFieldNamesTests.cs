using DataMigration.Domain.Xellent.Validation;

namespace DataMigration.Domain.Xellent.Tests.Validation;

public class XellentFieldNamesTests
{
    [Theory]
    [InlineData("Name")]
    [InlineData("StreetName")]
    [InlineData("PostalCode")]
    [InlineData("City")]
    [InlineData("NationalIdentificationNumber")]
    public void Validators_reference_only_registered_fields(string field)
    {
        Assert.Contains(field, XellentFieldNames.RegisteredFields);
        var mapped = XellentFieldNames.For(field);
        Assert.NotEqual(field, mapped); // mapping must differ from input — confirms it's registered
    }

    [Fact]
    public void Unknown_field_returns_input_unchanged()
    {
        Assert.Equal("UnknownThing", XellentFieldNames.For("UnknownThing"));
    }
}
