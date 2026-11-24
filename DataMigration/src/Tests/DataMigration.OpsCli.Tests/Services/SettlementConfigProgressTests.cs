using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementConfigProgressTests
{
    [Fact]
    public async Task First_history_progress_counts_selected_points_and_payers_across_dates_without_cached_history_phases()
    {
        var points = Enumerable.Range(0,402).ToDictionary(i => "mp-" + i, _ => Guid.NewGuid());
        var dates = points.Values.Select((id,i) => (id,date: new DateOnly(2026,9,i < 201 ? 1 : 2)))
            .ToDictionary(x => x.id,x => x.date);
        var customer = Guid.NewGuid();
        var rejected = points["mp-401"];
        var historyCalls = 0;
        var payerCalls = 0;
        var progress = new List<SettlementConfigProgress>();
        Task<(GetDeliveryStatusesResponse?,string?)> ReadHistory(Guid id,CancellationToken _)
        {
            Interlocked.Increment(ref historyCalls);
            return Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((History(dates[id],
                id == rejected ? DeliveryStartReason.MoveIn : DeliveryStartReason.ChangeOfSupplier),null));
        }
        Task<(List<PayerForMeteringPointsDto>?,string?)> ReadPayers(IReadOnlyList<Guid> ids,DateTime _,CancellationToken __)
        {
            payerCalls++;
            Assert.DoesNotContain(rejected,ids);
            return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>((ids.Select(id => Payer(id,customer,dates[id])).ToList(),null));
        }
        var observed = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(points,ReadHistory,ReadPayers,
            progress:p =>
            {
                if (p.Stage == "HistoricalPayers")
                {
                    Assert.Equal(402,historyCalls);
                    Assert.Equal(0,payerCalls % 2); // Complete a point only after both boundary reads.
                }
                progress.Add(p);
            });
        Assert.Equal(402,historyCalls);
        Assert.Equal(6,payerCalls);
        Assert.Equal(Enumerable.Range(0,403),progress.Where(p => p.Stage == "FirstDeliveryHistory").Select(p => p.Done!.Value));
        Assert.All(progress.Where(p => p.Stage == "FirstDeliveryHistory"),p => Assert.Equal(402,p.Total));
        Assert.Equal(new[] { 0,200,201,401 },progress.Where(p => p.Stage == "HistoricalPayers").Select(p => p.Done!.Value));
        Assert.All(progress.Where(p => p.Stage == "HistoricalPayers"),p => Assert.Equal(401,p.Total));
        Assert.Equal(new[] { "FirstDeliveryHistory","HistoricalPayers" },progress.Select(p => p.Stage).Distinct());
        Assert.All(progress,p => Assert.Equal("meteringPoints",p.Unit));

        var unobserved = await HistoricalSettlementSource.ReadFirstSupplierChangesAsync(points,ReadHistory,ReadPayers);
        Assert.Equal(observed.DeliveryStartByMeteringPoint.OrderBy(k => k.Key),unobserved.DeliveryStartByMeteringPoint.OrderBy(k => k.Key));
        Assert.Equal(observed.CustomerByMeteringPoint.OrderBy(k => k.Key),unobserved.CustomerByMeteringPoint.OrderBy(k => k.Key));
        Assert.Equal(observed.Issues,unobserved.Issues);
        Assert.True(observed.IncompleteCustomers.SetEquals(unobserved.IncompleteCustomers));
    }

    [Fact]
    public async Task Explicit_date_history_counts_failed_reads_and_completes_payer_batches()
    {
        var points = Enumerable.Range(0,201).ToDictionary(i => "mp-" + i, _ => Guid.NewGuid());
        var failed = points["mp-200"];
        var progress = new List<SettlementConfigProgress>();
        var payerCalls = 0;
        var result = await HistoricalSettlementSource.ReadAsync(new(2026,9,1),points,
            (id,_) => id == failed ? throw new InvalidOperationException("failed history")
                : Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((History(new(2026,9,1)),null)),
            (_,_,_) =>
            {
                payerCalls++;
                return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>((null,"failed payer read"));
            },progress:progress.Add);
        Assert.Equal(4,payerCalls);
        Assert.Equal(Enumerable.Range(0,202),progress.Where(p => p.Stage == "HistoricalDeliveryHistory").Select(p => p.Done!.Value));
        Assert.Equal(new[] { 0,200,201 },progress.Where(p => p.Stage == "HistoricalPayers").Select(p => p.Done!.Value));
        Assert.All(progress,p => Assert.Equal(201,p.Total));
        Assert.Equal(201,result.Issues.Count);
    }

    [Fact]
    public async Task Current_payer_progress_counts_completed_batches_including_failures()
    {
        var points = Enumerable.Range(0,401).ToDictionary(i => "mp-" + i, _ => Guid.NewGuid());
        var progress = new List<SettlementConfigProgress>();
        var calls = 0;
        var result = await SettlementCurrentCustomerSource.ReadAsync(points,new Dictionary<Guid,string>(),
            (ids,_,_) =>
            {
                calls++;
                Assert.InRange(ids.Count,1,200);
                throw new InvalidOperationException("failed payer read");
            },new(2026,10,5,10,0,0,DateTimeKind.Utc),progress:progress.Add);
        Assert.Equal(3,calls);
        Assert.Equal(new[] { 0,200,400,401 },progress.Select(p => p.Done!.Value));
        Assert.All(progress,p => Assert.Equal(new SettlementConfigProgress("CurrentPayers",p.Done,401,"meteringPoints"),p));
        Assert.Equal(401,result.Count);
        Assert.All(result.Values,r => Assert.Equal("ReadFailed",r.ReadStatus));
    }

    private static GetDeliveryStatusesResponse History(DateOnly date,DeliveryStartReason reason = DeliveryStartReason.ChangeOfSupplier)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue),zone);
        return new() { DeliveryStatuses = [new() { InDelivery = true,StartReason = reason,
            Period = new() { Start = new DateTimeOffset(start),End = DateTimeOffset.Parse("9999-12-31T23:59:58.999Z") } }] };
    }

    private static PayerForMeteringPointsDto Payer(Guid point,Guid customer,DateOnly date) =>
        new() { Payer = new() { CustomerId = customer },MeteringPoints = [
            new() { MeteringPointId = point,Start = date,End = DateOnly.MaxValue }] };
}
