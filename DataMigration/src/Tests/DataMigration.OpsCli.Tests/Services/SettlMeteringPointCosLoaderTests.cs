using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.OpsCli.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

/// <summary>
/// Where the loader gets each metering point's change-of-supplier date from. The Settl round trip is
/// stubbed; what is under test is the split — a metering point in delivery is dated from its
/// current/next period, and one that is not in delivery takes the caller's fallback date if there is
/// one, or the old completed-period-else-today rule if there is not.
/// </summary>
public class SettlMeteringPointCosLoaderTests
{
    private static readonly DateTimeOffset CurrentStart = new(2026, 4, 30, 22, 0, 0, TimeSpan.Zero);   // DK 2026-05-01
    private static readonly DateTimeOffset CompletedStart = new(2025, 12, 31, 23, 0, 0, TimeSpan.Zero); // DK 2026-01-01

    private static MeteringPointInfoDto Mp(string id, bool? inDelivery, bool current, bool completed) => new()
    {
        Id = Guid.NewGuid(),
        MeteringPointIdentification = id,
        InDelivery = inDelivery,
        CurrentOrNextDeliveryPeriod = current ? new TimePeriodDto { Start = CurrentStart } : null,
        MostRecentCompletedDeliveryPeriod = completed ? new TimePeriodDto { Start = CompletedStart } : null,
    };

    private static Task<Dictionary<string, SettlMpWithCos>> LoadAsync(
        IReadOnlyList<MeteringPointInfoDto> mps, DateOnly? fallback, bool includeNotInDelivery = true)
    {
        var loader = new SettlMeteringPointCosLoader(new StubMeteringPointClient(mps));
        return loader.LoadAsync(new CosPeriod(null, null), CancellationToken.None,
            includeNotInDelivery: includeNotInDelivery,
            commodity: CommodityFilter.Electricity,
            noDeliveryPeriodCos: fallback);
    }

    [Fact]
    public async Task Every_not_in_delivery_metering_point_takes_the_supplied_fallback_date()
    {
        // Including the ones carrying a completed delivery period: Settl fills that field in for a
        // change of supplier that was scheduled and then cancelled, so it does not mean "delivered".
        var fallback = new DateOnly(2026, 9, 1);
        var result = await LoadAsync(
        [
            Mp("571000000000000001", false, current: false, completed: false),
            Mp("571000000000000002", false, current: false, completed: true),
            Mp("571000000000000003", null, current: false, completed: false),
        ], fallback);

        Assert.Equal(fallback, result["571000000000000001"].Cos);
        Assert.Equal(fallback, result["571000000000000002"].Cos);
        Assert.Equal(fallback, result["571000000000000003"].Cos);
    }

    [Fact]
    public async Task A_metering_point_in_delivery_keeps_its_own_date()
    {
        var result = await LoadAsync(
            [Mp("571000000000000001", true, current: true, completed: false)], new DateOnly(2026, 9, 1));

        Assert.Equal(new DateOnly(2026, 5, 1), result["571000000000000001"].Cos);
    }

    [Fact]
    public async Task Without_a_supplied_date_the_old_rule_stands()
    {
        // What reconcile and payment-match depend on: completed period start, else today.
        var result = await LoadAsync(
        [
            Mp("571000000000000001", false, current: false, completed: true),
            Mp("571000000000000002", null, current: false, completed: false),
        ], fallback: null);

        Assert.Equal(new DateOnly(2026, 1, 1), result["571000000000000001"].Cos);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), result["571000000000000002"].Cos);
    }

    [Fact]
    public async Task Never_in_delivery_metering_points_stay_out_when_the_flag_is_off()
    {
        var result = await LoadAsync(
        [
            Mp("571000000000000001", true, current: true, completed: false),
            Mp("571000000000000002", false, current: false, completed: false),
        ], fallback: new DateOnly(2026, 9, 1), includeNotInDelivery: false);

        Assert.Single(result);
        Assert.True(result.ContainsKey("571000000000000001"));
    }

    [Fact]
    public async Task Fallback_metering_points_ignore_the_cos_window()
    {
        // The window selects historical change-of-supply events; a metering point that is not in
        // delivery has none, so a window that excludes the fallback date must not drop it.
        var fallback = new DateOnly(2026, 9, 1);
        var loader = new SettlMeteringPointCosLoader(
            new StubMeteringPointClient([Mp("571000000000000001", false, current: false, completed: false)]));

        var result = await loader.LoadAsync(
            new CosPeriod(new DateOnly(2020, 1, 1), new DateOnly(2020, 12, 31)), CancellationToken.None,
            includeNotInDelivery: true, commodity: CommodityFilter.Electricity, noDeliveryPeriodCos: fallback);

        Assert.Equal(fallback, result["571000000000000001"].Cos);
    }
}
