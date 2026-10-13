using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

public class DeliveryConsumerCorrelationTests
{
    [Theory]
    [InlineData(1, 4, 3, 8, 1)] // Carried into the delivery period.
    [InlineData(3, 5, 3, 8, 1)] // Starts at delivery start.
    [InlineData(4, 8, 3, 8, 1)] // Ends at delivery end.
    [InlineData(1, 3, 3, 8, 0)] // Ends at delivery start.
    [InlineData(8, 10, 3, 8, 0)] // Starts at delivery end.
    [InlineData(1, 2, 3, 8, 0)]
    [InlineData(9, 10, 3, 8, 0)]
    [InlineData(1, 10, 3, 8, 1)]
    public void Counts_only_versions_effective_during_period(int start, int end, int deliveryStart, int deliveryEnd, int count)
    {
        var result = DeliveryConsumerCorrelation.For(Period(deliveryStart, deliveryEnd), new([Version(start, end, true)], []));
        Assert.Equal(count, result.Count);
        Assert.Equal(count == 0, result.Latest is null);
        Assert.Equal(count > 0, result.AnyDuplicate);
    }

    [Fact]
    public void Uses_newest_version_not_newest_duplicate_and_counts_carry_in_and_open_version()
    {
        var old = Version(1, 5, true);
        var latest = Version(5, null, false);
        var result = DeliveryConsumerCorrelation.For(Period(3, null), new([latest, old], []));
        Assert.Equal(2, result.Count);
        Assert.Same(latest, result.Latest);
        Assert.False(result.Latest!.Duplicate);
        Assert.True(result.AnyDuplicate);
        Assert.Empty(result.Warning);
    }

    [Fact]
    public void Incomplete_history_does_not_claim_exact_count_or_newest_consumer()
    {
        var result = DeliveryConsumerCorrelation.For(Period(3, 8), new([Version(1, null, true)], ["Fixture read failure"]));
        Assert.Null(result.Count);
        Assert.Null(result.Latest);
        Assert.Contains("Fixture read failure", result.Warning);
        Assert.True(result.AnyDuplicate);
    }

    [Fact]
    public void Invalid_delivery_dates_do_not_claim_zero_consumer_versions()
    {
        var result = DeliveryConsumerCorrelation.For(Period(3, 8) with { Start = null }, new([Version(1, null, true)], []));
        Assert.Null(result.Count);
        Assert.Null(result.Latest);
        Assert.NotEmpty(result.Warning);
    }

    [Fact]
    public void Incomplete_history_without_confirmed_duplicate_shows_unknown_not_no()
    {
        var result = DeliveryConsumerCorrelation.For(Period(3, 8), new([Version(1, null, false)], ["Fixture read failure"]));
        Assert.Null(result.AnyDuplicate);
    }

    [Fact]
    public void Duplicate_outside_delivery_period_does_not_set_flag()
    {
        var result = DeliveryConsumerCorrelation.For(Period(5, 8), new([Version(1, 5, true), Version(5, null, false)], []));
        Assert.False(result.AnyDuplicate);
        Assert.Equal(1, result.Count);
    }

    private static DateTime Date(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);
    private static DeliveryPeriod Period(int start, int? end) => new(new(Date(start)), end is { } day ? new DateTimeOffset(Date(day)) : null,
        "", "", "true", "ChangeOfSupplier", "Na", "As returned");
    private static ConsumerVersion Version(int start, int? end, bool duplicate) => new(new MeteringPointV4Dto
    {
        Consumer = new()
        {
            Start = Date(start), End = end is { } day ? Date(day) : null, CustomerType = "Person",
            FirstConsumerParty = new() { Name = new() { FullName = "Synthetic" } },
            SecondConsumerParty = new() { Name = new() { FullName = duplicate ? "Synthetic" : "Different" } }
        }
    }, null, "As returned");
}
