using System.Reflection;
using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class HistoricalSettlementRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_reports_history_failure_for_currently_ended_or_active_MP_without_SQL_or_current_owner_fallback(bool currentInDelivery)
    {
        var mpId = Guid.NewGuid();
        var pageCalls = new List<int>();
        var historyCalls = 0;
        var mpClient = Stub<ISettlMeteringPointClient>((method, args) => method switch
        {
            nameof(ISettlMeteringPointClient.FindMeteringPointsAsync) => Page((int)args![1]!),
            nameof(ISettlMeteringPointClient.GetDeliveryStatusesAsync) => History(),
            _ => throw new InvalidOperationException(method)
        });
        Task<(List<MeteringPointInfoDto>, int, string?)> Page(int skip)
        {
            pageCalls.Add(skip);
            // Unknown total and server-capped page. The runner must continue after a short page.
            return Task.FromResult<(List<MeteringPointInfoDto>, int, string?)>((skip == 0
                ? [new() { Id = mpId, MeteringPointIdentification = "571313115200421466", InDelivery = currentInDelivery }]
                : [], 0, null));
        }
        Task<(GetDeliveryStatusesResponse?, string?)> History()
        {
            historyCalls++;
            return Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((null, "unavailable"));
        }
        var loader = Stub<IXellentDomainLoader>((method, _) => throw new InvalidOperationException($"No Xellent reads expected: {method}"));
        var output = Path.GetTempFileName();
        try
        {
            var runner = new ExportSettlementConfigRunner(mpClient, loader, "not-a-connection", _ => { });
            var code = await runner.RunAsync(new() { OutputPath = output, ChangeOfSupplierDate = new(2026, 9, 1) });
            Assert.Equal(0, code);
            Assert.Equal(1, historyCalls);
            Assert.Equal(new[] { 0, 1 }, pageCalls);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var selection = document.RootElement.GetProperty("historicalSelection");
            Assert.Equal("2026-09-01", selection.GetProperty("asOfDate").GetString());
            Assert.Equal("DeliveryHistoryReadFailed", selection.GetProperty("issues")[0].GetProperty("reason").GetString());
            Assert.Equal("DeliveryHistoryReadFailed", document.RootElement.GetProperty("unresolved")[0].GetProperty("reason").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("meteringPointsInSettl").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("totals").GetProperty("unresolved").GetInt32());
            Assert.Empty(document.RootElement.GetProperty("schedules").EnumerateArray());
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task Historical_exemption_reads_use_current_API_and_keep_failures_separate()
    {
        var ids = new Dictionary<string, Guid> { ["empty"] = Guid.NewGuid(), ["exempt"] = Guid.NewGuid(), ["failed"] = Guid.NewGuid() };
        var calls = 0;
        var mpClient = Stub<ISettlMeteringPointClient>((method, args) =>
        {
            Assert.Equal(nameof(ISettlMeteringPointClient.GetBillingByIdAsync), method);
            Assert.Null(args![1]); // Read current exemptions; EffectiveDate does not date-filter them.
            Interlocked.Increment(ref calls);
            if ((Guid)args[0]! == ids["failed"]) return Task.FromResult<(BillingMeteringPointCurrentDto?, string?)>((null, "unavailable"));
            return Task.FromResult<(BillingMeteringPointCurrentDto?, string?)>((new() {
                Exemptions = (Guid)args[0]! == ids["exempt"] ? [new() { ExemptionPercentage = 100 }] : [] }, null));
        });
        var loader = Stub<IXellentDomainLoader>((method, _) => throw new InvalidOperationException(method));
        var progress = new List<SettlementConfigProgress>();
        var runner = new ExportSettlementConfigRunner(mpClient, loader, "unused", _ => { }, progress:progress.Add);
        var method = typeof(ExportSettlementConfigRunner).GetMethod("LoadExemptionsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var options = new ExportSettlementConfigRunOptions { WithExemptions = true, ChangeOfSupplierDate = new(2026, 9, 1) };
        var task = (Task<IReadOnlyList<SettlementConfigExemptionRead>?>)method.Invoke(runner, [options, ids, CancellationToken.None])!;
        var reads = (await task)!.ToDictionary(r => r.MeteringPointIdentification);
        Assert.Equal(3, calls);
        Assert.Equal("Read", reads["empty"].ReadStatus);
        Assert.Empty(reads["empty"].CurrentExemptions);
        Assert.Equal("ReadFailed", reads["failed"].ReadStatus);
        Assert.Single(reads["exempt"].CurrentExemptions);
        Assert.All(reads.Values, r => Assert.Equal("NotVerified", r.HistoricalValidity));
        Assert.Equal(new[] { 0,1,2,3 },progress.Select(p => p.Done!.Value));
        Assert.All(progress,p => Assert.Equal(new SettlementConfigProgress("Exemptions",p.Done,3,"meteringPoints"),p));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Date_only_runner_keeps_September_origin_and_original_customer_after_September_28_move_in(bool currentlyInDelivery)
    {
        const string point = "571313179104682026";
        var pointId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        var laterId = Guid.NewGuid();
        var payerCalls = new List<DateTime>();
        var historyCalls = new List<Guid>();
        var mpClient = Stub<ISettlMeteringPointClient>((method,args) => method switch
        {
            nameof(ISettlMeteringPointClient.FindMeteringPointsAsync) => Task.FromResult<(List<MeteringPointInfoDto>,int,string?)>(((int)args![1]! == 0 ? [
                new() { Id = pointId,MeteringPointIdentification = point,InDelivery = currentlyInDelivery,
                    CurrentOrNextDeliveryPeriod = currentlyInDelivery ? new() { Start = DateTimeOffset.Parse("2026-09-27T22:00:00Z") } : null }] : [],1,null)),
            nameof(ISettlMeteringPointClient.GetDeliveryStatusesAsync) => History((Guid)args![0]!),
            _ => throw new InvalidOperationException(method)
        });
        Task<(GetDeliveryStatusesResponse?,string?)> History(Guid id)
        {
            historyCalls.Add(id);
            Assert.Equal(pointId,id);
            return Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((new() { DeliveryStatuses = [
                new() { InDelivery = true,StartReason = DeliveryStartReason.ChangeOfSupplier,
                    Period = new() { Start = DateTimeOffset.Parse("2026-08-31T22:00:00Z"),End = DateTimeOffset.Parse("2026-09-27T22:00:00Z") } },
                new() { InDelivery = true,StartReason = DeliveryStartReason.MoveIn,
                    Period = new() { Start = DateTimeOffset.Parse("2026-09-27T22:00:00Z"),End = DateTimeOffset.Parse("9999-12-31T23:59:58.999Z") } }] },null));
        }
        var customers = Stub<ISettlCustomerClient>((method,_) => method switch
        {
            nameof(ISettlCustomerClient.FindCustomersAsync) => Task.FromResult<(List<CustomerInfoDto>,int,string?)>(([
                new() { Id = originalId,CustomerNumber = "101681" },new() { Id = laterId,CustomerNumber = "600256" }],2,null)),
            nameof(ISettlCustomerClient.FindCustomerSegmentsAsync) => Task.FromResult<(List<CustomerSegmentDto>?,string?)>(([],null)),
            nameof(ISettlCustomerClient.GetRawByIdV3Async) => Task.FromResult<(string?,string?)>(("{\"CustomerSegmentId\":null}",null)),
            _ => throw new InvalidOperationException(method)
        });
        var payers = Stub<ISettlPayerClient>((method,args) =>
        {
            Assert.Equal(nameof(ISettlPayerClient.FindPayersForMeteringPointsAsync),method);
            Assert.Equal(new[] { pointId },(IReadOnlyList<Guid>)args![0]!);
            var date = (DateTime)args[1]!;
            payerCalls.Add(date);
            Assert.True(date >= new DateTime(2026,8,31,22,0,0,DateTimeKind.Utc)
                && date < new DateTime(2026,9,1,22,0,0,DateTimeKind.Utc));
            return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>(([
                new() { Payer = new() { CustomerId = originalId,IsPrimaryCustomerPayer = true },MeteringPoints = [
                    new() { MeteringPointId = pointId,Start = new(2026,9,1),End = new(2026,9,28) }] },
                new() { Payer = new() { CustomerId = laterId,IsPrimaryCustomerPayer = true },MeteringPoints = [
                    new() { MeteringPointId = pointId,Start = new(2026,9,28),End = DateOnly.MaxValue }] }],null));
        });
        var loader = Stub<IXellentDomainLoader>((method,_) => throw new InvalidOperationException("No source accounts in this boundary regression: " + method));
        var runner = new SourceBoundaryRunner(mpClient,loader,customers,payers);
        var output = Path.GetTempFileName();
        try
        {
            Assert.Equal(0,await runner.RunAsync(new() { OutputPath = output,ChangeOfSupplierDate = new(2026,9,1),Environment = "production" }));
            Assert.Equal(new DateOnly(2026,9,1),runner.SourceDates![point]);
            Assert.Single(runner.SourceDates);
            Assert.Single(historyCalls);
            Assert.Equal(2,payerCalls.Count);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var row = Assert.Single(json.RootElement.GetProperty("meteringPointCoverage").EnumerateArray());
            Assert.Equal(point,row.GetProperty("meteringPointIdentification").GetString());
            Assert.Equal("2026-09-01",row.GetProperty("changeOfSupplierDate").GetString());
            Assert.Equal("101681",row.GetProperty("customerNumber").GetString());
            Assert.DoesNotContain(row.GetProperty("problems").EnumerateArray(),p => p.GetString()!.Contains("not in delivery"));
            Assert.Equal("SettlDeliveryStatuses",json.RootElement.GetProperty("historicalSelection").GetProperty("deliverySource").GetString());
        }
        finally { File.Delete(output); }
    }

    private sealed class SourceBoundaryRunner(ISettlMeteringPointClient points,IXellentDomainLoader loader,
        ISettlCustomerClient customers,ISettlPayerClient payers)
        : ExportSettlementConfigRunner(points,loader,"unused",_ => { },customerClient:customers,payerClient:payers)
    {
        public IReadOnlyDictionary<string,DateOnly>? SourceDates { get; private set; }
        protected override Task<SettlementContractPartResolver.Result> ResolveContractPartsAsync(
            IReadOnlyDictionary<string,DateOnly> dates,string dataAreaId,CancellationToken ct)
        {
            SourceDates = dates;
            // No matching electricity contract: still verify the real runner retains its dated customer/date.
            return Task.FromResult(SettlementContractPartResolver.Fold(dates,[],dates.Keys.ToHashSet()));
        }
    }

    private static T Stub<T>(Func<string, object?[]?, object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)proxy).Call = invoke;
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!.Name, args);
    }
}
