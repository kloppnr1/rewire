using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class SettlementScheduleGrouperTests
{
    private static readonly DateTime GeneratedAt = new(2026, 7, 28, 6, 0, 0, DateTimeKind.Utc);

    private static SettlementConfigDocument Build(
        IReadOnlyCollection<SettlementConfigEntry> entries,
        IReadOnlyCollection<SettlementConfigUnresolved>? unresolved = null,
        IReadOnlyDictionary<string, string>? map = null,
        int meteringPointsInSettl = 0,
        IReadOnlyDictionary<string, string>? scheduleByCustomerFromGroup = null,
        IReadOnlyDictionary<string, string>? noteByCustomerFromGroup = null,
        IReadOnlyList<ScheduleConflictRule>? conflictRules = null)
        => SettlementScheduleGrouper.Build(
            entries,
            unresolved ?? [],
            map,
            meteringPointsInSettl == 0 ? entries.Count : meteringPointsInSettl,
            "featuretest",
            "hol",
            GeneratedAt,
            scheduleByCustomerFromGroup,
            noteByCustomerFromGroup,
            conflictRules);

    [Fact]
    public void Groups_customers_by_billing_plan_when_no_map_is_supplied()
    {
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952731", "Måned.", "446971", "Firma D"),
            new SettlementConfigEntry("571313179104952732", "KvartalAC", "447256", "Firma A A/S"),
        ]);

        Assert.Equal(2, doc.Schedules.Count);

        var kvartal = doc.Schedules.Single(s => s.Schedule == "KvartalAC");
        Assert.Equal(1, kvartal.CustomerCount);
        Assert.Equal("447256", kvartal.Customers.Single().CustomerNumber);

        var maaned = doc.Schedules.Single(s => s.Schedule == "Måned.");
        Assert.Equal(2, maaned.CustomerCount);
        Assert.Equal(["446970", "446971"], maaned.Customers.Select(c => c.CustomerNumber));
    }

    [Fact]
    public void Unmapped_plan_codes_keep_their_raw_name_and_are_reported()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952731", "HelårBagud", "446971", "Firma D"),
            ],
            map: new Dictionary<string, string> { ["Måned."] = "Månedlig opgørelse" });

        var mapped = doc.Schedules.Single(s => s.Schedule == "Månedlig opgørelse");
        Assert.True(mapped.Mapped);

        // Nothing is dropped just because the mapping is incomplete.
        var unmapped = doc.Schedules.Single(s => s.Schedule == "HelårBagud");
        Assert.False(unmapped.Mapped);
        Assert.Equal("446971", unmapped.Customers.Single().CustomerNumber);

        Assert.Equal(["HelårBagud"], doc.UnmappedBillingPlanNums);
    }

    [Fact]
    public void Several_plan_codes_may_map_to_the_same_schedule()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Kvartal", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952731", "KvartalOpg", "446971", "Firma D"),
            ],
            map: new Dictionary<string, string> { ["Kvartal"] = "Kvartalsvis", ["KvartalOpg"] = "Kvartalsvis" });

        var group = Assert.Single(doc.Schedules);
        Assert.Equal("Kvartalsvis", group.Schedule);
        Assert.Equal(["Kvartal", "KvartalOpg"], group.BillingPlanNums);
        Assert.Equal(2, group.CustomerCount);
    }

    [Fact]
    public void Customer_with_several_plans_is_a_conflict_and_lands_in_no_schedule()
    {
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952731", "KvartalAC", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952732", "KvartalAC", "446970", "Firma C"),
        ]);

        // A settlement schedule is a property of the customer, so two candidates is a decision to be
        // made — not a customer that belongs to both.
        Assert.Empty(doc.Schedules);
        Assert.Equal(1, doc.Totals.ScheduleConflicts);
        Assert.Equal(1, doc.Totals.UniqueCustomers);
        Assert.Equal(3, doc.Totals.MeteringPointsResolved);

        var conflict = Assert.Single(doc.ScheduleConflicts!);
        Assert.Equal("446970", conflict.CustomerNumber);
        // Most metering points first, so the candidate list reads as a ranking.
        Assert.Equal(["KvartalAC", "Måned."], conflict.Candidates.Select(c => c.Schedule));
        Assert.Equal([2, 1], conflict.Candidates.Select(c => c.MeteringPointCount));
        Assert.Equal(
            ["571313179104952731", "571313179104952732"],
            conflict.Candidates[0].MeteringPointIdentifications);
    }

    [Fact]
    public void Settlement_group_never_settles_Xellent_conflict()
    {
        var doc = Build([new("mp1","Måned.","446970",null), new("mp2","KvartalAC","446970",null)],
            scheduleByCustomerFromGroup: new Dictionary<string,string> { ["446970"] = "Md+MdAC" });
        Assert.Empty(doc.Schedules);
        Assert.Single(doc.ScheduleConflicts!);
        Assert.Equal(0, doc.Totals.SchedulesFromSettlementGroup);
    }

    [Fact]
    public void Without_a_group_answer_the_customer_stays_a_conflict()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952731", "KvartalAC", "446970", "Firma C"),
            ],
            scheduleByCustomerFromGroup: new Dictionary<string, string> { ["446971"] = "Md+MdAC" });

        Assert.Empty(doc.Schedules);
        Assert.Single(doc.ScheduleConflicts!);
        Assert.Equal(0, doc.Totals.SchedulesFromSettlementGroup);
    }

    [Fact]
    public void Conflict_table_settles_an_exact_match_when_the_group_gives_no_answer()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952731", "KvartalAC", "446970", "Firma C"),
            ],
            map: new Dictionary<string,string> { ["Måned."] = "Måned.", ["KvartalAC"] = "KvartalAC", ["HalvårBagud"] = "HalvårBagud" },
            conflictRules: [new ScheduleConflictRule(["KvartalAC", "Måned."], "KvartalAC")]);

        var group = Assert.Single(doc.Schedules);
        Assert.Equal("KvartalAC", group.Schedule);
        Assert.Empty(doc.ScheduleConflicts!);
        Assert.Equal(1, doc.Totals.SchedulesFromConflictTable);
        Assert.Equal(0, doc.Totals.SchedulesFromSettlementGroup);
    }

    [Fact]
    public void Xellent_conflict_rule_wins_regardless_of_Settl_group()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952731", "KvartalAC", "446970", "Firma C"),
            ],
            scheduleByCustomerFromGroup: new Dictionary<string, string> { ["446970"] = "Md+MdAC" },
            map: new Dictionary<string,string> { ["Måned."] = "Måned.", ["KvartalAC"] = "KvartalAC", ["HalvårBagud"] = "HalvårBagud" },
            conflictRules: [new ScheduleConflictRule(["KvartalAC", "Måned."], "KvartalAC")]);

        var group = Assert.Single(doc.Schedules);
        Assert.Equal("KvartalAC", group.Schedule);
        Assert.Equal(0, doc.Totals.SchedulesFromSettlementGroup);
        Assert.Equal(1, doc.Totals.SchedulesFromConflictTable);
    }

    [Fact]
    public void Conflict_table_row_must_match_the_candidate_set_exactly()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952731", "KvartalAC", "446970", "Firma C"),
                new SettlementConfigEntry("571313179104952732", "HalvårBagud", "446970", "Firma C"),
            ],
            // Row only covers KvartalAC+Måned.; this customer's set also has HalvårBagud, so no row
            // matches and she stays a conflict.
            map: new Dictionary<string,string> { ["Måned."] = "Måned.", ["KvartalAC"] = "KvartalAC", ["HalvårBagud"] = "HalvårBagud" },
            conflictRules: [new ScheduleConflictRule(["KvartalAC", "Måned."], "KvartalAC")]);

        Assert.Empty(doc.Schedules);
        Assert.Single(doc.ScheduleConflicts!);
        Assert.Equal(0, doc.Totals.SchedulesFromConflictTable);
    }

    [Fact]
    public void Customer_on_one_schedule_is_untouched_by_a_conflicting_neighbour()
    {
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952731", "KvartalAC", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952732", "Måned.", "446971", "Firma D"),
        ]);

        var group = Assert.Single(doc.Schedules);
        Assert.Equal("Måned.", group.Schedule);
        Assert.Equal("446971", group.Customers.Single().CustomerNumber);
        // The conflicted customer's metering point is not counted under the schedule it shares.
        Assert.Equal(1, group.MeteringPointCount);
    }

    [Fact]
    public void Metering_point_counts_are_per_customer_and_per_schedule()
    {
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952731", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952732", "Måned.", "446971", "Firma D"),
        ]);

        var group = Assert.Single(doc.Schedules);
        Assert.Equal(3, group.MeteringPointCount);
        Assert.Equal(2, group.Customers.Single(c => c.CustomerNumber == "446970").MeteringPointCount);
    }

    [Fact]
    public void Every_unresolved_metering_point_is_listed_with_its_reason()
    {
        var doc = Build(
            [new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C")],
            [
                new SettlementConfigUnresolved("111111111111111111", SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier),
                new SettlementConfigUnresolved("222222222222222222", SettlementConfigUnresolvedReason.NotFoundInXellent),
                new SettlementConfigUnresolved("333333333333333333", SettlementConfigUnresolvedReason.NoRepresentative),
            ],
            meteringPointsInSettl: 4);

        Assert.Equal(3, doc.Totals.Unresolved);
        Assert.Equal(
            ["111111111111111111", "222222222222222222", "333333333333333333"],
            doc.Unresolved.Select(u => u.MeteringPointIdentification));
        Assert.Equal(
            SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier,
            doc.Unresolved[0].Reason);
        Assert.Equal(4, doc.Totals.MeteringPointsInSettl);
    }

    [Fact]
    public void Casing_variants_of_an_unmapped_plan_code_still_form_one_schedule()
    {
        // Xellent spells the same code both ways. With no map entry the raw code becomes the
        // schedule name, and folding must happen in the grouping itself — not only in the map.
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313179104952731", "måned.", "446971", "Firma D"),
        ]);

        var group = Assert.Single(doc.Schedules);
        Assert.Equal(2, group.CustomerCount);
        Assert.Equal(["Måned.", "måned."], group.BillingPlanNums);
        // One unmapped code reported, not two spellings of the same one.
        Assert.Single(doc.UnmappedBillingPlanNums);
    }

    [Fact]
    public void Empty_map_entry_falls_back_to_the_raw_plan_code()
    {
        var doc = Build(
            [new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C")],
            map: new Dictionary<string, string> { ["Måned."] = "" });

        Assert.Equal("Måned.", Assert.Single(doc.Schedules).Schedule);
        Assert.Equal(["Måned."], doc.UnmappedBillingPlanNums);
    }

    // The Klinik L case: a customer created in Settl took over a metering point whose Xellent
    // contract belonged to a housing association billed half-yearly. The plan is the previous
    // holder's, so it must not put her in HalvårBagud.
    [Fact]
    public void Customer_without_Xellent_plan_remains_unresolved_despite_Settl_group()
    {
        var doc = Build([new("mp1", "", "600016", null, CustomerInXellent: false)],
            scheduleByCustomerFromGroup: new Dictionary<string,string> { ["600016"] = "KvartalAC" });
        Assert.Empty(doc.Schedules);
        Assert.Equal(1, doc.Totals.CustomersWithoutSchedule);
        Assert.Equal(0, doc.Totals.SchedulesFromSettlementGroup);
    }

    [Fact]
    public void Customer_who_is_no_Xellent_account_and_in_no_group_lands_in_no_schedule()
    {
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313115200092819", "", "600058", "Carelink Pleje", CustomerInXellent: false),
        ]);

        Assert.Equal("Måned.", Assert.Single(doc.Schedules).Schedule);
        Assert.DoesNotContain(doc.Schedules.SelectMany(s => s.Customers), c => c.CustomerNumber == "600058");
        Assert.Equal(1, doc.Totals.CustomersNotInXellent);
        Assert.Equal(1, doc.Totals.CustomersWithoutSchedule);
        // Absent, but not silently: she is counted rather than filed under a schedule she never named.
        Assert.Empty(doc.ScheduleConflicts!);
    }

    [Fact]
    public void Customers_without_a_schedule_are_named_not_just_counted()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
                new SettlementConfigEntry("571313115200092819", "", "600058", "Carelink Pleje", CustomerInXellent: false),
                new SettlementConfigEntry("571313115200092820", "", "600058", "Carelink Pleje", CustomerInXellent: false),
            ],
            noteByCustomerFromGroup: new Dictionary<string, string>
            {
                ["600058"] = "customer is in no settlement group in Settl",
            });

        var row = Assert.Single(doc.CustomersWithoutSchedule!);
        Assert.Equal("600058", row.CustomerNumber);
        Assert.Equal("Carelink Pleje", row.Name);
        Assert.Equal(2, row.MeteringPointCount);
        Assert.Equal(
            new[] { "571313115200092819", "571313115200092820" },
            row.MeteringPointIdentifications);
        // Grunden kommer ordret fra gruppe-resolveren: "ingen gruppe" og "gruppen står ikke i
        // groupSchedules" kræver hver sin handling, og listen skal kunne skelnes uden opslag.
        Assert.Equal("no usable Xellent billing plan", row.Reason);
    }

    [Fact]
    public void The_list_and_the_total_are_the_same_answer()
    {
        var doc = Build([
            new SettlementConfigEntry("571313179104952730", "Måned.", "446970", "Firma C"),
            new SettlementConfigEntry("571313115200092819", "", "600058", "Carelink Pleje", CustomerInXellent: false),
            new SettlementConfigEntry("571313115200092821", "", "600059", "Cobo IX", CustomerInXellent: false),
        ]);

        Assert.Equal(doc.Totals.CustomersWithoutSchedule, doc.CustomersWithoutSchedule!.Count);
        Assert.Equal(["600058", "600059"], doc.CustomersWithoutSchedule.Select(c => c.CustomerNumber));
    }

    [Fact]
    public void A_customer_with_only_a_Settl_group_stays_on_the_unresolved_list()
    {
        var doc = Build(
            [
                new SettlementConfigEntry("571313115200092819", "", "600058", "Carelink Pleje", CustomerInXellent: false),
            ],
            scheduleByCustomerFromGroup: new Dictionary<string, string> { ["600058"] = "KvartalAC" });

        Assert.Single(doc.CustomersWithoutSchedule!);
        Assert.Equal(1, doc.Totals.CustomersWithoutSchedule);
        Assert.Empty(doc.Schedules);
    }
}
