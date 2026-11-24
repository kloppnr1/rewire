using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class ChangeOfSupplierCohortTests
{
    private static readonly DateOnly Cutover = new(2026, 9, 1);
    private static readonly DateOnly Earlier = new(2026, 5, 1);

    /// <summary>The mixed customer the whole filter exists for: 446970 moved MP1 on the cutover date
    /// and MP2 back in May, 446971 moved nothing on the date at all.</summary>
    private static readonly Dictionary<string, DateOnly> MixedPopulation = new(StringComparer.Ordinal)
    {
        ["MP1"] = Cutover,
        ["MP2"] = Earlier,
        ["MP3"] = Earlier,
    };

    private static readonly Dictionary<string, string> MixedCustomers = new(StringComparer.Ordinal)
    {
        ["MP1"] = "446970",
        ["MP2"] = "446970",
        ["MP3"] = "446971",
    };

    [Fact]
    public void Keeps_every_metering_point_of_a_customer_who_moved_one_of_them_on_the_date()
    {
        var cohort = ChangeOfSupplierCohort.Select(Cutover, MixedPopulation, MixedCustomers);

        Assert.Equal(["MP1", "MP2"], cohort.KeptMeteringPointIdentifications);
        Assert.Equal(1, cohort.Filter.Customers);
        Assert.Equal(1, cohort.Filter.MeteringPointsOnDate);
        Assert.Equal(1, cohort.Filter.MeteringPointsFromSharedCustomer);
    }

    [Fact]
    public void Marks_which_metering_points_carried_the_date_and_which_came_along()
    {
        var cohort = ChangeOfSupplierCohort.Select(
            Cutover, MixedPopulation, MixedCustomers,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["446970"] = "Firma C" });

        var row = Assert.Single(cohort.Filter.CustomerRows);
        Assert.Equal("446970", row.CustomerNumber);
        Assert.Equal("Firma C", row.Name);
        Assert.Equal(["MP1"], row.MeteringPointsOnDate);
        Assert.Equal(["MP2"], row.MeteringPointsFromSharedCustomer);
    }

    /// <summary>
    /// The point of keeping a customer whole: her schedule and segment must be the ones a full run
    /// gives her. Judged on MP1 alone she would be a "Måned." customer in the Privat segment; with
    /// MP2 along she is a schedule conflict and a segment conflict, exactly as in the full run.
    /// </summary>
    [Fact]
    public void Mixed_customer_lands_in_the_same_schedule_and_segment_as_an_unfiltered_run()
    {
        SettlementConfigEntry[] all =
        [
            new("MP1", "Måned.", "446970", "Firma C", "V_Privat"),
            new("MP2", "KvartalAC", "446970", "Firma C", "V_ProdErhv"),
            new("MP3", "KvartalAC", "446971", "Firma D", "V_ProdErhv"),
        ];

        var cohort = ChangeOfSupplierCohort.Select(Cutover, MixedPopulation, MixedCustomers);
        var kept = cohort.KeptMeteringPointIdentifications.ToHashSet(StringComparer.Ordinal);
        var filtered = all.Where(e => kept.Contains(e.MeteringPointIdentification)).ToArray();

        Assert.Equal(2, filtered.Length);

        // Schedule: the customer's metering points name two schedules in both runs, so she is a
        // conflict in both — and would have been silently filed under "Måned." on MP1 alone.
        var fullSchedules = Schedules(all);
        var cohortSchedules = Schedules(filtered);
        Assert.Equal(Conflict(fullSchedules, "446970"), Conflict(cohortSchedules, "446970"));
        Assert.DoesNotContain(
            cohortSchedules.Schedules.SelectMany(s => s.Customers), c => c.CustomerNumber == "446970");

        // Segment: same two templates in both runs, same conflict.
        var fullSegments = Segments(all);
        var cohortSegments = Segments(filtered);
        Assert.Equal(SegmentOf(fullSegments, "446970"), SegmentOf(cohortSegments, "446970"));
        Assert.Equal(
            fullSegments.Conflicts.Single(c => c.CustomerNumber == "446970").Reason,
            cohortSegments.Conflicts.Single(c => c.CustomerNumber == "446970").Reason);
    }

    /// <summary>
    /// The same customer with an ordinary, undivided estate: cutover only, one schedule, one segment —
    /// and the cohort run puts her in exactly the group the full run does.
    /// </summary>
    [Fact]
    public void Whole_estate_on_the_date_keeps_its_group_from_the_unfiltered_run()
    {
        var population = new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            ["MP1"] = Cutover,
            ["MP2"] = Cutover,
            ["MP3"] = Earlier,
        };

        SettlementConfigEntry[] all =
        [
            new("MP1", "Måned.", "446970", "Firma C", "V_Privat"),
            new("MP2", "Måned.", "446970", "Firma C", "V_Privat"),
            new("MP3", "KvartalAC", "446971", "Firma D", "V_ProdErhv"),
        ];

        var cohort = ChangeOfSupplierCohort.Select(Cutover, population, MixedCustomers);
        Assert.Equal(["MP1", "MP2"], cohort.KeptMeteringPointIdentifications);
        Assert.Equal(0, cohort.Filter.MeteringPointsFromSharedCustomer);

        var kept = cohort.KeptMeteringPointIdentifications.ToHashSet(StringComparer.Ordinal);
        var filtered = all.Where(e => kept.Contains(e.MeteringPointIdentification)).ToArray();

        Assert.Equal(
            Schedules(all).Schedules.Single(s => s.Customers.Any(c => c.CustomerNumber == "446970")).Schedule,
            Schedules(filtered).Schedules.Single(s => s.Customers.Any(c => c.CustomerNumber == "446970")).Schedule);
        Assert.Equal(SegmentOf(Segments(all), "446970"), SegmentOf(Segments(filtered), "446970"));

        // And the customer who moved on another date is gone entirely.
        Assert.DoesNotContain(
            Schedules(filtered).Schedules.SelectMany(s => s.Customers), c => c.CustomerNumber == "446971");
    }

    [Fact]
    public void A_metering_point_with_no_customer_is_judged_on_its_own_date()
    {
        var population = new Dictionary<string, DateOnly>(StringComparer.Ordinal)
        {
            ["MP1"] = Cutover,
            ["MP2"] = Earlier,
        };

        var cohort = ChangeOfSupplierCohort.Select(
            Cutover, population, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(["MP1"], cohort.KeptMeteringPointIdentifications);
        Assert.Equal(["MP1"], cohort.Filter.MeteringPointsOnDateWithoutCustomer);
        Assert.Equal(1, cohort.Filter.MeteringPointsOnDate);
        Assert.Equal(0, cohort.Filter.Customers);
        Assert.Empty(cohort.Filter.CustomerRows);
    }

    [Fact]
    public void Nothing_on_the_date_keeps_nothing()
    {
        var cohort = ChangeOfSupplierCohort.Select(new DateOnly(2026, 10, 1), MixedPopulation, MixedCustomers);

        Assert.Empty(cohort.KeptMeteringPointIdentifications);
        Assert.Equal(0, cohort.Filter.Customers);
        Assert.Equal(0, cohort.Filter.MeteringPointsOnDate);
        Assert.Equal(0, cohort.Filter.MeteringPointsFromSharedCustomer);
    }

    [Fact]
    public void The_filter_reports_the_date_it_was_asked_for()
    {
        Assert.Equal(Cutover, ChangeOfSupplierCohort.Select(Cutover, MixedPopulation, MixedCustomers).Filter.Date);
    }

    private static SettlementConfigDocument Schedules(IReadOnlyCollection<SettlementConfigEntry> entries) =>
        SettlementScheduleGrouper.Build(
            entries, [], null, entries.Count, "featuretest", "hol",
            new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc));

    private static CustomerSegmentGrouper.CustomerSegmentResult Segments(
        IReadOnlyCollection<SettlementConfigEntry> entries) =>
        CustomerSegmentGrouper.Build(
            entries,
            new Dictionary<string, SegmentRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["V_Privat"] = SegmentRule.Uniform(["Privat"]),
                ["V_ProdErhv"] = SegmentRule.Uniform(["Erhverv"]),
            });

    private static string? SegmentOf(CustomerSegmentGrouper.CustomerSegmentResult result, string customer) =>
        result.Segments.FirstOrDefault(s => s.Customers.Any(c => c.CustomerNumber == customer))?.Segment;

    private static IReadOnlyList<string> Conflict(SettlementConfigDocument document, string customer) =>
        [.. (document.ScheduleConflicts ?? [])
            .Where(c => c.CustomerNumber == customer)
            .SelectMany(c => c.Candidates.Select(x => x.Schedule))
            .Order(StringComparer.Ordinal)];
}
