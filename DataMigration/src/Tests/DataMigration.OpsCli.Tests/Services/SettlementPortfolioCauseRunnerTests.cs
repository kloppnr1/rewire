using System.Reflection;
using System.Text.Json;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementPortfolioCauseRunnerTests
{
    private const string Good = "571313103190786504", Bad = "571313115139107820";

    [Theory]
    [InlineData(SettlementConfigUnresolvedReason.XellentCustomerMismatch)]
    [InlineData(SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous)]
    public async Task Matching_point_names_the_other_selected_blocker_and_both_proposals_remain_withheld(
        SettlementConfigUnresolvedReason reason)
    {
        var goodId = Guid.NewGuid();
        var badId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var badDate = reason is SettlementConfigUnresolvedReason.XellentCustomerMismatch
            or SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous ? new DateOnly(2026,9,1) : new DateOnly(2026,9,2);
        var points = Stub<ISettlMeteringPointClient>((method,args) => method switch
        {
            nameof(ISettlMeteringPointClient.FindMeteringPointsAsync) => Task.FromResult<(List<MeteringPointInfoDto>,int,string?)>(
                ((int)args![1]! == 0 ? [new() { Id = goodId,MeteringPointIdentification = Good },
                    new() { Id = badId,MeteringPointIdentification = Bad }] : [],2,null)),
            nameof(ISettlMeteringPointClient.GetDeliveryStatusesAsync) =>
                Task.FromResult<(GetDeliveryStatusesResponse?,string?)>((History((Guid)args![0]! == badId ? badDate : new(2026,9,1),
                    (Guid)args[0]! == badId && reason == SettlementConfigUnresolvedReason.DeliveryHistoryAmbiguous),null)),
            _ => throw new InvalidOperationException(method)
        });
        var customers = Stub<ISettlCustomerClient>((method,_) => method switch
        {
            nameof(ISettlCustomerClient.FindCustomersAsync) => Task.FromResult<(List<CustomerInfoDto>,int,string?)>(([
                new() { Id = customerId,CustomerNumber = "456037" },new() { Id = otherId,CustomerNumber = "500001" }],2,null)),
            nameof(ISettlCustomerClient.FindCustomerSegmentsAsync) => Task.FromResult<(List<CustomerSegmentDto>?,string?)>(([],null)),
            nameof(ISettlCustomerClient.GetRawByIdV3Async) => Task.FromResult<(string?,string?)>(("{\"CustomerSegmentId\":null}",null)),
            _ => throw new InvalidOperationException(method)
        });
        var payers = Stub<ISettlPayerClient>((method,args) =>
        {
            Assert.Equal(nameof(ISettlPayerClient.FindPayersForMeteringPointsAsync),method);
            var ids = (IReadOnlyList<Guid>)args![0]!;
            var at = (DateTime)args[1]!;
            var lastBadRead = ids.Contains(badId) && at > Start(badDate);
            if (lastBadRead && reason == SettlementConfigUnresolvedReason.HistoricalCustomerReadFailed)
                return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>((null,"read failed"));
            if (lastBadRead && reason == SettlementConfigUnresolvedReason.HistoricalCustomerMissing)
                return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>(([],null));
            var rows = ids.Select(id => Payer(id,customerId,id == badId ? badDate : new(2026,9,1))).ToList();
            if (ids.Contains(badId) && reason == SettlementConfigUnresolvedReason.HistoricalCustomerAmbiguous)
                rows.Add(Payer(badId,otherId,badDate));
            return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>((rows,null));
        });
        var loader = Stub<IXellentDomainLoader>((method,args) =>
        {
            Assert.Equal(nameof(IXellentDomainLoader.ResolveRepresentativesAsync),method);
            var accounts = (string[])args![0]!;
            return Task.FromResult(new AccountRepresentativeResolution(accounts.ToList(),0,new Dictionary<string,string>(),
                accounts.ToDictionary(a => a,a => a),new Dictionary<string,AccountFingerprint>()));
        });
        var runner = new SourceRunner(points,loader,customers,payers);
        var output = Path.GetTempFileName();
        try
        {
            Assert.Equal(0,await runner.RunAsync(new() { OutputPath = output,Environment = "production",ChangeOfSupplierDate = new(2026,9,1),
                ScheduleMap = new Dictionary<string,string> { ["Kvartal"] = "KvartalAC" },
                SegmentMap = new Dictionary<string,SegmentRule> { ["V_Partner"] = SegmentRule.Uniform(["Partner"]) } }));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var coverage = json.RootElement.GetProperty("meteringPointCoverage").EnumerateArray().ToDictionary(
                r => r.GetProperty("meteringPointIdentification").GetString()!);
            Assert.Equal(2,coverage.Count);
            var good = coverage[Good];
            Assert.Equal("456037",good.GetProperty("customerNumber").GetString());
            Assert.Equal("2026-09-01",good.GetProperty("changeOfSupplierDate").GetString());
            Assert.Null(good.GetProperty("proposedSegment").GetString());
            Assert.Null(good.GetProperty("proposedSchedule").GetString());
            var problems = good.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
            Assert.Contains($"Customer proposals are blocked by metering point {Bad}: {SettlementConfigCoverage.Explain(reason)}",problems);
            Assert.DoesNotContain("Historical customer information is incomplete",problems);
            if (reason != SettlementConfigUnresolvedReason.XellentCustomerMismatch)
                Assert.DoesNotContain(problems,p => p!.Contains("Xellent customer differs"));
            Assert.Contains(coverage[Bad].GetProperty("problems").EnumerateArray(),
                p => p.GetString() == SettlementConfigCoverage.Explain(reason));
            Assert.Empty(json.RootElement.GetProperty("schedules").EnumerateArray());
            Assert.Empty(json.RootElement.GetProperty("segments").EnumerateArray());
        }
        finally { File.Delete(output); }
    }

    private sealed class SourceRunner(ISettlMeteringPointClient points,IXellentDomainLoader loader,
        ISettlCustomerClient customers,ISettlPayerClient payers)
        : ExportSettlementConfigRunner(points,loader,"unused",_ => { },customerClient:customers,payerClient:payers)
    {
        protected override Task<SettlementContractPartResolver.Result> ResolveContractPartsAsync(
            IReadOnlyDictionary<string,DateOnly> dates,string dataAreaId,CancellationToken ct)
        {
            var result = SettlementContractPartResolver.Fold(dates,dates.Keys.Select(id => new SettlementContractPartResolver.PartRow(
                id,id == Good ? "456037" : "489733",id == Good ? "C1" : "C2",new(2020,1,1),new(2026,8,31),
                "Kvartal","V_Partner","FOR","A1","el-ekstern","P1","",new(2020,1,1),new(1900,1,1),0,"","Live",null,1)).ToList(),
                dates.Keys.ToHashSet());
            return Task.FromResult(result with { Resolution = result.Resolution with { MeterPointTypesByMpId =
                dates.Keys.ToDictionary(id => id,_ => new SettlementMeterPointTypeEvidence("FORB","Read")) } });
        }
        protected override Task<IReadOnlyDictionary<string,CustomerIdentityKind>> ResolveCustomerIdentitiesAsync(
            IReadOnlyCollection<string> accounts,string dataAreaId,CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string,CustomerIdentityKind>>(accounts.ToDictionary(a => a,_ => CustomerIdentityKind.Cvr));
    }
    private static DateTime Start(DateOnly date) => TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue),
        TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"));
    private static GetDeliveryStatusesResponse History(DateOnly date,bool ambiguous)
    {
        var status = new DeliveryStatusDto { InDelivery = true,StartReason = DeliveryStartReason.ChangeOfSupplier,
            Period = new() { Start = new DateTimeOffset(Start(date)),End = DateTimeOffset.Parse("9999-12-31T23:59:58.999Z") } };
        return new() { DeliveryStatuses = ambiguous ? [status,status] : [status] };
    }
    private static PayerForMeteringPointsDto Payer(Guid point,Guid customer,DateOnly date) =>
        new() { Payer = new() { CustomerId = customer },MeteringPoints = [new() { MeteringPointId = point,Start = date,End = DateOnly.MaxValue }] };
    private static T Stub<T>(Func<string,object?[]?,object?> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T,Proxy>();
        ((Proxy)(object)proxy).Call = invoke;
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<string,object?[]?,object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args) => Call(method!.Name,args);
    }
}
