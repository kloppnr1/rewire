using System.Net;
using System.Text;
using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Tests.Services;

public class ScopedSettlContractCaptureTests
{
    private const string Mp1 = "571313104402063390", Mp2 = "571313104402063437", UnrelatedMp = "571313104402364466";
    private static readonly Guid MpId1 = Guid.NewGuid(), MpId2 = Guid.NewGuid(), ContractId = Guid.NewGuid(), OtherContractId = Guid.NewGuid(), LinkId = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 9, 4, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Scoped_capture_uses_mp_guids_deduplicates_contracts_and_preserves_ended_paginated_links()
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!; calls.Add(uri.PathAndQuery);
            if (uri.AbsolutePath.Contains("api/v3/meteringPoints")) return PointResponse(uri);
            if (uri.AbsolutePath.EndsWith("/Contracts"))
            {
                Assert.Contains("MeteringPointId=", uri.Query);
                Assert.DoesNotContain("CommodityType=", uri.Query); // No global inventory call.
                Assert.Contains(Query(uri, "MeteringPointId"), new[] { MpId1.ToString(), MpId2.ToString() });
                return Json(new { Items = new[] { Contract(), Contract() } });
            }
            Assert.Contains(ContractId.ToString(), uri.AbsolutePath);
            Assert.DoesNotContain(OtherContractId.ToString(), uri.AbsolutePath);
            if (!uri.Query.Contains("Skip=")) return Json(new { Items = new[] { Link(MpId1, Mp1, LinkId) }, NextUri = $"https://offline.test/internal-billing/api/v1/contracts/{ContractId}/meteringPoints?Skip=1" });
            return Json(new { Items = new[] { Link(MpId2, Mp2, Guid.NewGuid()), Link(Guid.NewGuid(), UnrelatedMp, Guid.NewGuid()) } });
        });
        var (points, contracts, http) = Clients(handler);
        using (http)
        {
            var rows = await ScopedSettlContractCapture.ReadAsync(Scope(Mp1, Mp2, Mp1), points, contracts, default);
            Assert.Equal(2, rows.Count);
            var first = rows.Single(r => r.MeteringPoint == Mp1);
            Assert.Equal(Start, first.Start); Assert.Equal(End, first.End); Assert.Equal(LinkId, first.AssociationId);
            Assert.All(rows, r => Assert.Equal(ContractId, r.ContractId));
            Assert.DoesNotContain(rows, r => r.MeteringPoint == UnrelatedMp);
        }
        Assert.Equal(2, calls.Count(c => c.Contains("api/v3/meteringPoints")));
        Assert.Equal(2, calls.Count(c => c.Contains("/Contracts?")));
        Assert.Equal(2, calls.Count(c => c.Contains($"/contracts/{ContractId}/meteringPoints"))); // One SDK call, two HTTP pages.
    }

    [Fact]
    public async Task Empty_contract_result_is_valid_and_does_not_scan_global_contracts()
    {
        using var handler = new Handler(r => r.RequestUri!.AbsolutePath.Contains("api/v3") ? PointResponse(r.RequestUri!)
            : r.RequestUri.AbsolutePath.EndsWith("/Contracts") && r.RequestUri.Query.Contains("MeteringPointId=")
                ? "{\"Items\":[]}" : throw new InvalidOperationException("Unexpected unrelated API call"));
        var (points, contracts, http) = Clients(handler);
        using (http) Assert.Empty(await ScopedSettlContractCapture.ReadAsync(Scope(Mp1), points, contracts, default));
    }

    [Theory]
    [InlineData("missing-point")]
    [InlineData("mismatched-point")]
    [InlineData("point-error")]
    [InlineData("contract-error")]
    [InlineData("links-error")]
    [InlineData("missing-links")]
    [InlineData("missing-commodity")]
    [InlineData("mismatched-link-guid")]
    public async Task Failed_or_inconsistent_lookup_does_not_write_completed_link_section(string failure)
    {
        using var handler = new Handler(r =>
        {
            var uri = r.RequestUri!;
            if (uri.AbsolutePath.Contains("api/v3")) return failure switch
            {
                "missing-point" => "{\"Items\":[]}",
                "mismatched-point" => Json(new { Items = new[] { new { Id = MpId1, MeteringPointIdentification = Mp2 } } }),
                "point-error" => "HTTP_ERROR", _ => PointResponse(uri)
            };
            if (uri.AbsolutePath.EndsWith("/Contracts")) return failure switch
            {
                "contract-error" => "HTTP_ERROR",
                "missing-commodity" => Json(new { Items = new[] { new { Id = ContractId, ContractName = "Test" } } }),
                _ => Json(new { Items = new[] { Contract() } })
            };
            return failure switch
            {
                "links-error" => "HTTP_ERROR", "missing-links" => "{\"Items\":[]}",
                "mismatched-link-guid" => Json(new { Items = new[] { Link(Guid.NewGuid(), Mp1, LinkId) } }),
                _ => throw new InvalidOperationException("Unexpected call")
            };
        });
        var (points, contracts, http) = Clients(handler);
        var directory = Path.Combine(Path.GetTempPath(), "scoped-contract-" + Guid.NewGuid());
        try
        {
            using (http)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => new PullContractSourcesRunner(contracts, points)
                    .PullAsync("production", "hol", "NO DATABASE ACCESS", directory, "settl", scope: Scope(Mp1)));
                var store = StateSourceSnapshotStore.Open(directory, "production", Scope(Mp1));
                await store.LoadManifestAsync(default, requireComplete: false);
                Assert.False(store.HasSection("settl-contract-links"));
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadManifestAsync(default));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Explicit_nonelectric_contracts_are_not_fetched()
    {
        using var handler = new Handler(r => r.RequestUri!.AbsolutePath.Contains("api/v3") ? PointResponse(r.RequestUri!)
            : r.RequestUri.AbsolutePath.EndsWith("/Contracts") ? Json(new { Items = new[] { new { Id = OtherContractId, CommodityType = "Water" } } })
                : throw new InvalidOperationException("Non-electric contract links must not be fetched"));
        var (points, contracts, http) = Clients(handler);
        using (http) Assert.Empty(await ScopedSettlContractCapture.ReadAsync(Scope(Mp1), points, contracts, default));
    }

    [Fact]
    public async Task Unscoped_capture_keeps_global_electricity_inventory_and_original_constructor()
    {
        using var handler = new Handler(r =>
        {
            var uri = r.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/Contracts"))
            {
                Assert.Contains("CommodityType=Electricity", uri.Query);
                Assert.DoesNotContain("MeteringPointId=", uri.Query);
                return Json(new { Items = new[] { Contract() } });
            }
            Assert.Contains($"/contracts/{ContractId}/meteringPoints", uri.AbsolutePath);
            return Json(new { Items = new[] { Link(MpId1, Mp1, LinkId), Link(MpId2, Mp2, Guid.NewGuid()) } });
        });
        var (_, contracts, http) = Clients(handler);
        var directory = Path.Combine(Path.GetTempPath(), "all-contract-" + Guid.NewGuid());
        try
        {
            using (http) await new PullContractSourcesRunner(contracts).PullAsync("production", "hol", "NO DATABASE ACCESS", directory, "settl");
            var store = StateSourceSnapshotStore.Open(directory, "production");
            await store.LoadManifestAsync(default);
            Assert.Equal(2, (await store.ReadSectionAsync<SettlContractLinkRow>("settl-contract-links", default)).Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static StatePullScope Scope(params string[] targets) => new(targets, targets, [], []);
    private static ContractInfoDto Contract() => new() { Id = ContractId, ContractName = "Test electricity", CommodityType = "Electricity" };
    private static ContractMeteringPointInfoDto Link(Guid point, string identification, Guid id) => new() { Id = id, MeteringPointId = point, MeteringPointIdentification = identification, Start = Start, End = End };
    private static string PointResponse(Uri uri)
    {
        var target = Query(uri, "MeteringPointIdentification");
        Assert.Contains(target, new[] { Mp1, Mp2 });
        return Json(new { Items = new[] { new MeteringPointInfoDto { Id = target == Mp1 ? MpId1 : MpId2, MeteringPointIdentification = target } } });
    }
    private static string Query(Uri uri, string name) => Uri.UnescapeDataString(uri.Query.TrimStart('?').Split('&').Single(p => p.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    private static string Json(object value) => JsonSerializer.Serialize(value);
    private static (SettlMeteringPointClient, SettlContractClient, HttpClient) Clients(Handler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://offline.test" });
        return (new(http, new Auth(), options, NullLogger<SettlMeteringPointClient>.Instance), new(http, new Auth(), options, NullLogger<SettlContractClient>.Instance), http);
    }
    private sealed class Auth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("fake-token");
        public void InvalidateToken() { }
    }
    private sealed class Handler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = respond(request);
            return Task.FromResult(new HttpResponseMessage(body == "HTTP_ERROR" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                { Content = new StringContent(body == "HTTP_ERROR" ? "test failure" : body, Encoding.UTF8, "application/json") });
        }
    }
}
