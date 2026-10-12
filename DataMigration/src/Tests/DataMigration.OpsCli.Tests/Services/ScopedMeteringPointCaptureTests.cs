using System.Net;
using System.Text;
using System.Text.Json;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Tests.Services;

public class ScopedMeteringPointCaptureTests
{
    private const string First = "571313104402063390", Second = "571313104402063437";

    [Fact]
    public async Task Scoped_loader_uses_only_selected_identifications_and_preserves_Danish_dates()
    {
        var calls = new List<string>();
        using var handler = new Handler(request =>
        {
            var uri = request.RequestUri!;
            Assert.Contains("MeteringPointIdentification=", uri.Query);
            var target = uri.Query.Split("MeteringPointIdentification=")[1].Split('&')[0];
            Assert.Contains(target, new[] { First, Second });
            calls.Add(target);
            return JsonSerializer.Serialize(new { Items = new[] { new MeteringPointInfoDto
            {
                Id = Guid.NewGuid(), MeteringPointIdentification = target,
                InDelivery = target == First,
                CurrentOrNextDeliveryPeriod = target == First ? new TimePeriodDto { Start = new(2026, 8, 31, 22, 0, 0, TimeSpan.Zero) } : null,
                MostRecentCompletedDeliveryPeriod = target == Second ? new TimePeriodDto { Start = new(2026, 9, 4, 22, 0, 0, TimeSpan.Zero) } : null
            } } });
        });
        using var http = new HttpClient(handler);
        var loader = new SettlMeteringPointCosLoader(Client(http));
        var rows = await loader.LoadScopedAsync([First, Second, First], default, includeNotInDelivery: true);
        Assert.Equal(new[] { First, Second }, calls);
        Assert.Equal(new DateOnly(2026, 9, 1), rows[First].Cos);
        Assert.Equal(new DateOnly(2026, 9, 5), rows[Second].Cos);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("no-date")]
    [InlineData("error")]
    public async Task Selected_point_failures_are_explicit(string failure)
    {
        using var handler = new Handler(_ => failure switch
        {
            "missing" => "{\"items\":[]}",
            "error" => "HTTP_ERROR",
            _ => JsonSerializer.Serialize(new { Items = new[] { new MeteringPointInfoDto
            {
                Id = Guid.NewGuid(), MeteringPointIdentification = failure == "mismatch" ? Second : First,
                InDelivery = true
            } } })
        });
        using var http = new HttpClient(handler);
        var loader = new SettlMeteringPointCosLoader(Client(http));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadScopedAsync([First], default, includeNotInDelivery: true));
        Assert.Contains("capture remains incomplete", ex.Message);
    }

    [Fact]
    public async Task Scoped_capture_calls_histories_only_for_selected_resolved_guids()
    {
        var id = Guid.NewGuid(); var historyCalls = new List<Guid>();
        using var handler = new Handler(r =>
        {
            var uri = r.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/deliveryStatuses"))
            {
                historyCalls.Add(Guid.Parse(uri.AbsolutePath.Split('/')[^2]));
                return JsonSerializer.Serialize(new GetDeliveryStatusesResponse { DeliveryStatuses = [] });
            }
            if (uri.AbsolutePath.Contains("api/v3/meteringPoints"))
            {
                Assert.Contains("MeteringPointIdentification=" + First, uri.Query);
                return JsonSerializer.Serialize(new { Items = new[] { new MeteringPointInfoDto { Id = id, MeteringPointIdentification = First, InDelivery = false } } });
            }
            Assert.True(uri.AbsolutePath.EndsWith("/customers") || uri.AbsolutePath.EndsWith("/Payers") || uri.AbsolutePath.EndsWith("/Contracts", StringComparison.OrdinalIgnoreCase));
            return "{\"Items\":[]}";
        });
        using var http = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://offline.test" });
        var directory = Path.Combine(Path.GetTempPath(), "point-capture-" + Guid.NewGuid());
        try
        {
            var store = StateSourceSnapshotStore.Open(directory, "production", new([First], [First], [], []));
            await store.BeginUpdateAsync(default);
            await CustomerPayerRepairCapture.CaptureSettlAsync(store,
                new SettlCustomerClient(http, new Auth(), options, NullLogger<SettlCustomerClient>.Instance),
                new SettlPayerClient(http, new Auth(), options, NullLogger<SettlPayerClient>.Instance), Client(http), default, new SettlContractClient(http, new Auth(), options, NullLogger<SettlContractClient>.Instance));
            Assert.Equal(new[] { id }, historyCalls);
            var saved = Assert.Single(await store.ReadSectionAsync<RepairSettlEvidence>("repair-settl", default));
            Assert.Equal(First, Assert.Single(saved.Histories).MeteringPoint);
            Assert.Equal(First, Assert.Single(saved.MeteringPoints).Identification);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static SettlMeteringPointClient Client(HttpClient http) => new(http, new Auth(),
        Options.Create(new SettlApiOptions { BaseUrl = "https://offline.test" }), NullLogger<SettlMeteringPointClient>.Instance);
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
            { Content = new StringContent(body == "HTTP_ERROR" ? "test error" : body, Encoding.UTF8, "application/json") });
        }
    }
}
