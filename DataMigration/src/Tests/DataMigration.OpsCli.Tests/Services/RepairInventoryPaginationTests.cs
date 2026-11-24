using System.Net;
using System.Text;
using System.Text.Json;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Tests.Services;

public class RepairInventoryPaginationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Real_sdk_clients_accept_missing_count_and_server_capped_pages(bool payer, bool zeroCount)
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var offsets = new List<int>();
        using var handler = new FakeHandler(request =>
        {
            var skip = Offset(request);
            offsets.Add(skip);
            var rows = ids.Skip(skip).Take(2).Select(id => new { Id = id }).ToArray();
            return zeroCount ? JsonSerializer.Serialize(new { Items = rows, CountResult = new { Count = 0 } })
                : JsonSerializer.Serialize(new { Items = rows });
        });
        var captured = await Read(payer, handler);
        Assert.Equal(ids, captured);
        Assert.Equal(new[] { 0, 2, 4, 5 }, offsets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Known_total_can_be_omitted_later_but_final_empty_page_is_required(bool payer)
    {
        var id = Guid.NewGuid();
        var offsets = new List<int>();
        using var handler = new FakeHandler(request =>
        {
            var skip = Offset(request); offsets.Add(skip);
            return skip == 0 ? JsonSerializer.Serialize(new { Items = new[] { new { Id = id } }, CountResult = new { Count = 1 } })
                : "{\"Items\":[]}";
        });
        Assert.Equal(new[] { id }, await Read(payer, handler));
        Assert.Equal(new[] { 0, 1 }, offsets);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("empty-before-total")]
    [InlineData("more-than-total")]
    [InlineData("repeated")]
    [InlineData("api-error")]
    public async Task Incomplete_or_unstable_pages_fail_with_nonpersonal_diagnostics(string failure)
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        using var handler = new FakeHandler(request =>
        {
            var skip = Offset(request);
            if (skip == 0) return JsonSerializer.Serialize(new { Items = new[] { new { Id = first } }, CountResult = new { Count = failure == "more-than-total" ? 1 : 2 } });
            return failure switch
            {
                "changed" => JsonSerializer.Serialize(new { Items = new[] { new { Id = second } }, CountResult = new { Count = 3 } }),
                "empty-before-total" => "{\"Items\":[]}",
                "more-than-total" => JsonSerializer.Serialize(new { Items = new[] { new { Id = second } } }),
                "repeated" => JsonSerializer.Serialize(new { Items = new[] { new { Id = first } } }),
                _ => "HTTP_ERROR"
            };
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Read(false, handler));
        Assert.Contains("customer inventory", ex.Message);
        Assert.Contains("offset=1", ex.Message);
        Assert.Contains("pageCount=", ex.Message);
        Assert.DoesNotContain(first.ToString(), ex.Message);
        Assert.DoesNotContain("PRIVATE API ERROR", ex.Message);
    }

    [Fact]
    public async Task Empty_inventory_and_cancelled_enumeration_are_distinct()
    {
        using var handler = new FakeHandler(_ => "{\"Items\":[]}");
        Assert.Empty(await Read(false, handler));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(false, handler, cancelled.Token));
    }

    [Fact]
    public async Task Settl_only_refresh_retains_completed_xellent_section_of_failed_capture()
    {
        var path = Path.Combine(Path.GetTempPath(), "repair-resume-" + Guid.NewGuid());
        try
        {
            var scope = new StatePullScope(["571313104402063390"], ["571313104402063390"], ["456192"], ["456192"]);
            var before = StateSourceSnapshotStore.Open(path, "production", scope);
            await before.BeginUpdateAsync(default);
            await before.WriteAsync("repair-xellent", new[] { "captured source evidence" }, default);
            // Deliberately leave the manifest incomplete, as a failed Settl phase does.
            var refresh = StateSourceSnapshotStore.Open(path, "production", scope);
            await refresh.BeginUpdateAsync(default);
            await refresh.InvalidateRepairAsync(xellent: false, settl: true, default);
            Assert.Equal(new[] { "captured source evidence" }, await refresh.ReadSectionAsync<string>("repair-xellent", default));
            await refresh.WriteAsync("repair-settl", new[] { "new Settl evidence" }, default);
            await refresh.CompleteAsync(default);
            await refresh.LoadManifestAsync(default);
            Assert.True(refresh.HasSection("repair-xellent"));
            Assert.True(refresh.HasSection("repair-settl"));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public void Phase_diagnostics_distinguish_completed_from_failed_work()
    {
        var lines = new List<string>();
        using (var done = new CaptureStage("customer inventory", lines.Add)) done.Complete("entities=2");
        using (new CaptureStage("payer inventory", lines.Add, () => "associationCalls=1")) { }
        Assert.Contains(lines, line => line.Contains("customer inventory: started at"));
        Assert.Contains(lines, line => line.Contains("customer inventory: completed at") && line.Contains("entities=2"));
        Assert.Contains(lines, line => line.Contains("payer inventory: stopped incomplete at") && line.Contains("associationCalls=1"));
    }

    private static async Task<List<Guid>> Read(bool payer, FakeHandler handler, CancellationToken ct = default)
    {
        using var http = new HttpClient(handler, disposeHandler: false);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://offline.test" });
        var auth = new FakeAuth();
        var result = new List<Guid>();
        if (payer)
        {
            var client = new SettlPayerClient(http, auth, options, NullLogger<SettlPayerClient>.Instance);
            await foreach (var page in CustomerPayerRepairCapture.InventoryPagesAsync("payer", client.FindPayersAsync, p => p.Id, ct: ct)) result.AddRange(page.Select(p => p.Id));
        }
        else
        {
            var client = new SettlCustomerClient(http, auth, options, NullLogger<SettlCustomerClient>.Instance);
            await foreach (var page in CustomerPayerRepairCapture.InventoryPagesAsync("customer", client.FindCustomersAsync, p => p.Id, ct: ct)) result.AddRange(page.Select(p => p.Id));
        }
        return result;
    }

    private static int Offset(HttpRequestMessage request) => int.Parse(request.RequestUri!.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("Skip=", StringComparison.Ordinal))[5..]);
    private sealed class FakeAuth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("fake-token");
        public void InvalidateToken() { }
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = response(request);
            return Task.FromResult(new HttpResponseMessage(body == "HTTP_ERROR" ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                { Content = new StringContent(body == "HTTP_ERROR" ? "PRIVATE API ERROR" : body, Encoding.UTF8, "application/json") });
        }
    }
}
