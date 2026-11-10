using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

/// <summary>
/// The candidate-rule comparison exists to decide nothing: every rule keeps its own field, and a
/// rule that cannot answer says so. These tests pin exactly that — the failure mode worth guarding
/// against is one source quietly filling another's gap, which would make two rules look like they
/// agree when only one of them ever spoke.
/// </summary>
public class SettlementConfigComparisonTests
{
    private static SettlementConfig Config(
        IReadOnlyDictionary<string, string>? schedules = null,
        IReadOnlyDictionary<string, string>? groupSchedules = null,
        SettlSegmentRules? settlSegments = null,
        IReadOnlyDictionary<string, IReadOnlyList<TemplateProductPattern>>? templateProducts = null) =>
        new(
            schedules ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SegmentRule>(StringComparer.OrdinalIgnoreCase),
            groupSchedules,
            settlSegments,
            templateProducts);

    private static SettlSegmentRules SettlRules() =>
        new(["E_SME_Spot_Lb+1d", "E_SME_Pulje_Lb+1d"], "SME", "Erhverv", "Erhverv_UG");

    private static SettlementConfigEntry Entry(
        string mpId = "571000000000000001",
        string customer = "100001",
        string billingPlan = "Kvartal",
        string? template = "V_Privat",
        IReadOnlyList<string>? products = null,
        IReadOnlyList<string>? settlContracts = null,
        bool? hasExemption = null,
        bool customerInXellent = true) =>
        new(mpId, billingPlan, customer, "Kunde A", template, products, settlContracts, hasExemption, customerInXellent);

    // ---- SettlementGroupScheduleResolver ----

    [Fact]
    public void Group_schedule_comes_from_the_group_the_customer_is_actually_in()
    {
        var result = SettlementGroupScheduleResolver.Resolve(
            ["Kvartalsvis Aconto"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Kvartalsvis Aconto"] = "KvartalAC" });

        Assert.Equal("KvartalAC", result.Schedule);
        Assert.Null(result.Note);
    }

    [Fact]
    public void Two_groups_naming_the_same_schedule_is_not_a_conflict()
    {
        // Ambiguous membership is not ambiguous schedule — the answer is still unique.
        var result = SettlementGroupScheduleResolver.Resolve(
            ["Månedlige opgørelser", "PPA – afregning"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Månedlige opgørelser"] = "Måned.",
                ["PPA – afregning"] = "Måned."
            });

        Assert.Equal("Måned.", result.Schedule);
    }

    [Fact]
    public void Two_groups_naming_different_schedules_yields_no_schedule_and_says_why()
    {
        var result = SettlementGroupScheduleResolver.Resolve(
            ["Kvartalsvis Aconto", "1/2 år bagud"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Kvartalsvis Aconto"] = "KvartalAC",
                ["1/2 år bagud"] = "HalvårBagud"
            });

        Assert.Null(result.Schedule);
        Assert.Contains("several schedules", result.Note);
    }

    [Fact]
    public void No_group_membership_is_reported_rather_than_left_blank()
    {
        var result = SettlementGroupScheduleResolver.Resolve(
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X"] = "Y" });

        Assert.Null(result.Schedule);
        Assert.Contains("no settlement group", result.Note);
    }

    // ---- SettlDrivenSegmentResolver ----

    [Fact]
    public void Sme_contract_wins_over_exemption()
    {
        // An SME customer that also has an exemption is still SME; the reverse order would silently
        // reclassify them as Erhverv_UG.
        var result = SettlDrivenSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, ["E_SME_Spot_Lb+1d"], hasExemption: true, SettlRules());

        Assert.Equal("SME", result.Segment);
        Assert.True(result.Sme);
    }

    [Theory]
    [InlineData(true, "Erhverv_UG")]
    [InlineData(false, "Erhverv")]
    public void Cvr_without_sme_splits_on_the_exemption(bool hasExemption, string expected)
    {
        var result = SettlDrivenSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, ["E_ErhKvt"], hasExemption, SettlRules());

        Assert.Equal(expected, result.Segment);
        Assert.False(result.Sme);
    }

    [Fact]
    public void Unread_exemptions_leave_the_segment_undecided_instead_of_defaulting_to_Erhverv()
    {
        // The whole point: not reading is not the same as reading "no exemption". Defaulting here
        // would put customers in a segment the data never supported.
        var result = SettlDrivenSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, ["E_ErhKvt"], hasExemption: null, SettlRules());

        Assert.Null(result.Segment);
        Assert.Contains("--with-exemptions", result.Note);
    }

    [Fact]
    public void Without_cvr_the_settl_rule_declines_but_keeps_the_sme_evidence()
    {
        var result = SettlDrivenSegmentResolver.Resolve(
            CustomerIdentityKind.Cpr, ["E_SME_Spot_Lb+1d"], hasExemption: false, SettlRules());

        Assert.Null(result.Segment);
        Assert.True(result.Sme);
    }

    // ---- TemplateProductSegmentResolver ----

    /// <summary>
    /// Keyed through <see cref="SettlementConfigKeys.NormalizeTemplate"/> exactly as the loader does,
    /// so the fixture cannot pass on a key shape the real config never produces.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<TemplateProductPattern>> TemplateProducts() =>
        new Dictionary<string, IReadOnlyList<TemplateProductPattern>>(StringComparer.OrdinalIgnoreCase)
        {
            [SettlementConfigKeys.NormalizeTemplate("V Privat")] =
            [
                new TemplateProductPattern("V ErhKvt", "Erhverv_Uden_Kontrakt", "E_ErhKvt"),
                new TemplateProductPattern("V P Flex", "Fordel", "V P Flex"),
                new TemplateProductPattern("Spot*", "SME", null)
            ],
            [SettlementConfigKeys.NormalizeTemplate("E. ErhProd")] =
                [new TemplateProductPattern("*", "Erhverv", "V ProdErhv")]
        };

    [Fact]
    public void Template_written_with_a_space_matches_the_underscore_spelling_from_xellent()
    {
        // The source table writes "V Privat", Xellent stores "V_Privat" — the same template.
        var result = TemplateProductSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr,
            [Entry(template: "V_Privat", products: ["V ErhKvt"])],
            TemplateProducts());

        Assert.Equal("Erhverv_Uden_Kontrakt", result.Segment);
        Assert.Equal(["E_ErhKvt"], result.ExpectedContracts);
    }

    [Fact]
    public void Prefix_and_catch_all_patterns_match()
    {
        var spot = TemplateProductSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, [Entry(template: "V Privat", products: ["SpotH Lb+1d"])], TemplateProducts());
        var any = TemplateProductSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, [Entry(template: "E. ErhProd", products: ["hvad som helst"])], TemplateProducts());

        Assert.Equal("SME", spot.Segment);
        // "samme migreringsregel som erhverv" carries no contract of its own.
        Assert.Empty(spot.ExpectedContracts);
        Assert.Equal("Erhverv", any.Segment);
        Assert.Equal(["V ProdErhv"], any.ExpectedContracts);
    }

    [Fact]
    public void Template_and_product_are_paired_per_metering_point_not_pooled_per_customer()
    {
        // The two metering points must carry DIFFERENT combinations, or a pooling implementation
        // would produce the same answer and this test could not tell the two apart.
        //
        // MP1 = V_Privat + "V P Flex" → Fordel.  MP2 = E. ErhProd + "V ErhKvt" → Erhverv (catch-all).
        // Neither real combination yields Erhverv_Uden_Kontrakt — but pooling would cross V_Privat
        // with "V ErhKvt", a pair that exists on no metering point, and produce exactly that.
        var result = TemplateProductSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr,
            [
                Entry(mpId: "1", template: "V_Privat", products: ["V P Flex"]),
                Entry(mpId: "2", template: "E. ErhProd", products: ["V ErhKvt"])
            ],
            TemplateProducts());

        // Two real combinations → two segments → conflict, not a pick.
        Assert.Null(result.Segment);
        Assert.Contains("Fordel", result.Note);
        // The invented third segment must be absent — that is the actual assertion.
        Assert.DoesNotContain("Erhverv_Uden_Kontrakt", result.Note);
    }

    [Fact]
    public void Several_segments_across_metering_points_is_a_conflict_not_a_pick()
    {
        var result = TemplateProductSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr,
            [
                Entry(mpId: "1", template: "V_Privat", products: ["V ErhKvt"]),
                Entry(mpId: "2", template: "V_Privat", products: ["V P Flex"])
            ],
            TemplateProducts());

        Assert.Null(result.Segment);
        Assert.Contains("several segments", result.Note);
    }

    [Fact]
    public void Unmapped_combination_is_reported_rather_than_dropped()
    {
        var result = TemplateProductSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr,
            [Entry(template: "V_Ukendt", products: ["V ErhKvt"])],
            TemplateProducts());

        Assert.Null(result.Segment);
        Assert.Contains("V_Ukendt", result.Note);
    }

    // ---- The comparison itself ----

    [Fact]
    public void One_source_alone_is_undecided_because_silence_is_not_consensus()
    {
        // Only the billing plan names a schedule; the customer is in no settlement group. Counting
        // that as agreement would inflate the confidence the summary is read with.
        var (summary, rows) = SettlementConfigComparison.Build(
            [Entry(billingPlan: "Kvartal")],
            null,
            null,
            null,
            null,
            Config(schedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Kvartal"] = "KvartalOpg" }));

        Assert.Equal("KvartalOpg", rows[0].ScheduleFromBillingPlan);
        Assert.Null(rows[0].ScheduleFromGroup);
        Assert.Equal(SettlementConfigAgreement.Undecided, rows[0].ScheduleAgreement);
        Assert.Equal(1, summary.ScheduleUndecided);
        Assert.Equal(0, summary.ScheduleAgree);
    }

    [Fact]
    public void Disagreeing_schedule_sources_are_both_kept_and_counted()
    {
        var (summary, rows) = SettlementConfigComparison.Build(
            [Entry(customer: "100001", billingPlan: "Kvartal")],
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["100001"] = ["Månedlige opgørelser"]
            },
            null,
            ["Månedlige opgørelser"],
            Config(
                schedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Kvartal"] = "KvartalOpg" },
                groupSchedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Månedlige opgørelser"] = "Måned." }));

        // Neither source overwrites the other — the row is the evidence, not a verdict.
        Assert.Equal("Måned.", rows[0].ScheduleFromGroup);
        Assert.Equal("KvartalOpg", rows[0].ScheduleFromBillingPlan);
        Assert.Equal(SettlementConfigAgreement.Disagree, rows[0].ScheduleAgreement);
        Assert.Equal(1, summary.ScheduleDisagree);
        Assert.Equal(1, summary.CustomersInSettlementGroup);
    }

    [Fact]
    public void Agreeing_segment_sources_are_counted_as_agreement()
    {
        var (summary, rows) = SettlementConfigComparison.Build(
            [Entry(customer: "100001", template: "V_Privat", products: ["V ErhKvt"], settlContracts: ["E_ErhKvt"], hasExemption: true)],
            new Dictionary<string, CustomerIdentityKind>(StringComparer.Ordinal) { ["100001"] = CustomerIdentityKind.Cvr },
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["100001"] = "Erhverv_UG" },
            null,
            Config(settlSegments: SettlRules()));

        Assert.Equal("Erhverv_UG", rows[0].SegmentFromTemplate);
        Assert.Equal("Erhverv_UG", rows[0].SegmentFromSettl);
        Assert.Equal(SettlementConfigAgreement.Agree, rows[0].SegmentAgreement);
        Assert.Equal(1, summary.SegmentAgree);
        Assert.Equal(1, summary.CustomersWithExemption);
    }

    [Fact]
    public void One_exempt_metering_point_makes_the_customer_exempt()
    {
        // The Settl rule is any-of: a customer with one exempt MP qualifies even if the others are
        // plain.
        var (_, rows) = SettlementConfigComparison.Build(
            [
                Entry(mpId: "1", customer: "100001", settlContracts: ["E_ErhKvt"], hasExemption: false),
                Entry(mpId: "2", customer: "100001", settlContracts: ["E_ErhKvt"], hasExemption: true)
            ],
            new Dictionary<string, CustomerIdentityKind>(StringComparer.Ordinal) { ["100001"] = CustomerIdentityKind.Cvr },
            null, null, null,
            Config(settlSegments: SettlRules()));

        Assert.True(rows[0].HasExemption);
        Assert.Equal("Erhverv_UG", rows[0].SegmentFromSettl);
    }

    [Fact]
    public void A_partially_read_customer_stays_unknown_rather_than_reported_as_not_exempt()
    {
        var (_, rows) = SettlementConfigComparison.Build(
            [
                Entry(mpId: "1", customer: "100001", settlContracts: ["E_ErhKvt"], hasExemption: false),
                Entry(mpId: "2", customer: "100001", settlContracts: ["E_ErhKvt"], hasExemption: null)
            ],
            new Dictionary<string, CustomerIdentityKind>(StringComparer.Ordinal) { ["100001"] = CustomerIdentityKind.Cvr },
            null, null, null,
            Config(settlSegments: SettlRules()));

        Assert.Null(rows[0].HasExemption);
        Assert.Null(rows[0].SegmentFromSettl);
    }

    // ---- "Ikke læst" må aldrig ligne "ikke til stede" ----

    [Fact]
    public void Unread_settl_contracts_leave_the_settl_segment_null_instead_of_concluding_Erhverv()
    {
        // Fejler kontraktopslaget, ville en tom liste betyde "på ingen SME-kontrakt", og hver eneste
        // SME-kunde ville blive skrevet som et selvsikkert Erhverv — talt med som evidens.
        var result = SettlDrivenSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, settlContracts: null, hasExemption: false, SettlRules());

        Assert.Null(result.Segment);
        Assert.False(result.Sme);
        Assert.Contains("not read", result.Note);
    }

    [Fact]
    public void Read_but_empty_contract_list_still_decides()
    {
        // Modstykket: "vi spurgte, kunden er på ingen kontrakt" er et rigtigt svar.
        var result = SettlDrivenSegmentResolver.Resolve(
            CustomerIdentityKind.Cvr, settlContracts: [], hasExemption: false, SettlRules());

        Assert.Equal("Erhverv", result.Segment);
    }

    [Fact]
    public void Unread_group_membership_does_not_claim_the_customer_is_in_no_group()
    {
        var notRead = SettlementGroupScheduleResolver.Resolve(
            null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X"] = "Y" });
        var readAndEmpty = SettlementGroupScheduleResolver.Resolve(
            [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X"] = "Y" });

        Assert.Contains("not read", notRead.Note);
        // De to må ikke sige det samme — den ene er en påstand om Settl, den anden er en undskyldning.
        Assert.Contains("no settlement group", readAndEmpty.Note);
        Assert.NotEqual(notRead.Note, readAndEmpty.Note);
    }

    [Fact]
    public void Comparison_reports_which_inputs_were_never_read()
    {
        var (summary, rows) = SettlementConfigComparison.Build(
            [Entry(customer: "100001", settlContracts: ["E_SME_Spot_Lb+1d"])],
            new Dictionary<string, CustomerIdentityKind>(StringComparer.Ordinal) { ["100001"] = CustomerIdentityKind.Cvr },
            settlementGroupsByCustomer: null,
            null,
            settlementGroupNamesInSettl: null,
            Config(settlSegments: SettlRules(), groupSchedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Findes ikke"] = "Måned." }),
            settlContractsRead: false);

        Assert.False(summary.SettlementGroupMembershipRead);
        Assert.False(summary.SettlContractsRead);
        // Ikke en tom liste: en tom liste ville påstå at hvert gruppenavn matcher en rigtig gruppe.
        Assert.Null(summary.ScheduleNamesNotInSettl);
        Assert.Null(rows[0].SettlementGroups);
        Assert.Null(rows[0].SettlContracts);
        // SME-kontrakten ligger i entryen, men blev aldrig læst — så den må ikke tælle.
        Assert.Null(rows[0].SegmentFromSettl);
        Assert.False(rows[0].Sme);
    }

    [Fact]
    public void Map_entries_naming_a_group_settl_does_not_have_are_reported()
    {
        var (summary, _) = SettlementConfigComparison.Build(
            [Entry(customer: "100001")],
            null,
            null,
            null,
            ["Månedlige opgørelser"],
            Config(groupSchedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Månedlige opgørelser"] = "Måned.",
                ["PPA – afregning"] = "Måned."
            }));

        Assert.Equal(["PPA – afregning"], summary.ScheduleNamesNotInSettl);
    }

    [Fact]
    public void Settl_groups_with_members_but_no_map_entry_are_reported()
    {
        var (summary, _) = SettlementConfigComparison.Build(
            [Entry(customer: "100001")],
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["100001"] = ["Årlig opgørelse"] },
            null,
            ["Årlig opgørelse"],
            Config(groupSchedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Andet"] = "Måned." }));

        Assert.Equal(["Årlig opgørelse"], summary.SettlementGroupsWithoutSchedule);
    }

    [Fact]
    public void Customer_who_is_no_Xellent_account_keeps_the_group_answer_and_says_why_the_rest_is_blank()
    {
        // The runner clears the Xellent fields on the entry; the row must then read as "Xellent does
        // not describe this customer", not as "Xellent had nothing" — and the group answer stands
        // alone rather than being compared against a plan that belongs to someone else.
        var (_, rows) = SettlementConfigComparison.Build(
            [Entry(customer: "600016", billingPlan: "", template: null, customerInXellent: false)],
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["600016"] = ["Kvartalsvis Aconto"] },
            null,
            ["Kvartalsvis Aconto"],
            Config(
                schedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["½År_Bagud"] = "HalvårBagud" },
                groupSchedules: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Kvartalsvis Aconto"] = "KvartalAC" }));

        var row = Assert.Single(rows);
        Assert.Equal("KvartalAC", row.ScheduleFromGroup);
        Assert.Null(row.ScheduleFromBillingPlan);
        Assert.Empty(row.BillingPlanNums);
        Assert.Empty(row.ContractTemplates);
        Assert.Null(row.SegmentFromTemplate);
        // One source alone is not consensus, and it is not a disagreement either.
        Assert.Equal(SettlementConfigAgreement.Undecided, row.ScheduleAgreement);
        Assert.Contains(row.Notes, n => n.Contains("no Xellent account"));
        Assert.DoesNotContain(row.Notes, n => n.Contains("segmentConflicts"));
    }
}
