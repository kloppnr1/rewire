using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SettlementSourceProposalTests
{
    private static IReadOnlyDictionary<string, SegmentRule> Map => new Dictionary<string, SegmentRule>
    {
        ["V_LadePriv"] = new(["Erhverv"], ["Privat_Opladning_Eje", "Privat_Opladning_Leje"], ["Privat_Opladning_Eje", "Privat_Opladning_Leje"]),
        ["V_Privat"] = new(["Erhverv"], ["Privat"], ["Privat"])
    };

    [Theory]
    [InlineData("Privat_Opladning_Eje")]
    [InlineData("Privat_Opladning_Leje")]
    [InlineData("Privat")]
    public void Settl_contract_variations_do_not_change_dated_Xellent_proposal(string answer)
    {
        foreach (var contract in new[] { "LadeAbo_Eje_Variabel", "LadeAbo_Leje_Variabel", "" })
        {
            var result = CustomerSegmentGrouper.Build(
                [new("mp", "Måned.", "339097", null, "V_LadePriv", SettlContracts: [contract]),
                 new("ordinary", "Måned.", "339097", null, "V_Privat")], Map,
                segmentByCustomerFromXellentCharge: new Dictionary<string, string> { ["339097"] = answer });
            Assert.Equal(answer, Assert.Single(result.Segments).Segment);
            Assert.Empty(result.Conflicts);
            Assert.Equal(2, result.Segments[0].MeteringPointCount);
        }
    }

    [Fact]
    public void Failed_read_is_not_Privat_and_Settt_contract_cannot_rescue_it()
    {
        var result = CustomerSegmentGrouper.Build([new("mp", "Måned.", "106295", null, "V_LadePriv",
            SettlContracts: ["LadeAbo_Eje_Variabel"])], Map,
            chargeReadFailedCustomers: new HashSet<string> { "106295" });
        Assert.Empty(result.Segments);
        Assert.Single(result.Unresolved);
    }

    [Fact]
    public void Charging_answer_cannot_hide_unrelated_missing_template()
    {
        var result = CustomerSegmentGrouper.Build([new("mp", "Måned.", "106295", null, "unknown")], Map,
            segmentByCustomerFromXellentCharge: new Dictionary<string, string> { ["106295"] = "Privat" });
        Assert.DoesNotContain(result.Segments, g => g.Mapped);
        Assert.Contains("unknown", result.UnmappedContractTemplates);
    }

    [Fact]
    public void Cvr_keeps_business_mapping_despite_private_charging_products()
    {
        var result = CustomerSegmentGrouper.Build([new("mp", "Måned.", "123", null, "V_LadePriv")], Map,
            new Dictionary<string, CustomerIdentityKind> { ["123"] = CustomerIdentityKind.Cvr },
            segmentByCustomerFromXellentCharge: new Dictionary<string, string> { ["123"] = "Privat_Opladning_Eje" });
        Assert.Equal("Erhverv", Assert.Single(result.Segments).Segment);
    }

    [Theory]
    [InlineData("{}", "NotRead")]
    [InlineData("{\"customerSegmentId\":null}", "NotAssigned")]
    [InlineData("{\"customerSegmentId\":\"bad-id\"}", "Unmapped")]
    [InlineData("broken", "ReadFailed")]
    public void Missing_assignment_and_unreadable_response_are_distinct(string json, string expected) =>
        Assert.Equal(expected, SettlementConfigReview.ReadCurrentSegment(json, new Dictionary<Guid,string?>()).Status);

    [Fact]
    public void Segment_catalog_resolves_actual_id_and_failed_catalog_retains_id()
    {
        var id = Guid.Parse("b892b7cb-63ec-4a3c-8e3d-ed5d9da21926");
        var json = "{\"CustomerSegmentId\":\"" + id + "\"}";
        var resolved = SettlementConfigReview.ReadCurrentSegment(json,
            new Dictionary<Guid,string?> { [id] = "Privat_Opladning_Eje" });
        Assert.Equal("Read",resolved.Status);
        Assert.Equal("Privat_Opladning_Eje",resolved.Segment);
        Assert.Equal(id,resolved.SegmentId);
        var unavailable = SettlementConfigReview.ReadCurrentSegment(json,null);
        Assert.Equal("ReadFailed",unavailable.Status);
        Assert.Equal(id,unavailable.SegmentId);
        Assert.Null(unavailable.Segment);
    }

    [Fact]
    public void Review_keeps_primary_proposal_and_dated_basis_without_comparison_flags()
    {
        SettlementConfigEntry[] entries = [new("mp", "month", "339097", null, "V_LadePriv",
            SettlContracts: ["LadeAbo_Eje_Variabel"], XellentAccount: "339097", ReferenceDate: new(2026,8,31),
            ChargeProducts: ["OAboLejMEL"], ChargeReadStatus: "Read")];
        var doc = SettlementScheduleGrouper.Build(entries, [], new Dictionary<string,string> { ["month"] = "Måned." },
            1,"test","hol",DateTime.UtcNow) with
        {
            Segments = [new("Privat_Opladning_Leje",true,["V_LadePriv"],1,1,[new("339097",null,1)])]
        };
        var row = Assert.Single(SettlementConfigReview.Build(doc, entries,
            new Dictionary<string,IReadOnlyList<string>> { ["339097"] = ["quarter"] },
            new Dictionary<string,string> { ["quarter"] = "KvartalAC" },
            new Dictionary<string,SettlementConfigCurrentSegment> { ["339097"] = new("Privat_Opladning_Eje","Read") },true));
        Assert.Equal("Privat_Opladning_Leje", row.ProposedSegment);
        Assert.Equal(doc.Schedules[0].Schedule,row.ProposedSchedule);
        Assert.Null(row.SegmentDiffers);
        Assert.Null(row.ScheduleDiffers);
        Assert.Null(row.ChargingOwnershipDiffers);
        Assert.Null(row.SettlChargingSegments);
        Assert.Equal(new DateOnly(2026,8,31),Assert.Single(row.Xellent).ReferenceDate);
        Assert.Empty(row.UnresolvedReasons);
    }
}
