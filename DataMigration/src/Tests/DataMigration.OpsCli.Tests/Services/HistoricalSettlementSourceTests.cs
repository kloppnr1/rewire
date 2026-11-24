using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class HistoricalSettlementSourceTests
{
    private static readonly DateOnly Cutover = new(2026, 9, 1);
    private static readonly Guid Mp = Guid.Parse("e450b65d-e7b3-4356-991d-660237b3db3b");
    private static readonly Guid OldCustomer = Guid.NewGuid();
    private static readonly Guid NewCustomer = Guid.NewGuid();
    private static readonly DateTimeOffset OpenEnd = DateTimeOffset.Parse("9999-12-31T23:59:58.999Z");
    private static Dictionary<string, Guid> Points => new() { ["571313115200421466"] = Mp };

    private static GetDeliveryStatusesResponse History(string start = "2026-08-31T22:00:00Z", string? end = null,
        bool inDelivery = true, DeliveryStartReason reason = DeliveryStartReason.ChangeOfSupplier) => new()
    {
        DeliveryStatuses = [new() { InDelivery = inDelivery, StartReason = reason,
            Period = new() { Start = DateTimeOffset.Parse(start), End = end is null ? OpenEnd : DateTimeOffset.Parse(end) } }]
    };

    private static PayerForMeteringPointsDto Payer(Guid customer, Guid? mp = null, DateOnly? start = null, DateOnly? end = null, bool primary = true) => new()
    {
        Payer = new() { CustomerId = customer, IsPrimaryCustomerPayer = primary },
        MeteringPoints = [new() { MeteringPointId = mp ?? Mp, Start = start ?? new DateOnly(2016, 4, 1), End = end }]
    };

    private static Task<HistoricalSettlementSource.Result> Read(GetDeliveryStatusesResponse? history,
        List<PayerForMeteringPointsDto>? payers, string? historyError = null, string? payerError = null) =>
        HistoricalSettlementSource.ReadAsync(Cutover, Points,
            (_, _) => Task.FromResult((history, historyError)),
            (_, _, _) => Task.FromResult((payers, payerError)));

    [Fact]
    public async Task Production_101681_keeps_single_day_before_600256_moves_in()
    {
        var calls = new List<DateTime>();
        var result = await HistoricalSettlementSource.ReadAsync(Cutover, Points,
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((History(), null)),
            (_, date, _) =>
            {
                calls.Add(date);
                return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>((
                    date < DateTime.Parse("2026-09-01T22:00:00Z").ToUniversalTime()
                    ? [Payer(OldCustomer, end: new(2026, 9, 2))] : [Payer(NewCustomer, start: new(2026, 9, 2))], null));
            });
        Assert.Equal(OldCustomer, result.CustomerByMeteringPoint[Points.Single().Key]);
        Assert.Equal(Cutover, result.DeliveryStartByMeteringPoint[Points.Single().Key]);
        Assert.Equal(new DateOnly(2026, 8, 31), result.DeliveryStartByMeteringPoint[Points.Single().Key].AddDays(-1));
        Assert.Equal(DateTime.Parse("2026-08-31T22:00:00Z").ToUniversalTime(), calls[0]);
        Assert.Equal(DateTime.Parse("2026-09-01T22:00:00Z").ToUniversalTime().AddTicks(-1), calls[1]);
        Assert.All(calls, date => Assert.Equal(DateTimeKind.Utc, date.Kind));
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Ended_delivery_is_selected_on_its_last_day_but_excluded_on_exclusive_end()
    {
        var historic = History(end: "2026-09-01T22:00:00Z");
        var result = await Read(historic, [Payer(OldCustomer, end: new(2026, 9, 2))]);
        Assert.Single(result.CustomerByMeteringPoint);
        historic.DeliveryStatuses!.Add(new() { InDelivery = false, Period = new() { Start = DateTimeOffset.Parse("2026-09-01T22:00:00Z"), End = OpenEnd } });
        Assert.Null(HistoricalSettlementSource.SelectDelivery(new(2026, 9, 2), historic).Start);
        Assert.Null(HistoricalSettlementSource.SelectDelivery(new(2026, 9, 2), historic).Reason);
    }

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    public async Task Danish_day_boundary_handles_DST(int year, int month, int day, int hours)
    {
        var date = new DateOnly(year, month, day);
        var times = new List<DateTime>();
        await HistoricalSettlementSource.ReadAsync(date, Points,
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((History("2026-01-01T23:00:00Z"), null)),
            (_, instant, _) => { times.Add(instant); return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>(([Payer(OldCustomer)], null)); });
        Assert.Equal(TimeSpan.FromHours(hours), times[1].AddTicks(1) - times[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Different_dated_customers_are_ambiguous_even_with_one_primary(bool secondPrimary)
    {
        var result = await Read(History(), [Payer(OldCustomer), Payer(NewCustomer, primary: secondPrimary)]);
        Assert.Empty(result.CustomerByMeteringPoint);
        Assert.Equal(SettlementConfigUnresolvedReason.HistoricalCustomerAmbiguous, Assert.Single(result.Issues).Reason);
        Assert.Equal(2, result.IncompleteCustomers.Count);
    }

    [Fact]
    public async Task Multiple_secondary_payers_for_same_customer_are_not_ambiguous()
    {
        var result = await Read(History(), [Payer(OldCustomer, primary: false), Payer(OldCustomer, primary: false)]);
        Assert.Equal(OldCustomer, Assert.Single(result.CustomerByMeteringPoint).Value);
    }

    [Fact]
    public async Task Link_end_is_exclusive_and_future_link_is_not_a_historical_fallback()
    {
        var result = await Read(History(), [Payer(OldCustomer, end: Cutover), Payer(NewCustomer, start: Cutover.AddDays(1))]);
        Assert.Empty(result.CustomerByMeteringPoint);
        Assert.Equal(SettlementConfigUnresolvedReason.HistoricalCustomerMissing, Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public async Task Day_boundary_owner_disagreement_is_unresolved()
    {
        var calls = 0;
        var result = await HistoricalSettlementSource.ReadAsync(Cutover, Points,
            (_, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((History(), null)),
            (_, _, _) => Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>(([Payer(calls++ == 0 ? OldCustomer : NewCustomer)], null)));
        Assert.Empty(result.CustomerByMeteringPoint);
        Assert.Equal(SettlementConfigUnresolvedReason.HistoricalCustomerAmbiguous, Assert.Single(result.Issues).Reason);
    }

    [Fact]
    public async Task Missing_and_failed_reads_are_distinct_and_never_fall_back()
    {
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryMissing, Assert.Single((await Read(null, [])).Issues).Reason);
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryReadFailed, Assert.Single((await Read(null, [], "failure")).Issues).Reason);
        Assert.Equal(SettlementConfigUnresolvedReason.HistoricalCustomerMissing, Assert.Single((await Read(History(), [])).Issues).Reason);
        var failed = await Read(History(), null, payerError: "failure");
        Assert.Empty(failed.CustomerByMeteringPoint);
        Assert.Equal(SettlementConfigUnresolvedReason.HistoricalCustomerReadFailed, Assert.Single(failed.Issues).Reason);
    }

    [Fact]
    public async Task History_failure_blocks_known_customer_with_other_readable_metering_points()
    {
        var other = Guid.NewGuid();
        var result = await HistoricalSettlementSource.ReadAsync(Cutover, new Dictionary<string, Guid> { ["seed"] = Mp, ["missing"] = other },
            (id, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>(id == Mp ? (History(), null) : (null, "failure")),
            (_, _, _) => Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>(([Payer(OldCustomer), Payer(OldCustomer, other)], null)));
        Assert.Contains(OldCustomer, result.IncompleteCustomers);
        Assert.Equal(OldCustomer, result.CustomerByMeteringPoint["missing"]);
        Assert.Single(result.DeliveryStartByMeteringPoint);
    }

    [Fact]
    public void Gaps_malformed_periods_and_partial_delivery_are_reported()
    {
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryMissing, HistoricalSettlementSource.SelectDelivery(Cutover, History("2026-09-02T22:00:00Z")).Reason);
        var malformed = History();
        malformed.DeliveryStatuses![0].Period!.End = default;
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryMissing, HistoricalSettlementSource.SelectDelivery(Cutover, malformed).Reason);
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous, HistoricalSettlementSource.SelectDelivery(Cutover, History("2026-09-01T10:00:00Z")).Reason);
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryMissing, HistoricalSettlementSource.SelectDelivery(Cutover, History(reason: DeliveryStartReason.Unknown)).Reason);
        var overlapping = History();
        overlapping.DeliveryStatuses!.Add(overlapping.DeliveryStatuses[0]);
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous, HistoricalSettlementSource.SelectDelivery(Cutover, overlapping).Reason);
    }

    [Fact]
    public async Task Cohort_keeps_only_dated_portfolio_and_actual_change_of_supplier_seed()
    {
        var other = Guid.NewGuid();
        var moveIn = Guid.NewGuid();
        var ended = Guid.NewGuid();
        var result = await HistoricalSettlementSource.ReadAsync(Cutover,
            new Dictionary<string, Guid> { ["seed"] = Mp, ["other"] = other, ["move-in"] = moveIn, ["ended"] = ended },
            (id, _) => Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((id == ended ? History(inDelivery: false)
                : id == moveIn ? History(reason: DeliveryStartReason.MoveIn)
                : id == other ? History("2026-04-30T22:00:00Z") : History(), null)),
            (_, _, _) => Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>(([Payer(OldCustomer), Payer(OldCustomer, other), Payer(NewCustomer, moveIn)], null)));
        var cohort = ChangeOfSupplierCohort.Select(Cutover, result.DeliveryStartByMeteringPoint,
            result.CustomerByMeteringPoint.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()), changeOfSupplierMeteringPoints: result.ChangeOfSupplierMeteringPoints);
        Assert.Equal(new[] { "other", "seed" }, cohort.KeptMeteringPointIdentifications);
        Assert.Single(result.ChangeOfSupplierMeteringPoints);
    }

    [Fact]
    public async Task Exceptions_are_reported_but_cancellation_propagates()
    {
        var failed = await HistoricalSettlementSource.ReadAsync(Cutover, Points,
            (_, _) => throw new HttpRequestException(),
            (_, _, _) => throw new HttpRequestException());
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryReadFailed, Assert.Single(failed.Issues).Reason);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HistoricalSettlementSource.ReadAsync(Cutover, Points,
            (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException(), cancellationToken: cts.Token));
    }
}
