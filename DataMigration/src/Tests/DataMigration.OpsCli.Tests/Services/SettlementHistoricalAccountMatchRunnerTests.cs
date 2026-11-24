using System.Reflection;
using System.Text.Json;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementHistoricalAccountMatchRunnerTests
{
    [Theory]
    [InlineData("513713", "513713", "400000", true)]
    [InlineData("513713", "513713", null, true)]
    [InlineData("513713", "900000", "513713", true)]
    [InlineData("513713", "900000", "400000", false)]
    [InlineData("513713", "900000", null, false)]
    [InlineData("941589", "41589", "400000", true)]
    public async Task Runner_accepts_dated_account_or_representative_but_withholds_a_real_mismatch(
        string customerNumber, string contractAccount, string? representative, bool matches)
    {
        const string point = "571313103103253741";
        var pointId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var progress = new List<SettlementConfigProgress>();
        var points = Stub<ISettlMeteringPointClient>((method, args) => method switch
        {
            nameof(ISettlMeteringPointClient.FindMeteringPointsAsync) =>
                Task.FromResult<(List<MeteringPointInfoDto>, int, string?)>(((int)args![1]! == 0
                    ? [new() { Id = pointId, MeteringPointIdentification = point, InDelivery = false }]
                    : [], 1, null)),
            nameof(ISettlMeteringPointClient.GetDeliveryStatusesAsync) =>
                Task.FromResult<(GetDeliveryStatusesResponse?, string?)>((new() { DeliveryStatuses = [
                    new() { InDelivery = true, StartReason = DeliveryStartReason.ChangeOfSupplier,
                        Period = new() { Start = DateTimeOffset.Parse("2026-08-31T22:00:00Z"),
                            End = DateTimeOffset.Parse("9999-12-31T23:59:58.999Z") } }] }, null)),
            _ => throw new InvalidOperationException(method)
        });
        var customers = Stub<ISettlCustomerClient>((method, _) => method switch
        {
            nameof(ISettlCustomerClient.FindCustomersAsync) => Task.FromResult<(List<CustomerInfoDto>, int, string?)>(([
                new() { Id = customerId, CustomerNumber = customerNumber }], 1, null)),
            nameof(ISettlCustomerClient.FindCustomerSegmentsAsync) => Task.FromResult<(List<CustomerSegmentDto>?, string?)>(([], null)),
            nameof(ISettlCustomerClient.GetRawByIdV3Async) => Task.FromResult<(string?, string?)>(representative is null
                ? (null,"read failed") : ("{\"CustomerSegmentId\":null}", null)),
            _ => throw new InvalidOperationException(method)
        });
        var payers = Stub<ISettlPayerClient>((method, args) =>
        {
            Assert.Equal(nameof(ISettlPayerClient.FindPayersForMeteringPointsAsync), method);
            Assert.Equal(new[] { pointId }, (IReadOnlyList<Guid>)args![0]!);
            return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>(([
                new() { Payer = new() { CustomerId = customerId }, MeteringPoints = [
                    new() { MeteringPointId = pointId, Start = new(2026, 9, 1), End = DateOnly.MaxValue }] }], null));
        });
        var loader = Stub<IXellentDomainLoader>((method, args) =>
        {
            Assert.Equal(nameof(IXellentDomainLoader.ResolveRepresentativesAsync), method);
            Assert.Equal(new[] { contractAccount }, (string[])args![0]!);
            return Task.FromResult(new AccountRepresentativeResolution([contractAccount], 0,
                new Dictionary<string, string>(), representative is null ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [contractAccount] = representative },
                new Dictionary<string, AccountFingerprint>()));
        });
        var groupId = Guid.NewGuid();
        var failedGroupId = Guid.NewGuid();
        var groups = Stub<ISettlSettlementGroupClient>((method,args) =>
        {
            Assert.Equal("SettlementGroups",progress[^1].Stage);
            return method switch
            {
                nameof(ISettlSettlementGroupClient.ListAllAsync) => Task.FromResult<(List<SettlementGroupInfoDto>?,string?)>(([
                    new() { Id = groupId,GroupName = "A",IsActive = true },
                    new() { Id = failedGroupId,GroupName = "B",IsActive = true }],null)),
                nameof(ISettlSettlementGroupClient.ListCustomerIdsAsync) => Task.FromResult<(HashSet<Guid>?,string?)>(
                    (Guid)args![0]! == failedGroupId ? (null,"read failed") : (new HashSet<Guid> { customerId },null)),
                _ => throw new InvalidOperationException(method)
            };
        });
        var runner = new SourceRunner(points, loader, customers, payers, point, contractAccount, groups, progress.Add);
        var output = Path.GetTempFileName();
        try
        {
            Assert.Equal(0, await runner.RunAsync(new()
            {
                OutputPath = output, Environment = "production",
                MeteringPointIds = null,
                ChangeOfSupplierDate = new DateOnly(2026, 9, 1),
                ScheduleMap = new Dictionary<string, string> { ["year"] = "HelårBagud" },
                SegmentMap = new Dictionary<string, SegmentRule> { ["V_Partner"] = SegmentRule.Uniform(["Partner"]) }
            }));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var row = Assert.Single(json.RootElement.GetProperty("meteringPointCoverage").EnumerateArray());
            Assert.Equal(point, row.GetProperty("meteringPointIdentification").GetString());
            Assert.Equal(customerNumber, row.GetProperty("customerNumber").GetString());
            Assert.Equal("2026-09-01", row.GetProperty("changeOfSupplierDate").GetString());
            var mismatch = json.RootElement.GetProperty("unresolved").EnumerateArray()
                .Any(r => r.GetProperty("reason").GetString() == "XellentCustomerMismatch");
            Assert.Equal(!matches, mismatch);
            if (matches)
            {
                Assert.Equal("Partner", row.GetProperty("proposedSegment").GetString());
                Assert.Equal("HelårBagud", row.GetProperty("proposedSchedule").GetString());
                Assert.DoesNotContain(row.GetProperty("problems").EnumerateArray(),
                    p => p.GetString()!.Contains("Xellent customer differs"));
            }
            else
            {
                Assert.Null(row.GetProperty("proposedSegment").GetString());
                Assert.Null(row.GetProperty("proposedSchedule").GetString());
            }
            Assert.Equal(new SettlementConfigProgress("CustomerDirectory",1,1,"customers"),
                progress.Last(p => p.Stage == "CustomerDirectory"));
            Assert.Equal(new[] { 0,0,1,2 },progress.Where(p => p.Stage == "SettlementGroups").Select(p => p.Done!.Value));
            Assert.Equal(new SettlementConfigProgress("SettlementGroups",2,2,"groups"),
                progress.Last(p => p.Stage == "SettlementGroups"));
            Assert.Equal(new SettlementConfigProgress("CurrentSegments",1,1,"customers"),
                progress.Last(p => p.Stage == "CurrentSegments"));
            Assert.All(progress.Where(p => p.Stage is "ContractParts" or "Representatives" or "CustomerIdentities" or "ChargingHistory"),
                p => { Assert.Null(p.Done); Assert.Null(p.Total); });
            Assert.Equal(new SettlementConfigProgress("JsonWriting",1,1,"files"),progress[^1]);
        }
        finally { File.Delete(output); }
    }

    private sealed class SourceRunner(ISettlMeteringPointClient points, IXellentDomainLoader loader,
        ISettlCustomerClient customers, ISettlPayerClient payers, string point, string account,
        ISettlSettlementGroupClient groups, Action<SettlementConfigProgress> progress)
        : ExportSettlementConfigRunner(points, loader, "unused", _ => { }, customerClient: customers, payerClient: payers,
            settlementGroupClient:groups, progress:progress)
    {
        protected override Task<SettlementContractPartResolver.Result> ResolveContractPartsAsync(
            IReadOnlyDictionary<string, DateOnly> dates, string dataAreaId, CancellationToken ct)
        {
            Assert.Equal(new DateOnly(2026, 9, 1), dates[point]);
            var result = SettlementContractPartResolver.Fold(dates, [new(point, account, "C1",
                new(2020, 1, 1), new(2026, 8, 31), "year", "V_Partner", "FOR", "A1", "el-ekstern",
                "P1", "", new(2020, 1, 1), new(1900, 1, 1), 0, "", "Live", null, 1)],
                new HashSet<string> { point });
            return Task.FromResult(result with { Resolution = result.Resolution with {
                MeterPointTypesByMpId = new Dictionary<string, SettlementMeterPointTypeEvidence> {
                    [point] = new("D01", "Read") } } });
        }

        protected override Task<IReadOnlyDictionary<string, CustomerIdentityKind>> ResolveCustomerIdentitiesAsync(
            IReadOnlyCollection<string> accounts, string dataAreaId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, CustomerIdentityKind>>(
                accounts.ToDictionary(a => a, _ => CustomerIdentityKind.Cvr));
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
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args);
    }
}
