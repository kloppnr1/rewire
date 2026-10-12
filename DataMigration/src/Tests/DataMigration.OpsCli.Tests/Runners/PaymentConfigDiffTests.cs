using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.Tests.Runners;

public class PaymentConfigDiffTests
{
    [Theory]
    [InlineData("ACME", "ACME")]   // identical
    [InlineData("", "")]            // both blank
    [InlineData(null, null)]        // both null
    public void Equal_or_both_blank_is_None(string? existing, string? wanted)
        => Assert.Equal(PaymentConfigDiffAction.None, PaymentConfigDiff.Classify(existing, wanted));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Blank_wanted_is_None_even_when_settl_has_a_value(string? wanted)
    {
        // Drift-model rule (replaces the legacy "EAN must be blank on MP" heuristic): when Xellent has
        // no expectation we do NOT flag a value sitting in Settl — same as fix-payment-config, which
        // never nulls a field it has no source for.
        Assert.Equal(PaymentConfigDiffAction.None, PaymentConfigDiff.Classify("5790001234567", wanted));
    }

    [Theory]
    [InlineData(null, "ACME")]
    [InlineData("", "ACME")]
    public void Blank_settl_with_wanted_is_Backfill(string? existing, string wanted)
        => Assert.Equal(PaymentConfigDiffAction.Backfill, PaymentConfigDiff.Classify(existing, wanted));

    [Fact]
    public void Different_nonblank_values_is_Mismatch()
        => Assert.Equal(PaymentConfigDiffAction.Mismatch, PaymentConfigDiff.Classify("OLD", "NEW"));

    [Fact]
    public void Comparison_is_case_sensitive()
        => Assert.Equal(PaymentConfigDiffAction.Mismatch, PaymentConfigDiff.Classify("acme", "ACME"));
}
