using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class RepairDeliveryHistoryTests
{
    private const string Mp = "571313181101966528";
    private static readonly DateOnly Start = new(2023, 6, 1);
    private static readonly DateOnly Join = new(2026, 9, 1);
    private static readonly DateOnly Event = new(2026, 10, 5);

    [Fact]
    public void Joining_keeps_migration_starts_and_open_ends()
    {
        var d = Decision();
        var result = RepairDeliveryHistory.Apply(d, Snapshot(Period(true, Join, null)));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(Start, result.Start);
        Assert.Null(result.End);
        Assert.Equal(d.Contracts, result.Contracts);
        Assert.True(result.DeliveryHistoryChecked);
    }

    [Fact]
    public void Later_move_in_ends_links_preserves_earlier_ends_and_omits_future_contracts()
    {
        var d = Decision() with { Contracts = [new("Earlier", Start, Event.AddDays(-5), true, "model"),
            new("Open", Join, DateOnly.MaxValue, true, "model"),
            new("Future", Event, DateOnly.MaxValue, true, "model")] };
        var result = RepairDeliveryHistory.Apply(d, Snapshot(Period(true, Join, Event),
            Period(true, Event, null, DeliveryStartReason.MoveIn)));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(Event.AddDays(-1), result.End);
        Assert.Equal(d.CustomerStart, result.CustomerStart);
        Assert.Equal(d.PayerStart, result.PayerStart);
        Assert.Equal(2, result.Contracts.Count);
        Assert.Equal(Event.AddDays(-5), result.Contracts[0].End);
        Assert.Equal(Event.AddDays(-1), result.Contracts[1].End);
        Assert.Equal(3, result.OriginalContracts.Length);
        Assert.Contains("Udeladt", RepairDeliveryHistory.Explanation(result, Snapshot()));
    }

    [Theory]
    [InlineData(DeliveryStopReason.MoveOut)]
    [InlineData(DeliveryStopReason.ChangeOfSupplier)]
    public void End_reason_on_non_delivery_period_uses_its_start_as_exclusive_boundary(DeliveryStopReason stop)
    {
        var result = RepairDeliveryHistory.Apply(Decision(), Snapshot(Period(true, Join, Event),
            Period(false, Event, null, DeliveryStartReason.Na, stop)));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(Event.AddDays(-1), result.End);
        Assert.Equal(Event.AddDays(-1), Assert.Single(result.Contracts).End);
    }

    [Theory]
    [InlineData(DeliveryStopReason.MoveOut)]
    [InlineData(DeliveryStopReason.ChangeOfSupplier)]
    public void End_reason_on_completed_supply_uses_its_exclusive_end(DeliveryStopReason stop)
    {
        var result = RepairDeliveryHistory.Apply(Decision(), Snapshot(Period(true, Join, Event,
            DeliveryStartReason.ChangeOfSupplier, stop)));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(Event.AddDays(-1), result.End);
    }

    [Fact]
    public void Earliest_departure_wins_even_if_supply_later_returns()
    {
        var result = RepairDeliveryHistory.Apply(Decision(), Snapshot(Period(true, Join, Event),
            Period(false, Event, Event.AddDays(10), DeliveryStartReason.Na, DeliveryStopReason.ChangeOfSupplier),
            Period(true, Event.AddDays(10), null)));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(Event.AddDays(-1), result.End);
    }

    [Fact]
    public void Move_in_without_known_new_customer_is_enough()
    {
        var result = RepairDeliveryHistory.Apply(Decision(), Snapshot(Period(true, Event, null, DeliveryStartReason.MoveIn)));
        Assert.Equal("Ready", result.Status);
        Assert.Equal(Event.AddDays(-1), result.End);
    }

    [Fact]
    public void Missing_history_is_held()
    {
        var result = RepairDeliveryHistory.Apply(Decision(), Snapshot());
        Assert.Equal(RepairDeliveryHistory.Missing, result.Status);
    }

    [Theory]
    [InlineData(DeliveryStopReason.MoveOut)]
    [InlineData(DeliveryStopReason.ChangeOfSupplier)]
    public void Stop_reason_without_a_finite_departure_is_held(DeliveryStopReason stop)
    {
        var result = RepairDeliveryHistory.Apply(Decision(), Snapshot(Period(true, Join, null, DeliveryStartReason.Na, stop)));
        Assert.Equal(RepairDeliveryHistory.Unclear, result.Status);
        Assert.Null(result.End);
    }

    [Fact]
    public void Conflicting_reasons_and_unexplained_supply_end_are_held()
    {
        Assert.Equal(RepairDeliveryHistory.Unclear, RepairDeliveryHistory.Apply(Decision(), Snapshot(
            Period(true, Event, null), Period(true, Event, null, DeliveryStartReason.MoveIn))).Status);
        Assert.Equal(RepairDeliveryHistory.Unclear, RepairDeliveryHistory.Apply(Decision(), Snapshot(
            Period(true, Join, Event))).Status);
    }

    [Fact]
    public void Cutoff_before_link_start_is_held_instead_of_exporting_invalid_period()
    {
        var result = RepairDeliveryHistory.Apply(Decision() with { Start = Event }, Snapshot(
            Period(true, Event, null, DeliveryStartReason.MoveIn)));
        Assert.Equal(RepairDeliveryHistory.Unclear, result.Status);
    }

    [Fact]
    public void Summer_and_winter_UTC_boundaries_use_Danish_calendar_dates()
    {
        foreach (var day in new[] { new DateOnly(2026, 10, 5), new DateOnly(2026, 11, 5) })
        {
            var result = RepairDeliveryHistory.Apply(Decision(), Snapshot(Period(true, day, null, DeliveryStartReason.MoveIn)));
            Assert.Equal(day.AddDays(-1), result.End);
            Assert.Equal(day, result.DeliveryEvent!.Date);
        }
    }

    private static CustomerPayerRepairDecision Decision() => new(Mp, "501230", "501230", Start,
        true, true, true, true, "Ready") { CustomerStart = Start, PayerStart = Start,
        Contracts = [new("HomeCharge", Start, DateOnly.MaxValue, true, "model")] };

    private static DeliveryStatusDto Period(bool supplied, DateOnly start, DateOnly? end,
        DeliveryStartReason reason = DeliveryStartReason.ChangeOfSupplier, DeliveryStopReason stop = DeliveryStopReason.Na)
        => new() { InDelivery = supplied, StartReason = reason, StopReason = stop,
            Period = new() { Start = At(start), End = end is { } e ? At(e) : DateTimeOffset.MaxValue } };

    private static DateTimeOffset At(DateOnly date)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static RepairSettlEvidence Snapshot(params DeliveryStatusDto[] statuses) => new(DateTimeOffset.UtcNow,
        [], statuses.Length == 0 ? [] : [new(Mp, Guid.NewGuid(), true, null, null,
            new() { DeliveryStatuses = statuses.ToList() }, null, Join, "ChangeOfSupplier")], [], [], [], []);
}
