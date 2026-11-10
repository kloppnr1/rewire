using DataMigration.OpsCli.Services;
namespace DataMigration.OpsCli.Tests.Services;

public class SettlementE18ProposalTests
{
    private static readonly DateOnly On = new(2026, 8, 31);
    private static SettlementConfigEntry Entry(string mp = "mp", string? template = "business", string? code = "PROD", string status = "Read") =>
        new(mp, "Month", "101681", null, template, ReferenceDate: On, MeterPointTypeCode: code, MeterPointTypeReadStatus: status);
    private static readonly IReadOnlyDictionary<string, SegmentRule> Map = new Dictionary<string, SegmentRule> {
        ["business"] = new(["Erhverv"], ["Privat"], ["Privat"]),
        ["SME"] = new(["SME"], ["SME"], ["SME"]),
        ["partner"] = new(["Partner"], ["Partner"], ["Partner"])
    };
    private static (CustomerSegmentGrouper.CustomerSegmentResult Segments, SettlementConfigE18Review Review) Apply(
        IReadOnlyCollection<SettlementConfigEntry> entries, CustomerIdentityKind kind = CustomerIdentityKind.Cvr, string? multiple = "Erhverv")
    {
        var identity = new Dictionary<string, CustomerIdentityKind> { ["101681"] = kind };
        return SettlementE18Proposal.Apply(CustomerSegmentGrouper.Build(entries, Map, identity, multiple), entries, identity);
    }
    [Theory]
    [InlineData("business", CustomerIdentityKind.Cvr, "Erhverv_ProduktionE18_UG")]
    [InlineData("SME", CustomerIdentityKind.Cvr, "Erhverv_ProduktionE18_UG")]
    [InlineData("SME", CustomerIdentityKind.Cpr, "Erhverv_ProduktionE18_UG")]
    [InlineData("business", CustomerIdentityKind.Cpr, "Privat")]
    [InlineData("partner", CustomerIdentityKind.Cvr, "Erhverv_ProduktionE18_UG")]
    public void E18_rule_applies_to_CVR_or_mapped_SME_only(string template, CustomerIdentityKind identity, string expected)
    {
        var result = Apply([Entry(template: template)], identity);
        Assert.Equal(expected, Assert.Single(result.Segments.Segments).Segment);
        if (expected == SettlementE18Proposal.Segment) Assert.Equal("mp", Assert.Single(Assert.Single(result.Review.Customers).MeteringPointIdentifications));
        else Assert.Empty(result.Review.Customers);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("unmapped")]
    public void Confirmed_E18_supplies_segment_without_ordinary_template_mapping(string? template)
    {
        var result = Apply([Entry(template: template)]);
        Assert.Equal(SettlementE18Proposal.Segment, Assert.Single(result.Segments.Segments).Segment);
        Assert.True(result.Segments.Segments[0].Mapped);
        Assert.Empty(result.Segments.Unresolved);
    }
    [Fact]
    public void One_E18_on_mixed_portfolio_wins_and_schedules_stay_unchanged()
    {
        SettlementConfigEntry[] entries = [Entry("E17", "business", "FORB"), Entry("E18", "partner")];
        var before = SettlementScheduleGrouper.Build(entries, [], new Dictionary<string, string> { ["Month"] = "Monthly" }, 2, "production", "hol", DateTime.UtcNow);
        var result = Apply(entries, multiple: null);
        Assert.Equal(SettlementE18Proposal.Segment, Assert.Single(result.Segments.Segments).Segment);
        Assert.Empty(result.Segments.Conflicts);
        Assert.Equal(2, result.Segments.Segments[0].MeteringPointCount);
        Assert.Equal("E18", Assert.Single(Assert.Single(result.Review.Customers).MeteringPointIdentifications));
        Assert.Equal("Monthly", Assert.Single(before.Schedules).Schedule);
        Assert.Equal(2, before.Schedules[0].MeteringPointCount);
        Assert.All(entries, e => Assert.Equal("Month", e.BillingPlanNum));
    }
    [Theory]
    [InlineData("D18")]
    [InlineData("OtherProduction")]
    [InlineData("FORB")]
    public void Other_types_do_not_mean_E18(string code) => Assert.Equal("Erhverv", Assert.Single(Apply([Entry(code: code)]).Segments.Segments).Segment);
    [Theory]
    [InlineData("Missing")]
    [InlineData("Ambiguous")]
    [InlineData("ReadFailed")]
    [InlineData("NotRead")]
    public void Unknown_type_is_reported_and_eligible_segment_stays_unresolved(string status)
    {
        var result = Apply([Entry(code: null, status: status)]);
        Assert.Empty(result.Segments.Segments);
        Assert.Single(result.Segments.Unresolved);
        Assert.Equal("MeterPointType" + status, Assert.Single(result.Review.Issues).Reason);
        Assert.Null(Assert.Single(result.Review.Customers).FinalProposedSegment);
        var confirmed = Apply([Entry(), Entry("unknown", code: null, status: status)]);
        Assert.Equal(SettlementE18Proposal.Segment, Assert.Single(confirmed.Segments.Segments).Segment);
        Assert.Single(confirmed.Review.Issues);
    }
    [Fact]
    public void Production_segment_wins_over_current_exemptions_and_review_does_not_credit_exemption()
    {
        SettlementConfigEntry[] entries = [Entry()];
        var identity = new Dictionary<string, CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var e18 = Apply(entries);
        var final = SettlementExemptionProposal.Apply(e18.Segments, entries, identity,
            [new("mp", null, "Read", [new(Guid.NewGuid(), "ProductElement", Guid.NewGuid(), 100)], "NotVerified", null)], new(2026, 9, 1), DateTime.UtcNow);
        Assert.Equal(SettlementE18Proposal.Segment, Assert.Single(final.Segments.Segments).Segment);
        var review = Assert.Single(final.Review!.Customers);
        Assert.False(review.Changed);
        Assert.Equal("E18", review.AppliedRule);
        Assert.Equal(SettlementE18Proposal.Segment, review.FinalProposedSegment);
    }
    [Fact]
    public void No_selected_owned_entries_cannot_apply_E18_from_a_later_tenant()
    {
        var identity = new Dictionary<string, CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build([], Map, identity);
        Assert.Empty(SettlementE18Proposal.Apply(original, [], identity).Segments.Segments);
        Assert.Empty(Apply([Entry() with { CustomerInXellent = false, MeterPointTypeCode = null, MeterPointTypeReadStatus = "NotRead" }]).Segments.Segments);
    }
    [Fact]
    public void Type_period_uses_inclusive_dated_bounds_and_not_current_open_type()
    {
        SettlementMeterPointTypePeriod[] periods = [new("FORB", new(2020, 1, 1), On), new("PROD", On.AddDays(1), new(1900, 1, 1))];
        Assert.Equal("FORB", SettlementMeterPointTypeEvidence.Resolve(periods, On).Code);
        Assert.Equal("PROD", SettlementMeterPointTypeEvidence.Resolve(periods, On.AddDays(1)).Code);
        Assert.Equal("Missing", SettlementMeterPointTypeEvidence.Resolve(periods, new(2019, 12, 31)).ReadStatus);
        Assert.Equal("Read", SettlementMeterPointTypeEvidence.Resolve([new("PROD", On, On)], On).ReadStatus);
    }
    [Fact]
    public void Overlapping_periods_and_blank_code_are_not_guessed()
    {
        Assert.Equal("Ambiguous", SettlementMeterPointTypeEvidence.Resolve([new("FORB", On, On), new("PROD", On, On)], On).ReadStatus);
        Assert.Equal("Missing", SettlementMeterPointTypeEvidence.Resolve([new("", On, new(1900, 1, 1))], On).ReadStatus);
    }
}
