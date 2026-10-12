using DataMigration.Migration.Services;

namespace DataMigration.Migration.Tests;

public class CustomerNumberPolicyTests
{
    [Theory]
    [InlineData("production", 6)]
    [InlineData("PRODUCTION", 6)]            // case-insensitive
    [InlineData("production-vhel", null)]    // VHEL paddes aldrig
    [InlineData("featuretest", null)]
    [InlineData("production-hev", null)]
    [InlineData("staging", null)]
    [InlineData(null, null)]
    public void LengthForEnv_maps_environments(string? env, int? expected)
    {
        Assert.Equal(expected, CustomerNumberPolicy.LengthForEnv(env));
    }

    [Theory]
    [InlineData("57923", 6, "957923")]
    [InlineData("5792", 5, "95792")]
    public void Pads_shorter_numbers_with_nine(string input, int length, string expected)
    {
        var result = CustomerNumberPolicy.Pad(input, length, out var wasPadded);

        Assert.Equal(expected, result);
        Assert.True(wasPadded);
    }

    [Theory]
    [InlineData("100029", 6)]
    [InlineData("57923", 5)]
    public void Leaves_exact_length_unchanged(string input, int length)
    {
        var result = CustomerNumberPolicy.Pad(input, length, out var wasPadded);

        Assert.Equal(input, result);
        Assert.False(wasPadded);
    }

    [Fact]
    public void Null_length_disables_normalisation()
    {
        var result = CustomerNumberPolicy.Pad("VRFY-1", targetLength: null, out var wasPadded);

        Assert.Equal("VRFY-1", result);
        Assert.False(wasPadded);
    }

    [Theory]
    [InlineData("1000291", 6)]   // over-length
    [InlineData("100029", 5)]    // over-length
    [InlineData("5792A", 6)]     // non-numeric
    [InlineData("", 6)]          // empty
    public void Over_length_or_non_numeric_hard_fails(string input, int length)
    {
        Assert.Throws<InvalidOperationException>(
            () => CustomerNumberPolicy.Pad(input, length, out _));
    }

    [Theory]
    // Fan-out {account}-{n}: only the account part is padded, suffix kept verbatim.
    [InlineData("57923-1", "57923", "production", "957923-1", true)]   // account paddes
    [InlineData("352639-1", "352639", "production", "352639-1", false)] // 6-cifret account, intet pad — men suffix må ikke crashe
    [InlineData("57923-1", "57923", "featuretest", "57923-1", false)]  // no-op env
    // Plain identifier (intet fan-out-suffix) falder igennem til PadForEnv.
    [InlineData("57923", "57923", "production", "957923", true)]
    [InlineData("352639", "352639", "featuretest", "352639", false)]
    public void PadPayerIdentifierForEnv_pads_account_part_only(
        string identifier, string account, string env, string expected, bool expectedPadded)
    {
        var result = CustomerNumberPolicy.PadPayerIdentifierForEnv(identifier, account, env, out var wasPadded);

        Assert.Equal(expected, result);
        Assert.Equal(expectedPadded, wasPadded);
    }

    [Theory]
    // Randers Sportsdykkerklub: konto 41589 i Xellent, kunde 941589 i Settl.
    [InlineData("941589", "production", "941589,41589")]
    // Sekscifret konto der tilfældigvis begynder med 9: sig selv først, den de-paddede bagefter.
    [InlineData("964721", "production", "964721,64721")]
    // Flere '9'er kan være padding — længste form først, for færrest fjernede cifre er mest sandsynligt.
    [InlineData("999999", "production", "999999,99999,9999,999,99,9")]
    // Ingen ledende '9': intet at pakke ud.
    [InlineData("446970", "production", "446970")]
    // Miljøet padder ikke, så tallet er kontoen.
    [InlineData("941589", "featuretest", "941589")]
    // Forkert længde eller ikke-cifre: ikke noget padding kunne have produceret.
    [InlineData("94158", "production", "94158")]
    [InlineData("9A1589", "production", "9A1589")]
    public void AccountCandidates_undoes_the_nine_padding(string customerNumber, string env, string expected)
    {
        Assert.Equal(expected.Split(','), CustomerNumberPolicy.AccountCandidates(customerNumber, env));
    }

    [Theory]
    [InlineData("41589")]
    [InlineData("5792")]
    [InlineData("352639")]
    public void AccountCandidates_contains_whatever_Pad_produced(string account)
    {
        var padded = CustomerNumberPolicy.PadForEnv(account, "production", out _);

        Assert.Contains(account, CustomerNumberPolicy.AccountCandidates(padded, "production"));
    }
}
