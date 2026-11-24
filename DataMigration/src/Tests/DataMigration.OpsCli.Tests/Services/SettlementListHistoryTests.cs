using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementListHistoryTests
{
    private static readonly Guid Original = Guid.NewGuid(), Later = Guid.NewGuid();
    private const string Mp1 = "571313179104682026", Mp2 = "571313179104952731";
    private static readonly DateTimeOffset Open = DateTimeOffset.Parse("9999-12-31T23:59:58.999Z");
    private static DeliveryStatusDto Status(string start,DeliveryStartReason reason,string? end = null,bool active = true) =>
        new() { InDelivery = active,StartReason = reason,Period = new() { Start = DateTimeOffset.Parse(start),End = end is null ? Open : DateTimeOffset.Parse(end) } };
    private static PayerForMeteringPointsDto Payer(Guid mp,Guid customer,DateOnly start,DateOnly? end = null) =>
        new() { Payer = new() { CustomerId = customer,IsPrimaryCustomerPayer = true },
            MeteringPoints = [new() { MeteringPointId = mp,Start = start,End = end }] };

    [Fact]
    public async Task First_supplier_change_keeps_original_customer_after_later_move_in_and_uses_each_points_own_date()
    {
        var points = new Dictionary<string,Guid> { [Mp1] = Guid.NewGuid(),[Mp2] = Guid.NewGuid() };
        var histories = new Dictionary<Guid,GetDeliveryStatusesResponse>
        {
            [points[Mp1]] = new() { DeliveryStatuses = [
                Status("2026-08-31T22:00:00Z",DeliveryStartReason.ChangeOfSupplier,"2026-09-27T22:00:00Z"),
                Status("2026-09-27T22:00:00Z",DeliveryStartReason.MoveIn)] },
            [points[Mp2]] = new() { DeliveryStatuses = [Status("2026-09-17T22:00:00Z",DeliveryStartReason.ChangeOfSupplier)] }
        };
        var historyCalls = new List<Guid>();
        var payerDates = new List<DateTime>();
        var result = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(points,(id,_) =>
        {
            lock (historyCalls) historyCalls.Add(id);
            return Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((histories[id],null));
        },(ids,instant,_) =>
        {
            payerDates.Add(instant);
            Assert.All(ids,id => Assert.Contains(id,points.Values));
            return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>(([
                Payer(points[Mp1],Original,new(2026,9,1),new(2026,9,28)),
                Payer(points[Mp1],Later,new(2026,9,28)),Payer(points[Mp2],Original,new(2026,9,18))],null));
        });
        Assert.Equal(new DateOnly(2026,9,1),result.DeliveryStartByMeteringPoint[Mp1]);
        Assert.Equal(new DateOnly(2026,9,18),result.DeliveryStartByMeteringPoint[Mp2]);
        Assert.Equal(Original,result.CustomerByMeteringPoint[Mp1]);
        Assert.Equal(Original,result.CustomerByMeteringPoint[Mp2]);
        Assert.Equal(2,historyCalls.Count);
        Assert.Equal(4,payerDates.Count);
        Assert.All(payerDates,date => Assert.True(date < new DateTime(2026,9,28)));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(DeliveryStartReason.MoveIn,SettlementConfigUnresolvedReason.FirstDeliveryIsMoveIn)]
    [InlineData(DeliveryStartReason.Unknown,SettlementConfigUnresolvedReason.FirstDeliveryStartReasonUnknown)]
    [InlineData(DeliveryStartReason.Na,SettlementConfigUnresolvedReason.FirstDeliveryNotChangeOfSupplier)]
    public async Task Later_supplier_change_cannot_replace_an_unverified_or_move_in_origin(DeliveryStartReason firstReason,SettlementConfigUnresolvedReason expected)
    {
        var result = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(new Dictionary<string,Guid> { [Mp1] = Guid.NewGuid() },
            (_,_) => Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((new() { DeliveryStatuses = [
                Status("2026-08-31T22:00:00Z",firstReason,"2026-09-27T22:00:00Z"),
                Status("2026-09-27T22:00:00Z",DeliveryStartReason.ChangeOfSupplier)] },null)),
            (_,_,_) => throw new InvalidOperationException("No guessed payer date may be read"));
        Assert.Equal(expected,Assert.Single(result.Issues).Reason);
        Assert.Empty(result.DeliveryStartByMeteringPoint);
        Assert.Empty(result.CustomerByMeteringPoint);
        Assert.DoesNotContain(expected.ToString(),SettlementConfigCoverage.Explain(expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_failed_history_does_not_fall_back_to_current_data(bool failed)
    {
        var result = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(new Dictionary<string,Guid> { [Mp1] = Guid.NewGuid() },
            (_,_) => Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((null,failed ? "unavailable" : null)),
            (_,_,_) => throw new InvalidOperationException("No guessed payer date may be read"));
        Assert.Equal(failed ? SettlementConfigUnresolvedReason.DeliveryHistoryReadFailed : SettlementConfigUnresolvedReason.DeliveryHistoryMissing,Assert.Single(result.Issues).Reason);
        Assert.Empty(result.CustomerByMeteringPoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_first_reasons_and_overlapping_duplicate_periods_remain_ambiguous(bool sameReason)
    {
        var result = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(new Dictionary<string,Guid> { [Mp1] = Guid.NewGuid() },
            (_,_) => Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((new() { DeliveryStatuses = [
                Status("2026-08-31T22:00:00Z",DeliveryStartReason.ChangeOfSupplier),
                Status("2026-08-31T22:00:00Z",sameReason ? DeliveryStartReason.ChangeOfSupplier : DeliveryStartReason.MoveIn)] },null)),
            (_,_,_) => Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>(([],null)));
        Assert.Equal(SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous,Assert.Single(result.Issues).Reason);
        Assert.Empty(result.DeliveryStartByMeteringPoint);
    }
}
