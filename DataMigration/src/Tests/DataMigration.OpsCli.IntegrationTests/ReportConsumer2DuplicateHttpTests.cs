using System.Net;
using System.Text;
using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>Exercises the installed Settl client mapping offline; every request must be GET.</summary>
public class ReportConsumer2DuplicateHttpTests
{
    [Fact]
    public async Task Typed_http_mapping_reports_inactive_duplicates_and_omits_raw_cpr()
    {
        var id = Guid.NewGuid();
        using var handler = new ReportHandler(id);
        using var http = new HttpClient(handler);
        var client = new SettlMeteringPointClient(http, new OfflineAuth(),
            Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid", SubscriptionKey = "fixture" }),
            NullLogger<SettlMeteringPointClient>.Instance);
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client).RunAsync("featuretest", [], null, output));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("Possible delivery change", output.ToString());
        Assert.Contains("2026-10-01", output.ToString());
        Assert.Contains(id.ToString(), output.ToString());
        Assert.DoesNotContain("1234567890", output.ToString());
    }

    [Fact]
    public async Task Cleanup_dry_run_uses_real_client_mapping_without_writes()
    {
        var id = Guid.NewGuid();
        using var handler = new ReportHandler(id, inDelivery: true);
        using var http = new HttpClient(handler);
        var client = new SettlMeteringPointClient(http, new OfflineAuth(),
            Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid", SubscriptionKey = "fixture" }),
            NullLogger<SettlMeteringPointClient>.Instance);
        var logs = new List<string>();
        using var progress = new StringWriter();
        Assert.Equal(0, await new FixConsumer2DuplicateRunner(client, logs.Add) { ProgressOutput = progress }
            .RunAsync(new() { DryRun = true }));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains(logs, line => line.Contains("would blank", StringComparison.Ordinal));
        Assert.Contains("Checked: 1 / 1", progress.ToString());
        Assert.DoesNotContain("1234567890", string.Join("\n", logs));
    }

    private sealed class OfflineAuth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture");
        public void InvalidateToken() { }
    }

    private sealed class ReportHandler(Guid id, bool inDelivery = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            Assert.Equal(HttpMethod.Get, request.Method);
            var json = request.RequestUri.AbsolutePath.Contains("/v4/", StringComparison.Ordinal)
                ? """{"id":"ID","identification":{"meteringPointIdentification":"571313100000000002"},"consumer":{"customerType":"Person","firstConsumerParty":{"name":{"fullName":"Fixture"}},"secondConsumerParty":{"name":{"fullName":"Fixture"},"nationalIdentificationNumber":"1234567890"}}}"""
                : """{"items":[{"id":"ID","meteringPointIdentification":"571313100000000002","inDelivery":false,"lastUpdated":"2026-10-01T10:00:00Z","mostRecentCompletedDeliveryPeriod":{"start":"2026-01-01T00:00:00Z","end":"2026-09-01T00:00:00Z"}}]}""";
            json = json.Replace("ID", id.ToString());
            if (inDelivery) json = json.Replace("\"inDelivery\":false", "\"inDelivery\":true");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
