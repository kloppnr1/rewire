using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class CustomerSegmentGrouperTests
{
    /// <summary>Map whose entries apply whatever the customer carries — the plain-string config form.</summary>
    private static IReadOnlyDictionary<string, SegmentRule> Map(
        params (string Template, string[] Segments)[] entries)
        => entries.ToDictionary(e => e.Template, e => SegmentRule.Uniform(e.Segments), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, CustomerIdentityKind> Identities(
        params (string Customer, CustomerIdentityKind Identity)[] entries)
        => entries.ToDictionary(e => e.Customer, e => e.Identity, StringComparer.Ordinal);

    private static SettlementConfigEntry Entry(string mpId, string customer, string? template, string name = "Firma A")
        => new(mpId, "Måned.", customer, name, template);

    [Fact]
    public void Groups_customers_by_mapped_segment()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446971", "V_Privat"),
                Entry("571313179104952732", "447256", "V_SME"),
            ],
            Map(("V_Privat", ["Privat"]), ("V_SME", ["SME"])));

        Assert.Equal(2, result.Segments.Count);
        Assert.Empty(result.Conflicts);

        var privat = result.Segments.Single(s => s.Segment == "Privat");
        Assert.True(privat.Mapped);
        Assert.Equal(["446970", "446971"], privat.Customers.Select(c => c.CustomerNumber));
        Assert.Equal(2, privat.MeteringPointCount);
    }

    [Fact]
    public void Several_templates_may_map_to_the_same_segment()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "M_Erhverv"),
                Entry("571313179104952731", "446971", "V_Erhverv"),
            ],
            Map(("M_Erhverv", ["Erhverv"]), ("V_Erhverv", ["Erhverv"])));

        var group = Assert.Single(result.Segments);
        Assert.Equal("Erhverv", group.Segment);
        Assert.Equal(["M_Erhverv", "V_Erhverv"], group.ContractTemplates);
        Assert.Equal(2, group.CustomerCount);
    }

    [Fact]
    public void Unmapped_template_keeps_its_raw_name_and_is_reported()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446971", "V_Ukendt"),
            ],
            Map(("V_Privat", ["Privat"])));

        // Nothing is dropped just because the mapping is incomplete.
        var unmapped = result.Segments.Single(s => s.Segment == "V_Ukendt");
        Assert.False(unmapped.Mapped);
        Assert.Equal("446971", unmapped.Customers.Single().CustomerNumber);
        Assert.Equal(["V_Ukendt"], result.UnmappedContractTemplates);
    }

    /// <summary>Map keyed the way the loader keys it — space and underscore are one template.</summary>
    private static IReadOnlyDictionary<string, SegmentRule> NormalizedMap(
        params (string Template, string[] Segments)[] entries)
        => entries.ToDictionary(
            e => SettlementConfigKeys.NormalizeTemplate(e.Template),
            e => SegmentRule.Uniform(e.Segments),
            StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Template_spelled_with_a_space_finds_the_entry_written_with_an_underscore()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V Privat")],
            NormalizedMap(("V_Privat", ["Privat"])));

        Assert.Empty(result.UnmappedContractTemplates);
        var group = Assert.Single(result.Segments);
        Assert.Equal("Privat", group.Segment);
        Assert.True(group.Mapped);
    }

    [Fact]
    public void Template_spelled_with_an_underscore_finds_the_entry_written_with_a_space()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_Privat")],
            NormalizedMap(("V Privat", ["Privat"])));

        Assert.Empty(result.UnmappedContractTemplates);
        Assert.Equal("Privat", Assert.Single(result.Segments).Segment);
    }

    [Fact]
    public void Unmapped_template_is_reported_in_the_spelling_xellent_holds()
    {
        // Only the matching is normalised. The operator has to see the string Xellent actually has,
        // or she goes looking for the wrong thing in the file.
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V Ukendt")],
            NormalizedMap(("V_Privat", ["Privat"])));

        Assert.Equal(["V Ukendt"], result.UnmappedContractTemplates);
        Assert.Equal("V Ukendt", Assert.Single(result.Segments).Segment);
    }

    [Fact]
    public void Ambiguous_template_makes_the_customer_a_conflict_with_both_candidates()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_LadePriv")],
            Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"])));

        Assert.Empty(result.Segments);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(CustomerSegmentConflictReason.AmbiguousTemplate, conflict.Reason);

        // The MP ids are what makes the conflict actionable — that is where the charging
        // installation's contract is looked up.
        var candidate = Assert.Single(conflict.Candidates);
        Assert.Equal("V_LadePriv", candidate.ContractTemplate);
        Assert.Equal(["Privat_Opladning_Eje", "Privat_Opladning_Leje"], candidate.Segments);
        Assert.Equal(["571313179104952730"], candidate.MeteringPointIdentifications);
    }

    [Fact]
    public void Customer_with_metering_points_in_two_segments_is_a_conflict_not_a_member_of_both()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446970", "V_SME"),
                Entry("571313179104952732", "446971", "V_Privat"),
            ],
            Map(("V_Privat", ["Privat"]), ("V_SME", ["SME"])));

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("446970", conflict.CustomerNumber);
        Assert.Equal(CustomerSegmentConflictReason.MultipleSegments, conflict.Reason);
        Assert.Equal(["V_Privat", "V_SME"], conflict.Candidates.Select(c => c.ContractTemplate));

        // The conflicting customer is absent from the groups entirely — the clean one is not.
        var privat = Assert.Single(result.Segments);
        Assert.Equal("446971", privat.Customers.Single().CustomerNumber);
    }

    [Fact]
    public void Divergent_segments_land_in_the_configured_answer_instead_of_a_conflict()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446970", "V_SME"),
            ],
            Map(("V_Privat", ["Privat"]), ("V_SME", ["SME"])),
            multipleSegments: "Erhverv");

        Assert.Empty(result.Conflicts);
        var group = Assert.Single(result.Segments);
        Assert.Equal("Erhverv", group.Segment);
        Assert.Equal(2, group.Customers.Single().MeteringPointCount);
        // Countable, so the run can say how many customers the file placed rather than the data.
        Assert.Equal(["446970"], result.MultipleSegmentCustomers);
    }

    [Theory]
    [InlineData("339097", "LadeAbo_Leje_Variabel", "Privat_Opladning_Leje")]
    [InlineData("445400", "Gl.LadeChargeLejeMel(Gl.ClejMEL)", "Privat_Opladning_Leje")]
    [InlineData("108106", "LadeAbo_Eje_Variabel", "Privat_Opladning_Eje")]
    public void Xellent_ownership_overrules_any_Settl_charger_contract(
        string customer, string contract, string expected)
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("mp1", customer, "V_LadePriv") with { SettlContracts = [contract] }],
            Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"])),
            Identities((customer, CustomerIdentityKind.Cpr)),
            multipleSegments: "Erhverv",
            segmentByCustomerFromXellentCharge: new Dictionary<string,string> { [customer] = expected });

        Assert.Equal(expected, Assert.Single(result.Segments).Segment);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Private_customer_with_charging_and_ordinary_metering_points_stays_in_charging_segment()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("charge", "123966", "V_LadePriv") with { SettlContracts = ["LadeAbo_Eje_Variabel"] },
                Entry("ordinary", "123966", "V_Privat")
            ],
            Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"]),
                ("V_Privat", ["Privat"])),
            Identities(("123966", CustomerIdentityKind.Cpr)),
            multipleSegments: "Erhverv",
            segmentByCustomerFromXellentCharge: new Dictionary<string,string> { ["123966"] = "Privat_Opladning_Eje" });

        var group = Assert.Single(result.Segments);
        Assert.Equal("Privat_Opladning_Eje", group.Segment);
        Assert.Equal(2, group.Customers.Single().MeteringPointCount);
        Assert.Empty(result.MultipleSegmentCustomers);
    }

    [Fact]
    public void Charger_contract_on_an_ordinary_template_still_places_the_whole_private_customer()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("charge", "124278", "V_Privat") with { SettlContracts = ["LadeAbo_Eje_VariabelUA"] },
                Entry("ordinary", "124278", "V_Privat")
            ],
            Map(("V_Privat", ["Privat"])),
            Identities(("124278", CustomerIdentityKind.Cpr)),
            multipleSegments: "Erhverv",
            segmentByCustomerFromXellentCharge: new Dictionary<string,string> { ["124278"] = "Privat_Opladning_Eje" });

        Assert.Equal("Privat_Opladning_Eje", Assert.Single(result.Segments).Segment);
    }

    [Fact]
    public void Opposing_charger_contracts_remain_a_conflict()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("owned", "108283", "V_LadePriv") with { SettlContracts = ["LadeAbo_Eje_Variabel"] },
                Entry("rented", "108283", "V_LadePriv") with { SettlContracts = ["LadeAbo_Leje_Variabel"] }
            ],
            Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"])),
            Identities(("108283", CustomerIdentityKind.Cpr)),
            multipleSegments: "Erhverv");

        Assert.Empty(result.Segments);
        Assert.Equal("108283", Assert.Single(result.Conflicts).CustomerNumber);
    }

    [Fact]
    public void Dated_Xellent_subscription_resolves_a_charging_template_without_a_Settl_contract()
    {
        var entries = new[]
        {
            Entry("charge", "102403", "V_LadePriv"),
            Entry("ordinary", "102403", "V_Privat")
        };
        var map = Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"]),
            ("V_Privat", ["Privat"]));
        var identities = Identities(("102403", CustomerIdentityKind.Cpr));

        Assert.Single(CustomerSegmentGrouper.Build(entries, map, identities).Conflicts);

        var result = CustomerSegmentGrouper.Build(
            entries, map, identities,
            segmentByCustomerFromXellentCharge: new Dictionary<string, string>
            {
                ["102403"] = "Privat_Opladning_Eje"
            });

        Assert.Equal("Privat_Opladning_Eje", Assert.Single(result.Segments).Segment);
        Assert.Equal(2, result.Segments.Single().Customers.Single().MeteringPointCount);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Private_customer_with_disagreeing_non_charging_templates_stays_private()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("mp1", "108363", "V_Privat"), Entry("mp2", "108363", "V_SME")],
            Map(("V_Privat", ["Privat"]), ("V_SME", ["SME"])),
            Identities(("108363", CustomerIdentityKind.Cpr)),
            multipleSegments: "Erhverv");

        Assert.Equal("Privat", Assert.Single(result.Segments).Segment);
    }

    [Fact]
    public void Company_with_disagreeing_templates_keeps_business_answer()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("mp1", "200806", "V_Privat"), Entry("mp2", "200806", "V_SME")],
            Map(("V_Privat", ["Privat"]), ("V_SME", ["SME"])),
            Identities(("200806", CustomerIdentityKind.Cvr)),
            multipleSegments: "Erhverv");

        Assert.Equal("Erhverv", Assert.Single(result.Segments).Segment);
    }

    [Fact]
    public void Configured_answer_does_not_rescue_an_unmapped_template()
    {
        // A template left out of the map on purpose keeps the customer visible as unmapped. Letting
        // the disagreement rule place them would resolve exactly the case we chose not to answer.
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_LadePriv"),
                Entry("571313179104952731", "446970", "V_Erhverv"),
            ],
            Map(("V_Erhverv", ["Erhverv"])),
            multipleSegments: "Erhverv");

        Assert.Empty(result.Segments);
        Assert.Equal(CustomerSegmentConflictReason.MultipleSegments, Assert.Single(result.Conflicts).Reason);
        Assert.Equal(["V_LadePriv"], result.UnmappedContractTemplates);
    }

    [Fact]
    public void Configured_answer_does_not_rescue_an_ambiguous_template()
    {
        // The template itself offers two candidates, so there is no determined segment to overrule —
        // that stays a conflict whatever the file says about disagreement.
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_LadePriv")],
            Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"])),
            multipleSegments: "Erhverv");

        Assert.Equal(CustomerSegmentConflictReason.AmbiguousTemplate, Assert.Single(result.Conflicts).Reason);
        Assert.Empty(result.MultipleSegmentCustomers);
    }

    [Fact]
    public void Ambiguous_plus_divergent_is_reported_as_both()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_LadePriv"),
                Entry("571313179104952731", "446970", "V_Privat"),
                Entry("571313179104952732", "446970", "V_SME"),
            ],
            Map(("V_LadePriv", ["Privat_Opladning_Eje", "Privat_Opladning_Leje"]),
                ("V_Privat", ["Privat"]),
                ("V_SME", ["SME"])));

        Assert.Equal(CustomerSegmentConflictReason.Both, Assert.Single(result.Conflicts).Reason);
    }

    [Fact]
    public void Metering_point_without_a_template_is_reported_and_does_not_decide_the_segment()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446970", null),
            ],
            Map(("V_Privat", ["Privat"])));

        var group = Assert.Single(result.Segments);
        Assert.Equal("Privat", group.Segment);
        // Only the MP that actually carries a template counts towards the segment.
        Assert.Equal(1, group.Customers.Single().MeteringPointCount);

        var unresolved = Assert.Single(result.Unresolved);
        Assert.Equal("446970", unresolved.CustomerNumber);
        Assert.Equal(["571313179104952731"], unresolved.MeteringPointIdentifications);
    }

    [Fact]
    public void Customer_appears_once_in_the_unresolved_list_however_many_metering_points_she_has()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446970", null),
                Entry("571313179104952732", "446970", null),
            ],
            Map(("V_Privat", ["Privat"])));

        var unresolved = Assert.Single(result.Unresolved);
        Assert.Equal(["571313179104952731", "571313179104952732"], unresolved.MeteringPointIdentifications);
    }

    [Fact]
    public void Customer_whose_metering_points_all_lack_a_template_is_neither_grouped_nor_a_conflict()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", null)],
            Map(("V_Privat", ["Privat"])));

        Assert.Empty(result.Segments);
        Assert.Empty(result.Conflicts);
        Assert.Single(result.Unresolved);
        Assert.Empty(result.SettlRuleCustomers);
    }

    [Fact]
    public void Settl_rule_never_fills_a_missing_Xellent_template()
    {
        var result = CustomerSegmentGrouper.Build([Entry("mp1", "599999", null)],
            Map(("V_Erhverv", ["Erhverv"])),
            segmentByCustomerFromSettlRule: new Dictionary<string,string> { ["599999"] = "Erhverv" });
        Assert.Empty(result.Segments);
        Assert.Empty(result.SettlRuleCustomers);
        Assert.Single(result.Unresolved);
    }

    [Fact]
    public void Customer_with_no_template_and_no_Settl_answer_stays_unresolved_once()
    {
        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "600058", null),
                Entry("571313179104952731", "600058", null),
            ],
            Map(("V_Privat", ["Privat"])),
            segmentByCustomerFromSettlRule: new Dictionary<string, string> { ["600016"] = "Erhverv" });

        Assert.Empty(result.Segments);
        Assert.Empty(result.SettlRuleCustomers);
        var unresolved = Assert.Single(result.Unresolved);
        Assert.Equal("600058", unresolved.CustomerNumber);
        Assert.Equal(2, unresolved.MeteringPointIdentifications.Count);
    }

    [Fact]
    public void The_Settl_rule_does_not_second_guess_a_customer_her_own_template_already_places()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_Privat")],
            Map(("V_Privat", ["Privat"])),
            segmentByCustomerFromSettlRule: new Dictionary<string, string> { ["446970"] = "Erhverv" });

        Assert.Equal("Privat", Assert.Single(result.Segments).Segment);
        Assert.Empty(result.SettlRuleCustomers);
    }

    [Fact]
    public void Same_template_yields_different_segments_for_a_company_and_a_person()
    {
        // The rule Janni asked for: Settl will not take a private segment on a CVR number, and
        // Xellent has both kinds of customer on V_Privat.
        var map = new Dictionary<string, SegmentRule>(StringComparer.Ordinal)
        {
            ["V_Privat"] = new(Cvr: ["Erhverv"], Cpr: ["Privat"], Blank: ["Privat"])
        };

        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_Privat"),
                Entry("571313179104952731", "446971", "V_Privat"),
                Entry("571313179104952732", "446972", "V_Privat"),
            ],
            map,
            Identities(
                ("446970", CustomerIdentityKind.Cvr),
                ("446971", CustomerIdentityKind.Cpr)));

        Assert.Empty(result.Conflicts);
        Assert.Equal("446970", result.Segments.Single(s => s.Segment == "Erhverv").Customers.Single().CustomerNumber);
        // The CPR customer and the one with no identifier at all share the private segment.
        Assert.Equal(["446971", "446972"],
            result.Segments.Single(s => s.Segment == "Privat").Customers.Select(c => c.CustomerNumber));
    }

    [Fact]
    public void Identification_can_resolve_a_template_that_is_ambiguous_for_a_person()
    {
        var map = new Dictionary<string, SegmentRule>(StringComparer.Ordinal)
        {
            ["V_LadePriv"] = new(
                Cvr: ["Erhverv"],
                Cpr: ["Privat_Opladning_Eje", "Privat_Opladning_Leje"],
                Blank: ["Privat_Opladning_Eje", "Privat_Opladning_Leje"])
        };

        var result = CustomerSegmentGrouper.Build(
            [
                Entry("571313179104952730", "446970", "V_LadePriv"),
                Entry("571313179104952731", "446971", "V_LadePriv"),
            ],
            map,
            Identities(("446970", CustomerIdentityKind.Cvr), ("446971", CustomerIdentityKind.Cpr)));

        Assert.Equal("446970", Assert.Single(result.Segments).Customers.Single().CustomerNumber);
        // Eje vs. Leje is still undecidable for the private customer — and the conflict carries the
        // identification, so the manual decision starts from the right column.
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("446971", conflict.CustomerNumber);
        Assert.Equal(CustomerIdentityKind.Cpr, conflict.Identity);
    }

    [Fact]
    public void Template_that_says_nothing_about_this_identification_is_unmapped_not_dropped()
    {
        var map = new Dictionary<string, SegmentRule>(StringComparer.Ordinal)
        {
            ["V_Privat"] = new(Cvr: ["Erhverv"], Cpr: [], Blank: [])
        };

        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_Privat")],
            map,
            Identities(("446970", CustomerIdentityKind.Cpr)));

        var group = Assert.Single(result.Segments);
        Assert.Equal("V_Privat", group.Segment);
        Assert.False(group.Mapped);
        Assert.Equal(["V_Privat"], result.UnmappedContractTemplates);
    }

    [Fact]
    public void Customer_missing_from_the_identity_map_is_treated_as_blank()
    {
        var map = new Dictionary<string, SegmentRule>(StringComparer.Ordinal)
        {
            ["V_Privat"] = new(Cvr: ["Erhverv"], Cpr: ["Privat"], Blank: ["Ukendt"])
        };

        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_Privat")],
            map,
            identityByCustomer: null);

        Assert.Equal("Ukendt", Assert.Single(result.Segments).Segment);
    }

    [Fact]
    public void No_map_falls_back_to_raw_template_names()
    {
        var result = CustomerSegmentGrouper.Build(
            [Entry("571313179104952730", "446970", "V_Privat")],
            segmentMap: null);

        var group = Assert.Single(result.Segments);
        Assert.Equal("V_Privat", group.Segment);
        Assert.False(group.Mapped);
        Assert.Equal(["V_Privat"], result.UnmappedContractTemplates);
    }
}
