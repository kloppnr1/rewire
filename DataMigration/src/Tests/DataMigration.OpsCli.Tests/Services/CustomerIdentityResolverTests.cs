using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class CustomerIdentityResolverTests
{
    [Fact]
    public void Eight_digit_cvr_makes_the_customer_a_company()
    {
        Assert.Equal(CustomerIdentityKind.Cvr, CustomerIdentityResolver.Classify("29188440", null, null));
    }

    [Fact]
    public void Cvr_wins_over_cpr_just_like_the_migrations_CustomerType()
    {
        Assert.Equal(CustomerIdentityKind.Cvr, CustomerIdentityResolver.Classify("29188440", "0101801234", null));
    }

    [Fact]
    public void Malformed_cvr_does_not_count_as_a_cvr()
    {
        // Same rule as CustomerMapper.NormalizeCvr: anything but 8 digits is not a CVR, and the
        // customer must not be pushed into a business segment on the strength of a typo.
        Assert.Equal(CustomerIdentityKind.Blank, CustomerIdentityResolver.Classify("2918844", null, null));
        Assert.Equal(CustomerIdentityKind.Blank, CustomerIdentityResolver.Classify("DK29188440", null, null));
    }

    [Fact]
    public void Cpr_is_read_from_either_xellent_source()
    {
        Assert.Equal(CustomerIdentityKind.Cpr, CustomerIdentityResolver.Classify(null, "0101801234", null));
        Assert.Equal(CustomerIdentityKind.Cpr, CustomerIdentityResolver.Classify(null, null, "PNR=0101801234"));
    }

    [Fact]
    public void Neither_identifier_is_blank()
    {
        Assert.Equal(CustomerIdentityKind.Blank, CustomerIdentityResolver.Classify(null, null, null));
        Assert.Equal(CustomerIdentityKind.Blank, CustomerIdentityResolver.Classify("", "", ""));
        // A too-short CPR is no CPR — ResolveCpr requires exactly 10 digits.
        Assert.Equal(CustomerIdentityKind.Blank, CustomerIdentityResolver.Classify(null, "010180", null));
    }
}
