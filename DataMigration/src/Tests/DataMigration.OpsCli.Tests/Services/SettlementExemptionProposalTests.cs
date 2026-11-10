using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SettlementExemptionProposalTests
{
    private static readonly DateOnly Date = new(2026, 9, 1);
    private static readonly SettlementConfigCurrentExemption Exemption = new(Guid.NewGuid(), "ProductElement", Guid.NewGuid(), 100);
    [Theory]
    [InlineData("Erhverv", CustomerIdentityKind.Cvr, "Erhverv_UG")]
    [InlineData("Partner", CustomerIdentityKind.Cvr, "Erhverv_UG")]
    [InlineData("Erhverv_UG", CustomerIdentityKind.Cvr, "Erhverv_UG")]
    [InlineData("SME", CustomerIdentityKind.Cvr, "SME")]
    [InlineData("Privat", CustomerIdentityKind.Cpr, "Privat")]
    public void Current_exemption_changes_resolved_CVR_proposals_except_SME(string initial, CustomerIdentityKind identity, string expected)
    {
        var entries = new SettlementConfigEntry[] { new("mp", "plan", "101681", null, "template") };
        var identities = new Dictionary<string, CustomerIdentityKind> { ["101681"] = identity };
        var original = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string, SegmentRule> { ["template"] = new([initial], [initial], [initial]) }, identities);
        var (final, review) = SettlementExemptionProposal.Apply(original, entries, identities,
            [new("mp", null, "Read", [Exemption], "NotVerified", null)], Date, DateTime.UtcNow,
            new Dictionary<string, SettlementCurrentCustomerEvidence> { ["mp"] = new("101681", "Read") });
        Assert.Equal(expected, Assert.Single(final.Segments).Segment);
        var customer = Assert.Single(review!.Customers);
        Assert.Equal(initial, customer.XellentProposedSegment);
        Assert.Equal(expected, customer.FinalProposedSegment);
        Assert.Equal(initial != expected, customer.Changed);
        var mp = Assert.Single(review.MeteringPoints);
        Assert.Equal("101681", mp.CustomerNumber);
        Assert.Equal("NotVerified", mp.HistoricalValidity);
        Assert.Equal(SettlementExemptionProposal.HistoricalWarning, mp.Warning);
        Assert.Equal(Exemption, Assert.Single(mp.CurrentExemptions));
        if (customer.Changed) Assert.Equal(SettlementExemptionProposal.ChangedHistoricalWarning, customer.Warning);
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("ReadFailed")]
    public void Empty_current_result_and_failed_read_keep_Xellent_proposal_and_remain_distinct(string status)
    {
        var entries = new SettlementConfigEntry[] { new("mp", "plan", "101681", null, "template") };
        var identities = new Dictionary<string, CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string, SegmentRule> { ["template"] = new(["Erhverv"], ["Privat"], ["Privat"]) }, identities);
        var (final, review) = SettlementExemptionProposal.Apply(original, entries, identities,
            [new("mp", null, status, [], "NotVerified", null)], Date, DateTime.UtcNow);
        Assert.Equal("Erhverv", Assert.Single(final.Segments).Segment);
        Assert.False(Assert.Single(review!.Customers).Changed);
        var read = Assert.Single(review.MeteringPoints);
        Assert.Equal(status, read.ReadStatus);
        Assert.NotNull(read.Warning);
        Assert.Empty(read.CurrentExemptions);
    }

    [Fact]
    public void Missing_template_is_not_rescued_by_exemption_and_opt_out_has_no_review()
    {
        var entries = new SettlementConfigEntry[] { new("mp", "plan", "101681", null) };
        var identities = new Dictionary<string, CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build(entries, null, identities);
        var (final, review) = SettlementExemptionProposal.Apply(original, entries, identities,
            [new("mp", null, "Read", [Exemption], "NotVerified", null)], Date, DateTime.UtcNow,
            new Dictionary<string, SettlementCurrentCustomerEvidence> { ["mp"] = new("101681", "Read") });
        Assert.Empty(final.Segments);
        Assert.Single(final.Unresolved);
        Assert.Null(Assert.Single(review!.Customers).FinalProposedSegment);
        Assert.Null(SettlementExemptionProposal.Apply(original, entries, identities, null, Date, DateTime.UtcNow).Review);
    }

    [Fact]
    public void Per_point_history_dates_prevent_current_tenants_exemption_from_changing_original_proposal()
    {
        SettlementConfigEntry[] entries = [new("mp","plan","101681",null,"template")];
        var identities = new Dictionary<string,CustomerIdentityKind> { ["101681"] = CustomerIdentityKind.Cvr };
        var original = CustomerSegmentGrouper.Build(entries,
            new Dictionary<string,SegmentRule> { ["template"] = new(["Erhverv"],["Privat"],["Privat"]) },identities);
        var (final,review) = SettlementExemptionProposal.Apply(original,entries,identities,
            [new("mp",null,"Read",[Exemption],"Current",null)],null,DateTime.UtcNow,
            new Dictionary<string,SettlementCurrentCustomerEvidence> { ["mp"] = new("600256","Read") },
            requestedDatesByMp:new Dictionary<string,DateOnly> { ["mp"] = Date });
        Assert.Equal("Erhverv",Assert.Single(final.Segments).Segment);
        var row = Assert.Single(review!.MeteringPoints);
        Assert.Equal("DifferentCustomer",row.AttributionStatus);
        Assert.Equal(Date,row.RequestedDate);
        Assert.Equal("NotVerified",row.HistoricalValidity);
        Assert.Contains("different customer",row.Warning);
        Assert.Null(review.RequestedDate);
    }
}
