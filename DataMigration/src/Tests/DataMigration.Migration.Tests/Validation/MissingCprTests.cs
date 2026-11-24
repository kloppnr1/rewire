using DataMigration.Domain.Xellent;

namespace DataMigration.Migration.Tests.Validation;

public class MissingCprTests
{
    [Fact]
    public void ResolveCpr_BothEmpty_ReturnsNull()
    {
        var result = IdentifierHelpers.ResolveCpr(null, null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCpr_CprDisponentSet_ReturnsCpr()
    {
        var result = IdentifierHelpers.ResolveCpr("1234567890", null);
        Assert.Equal("1234567890", result);
    }

    [Fact]
    public void ResolveCpr_AlternativeWithPnrPrefix_ReturnsParsedCpr()
    {
        var result = IdentifierHelpers.ResolveCpr(null, "PNR=1234567890");
        Assert.Equal("1234567890", result);
    }

    [Fact]
    public void ResolveCpr_AlternativeWithPnrPrefix_WrongLength_ReturnsNull()
    {
        var result = IdentifierHelpers.ResolveCpr(null, "PNR=12345");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCpr_CprTakesPrecedenceOverAlternative()
    {
        var result = IdentifierHelpers.ResolveCpr("1111111111", "PNR=2222222222");
        Assert.Equal("1111111111", result);
    }

    [Fact]
    public void ResolveCpr_InvalidLength8_ReturnsNull()
    {
        var result = IdentifierHelpers.ResolveCpr("36665030", null);
        Assert.Null(result);
    }

    [Fact]
    public void ResolveCpr_InvalidLength8_FallsToAlternative()
    {
        var result = IdentifierHelpers.ResolveCpr("36665030", "PNR=9876543210");
        Assert.Equal("9876543210", result);
    }
}
