using System.Text.Json;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementConfigMpSelectionTests
{
    private const string Mp1 = "571313179104952730";
    private const string Mp2 = "571313179104952731";
    private const string Missing = "571313179104952732";

    [Fact]
    public void Portfolio_causes_are_scoped_deduplicated_sorted_and_bounded_for_Excel()
    {
        var blockers = Enumerable.Range(0,25).Select(i => (571313179104950000L + i).ToString()).ToArray();
        var unresolved = blockers.Select(id => new SettlementConfigUnresolved(id,SettlementConfigUnresolvedReason.HistoricalCustomerMissing,"100"))
            .Append(new(Mp1,SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete,"100")).ToArray();
        var doc = SettlementScheduleGrouper.Build([],unresolved,null,26,"test","hol",DateTime.UtcNow);
        var causes = blockers.Reverse().Select(id => new SettlementConfigHistoricalIssue(id,SettlementConfigUnresolvedReason.HistoricalCustomerMissing))
            .Append(new(blockers[0],SettlementConfigUnresolvedReason.XellentCustomerMismatch))
            .Append(new(Mp2,SettlementConfigUnresolvedReason.XellentCustomerMismatch)).ToArray();
        var rows = SettlementConfigCoverage.Build(blockers.Append(Mp1).ToArray(),new Dictionary<string,DateOnly>(),doc,[],
            portfolioCauses:new Dictionary<string,IReadOnlyList<SettlementConfigHistoricalIssue>> { ["100"] = causes });
        Assert.Equal(26,rows.Count);
        var problems = rows.Single(r => r.MeteringPointIdentification == Mp1).Problems;
        Assert.Equal(11,problems.Count);
        Assert.Equal(blockers.Take(10).Select(id => $"Customer proposals are blocked by metering point {id}: Missing historical Settl customer"),
            problems.Take(10));
        Assert.Equal("Plus 15 more blocking metering points; see the JSON unresolved rows.",problems[^1]);
        Assert.DoesNotContain(problems,p => p.Contains(Mp2) || p.Contains("Xellent customer differs"));
        Assert.True(string.Join("\n",problems).Length < 32767);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_or_unlisted_portfolio_cause_keeps_fallback_without_inventing_a_point(bool unlisted)
    {
        var doc = SettlementScheduleGrouper.Build([],[new(Mp1,SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete,"100")],
            null,1,"test","hol",DateTime.UtcNow);
        var row = Assert.Single(SettlementConfigCoverage.Build([Mp1],new Dictionary<string,DateOnly>(),doc,[],
            portfolioCauses:unlisted ? new Dictionary<string,IReadOnlyList<SettlementConfigHistoricalIssue>> {
                ["100"] = [new(Mp2,SettlementConfigUnresolvedReason.XellentCustomerMismatch)] } : null));
        Assert.Equal(new[] { "Historical customer information is incomplete" },row.Problems);
    }

    [Fact]
    public void Validation_counts_duplicates_and_rejects_mixed_or_invalid_selection()
    {
        Assert.Equal(new SettlementConfigMeteringPointSelection(3, 2, 1) { ChangeOfSupplierDate = new(2026,9,1) },
            SettlementConfigCoverage.Validate([Mp1, Mp2, Mp1], new(2026,9,1), 0));
        Assert.Null(SettlementConfigCoverage.Validate(null, new(2026,9,1), 10));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate([], null, 0));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate(["57131317910495273e"], null, 0));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate(["５７１３１３１７９１０４９５２７３０"], null, 0));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate([Mp1], null, 0));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate([Mp1], DateOnly.MinValue, 0));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate(null, DateOnly.MinValue, 0));
        Assert.Throws<ArgumentException>(() => SettlementConfigCoverage.Validate([Mp1], null, 1));
    }

    [Fact]
    public void Coverage_keeps_every_unique_input_and_does_not_add_an_unlisted_sibling()
    {
        SettlementConfigEntry[] entries = [new(Mp1,"month","100",null,"private"), new(Mp2,"month","100",null,"private")];
        var document = SettlementScheduleGrouper.Build(entries, [new(Missing, SettlementConfigUnresolvedReason.NotFoundInSettl)],
            new Dictionary<string,string> { ["month"] = "Måned." }, 2,"test","hol",DateTime.UtcNow) with
        {
            Review = [new("100","Privat","Måned.",null,null,"NotAssigned","ReadFailed",null,null,null,null,[],
                ["No segment assigned in Settl","Could not read the Settl schedule"])]
        };
        var coverage = SettlementConfigCoverage.Build([Mp1,Missing,Mp1],
            new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1), [Mp2] = new(2026,10,1) }, document,entries);
        Assert.Equal(2, coverage.Count);
        Assert.DoesNotContain(coverage, r => r.MeteringPointIdentification == Mp2);
        Assert.Equal(new DateOnly(2026,9,1),coverage[0].ChangeOfSupplierDate);
        Assert.Equal("Privat",coverage[0].ProposedSegment);
        Assert.Contains("No segment assigned in Settl",coverage[0].Problems);
        Assert.Contains("Could not read the Settl schedule",coverage[0].Problems);
        Assert.Null(coverage[1].ChangeOfSupplierDate);
        Assert.Contains("Listed metering point not found in Settl",coverage[1].Problems);
    }

    [Theory]
    [InlineData(SettlementConfigUnresolvedReason.ChildMeteringPoint)]
    [InlineData(SettlementConfigUnresolvedReason.NotInDelivery)]
    [InlineData(SettlementConfigUnresolvedReason.NoDeliveryPeriod)]
    [InlineData(SettlementConfigUnresolvedReason.AmbiguousHistoricalContract)]
    public void Listed_points_excluded_by_existing_rules_remain_problem_rows(SettlementConfigUnresolvedReason reason)
    {
        var doc = SettlementScheduleGrouper.Build([], [new(Mp1,reason)],null,0,"test","hol",DateTime.UtcNow);
        var row = Assert.Single(SettlementConfigCoverage.Build([Mp1],new Dictionary<string,DateOnly>(),doc,[]));
        Assert.Contains(SettlementConfigCoverage.Explain(reason),row.Problems);
        Assert.Null(row.ProposedSchedule);
        Assert.Null(row.ProposedSegment);
    }

    [Fact]
    public void Json_omits_comparisons_and_keeps_assignment_status_and_text_ids()
    {
        var row = new SettlementConfigReviewRow("100","Privat","Måned.","Other","Other","Read","Read",
            true,true,true,["Privat_Opladning_Eje"],[],[]);
        var json = JsonSerializer.Serialize(row, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.DoesNotContain("segmentDiffers",json);
        Assert.DoesNotContain("scheduleDiffers",json);
        Assert.DoesNotContain("chargingOwnershipDiffers",json);
        Assert.DoesNotContain("settlChargingSegments",json);
        Assert.Contains("currentSegmentStatus",json);
        var coverageJson = JsonSerializer.Serialize(new SettlementConfigMeteringPointCoverage(Mp1,new(2026,9,1),"100","Privat","Måned.",[]));
        Assert.Contains("\"" + Mp1 + "\"",coverageJson);
    }

    [Theory]
    [InlineData("NotAssigned","No segment assigned in Settl")]
    [InlineData("ReadFailed","Could not read the Settl segment")]
    [InlineData("NotRead","Settl segment was not read")]
    [InlineData("Unmapped","Could not match the Settl segment to a known name")]
    public void Current_assignment_problems_distinguish_missing_failed_and_unrecognised_values(string status,string expected)
    {
        SettlementConfigEntry[] entries = [new(Mp1,"month","100",null,"private")];
        var document = SettlementScheduleGrouper.Build(entries,[],new Dictionary<string,string> { ["month"] = "Måned." },1,"test","hol",DateTime.UtcNow);
        var row = Assert.Single(SettlementConfigReview.Build(document,entries,null,null,
            new Dictionary<string,SettlementConfigCurrentSegment> { ["100"] = new(null,status) },false));
        Assert.Contains(expected,row.UnresolvedReasons);
        Assert.Contains("Could not read the Settl schedule",row.UnresolvedReasons);
        var unmatched = Assert.Single(SettlementConfigReview.Build(document,entries,
            new Dictionary<string,IReadOnlyList<string>> { ["100"] = ["unknown"] },new Dictionary<string,string>(),
            new Dictionary<string,SettlementConfigCurrentSegment> { ["100"] = new(null,status) },false));
        Assert.Contains("Could not match the Settl schedule to a known name",unmatched.UnresolvedReasons);
    }

    [Fact]
    public void Coverage_explains_metering_point_type_and_current_exemption_date_uncertainty()
    {
        var document = SettlementScheduleGrouper.Build([],[],null,0,"test","hol",DateTime.UtcNow) with
        {
            E18Review = new("test",[],[new(Mp1,"100","MeterPointTypeMissing")])
        };
        var dates = new Dictionary<string,DateOnly> { [Mp1] = new(2026,9,1),[Mp2] = new(2026,10,5) };
        var rows = SettlementConfigCoverage.Build([Mp1,Mp2],dates,document,[],currentExemptionDate:new(2026,10,5));
        Assert.Contains("Missing Xellent metering-point type for the day before delivery starts",rows[0].Problems);
        Assert.DoesNotContain("MeterPointTypeMissing",rows[0].Problems);
        Assert.Contains(SettlementExemptionProposal.HistoricalWarning,rows[0].Problems);
        Assert.DoesNotContain(SettlementExemptionProposal.HistoricalWarning,rows[1].Problems);
        Assert.DoesNotContain(SettlementExemptionProposal.HistoricalWarning,
            SettlementConfigCoverage.Build([Mp1],dates,document,[])[0].Problems);
    }
}
