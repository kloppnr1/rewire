using DataMigration.Core;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The routing half of the contract-part restore, with no database in sight: which logged change
/// types are acted on, which are refused, and what happens when a deleted part's agreement resolves
/// to nothing.
/// </summary>
public class ContractPartRestorePlanTests
{
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;
    private static readonly DateTime Open = MigrationConstants.EndDateNull;

    private static ContractPartLogKey Key(string contract = "C1", string product = "P1", string agreement = "A1")
        => new(contract, agreement, Category, "01", product, "ST 1000", new DateTime(2023, 10, 1));

    private static ContractPartLogChange Change(string changeType, ContractPartLogKey? key = null, DateTime? endDate = null)
    {
        var k = key ?? Key();
        return new ContractPartLogChange(k, k.PartStartDate, endDate ?? Open, changeType, null);
    }

    private static ContractPartLogChangeSet Set(params ContractPartLogChange[] changes)
        => new(changes, new HashSet<ContractPartLogKey>());

    // ── The three part-level change types ─────────────────────────────────────────────────────

    [Fact]
    public void An_end_date_change_puts_the_logged_end_date_back()
    {
        var warnings = new WarningCollector();

        var plan = ContractPartRestore.BuildPlan(
            Set(Change(ContractPartRestore.ChangeTypeEndDateChanged, endDate: Open)), warnings);

        Assert.Equal(Open, Assert.Single(plan.EndDates).Value);
        Assert.Empty(plan.DeletedParts);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void A_deletion_becomes_a_part_to_synthesise()
    {
        var warnings = new WarningCollector();

        var plan = ContractPartRestore.BuildPlan(
            Set(Change(ContractPartRestore.ChangeTypePartDeleted)), warnings);

        Assert.Single(plan.DeletedParts);
        Assert.Empty(plan.EndDates);
    }

    [Fact]
    public void A_start_date_change_is_warned_about_and_not_applied()
    {
        var warnings = new WarningCollector();

        var plan = ContractPartRestore.BuildPlan(
            Set(Change(ContractPartRestore.ChangeTypeStartDateChanged)), warnings);

        Assert.Empty(plan.EndDates);
        Assert.Empty(plan.DeletedParts);
        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("C1", warning);
        Assert.Contains("start date", warning);
    }

    // ── Everything else is not a part-level change ────────────────────────────────────────────

    [Fact]
    public void A_one_time_charge_change_is_ignored_without_a_word()
    {
        var warnings = new WarningCollector();

        // The real text carries the charge's name, so it never matches anything on the whitelist.
        var plan = ContractPartRestore.BuildPlan(
            Set(Change("Engangsydelse V Gebyr01 er slettet")), warnings);

        Assert.Empty(plan.EndDates);
        Assert.Empty(plan.DeletedParts);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void An_unknown_change_type_is_ignored_rather_than_guessed_at()
    {
        var warnings = new WarningCollector();

        var plan = ContractPartRestore.BuildPlan(Set(Change("Noget helt andet")), warnings);

        Assert.Empty(plan.EndDates);
        Assert.Empty(plan.DeletedParts);
    }

    // ── A key with history on both sides of the window ────────────────────────────────────────

    [Fact]
    public void A_key_changed_outside_the_window_too_is_still_restored_and_only_noted()
    {
        var warnings = new WarningCollector();
        var key = Key();
        var changeSet = new ContractPartLogChangeSet(
            [Change(ContractPartRestore.ChangeTypeEndDateChanged, key)],
            new HashSet<ContractPartLogKey> { key });

        var plan = ContractPartRestore.BuildPlan(changeSet, warnings);

        Assert.Single(plan.EndDates);
        Assert.Empty(warnings.GetWarnings());
        Assert.Contains(warnings.GetInfos(), i => i.Contains("outside the restore window"));
    }

    // ── Resolving the deleted parts ───────────────────────────────────────────────────────────

    [Fact]
    public void A_deleted_part_lands_on_the_metering_point_its_agreement_resolves_to()
    {
        var warnings = new WarningCollector();
        var plan = ContractPartRestore.BuildPlan(
            Set(Change(ContractPartRestore.ChangeTypePartDeleted)), warnings);

        var restore = ContractPartRestore.FromPlan(
            plan,
            new Dictionary<(string, string, string), IReadOnlyList<string>>
            {
                [(Category, "01", "A1")] = new[] { "571313100000000001" }
            },
            warnings);

        var part = Assert.Single(restore.DeletedPartsFor(Category));
        Assert.Equal("571313100000000001", part.MeteringPoint);
        Assert.Empty(warnings.GetWarnings());
    }

    [Fact]
    public void A_deleted_part_whose_agreement_resolves_to_nothing_is_dropped_with_a_warning()
    {
        var warnings = new WarningCollector();
        var plan = ContractPartRestore.BuildPlan(
            Set(Change(ContractPartRestore.ChangeTypePartDeleted)), warnings);

        var restore = ContractPartRestore.FromPlan(
            plan,
            new Dictionary<(string, string, string), IReadOnlyList<string>>(),
            warnings);

        Assert.Empty(restore.DeletedPartsFor(Category));
        var warning = Assert.Single(warnings.GetWarnings());
        Assert.Contains("C1", warning);
        Assert.Contains("A1", warning);
        Assert.Contains("P1", warning);
    }

    [Fact]
    public void The_restore_is_empty_when_nothing_survived_routing()
    {
        var warnings = new WarningCollector();
        var plan = ContractPartRestore.BuildPlan(Set(Change("Engangsydelse V Gebyr01 er slettet")), warnings);

        var restore = ContractPartRestore.FromPlan(
            plan, new Dictionary<(string, string, string), IReadOnlyList<string>>(), warnings);

        Assert.True(restore.IsEmpty);
        Assert.True(ContractPartRestore.Empty.IsEmpty);
    }

    // ── The shared include rule ───────────────────────────────────────────────────────────────

    [Fact]
    public void Electricity_has_no_start_cap_but_the_installation_commodities_do()
    {
        var today = new DateTime(2026, 9, 1);
        var future = new DateTime(2026, 10, 1);

        Assert.True(ContractPartInclusion.Included(
            Domain.Customers.Commodity.Electricity, future, Open, today, null));
        Assert.False(ContractPartInclusion.Included(
            Domain.Customers.Commodity.Water, future, Open, today, null));
    }

    [Fact]
    public void A_restored_end_date_that_is_still_in_the_past_does_not_come_back()
    {
        var today = new DateTime(2026, 9, 1);

        // What the batch wrote (2026-08-31) and what it overwrote (2026-05-31) are both in the past:
        // restoring the old value must not make the part live again.
        Assert.False(ContractPartInclusion.Included(
            Domain.Customers.Commodity.Electricity, new DateTime(2020, 1, 1), new DateTime(2026, 5, 31), today, null));
        Assert.True(ContractPartInclusion.Included(
            Domain.Customers.Commodity.Electricity, new DateTime(2020, 1, 1), Open, today, null));
    }
}
