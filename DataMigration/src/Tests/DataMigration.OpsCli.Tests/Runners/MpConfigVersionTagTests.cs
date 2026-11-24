using DataMigration.OpsCli.Services.Reconcile;

namespace DataMigration.OpsCli.Tests.Runners;

public class MpConfigVersionTagTests
{
    [Fact]
    public void Prefix_then_Split_round_trips_the_version_and_leaf()
    {
        var field = MpConfigVersionTag.Prefix("BillingConfiguration", new DateOnly(2023, 5, 1)) + "JointInvoice";
        Assert.Equal("BillingConfiguration@2023-05-01.JointInvoice", field);

        var (version, bare) = MpConfigVersionTag.Split(field);
        Assert.Equal(new DateOnly(2023, 5, 1), version);
        Assert.Equal("BillingConfiguration.JointInvoice", bare);
    }

    [Fact]
    public void Split_strips_payment_version_tag()
    {
        var (version, bare) = MpConfigVersionTag.Split("PaymentConfiguration@2024-01-15.ElectronicNumber");
        Assert.Equal(new DateOnly(2024, 1, 15), version);
        Assert.Equal("PaymentConfiguration.ElectronicNumber", bare);
    }

    [Theory]
    // Current/latest-version (bare) and non-config fields are untouched — null version, field verbatim.
    [InlineData("BillingConfiguration.JointInvoice")]
    [InlineData("PaymentConfiguration.ElectronicNumber")]
    [InlineData("EstimatedAnnualVolume")]
    [InlineData("*")]
    [InlineData("")]
    public void Split_leaves_untagged_fields_unchanged(string field)
    {
        var (version, bare) = MpConfigVersionTag.Split(field);
        Assert.Null(version);
        Assert.Equal(field, bare);
    }

    [Theory]
    // A malformed tag (bad date, no dot) is not parsed — returned verbatim, no version.
    [InlineData("uz@dm.example.dk")]
    [InlineData("BillingConfiguration@2023-05-01")]
    public void Split_ignores_malformed_tags(string field)
    {
        var (version, bare) = MpConfigVersionTag.Split(field);
        Assert.Null(version);
        Assert.Equal(field, bare);
    }

    [Theory]
    // Version-tagged billing/payment rows must stay applicable for reconcile-apply (same write path as
    // the bare current-version rows), so the dashboard shows "can apply" for every version.
    [InlineData("BillingConfiguration@2023-05-01.JointInvoice")]
    [InlineData("PaymentConfiguration@2024-01-15.ElectronicNumber")]
    public void Versioned_billing_rows_remain_applicable(string field)
        => Assert.True(ReconcileApplyClassifier.Classify("MeteringPoint", "Billing", "FieldDiff", field).Applicable);

    [Fact]
    public void Row_exposes_version_and_bare_path_while_keeping_tagged_Field()
    {
        var row = new ReconcileDriftRow("100", "MeteringPoint", "571313179104952730",
            "PaymentConfiguration@2024-01-01.BuyerReference", "x", "y", DriftKind.FieldDiff, SubType: "Billing");

        Assert.Equal("PaymentConfiguration@2024-01-01.BuyerReference", row.Field); // tagged — apply contract
        Assert.Equal(new DateOnly(2024, 1, 1), row.FieldVersion);
        Assert.Equal("PaymentConfiguration.BuyerReference", row.FieldPath);
    }

    [Fact]
    public void Row_with_untagged_Field_has_null_version_and_verbatim_path()
    {
        var row = new ReconcileDriftRow("100", "MeteringPoint", "571313179104952730",
            "EstimatedAnnualVolume", "x", "y", DriftKind.FieldDiff);

        Assert.Null(row.FieldVersion);
        Assert.Equal("EstimatedAnnualVolume", row.FieldPath);
    }
}
