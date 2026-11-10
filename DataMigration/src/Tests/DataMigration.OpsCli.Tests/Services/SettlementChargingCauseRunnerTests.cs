using System.Reflection;
using System.Text.Json;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementChargingCauseRunnerTests
{
    private const string Bad = "571313115139107820", Good = "571313103190786504";
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runner_keeps_customer_segment_blocking_and_names_only_actual_failed_charging_points(bool readException)
    {
        var client = Stub<ISettlMeteringPointClient>((method,args) =>
        {
            Assert.Equal(nameof(ISettlMeteringPointClient.FindMeteringPointsAsync),method);
            return Task.FromResult<(List<MeteringPointInfoDto>,int,string?)>(((int)args![1]! == 0 ? [
                new() { Id = Guid.NewGuid(),MeteringPointIdentification = Bad,InDelivery = true,
                    CurrentOrNextDeliveryPeriod = new() { Start = new(2026,8,31,22,0,0,TimeSpan.Zero) } },
                new() { Id = Guid.NewGuid(),MeteringPointIdentification = Good,InDelivery = true,
                    CurrentOrNextDeliveryPeriod = new() { Start = new(2026,7,31,22,0,0,TimeSpan.Zero) } }] : [],2,null));
        });
        var loader = Stub<IXellentDomainLoader>((method,args) =>
        {
            Assert.Equal(nameof(IXellentDomainLoader.ResolveRepresentativesAsync),method);
            Assert.Equal(new[] { "347772" },(string[])args![0]!);
            return Task.FromResult(new AccountRepresentativeResolution(["347772"],0,new Dictionary<string,string>(),
                new Dictionary<string,string> { ["347772"] = "347772" },new Dictionary<string,AccountFingerprint>()));
        });
        var output = Path.GetTempFileName();
        try
        {
            var runner = new SourceRunner(client,loader,readException);
            Assert.Equal(0,await runner.RunAsync(new() { OutputPath = output,Environment = "production",
                ScheduleMap = new Dictionary<string,string> { ["month"] = "Måned." },
                SegmentMap = new Dictionary<string,SegmentRule> { ["V_LadePriv"] = SegmentRule.Uniform(["Privat"]) } }));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var root = json.RootElement;
            var review = Assert.Single(root.GetProperty("review").EnumerateArray());
            var basis = review.GetProperty("xellent").EnumerateArray().ToDictionary(r => r.GetProperty("meteringPointIdentification").GetString()!);
            Assert.Equal(readException ? "ReadFailed" : "Ambiguous",basis[Bad].GetProperty("chargeReadStatus").GetString());
            Assert.Equal(readException ? "ReadFailed" : "Read",basis[Good].GetProperty("chargeReadStatus").GetString());
            Assert.Null(review.GetProperty("proposedSegment").GetString());
            Assert.Equal("Måned.",review.GetProperty("proposedSchedule").GetString());
            var coverage = root.GetProperty("meteringPointCoverage").EnumerateArray().ToDictionary(r => r.GetProperty("meteringPointIdentification").GetString()!);
            var cause = Assert.Single(coverage[Good].GetProperty("problems").EnumerateArray().Select(p => p.GetString()!),
                p => p.Contains(readException ? "Xellent Charge_Abo read failed" : "ambiguous historical charging contract parts"));
            Assert.Contains(Bad,cause);
            if (readException) Assert.Contains(Good,cause);
            else Assert.DoesNotContain(Good,cause);
            Assert.All(coverage.Values,r => Assert.Null(r.GetProperty("proposedSegment").GetString()));
            if (!readException) Assert.Equal("OAboLejMEL",Assert.Single(basis[Good].GetProperty("chargeProducts").EnumerateArray()).GetString());
        }
        finally { File.Delete(output); }
    }

    private sealed class SourceRunner(ISettlMeteringPointClient points,IXellentDomainLoader loader,bool fail)
        : ExportSettlementConfigRunner(points,loader,"unused",_ => { })
    {
        protected override Task<SettlementContractPartResolver.Result> ResolveContractPartsAsync(
            IReadOnlyDictionary<string,DateOnly> dates,string dataAreaId,CancellationToken ct) =>
            Task.FromResult(SettlementContractPartResolver.Fold(dates,dates.Select(kv =>
                new SettlementContractPartResolver.PartRow(kv.Key,"347772",kv.Key,new(2020,1,1),kv.Value.AddDays(-1).ToDateTime(TimeOnly.MinValue),
                    "month","V_LadePriv","FOR","A1","el-ekstern","P1","",new(2020,1,1),new(1900,1,1),0,"","Live",null,1)).ToArray(),dates.Keys.ToHashSet()));
        protected override Task<IReadOnlyDictionary<string,CustomerIdentityKind>> ResolveCustomerIdentitiesAsync(
            IReadOnlyCollection<string> accounts,string dataAreaId,CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string,CustomerIdentityKind>>(accounts.ToDictionary(a => a,_ => CustomerIdentityKind.Cpr));
        protected override Task<SettlementChargingHistoryReader.Result> ReadChargingHistoryAsync(
            IReadOnlyCollection<(string Account,DateOnly Date)> requests,string dataAreaId,CancellationToken ct)
        {
            Assert.Equal(2,requests.Count);
            Assert.Contains(("347772",new DateOnly(2026,8,31)),requests);
            Assert.Contains(("347772",new DateOnly(2026,7,31)),requests);
            if (fail) throw new InvalidOperationException("Representative offline reader failure");
            var bad = new DateOnly(2026,8,31);
            var good = new DateOnly(2026,7,31);
            return Task.FromResult(new SettlementChargingHistoryReader.Result(
                new Dictionary<(string,DateOnly),IReadOnlyList<string>> { [("347772",good)] = ["OAboLejMEL"] },
                [new("347772",bad,"C1","FOR","A1","OAboLejMEL",new(2020,1,1),new(1900,1,1),"ContractPartLog",null,null,true)]));
        }
    }
    private static T Stub<T>(Func<string,object?[]?,object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T,Proxy>();
        ((Proxy)(object)proxy).Call = call;
        return proxy;
    }
    public class Proxy : DispatchProxy
    {
        public Func<string,object?[]?,object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method,object?[]? args) => Call(method!.Name,args);
    }
}
