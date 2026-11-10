using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class UnresolvedSegmentResolverTests
{
    private static readonly IReadOnlyDictionary<SettlementConfigUnresolvedReason, SegmentRule> Rules =
        new Dictionary<SettlementConfigUnresolvedReason, SegmentRule>
        {
            [SettlementConfigUnresolvedReason.NotFoundInXellent] = SegmentRule.Uniform(["Erhverv"]),
            [SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier] =
                new SegmentRule(["Erhverv"], ["Privat"], ["Erhverv"])
        };

    private static SettlementConfigUnresolved Row(string mpId, SettlementConfigUnresolvedReason reason) =>
        new(mpId, reason);

    private static Dictionary<string, UnresolvedSegmentResolver.UnresolvedCustomer> Customer(
        string mpId, string customerNumber, CustomerIdentityKind identity) =>
        new(StringComparer.Ordinal)
        {
            [mpId] = new UnresolvedSegmentResolver.UnresolvedCustomer(customerNumber, "Kunde", identity)
        };

    [Fact]
    public void Settl_native_metering_points_all_get_the_business_segment()
    {
        var result = UnresolvedSegmentResolver.Apply(
            [Row("111", SettlementConfigUnresolvedReason.NotFoundInXellent)],
            Customer("111", "100001", CustomerIdentityKind.Cpr),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Rules);

        var row = Assert.Single(result);
        // The answer is uniform on purpose: these were created directly in Settl, and the decision was
        // that they are all business — the identification does not get a say.
        Assert.Equal("Erhverv", row.Segment);
        Assert.Equal("100001", row.CustomerNumber);
        Assert.False(row.CustomerHasOwnSegment);
    }

    [Theory]
    [InlineData(CustomerIdentityKind.Cvr, "Erhverv")]
    [InlineData(CustomerIdentityKind.Cpr, "Privat")]
    [InlineData(CustomerIdentityKind.Blank, "Erhverv")]
    public void A_missing_contract_answers_by_what_Settl_says_the_customer_is(
        CustomerIdentityKind identity, string expected)
    {
        var result = UnresolvedSegmentResolver.Apply(
            [Row("111", SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier)],
            Customer("111", "100001", identity),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Rules);

        Assert.Equal(expected, Assert.Single(result).Segment);
        Assert.Equal(identity, Assert.Single(result).Identity);
    }

    [Fact]
    public void The_customers_own_segment_wins_over_the_standing_answer()
    {
        var result = UnresolvedSegmentResolver.Apply(
            [Row("111", SettlementConfigUnresolvedReason.NotFoundInXellent)],
            Customer("111", "100001", CustomerIdentityKind.Cvr),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["100001"] = "SME" },
            Rules);

        var row = Assert.Single(result);
        // The customer's other metering points already decided this. One metering point we know
        // nothing about must not move her out of the segment her own data placed her in.
        Assert.Equal("SME", row.Segment);
        Assert.True(row.CustomerHasOwnSegment);
    }

    [Fact]
    public void An_unknown_customer_falls_to_the_blank_column_and_stays_nameless()
    {
        var result = UnresolvedSegmentResolver.Apply(
            [Row("111", SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier)],
            new Dictionary<string, UnresolvedSegmentResolver.UnresolvedCustomer>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Rules);

        var row = Assert.Single(result);
        Assert.Equal("Erhverv", row.Segment);
        Assert.Null(row.CustomerNumber);
        // Not Blank — Blank is a customer Settl holds no identifier for, and this is no customer at all.
        Assert.Null(row.Identity);
    }

    [Fact]
    public void A_reason_the_file_says_nothing_about_stays_undecided()
    {
        var result = UnresolvedSegmentResolver.Apply(
            [Row("111", SettlementConfigUnresolvedReason.NoBillingPlan)],
            Customer("111", "100001", CustomerIdentityKind.Cvr),
            new Dictionary<string, string>(StringComparer.Ordinal),
            Rules);

        var row = Assert.Single(result);
        Assert.Null(row.Segment);
        // The customer is still named — that is what makes the remaining decision possible.
        Assert.Equal("100001", row.CustomerNumber);
    }

    [Fact]
    public void An_ambiguous_answer_is_no_answer()
    {
        var ambiguous = new Dictionary<SettlementConfigUnresolvedReason, SegmentRule>
        {
            [SettlementConfigUnresolvedReason.NotFoundInXellent] = SegmentRule.Uniform(["Erhverv", "SME"])
        };

        var result = UnresolvedSegmentResolver.Apply(
            [Row("111", SettlementConfigUnresolvedReason.NotFoundInXellent)],
            Customer("111", "100001", CustomerIdentityKind.Cvr),
            new Dictionary<string, string>(StringComparer.Ordinal),
            ambiguous);

        Assert.Null(Assert.Single(result).Segment);
    }
}
