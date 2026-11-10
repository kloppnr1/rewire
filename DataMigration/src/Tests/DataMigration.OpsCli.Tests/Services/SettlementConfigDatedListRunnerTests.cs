using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public sealed class SettlementConfigDatedListRunnerTests
{
    private const string Point = "571313179104682026", Missing = "571313179104952731", Unlisted = "571313179104952732";
    private static readonly DateOnly Cos = new(2026,9,1);

    [Theory]
    [InlineData("production","41589","941589")]
    [InlineData("production-vhel","41589","41589")]
    [InlineData("production","513713","513713")]
    public async Task List_uses_common_date_and_raw_account_without_Settl_or_merge_reads(string environment,string account,string expectedNumber)
    {
        var progress = new List<SettlementConfigProgress>();
        var output = Path.GetTempFileName();
        try
        {
            var runner = new SourceRunner(null,null,null,account,progress.Add);
            var options = Options(output,environment);
            options.MeteringPointIds = [Point,Missing,Point];
            Assert.Equal(0,await runner.RunAsync(options));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var root = json.RootElement;
            var selection = root.GetProperty("meteringPointSelection");
            Assert.Equal(3,selection.GetProperty("supplied").GetInt32());
            Assert.Equal(2,selection.GetProperty("unique").GetInt32());
            Assert.Equal(1,selection.GetProperty("duplicates").GetInt32());
            Assert.Equal("SuppliedChangeOfSupplierDate",selection.GetProperty("dateSource").GetString());
            Assert.Equal("XellentContractAtChangeOfSupplierMinusOne",selection.GetProperty("customerSource").GetString());
            Assert.Equal("2026-09-01",selection.GetProperty("changeOfSupplierDate").GetString());
            Assert.False(root.TryGetProperty("historicalSelection",out _));
            var rows = Coverage(root);
            Assert.Equal(2,rows.Length);
            var resolved = rows.Single(r => r.GetProperty("meteringPointIdentification").GetString() == Point);
            Assert.Equal(expectedNumber,resolved.GetProperty("customerNumber").GetString());
            Assert.Equal("Partner",resolved.GetProperty("proposedSegment").GetString());
            Assert.Equal("KvartalAC",resolved.GetProperty("proposedSchedule").GetString());
            Assert.Empty(resolved.GetProperty("problems").EnumerateArray());
            Assert.Equal("Selected account",Assert.Single(root.GetProperty("customerFacts").EnumerateArray()).GetProperty("name").GetString());
            var missing = rows.Single(r => r.GetProperty("meteringPointIdentification").GetString() == Missing);
            Assert.Equal("2026-09-01",missing.GetProperty("changeOfSupplierDate").GetString());
            Assert.Null(missing.GetProperty("customerNumber").GetString());
            Assert.NotEmpty(missing.GetProperty("problems").EnumerateArray());
            var review = Assert.Single(root.GetProperty("review").EnumerateArray());
            Assert.Equal("Skipped",review.GetProperty("currentSegmentStatus").GetString());
            Assert.Equal("Skipped",review.GetProperty("currentScheduleStatus").GetString());
            Assert.Equal(new SettlementConfigProgress("MeteringPointSelection",2,2,"meteringPoints"),progress[0]);
            Assert.DoesNotContain(progress,p => p.Stage is "MeteringPointInventory" or "CustomerDirectory" or "DeliveryHistory"
                or "FirstDeliveryHistory" or "HistoricalPayers" or "SettlementGroups" or "CurrentSegments" or "ExemptionMeteringPoints");
            var expected = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            expected.AsObject().Remove("generatedAt");
            progress.Clear();
            Assert.Equal(0,await runner.RunAsync(options));
            var repeated = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            repeated.AsObject().Remove("generatedAt");
            Assert.True(JsonNode.DeepEquals(expected,repeated));
            Assert.Equal(new SettlementConfigProgress("MeteringPointSelection",2,2,"meteringPoints"),progress[0]);
            Assert.Equal(0,await new SourceRunner(null,null,null,account).RunAsync(options));
            var unobserved = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            unobserved.AsObject().Remove("generatedAt");
            Assert.True(JsonNode.DeepEquals(expected,unobserved));
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [InlineData("Missing",false)]
    [InlineData("PointFailed",false)]
    [InlineData("WrongPoint",false)]
    [InlineData("BillingFailed",false)]
    [InlineData("PayerFailed",false)]
    [InlineData("CustomerFailed",false)]
    [InlineData("DifferentCustomer",false)]
    [InlineData("SameCustomer",true)]
    [InlineData("NoExemptions",false)]
    public async Task Optional_exemptions_use_targeted_reads_without_removing_Xellent_proposals(string scenario,bool changed)
    {
        var pointId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var calls = new ConcurrentBag<string>();
        var progress = new List<SettlementConfigProgress>();
        var points = Stub<ISettlMeteringPointClient>((method,args) =>
        {
            calls.Add(method);
            return method switch
            {
                nameof(ISettlMeteringPointClient.SearchByIdentificationAsync) => Search(),
                nameof(ISettlMeteringPointClient.GetBillingByIdAsync) => Billing(),
                _ => throw new InvalidOperationException("Unexpected Settl call: " + method)
            };
            Task<(MeteringPointInfoDto?,string?)> Search()
            {
                Assert.Equal(Point,args![0]);
                return Task.FromResult<(MeteringPointInfoDto?,string?)>(scenario switch {
                    "Missing" => (null,null),"PointFailed" => (null,"failed"),
                    _ => (new() { Id = pointId,MeteringPointIdentification = scenario == "WrongPoint" ? Unlisted : Point },null) });
            }
            Task<(BillingMeteringPointCurrentDto?,string?)> Billing()
            {
                Assert.Equal(pointId,args![0]);
                Assert.Null(args[1]);
                return Task.FromResult<(BillingMeteringPointCurrentDto?,string?)>(scenario == "BillingFailed" ? (null,"failed")
                    : (new() { Exemptions = scenario == "NoExemptions" ? [] : [new() { ExemptionPercentage = 100 }] },null));
            }
        });
        var customers = Stub<ISettlCustomerClient>((method,args) =>
        {
            calls.Add(method);
            Assert.Equal(nameof(ISettlCustomerClient.GetByIdAsync),method);
            Assert.Equal(customerId,args![0]);
            return Task.FromResult<(CustomerResponse?,string?)>(scenario == "CustomerFailed" ? (null,"failed") :
                (new() { Id = customerId,CustomerNumber = scenario == "DifferentCustomer" ? "600256" : "941589" },null));
        });
        var payers = Stub<ISettlPayerClient>((method,args) =>
        {
            calls.Add(method);
            Assert.Equal(nameof(ISettlPayerClient.FindPayersForMeteringPointsAsync),method);
            Assert.Equal(new[] { pointId },(IReadOnlyList<Guid>)args![0]!);
            Assert.True((DateTime)args[1]! > Cos.ToDateTime(TimeOnly.MinValue));
            return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>(scenario == "PayerFailed" ? (null,"failed") :
                ([new() { Payer = new() { CustomerId = customerId },MeteringPoints = [new() { MeteringPointId = pointId,
                    Start = new(2026,9,1),End = DateOnly.MaxValue }] }],null));
        });
        var output = Path.GetTempFileName();
        try
        {
            var options = Options(output,"production");
            options.WithExemptions = true;
            Assert.Equal(0,await new SourceRunner(points,customers,payers,"41589",progress.Add).RunAsync(options));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var row = Assert.Single(Coverage(json.RootElement));
            Assert.Equal("941589",row.GetProperty("customerNumber").GetString());
            Assert.Equal(changed ? "Erhverv_UG" : "Partner",row.GetProperty("proposedSegment").GetString());
            Assert.Equal("KvartalAC",row.GetProperty("proposedSchedule").GetString());
            Assert.Equal(1,calls.Count(c => c == nameof(ISettlMeteringPointClient.SearchByIdentificationAsync)));
            Assert.Equal(new SettlementConfigProgress("ExemptionMeteringPoints",1,1,"meteringPoints"),
                progress.Last(p => p.Stage == "ExemptionMeteringPoints"));
            Assert.DoesNotContain(row.GetProperty("problems").EnumerateArray(),p => p.GetString()!.Contains("Listed metering point not found"));
            if (scenario is "Missing" or "PointFailed" or "WrongPoint")
            {
                Assert.Single(calls);
                Assert.Contains(row.GetProperty("problems").EnumerateArray(),p => p.GetString()!.Contains("Could not read Settl billing exemptions"));
            }
            if (scenario == "CustomerFailed")
                Assert.Equal("ReadFailed",Assert.Single(json.RootElement.GetProperty("exemptionReview").GetProperty("meteringPoints").EnumerateArray())
                    .GetProperty("currentCustomerReadStatus").GetString());
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task Optional_reads_are_bounded_and_customer_lookups_are_cached_across_payer_batches()
    {
        var selected = Enumerable.Range(0,201).Select(i => (571313179104950000L + i).ToString()).Append(Point).ToArray();
        var pointIds = selected.ToDictionary(id => id,_ => Guid.NewGuid());
        var customerId = Guid.NewGuid();
        var pointCalls = new ConcurrentBag<string>();
        var customerCalls = 0;
        var active = 0;
        var maximum = 0;
        var sync = new object();
        var points = Stub<ISettlMeteringPointClient>((method,args) => method switch
        {
            nameof(ISettlMeteringPointClient.SearchByIdentificationAsync) => Search((string)args![0]!),
            nameof(ISettlMeteringPointClient.GetBillingByIdAsync) => Task.FromResult<(BillingMeteringPointCurrentDto?,string?)>((new(),null)),
            _ => throw new InvalidOperationException(method)
        });
        async Task<(MeteringPointInfoDto?,string?)> Search(string id)
        {
            pointCalls.Add(id);
            lock (sync) maximum = Math.Max(maximum,++active);
            try { await Task.Delay(5); return (new() { Id = pointIds[id],MeteringPointIdentification = id },null); }
            finally { lock (sync) active--; }
        }
        var customers = Stub<ISettlCustomerClient>((method,args) =>
        {
            Assert.Equal(nameof(ISettlCustomerClient.GetByIdAsync),method);
            Assert.Equal(customerId,args![0]);
            Interlocked.Increment(ref customerCalls);
            return Task.FromResult<(CustomerResponse?,string?)>((new() { Id = customerId,CustomerNumber = "513713" },null));
        });
        var payerCalls = 0;
        var payers = Stub<ISettlPayerClient>((method,args) =>
        {
            Assert.Equal(nameof(ISettlPayerClient.FindPayersForMeteringPointsAsync),method);
            var ids = (IReadOnlyList<Guid>)args![0]!;
            Assert.InRange(ids.Count,1,200);
            payerCalls++;
            return Task.FromResult<(List<PayerForMeteringPointsDto>?,string?)>(([new() {
                Payer = new() { CustomerId = customerId },MeteringPoints = ids.Select(id => new PayerMeteringPointDto {
                    MeteringPointId = id,Start = Cos,End = DateOnly.MaxValue }).ToList() }],null));
        });
        var output = Path.GetTempFileName();
        try
        {
            var options = Options(output,"production");
            options.MeteringPointIds = selected.Append(Point).ToArray();
            options.WithExemptions = true;
            Assert.Equal(0,await new SourceRunner(points,customers,payers,"513713").RunAsync(options));
            Assert.Equal(selected.Length,pointCalls.Count);
            Assert.Equal(selected.Length,pointCalls.Distinct().Count());
            Assert.InRange(maximum,2,16);
            Assert.Equal(2,payerCalls);
            Assert.Equal(1,customerCalls);
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task Missing_optional_clients_keep_Xellent_proposals_and_report_only_exemption_failure()
    {
        var output = Path.GetTempFileName();
        try
        {
            var options = Options(output,"production");
            options.WithExemptions = true;
            Assert.Equal(0,await new SourceRunner(null,null,null,"513713").RunAsync(options));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var row = Assert.Single(Coverage(json.RootElement));
            Assert.Equal("Partner",row.GetProperty("proposedSegment").GetString());
            Assert.Equal("KvartalAC",row.GetProperty("proposedSchedule").GetString());
            Assert.Contains(row.GetProperty("problems").EnumerateArray(),p => p.GetString()!.Contains("Could not read Settl billing exemptions"));
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [InlineData("FORB","Partner")]
    [InlineData("PROD","Erhverv_ProduktionE18_UG")]
    public async Task Missing_plan_does_not_discard_segment_and_E18_keeps_priority(string type,string expectedSegment)
    {
        var output = Path.GetTempFileName();
        try
        {
            Assert.Equal(0,await new SourceRunner(null,null,null,"513713",plan:"",type:type).RunAsync(Options(output,"production")));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var row = Assert.Single(Coverage(json.RootElement));
            Assert.Equal(expectedSegment,row.GetProperty("proposedSegment").GetString());
            Assert.Null(row.GetProperty("proposedSchedule").GetString());
            Assert.DoesNotContain(row.GetProperty("problems").EnumerateArray(),p => p.GetString()!.Contains("Historical customer"));
        }
        finally { File.Delete(output); }
    }
    [Theory]
    [InlineData("Ordinary business",null,null)]
    [InlineData("  hYbEl  sYd a/s ","Partner","HalvårBagud")]
    [InlineData("Hybel Syd","Partner","HalvårBagud")]
    [InlineData("Hybel Sjælland afdeling 2","Partner","HalvårBagud")]
    [InlineData("Hybel Sydvest",null,null)]
    public async Task Real_list_runner_groups_independent_values_before_configured_winners(string name,string? commonSegment,string? commonSchedule)
    {
        var output = Path.GetTempFileName();
        try
        {
            var runner = new SourceRunner(null,null,null,"513713",name:name,secondPlan:"Month",secondTemplate:"V_Business");
            var options = Options(output,"production");
            options.MeteringPointIds = [Point,Missing,Point];
            options.ScheduleMap = new Dictionary<string,string> { ["Kvartal"] = "KvartalAC",["Month"] = "Måned.",["HalfYear"] = "HalvårBagud" };
            options.SegmentMap = new Dictionary<string,SegmentRule> { ["V_Partner"] = SegmentRule.Uniform(["Partner"]),["V_Business"] = SegmentRule.Uniform(["Erhverv"]) };
            options.Config = new(options.ScheduleMap,options.SegmentMap,MultipleSegments:"Partner",
                ScheduleConflicts:[new(["KvartalAC","Måned."],"KvartalAC")],CustomerExceptions:
                [new("Hybel Syd*","Partner","HalvårBagud"),new("Hybel Sjælland*","Partner","HalvårBagud")]);
            Assert.Equal(0,await runner.RunAsync(options));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            var root = json.RootElement;
            Assert.False(root.TryGetProperty("meteringPointCoverage",out _));
            var customer = Assert.Single(root.GetProperty("customerResults").EnumerateArray());
            Assert.Equal("513713",customer.GetProperty("customerNumber").GetString());
            Assert.Equal(commonSegment,customer.GetProperty("proposedSegment").GetString());
            Assert.Equal(commonSchedule,customer.GetProperty("proposedSchedule").GetString());
            var points = customer.GetProperty("meteringPoints").EnumerateArray().ToArray();
            Assert.Equal(2,points.Length);
            Assert.Equal(2,points.Select(p => p.GetProperty("meteringPointIdentification").GetString()).Distinct().Count());
            Assert.DoesNotContain(points,p => p.GetProperty("meteringPointIdentification").GetString() == Unlisted);
            Assert.Equal(commonSegment ?? "Erhverv",points.Single(p => p.GetProperty("meteringPointIdentification").GetString() == Missing)
                .GetProperty("proposedSegment").GetString());
            Assert.Equal(commonSchedule ?? "Måned.",points.Single(p => p.GetProperty("meteringPointIdentification").GetString() == Missing)
                .GetProperty("proposedSchedule").GetString());
            Assert.Equal(0,root.GetProperty("totals").GetProperty("segmentsFromMultiple").GetInt32());
            Assert.Equal(0,root.GetProperty("totals").GetProperty("schedulesFromConflictTable").GetInt32());
            Assert.Equal(commonSegment,Assert.Single(root.GetProperty("review").EnumerateArray()).GetProperty("proposedSegment").GetString());
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task Real_runner_uses_reloaded_config_and_has_no_implicit_name_exception()
    {
        var output = Path.GetTempFileName();
        var configFile = Path.GetTempFileName();
        try
        {
            var runner = new SourceRunner(null,null,null,"513713",name:"Hybel Syd");
            var options = Options(output,"production");
            Assert.Equal(0,await runner.RunAsync(options));
            using (var old = JsonDocument.Parse(await File.ReadAllTextAsync(output)))
                Assert.Equal("KvartalAC",Assert.Single(old.RootElement.GetProperty("customerResults").EnumerateArray()).GetProperty("proposedSchedule").GetString());
            foreach (var target in new[] { "Partner","Erhverv" })
            {
                File.WriteAllText(configFile,"""
                    {"schedules":{"q":"KvartalAC","h":"HalvårBagud"},"segments":{"p":"Partner","e":"Erhverv"},"customerExceptions":
                    """ + $"[{{\"name\":\"Hybel Syd*\",\"segment\":\"{target}\",\"schedule\":\"HalvårBagud\"}}]}}");
                options.Config = SettlementConfigLoader.Load(configFile);
                Assert.Equal(0,await runner.RunAsync(options));
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
                var result = Assert.Single(json.RootElement.GetProperty("customerResults").EnumerateArray());
                Assert.Equal(target,result.GetProperty("proposedSegment").GetString());
                Assert.Equal("HalvårBagud",result.GetProperty("proposedSchedule").GetString());
                var evidence = Assert.Single(json.RootElement.GetProperty("namedCustomerExceptionEvidence").EnumerateArray());
                Assert.Equal("Hybel Syd*",evidence.GetProperty("matchedPattern").GetString());
                Assert.Equal("Partner",evidence.GetProperty("originalProposedSegment").GetString());
            }
            Assert.Equal(3,runner.ContractReads);
        }
        finally { File.Delete(output); File.Delete(configFile); }
    }

    [Fact]
    public async Task Invalid_programmatic_exception_config_is_rejected_before_contract_source_read()
    {
        var output = Path.GetTempFileName();
        try
        {
            var runner = new SourceRunner(null,null,null,"513713");
            var options = Options(output,"production");
            options.Config = new(options.ScheduleMap!,options.SegmentMap!,CustomerExceptions:[new("*","Partner","KvartalAC")]);
            Assert.Contains("customerExceptions",(await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(options))).Message);
            Assert.Equal(0,runner.ContractReads);
        }
        finally { File.Delete(output); }
    }

    private static JsonElement[] Coverage(JsonElement root)
    {
        Assert.False(root.TryGetProperty("meteringPointCoverage",out _));
        return root.GetProperty("customerResults").EnumerateArray().SelectMany(customer =>
            customer.GetProperty("meteringPoints").EnumerateArray().Select(point =>
            {
                var copy = JsonNode.Parse(point.GetRawText())!.AsObject();
                copy["customerNumber"] = customer.GetProperty("customerNumber").GetString();
                return JsonSerializer.SerializeToElement(copy);
            })).ToArray();
    }
    private static ExportSettlementConfigRunOptions Options(string output,string environment) => new()
    {
        OutputPath = output,Environment = environment,MeteringPointIds = [Point],ChangeOfSupplierDate = Cos,
        ScheduleMap = new Dictionary<string,string> { ["Kvartal"] = "KvartalAC" },
        SegmentMap = new Dictionary<string,SegmentRule> { ["V_Partner"] = SegmentRule.Uniform(["Partner"]) }
    };
    private sealed class SourceRunner(ISettlMeteringPointClient? points,ISettlCustomerClient? customers,ISettlPayerClient? payers,
        string account,Action<SettlementConfigProgress>? progress = null,string plan = "Kvartal",string type = "FORB",
        string name = "Selected account",string? secondPlan = null,string? secondTemplate = null)
        : ExportSettlementConfigRunner(points,Stub<IXellentDomainLoader>((method,args) =>
            {
                Assert.Equal(nameof(IXellentDomainLoader.ResolveRepresentativesAsync),method);
                Assert.Equal(new[] { account },(string[])args![0]!);
                Assert.True((bool)args[3]!);
                return Task.FromResult(new AccountRepresentativeResolution([account],0,
                    new Dictionary<string,string> { [account] = name },
                    new Dictionary<string,string> { [account] = "400000" },new Dictionary<string,AccountFingerprint>()));
            }),
            "unused",_ => { },customerClient:customers,payerClient:payers,progress:progress)
    {
        public int ContractReads { get; private set; }
        protected override Task<SettlementContractPartResolver.Result> ResolveContractPartsAsync(
            IReadOnlyDictionary<string,DateOnly> dates,string dataAreaId,CancellationToken ct)
        {
            ContractReads++;
            Assert.DoesNotContain(Unlisted,dates.Keys);
            Assert.All(dates.Values,date => Assert.Equal(Cos,date));
            var rows = new List<SettlementContractPartResolver.PartRow> { Row(Point),Row(Unlisted) };
            if (secondTemplate is not null) rows.Add(Row(Missing) with { BillingPlan = secondPlan!,Template = secondTemplate });
            var result = SettlementContractPartResolver.Fold(dates,rows,new HashSet<string> { Point,Missing,Unlisted });
            return Task.FromResult(result with { Resolution = result.Resolution with { MeterPointTypesByMpId =
                new Dictionary<string,SettlementMeterPointTypeEvidence> { [Point] = new(type,"Read"),[Missing] = new(type,"Read") } } });
        }
        private SettlementContractPartResolver.PartRow Row(string id) => new(id,account,"C1",new(2020,1,1),
            new(2026,8,31),plan,"V_Partner","FOR","A1","el-ekstern","P1","",new(2020,1,1),new(1900,1,1),0,"","Live",null,1);
        protected override Task<IReadOnlyDictionary<string,CustomerIdentityKind>> ResolveCustomerIdentitiesAsync(
            IReadOnlyCollection<string> accounts,string dataAreaId,CancellationToken ct)
        {
            Assert.Equal(new[] { account },accounts);
            return Task.FromResult<IReadOnlyDictionary<string,CustomerIdentityKind>>(accounts.ToDictionary(a => a,_ => CustomerIdentityKind.Cvr));
        }
    }
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
