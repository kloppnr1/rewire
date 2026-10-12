using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementConfigCustomerResultsTests
{
    private const string A = "571313179104682026", B = "571313179104952731", C = "571313179104952732";
    private static readonly DateOnly Cos = new(2026,9,1);
    private static SettlementConfigEntry Entry(string id,string plan = "P1",string? template = "T1",string? name = "Ordinary business") =>
        new(id,plan,"513713",name,template,[],CustomerInXellent:true,XellentAccount:"513713",ReferenceDate:Cos.AddDays(-1),
            MeterPointTypeCode:"FORB",MeterPointTypeReadStatus:"Read");
    private static ExportSettlementConfigRunOptions Options()
    {
        var options = new ExportSettlementConfigRunOptions {
        MeteringPointIds = [A,B],ChangeOfSupplierDate = Cos,
        ScheduleMap = new Dictionary<string,string> { ["P1"] = "KvartalAC",["Alias"] = "KvartalAC",["P2"] = "Måned.",["HalfYear"] = "HalvårBagud" },
        SegmentMap = new Dictionary<string,SegmentRule> { ["T1"] = SegmentRule.Uniform(["Partner"]),
            ["Alias"] = SegmentRule.Uniform(["Partner"]),["T2"] = SegmentRule.Uniform(["Erhverv"]),
            ["Ambiguous"] = SegmentRule.Uniform(["Partner","Erhverv"]) } };
        options.Config = new(options.ScheduleMap,options.SegmentMap,CustomerExceptions:
            [new("Hybel Nord*","Partner","HalvårBagud"),new("Hybel Sjælland*","Partner","HalvårBagud"),
                new("Hybel Syd*","Partner","HalvårBagud"),new("Hybel Midt*","Partner","HalvårBagud")]);
        return options;
    }

    private static SettlementConfigDocument Apply(SettlementConfigEntry[] entries,
        IReadOnlyList<SettlementConfigExemptionRead>? exemptions = null,
        IReadOnlyDictionary<string,SettlementCurrentCustomerEvidence>? owners = null,
        IReadOnlySet<string>? incomplete = null,IReadOnlySet<string>? chargeFailures = null,
        IReadOnlyList<SettlementConfigMeteringPointCoverage>? coverage = null)
    {
        var options = Options();
        var identities = new Dictionary<string,CustomerIdentityKind> { ["513713"] = CustomerIdentityKind.Cvr };
        var dates = (coverage?.Select(c => c.MeteringPointIdentification) ?? entries.Select(e => e.MeteringPointIdentification))
            .Distinct().ToDictionary(id => id,_ => Cos);
        var document = SettlementScheduleGrouper.Build(entries,[],options.ScheduleMap,dates.Count,"production","hol",new(2026,10,7));
        var original = CustomerSegmentGrouper.Build(entries,options.SegmentMap,identities);
        var e18 = SettlementE18Proposal.Apply(original,entries,identities);
        var exemption = SettlementExemptionProposal.Apply(e18.Segments,entries,identities,exemptions,Cos,document.GeneratedAt,owners,"production",dates);
        document = document with { Segments = exemption.Segments.Segments, E18Review = e18.Review, ExemptionReview = exemption.Review,
            MeteringPointCoverage = coverage ?? entries.Select(e => new SettlementConfigMeteringPointCoverage(
                e.MeteringPointIdentification,Cos,e.CustomerNumber,null,null,[])).ToArray() };
        document = document with { Review = SettlementConfigReview.Build(document,entries,null,null,
            new Dictionary<string,SettlementConfigCurrentSegment>(),false,true) };
        return SettlementConfigCustomerResults.Apply(document,entries,dates,options,identities,
            incomplete ?? new HashSet<string>(),chargeFailures ?? new HashSet<string>(),exemptions,owners);
    }

    [Theory]
    [InlineData("P1","T1","Partner","KvartalAC",0,0)]
    [InlineData("Alias","Alias","Partner","KvartalAC",0,0)]
    [InlineData("P1","T2",null,"KvartalAC",1,0)]
    [InlineData("P2","T1","Partner",null,0,1)]
    [InlineData("P2","T2",null,null,1,1)]
    [InlineData("","T1","Partner",null,0,0)]
    [InlineData("P1",null,null,"KvartalAC",0,0)]
    public void Compare_mapped_values_without_customer_winners(string plan,string? template,string? segment,string? schedule,int segments,int schedules)
    {
        var document = Apply([Entry(A),Entry(B,plan,template)]);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Equal(segment,result.ProposedSegment);
        Assert.Equal(schedule,result.ProposedSchedule);
        Assert.Equal(segments,document.SegmentConflicts!.Count);
        Assert.Equal(schedules,document.ScheduleConflicts!.Count);
        Assert.Equal("Partner",result.MeteringPoints[0].ProposedSegment);
        Assert.Equal("KvartalAC",result.MeteringPoints[0].ProposedSchedule);
        Assert.Equal(segment,Assert.Single(document.Review!).ProposedSegment);
        Assert.Equal(schedule,Assert.Single(document.Review!).ProposedSchedule);
        Assert.Equal(segment is null ? 0 : 1,document.Totals.SegmentsAssigned);
        Assert.Equal(schedule is null ? 0 : 1,document.Schedules.Sum(g => g.CustomerCount));
        if (segments + schedules > 0)
        {
            var conflict = string.Join(" ",result.Problems);
            Assert.Contains(A,conflict); Assert.Contains(B,conflict);
            Assert.Contains("Partner",conflict.Contains("segment") ? conflict : string.Join(" ",result.MeteringPoints.Select(p => p.ProposedSegment)));
        }
        if (plan.Length == 0 || template is null) Assert.NotEmpty(result.MeteringPoints[1].Problems);
    }

    [Theory]
    [InlineData("Hybel Nord A/S")]
    [InlineData("Hybel Sjælland A/S")]
    [InlineData("Hybel Syd A/S")]
    [InlineData("Hybel Midt A/S")]
    [InlineData("Hybel Nord")]
    [InlineData("Hybel Sjælland")]
    [InlineData("Hybel Syd")]
    [InlineData("Hybel Midt")]
    [InlineData("Hybel Nord A/S extra")]
    [InlineData("Hybel Nord AS")]
    [InlineData("Hybel Sjælland A/S.")]
    [InlineData("Hybel Syd afdeling 2")]
    [InlineData(" HYBEL\t SYD  \n")]
    [InlineData("  HYBEL\t nord   a/s \n")]
    public void Hybel_name_prefixes_override_before_agreement_and_preserve_source_evidence(string name)
    {
        var document = Apply([Entry(A,"P1","T1",name),Entry(B,"P2","T2",name)]);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Equal("Partner",result.ProposedSegment); Assert.Equal("HalvårBagud",result.ProposedSchedule);
        Assert.Empty(document.ScheduleConflicts!); Assert.Empty(document.SegmentConflicts!);
        Assert.Equal(2,document.NamedCustomerExceptionEvidence!.Count);
        var original = document.NamedCustomerExceptionEvidence!.Single(e => e.MeteringPointIdentification == B);
        Assert.Equal("ConfiguredCustomerException",original.Rule);
        Assert.Equal("Wildcard",original.MatchMode);
        Assert.NotNull(original.MatchedPattern);
        Assert.Equal("Erhverv",original.OriginalProposedSegment); Assert.Equal("Måned.",original.OriginalProposedSchedule);
        Assert.True(original.SegmentApplied); Assert.True(original.ScheduleApplied);
        Assert.Equal("T2",Assert.Single(document.Review!).Xellent.Single(p => p.MeteringPointIdentification == B).ContractTemplate);
    }

    [Theory]
    [InlineData("Hybel Nordvest A/S")]
    [InlineData("Hybel Sydvest")]
    [InlineData("Hybel Vest A/S")]
    [InlineData("Hybel Sjællands A/S")]
    [InlineData("Hybel Midtjylland")]
    [InlineData("Peter Hybel Syd")]
    [InlineData("Hybel Hans Nord")]
    [InlineData("Hybel")]
    [InlineData("Hybel Syd-A/S")]
    public void Near_names_are_not_exceptions(string name)
    {
        Assert.False(SettlementConfigCustomerResults.HasNamedException(name));
        var document = Apply([Entry(A,name:name)]);
        Assert.Empty(document.NamedCustomerExceptionEvidence!);
        Assert.Equal("KvartalAC",Assert.Single(document.CustomerResults!).ProposedSchedule);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Ambiguous")]
    public void Hybel_supersedes_only_mapping_errors(string template)
    {
        var document = Apply([Entry(A,"Unknown",template,"Hybel Midt A/S")]);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Equal("Partner",result.ProposedSegment); Assert.Equal("HalvårBagud",result.ProposedSchedule);
        Assert.DoesNotContain(result.MeteringPoints[0].Problems,p => p.Contains("No mapped") || p.Contains("mapping") || p.Contains("ambiguous segment"));
        Assert.Empty(document.SegmentConflicts!);
        Assert.Null(Assert.Single(document.NamedCustomerExceptionEvidence!).OriginalProposedSegment);
    }

    [Fact]
    public void Known_name_after_missing_name_applies_consistently_but_conflicting_names_do_not()
    {
        var result = Assert.Single(Apply([Entry(A,name:null),Entry(B,name:"Hybel Syd A/S")]).CustomerResults!);
        Assert.Equal("Hybel Syd A/S",result.CustomerName); Assert.Equal("HalvårBagud",result.ProposedSchedule);
        Assert.All(result.MeteringPoints,p => Assert.Equal("HalvårBagud",p.ProposedSchedule));
        var conflict = Apply([Entry(A,name:"Hybel Syd A/S"),Entry(B,name:"Ordinary business")]);
        Assert.Empty(conflict.NamedCustomerExceptionEvidence!);
        Assert.Null(Assert.Single(conflict.CustomerResults!).CustomerName);
        Assert.Contains(Assert.Single(conflict.CustomerResults!).Problems,p => p.Contains("Conflicting Xellent customer names"));
    }

    [Theory]
    [InlineData("Hybel Syd","Hybel Syd A/S")]
    [InlineData("Hybel Nord AS","  HYBEL\tNORD  afdeling 2 ")]
    [InlineData("Hybel Sjælland","Hybel Sjælland A/S")]
    [InlineData("Hybel Midt A/S","Hybel Midt")]
    public void Same_region_name_variants_apply_consistently_and_retain_each_source_spelling(string first,string second)
    {
        var document = Apply([Entry(A,"P1","T1",first),Entry(B,"P2","T2",second)]);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Equal(first,result.CustomerName);
        Assert.Equal("Partner",result.ProposedSegment); Assert.Equal("HalvårBagud",result.ProposedSchedule);
        Assert.All(result.MeteringPoints,p => Assert.Equal("HalvårBagud",p.ProposedSchedule));
        Assert.DoesNotContain(result.Problems,p => p.Contains("Conflicting Xellent customer names"));
        Assert.Equal(second,document.NamedCustomerExceptionEvidence!.Single(e => e.MeteringPointIdentification == B).CustomerName);
    }

    [Theory]
    [InlineData("Hybel Syd","Hybel Nord A/S")]
    [InlineData("Hybel Syd","Ordinary business")]
    [InlineData("Hybel Syd A/S","Hybel Sydvest")]
    public void Different_regions_or_unrelated_names_retain_conflict_safety(string first,string second)
    {
        var document = Apply([Entry(A,name:first),Entry(B,name:second)]);
        Assert.Empty(document.NamedCustomerExceptionEvidence!);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Null(result.CustomerName);
        Assert.Equal("KvartalAC",result.ProposedSchedule);
        Assert.Contains(result.Problems,p => p.Contains("Conflicting Xellent customer names"));
    }

    [Fact]
    public void Unknown_customers_are_separate_and_scope_is_exact()
    {
        var document = Apply([Entry(A),Entry(C,name:"Hybel Nord A/S")],coverage:[new(A,Cos,"513713",null,null,[]),new(B,Cos,null,null,null,["Missing customer"]),
            new("571313179104952733",Cos,null,null,null,["Missing customer"])]);
        Assert.Equal(3,document.CustomerResults!.Count);
        Assert.Equal("Ordinary business",document.CustomerResults[0].CustomerName);
        Assert.Equal("KvartalAC",document.CustomerResults[0].ProposedSchedule);
        Assert.All(document.CustomerResults!.Where(r => r.CustomerNumber is null),r => Assert.Single(r.MeteringPoints));
        Assert.DoesNotContain(document.CustomerResults!.SelectMany(r => r.MeteringPoints),p => p.MeteringPointIdentification == C);
    }

    [Fact]
    public void Current_exemption_date_warning_survives_point_projection()
    {
        var document = Apply([Entry(A)],coverage:[new(A,Cos,"513713",null,null,[SettlementExemptionProposal.HistoricalWarning])]);
        Assert.Contains(SettlementExemptionProposal.HistoricalWarning,Assert.Single(Assert.Single(document.CustomerResults!).MeteringPoints).Problems);
    }

    [Fact]
    public void Genuine_customer_source_block_keeps_individual_values_and_names_actual_selected_cause()
    {
        var document = Apply([Entry(A) with { ChargeReadStatus = "Ambiguous" },Entry(B)],chargeFailures:new HashSet<string> { "513713" });
        var result = Assert.Single(document.CustomerResults!);
        Assert.Null(result.ProposedSegment); Assert.Equal("Partner",result.MeteringPoints[1].ProposedSegment);
        Assert.Equal("KvartalAC",result.ProposedSchedule);
        Assert.Contains(result.Problems,p => p.Contains(A) && p.Contains("ambiguous historical charging"));
        Assert.DoesNotContain(result.Problems,p => p.Contains(B) && p.Contains("ambiguous historical charging"));
    }

    [Fact]
    public void Hybel_does_not_hide_failed_dated_type_or_charging_source()
    {
        var document = Apply([Entry(A,name:"Hybel Nord A/S") with { MeterPointTypeReadStatus = "ReadFailed",MeterPointTypeCode = null },
            Entry(B,name:"Hybel Nord A/S") with { ChargeReadStatus = "ReadFailed" }],chargeFailures:new HashSet<string> { "513713" });
        var result = Assert.Single(document.CustomerResults!);
        Assert.Null(result.ProposedSegment); Assert.Equal("HalvårBagud",result.ProposedSchedule);
        Assert.All(document.NamedCustomerExceptionEvidence!,e => Assert.False(e.SegmentApplied));
        Assert.Contains(result.Problems,p => p.Contains(A) && p.Contains("Could not read the Xellent metering-point type"));
        Assert.Contains(result.Problems,p => p.Contains(B) && p.Contains("read failed"));
    }

    [Fact]
    public void Mixed_E18_points_clear_customer_segment_and_final_audit_without_changing_point_values()
    {
        var document = Apply([Entry(A) with { MeterPointTypeCode = "PROD" },Entry(B)]);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Null(result.ProposedSegment);
        Assert.Equal(SettlementE18Proposal.Segment,result.MeteringPoints[0].ProposedSegment);
        Assert.Equal("Partner",result.MeteringPoints[1].ProposedSegment);
        Assert.Null(Assert.Single(document.E18Review!.Customers).FinalProposedSegment);
        Assert.False(Assert.Single(document.E18Review.Customers).Changed);
    }

    [Fact]
    public void Mixed_exemptions_compare_per_point_and_align_final_audit()
    {
        var owners = new Dictionary<string,SettlementCurrentCustomerEvidence> { [A] = new("513713","Read"),[B] = new("513713","Read") };
        var document = Apply([Entry(A),Entry(B)],
            [new(A,"513713","Read",[new(Guid.NewGuid(),"x",Guid.NewGuid(),100)],"Current",null),
                new(B,"513713","Read",[],"Current",null)],owners);
        var result = Assert.Single(document.CustomerResults!);
        Assert.Null(result.ProposedSegment);
        Assert.Equal("Erhverv_UG",result.MeteringPoints[0].ProposedSegment); Assert.Equal("Partner",result.MeteringPoints[1].ProposedSegment);
        Assert.Null(Assert.Single(document.ExemptionReview!.Customers).FinalProposedSegment);
        Assert.False(Assert.Single(document.ExemptionReview.Customers).Changed);
    }
}
