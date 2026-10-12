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
        Assert.Equal(4, handler.Requests.Count);
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
        using var handler = new ReportHandler(id, inDelivery: false);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pattern_report_preserves_unknown_history_values_in_actual_raw_client_response(bool missingIdentification)
    {
        var id = Guid.NewGuid();
        using var handler = new ReportHandler(id, missingIdentification: missingIdentification);
        using var http = new HttpClient(handler);
        var client = new SettlMeteringPointClient(http, new OfflineAuth(),
            Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid" }), NullLogger<SettlMeteringPointClient>.Instance);
        using var patterns = new StringWriter();
        using var examples = new StringWriter();
        using var sources = new StringWriter();
        using var progress = new StringWriter();
        Assert.Equal(0, await new ReportDeliveryPatternsRunner(client) { ProgressOutput = progress }
            .RunAsync("featuretest", false, patterns, examples, sources));
        Assert.Contains("FutureReason", patterns.ToString());
        Assert.Contains("42", examples.ToString());
        if (missingIdentification)
        {
            Assert.Contains(id.ToString(), patterns.ToString());
            Assert.Contains("(missing)", examples.ToString());
        }
        using var source = System.Text.Json.JsonDocument.Parse(sources.ToString().Trim());
        using var response = System.Text.Json.JsonDocument.Parse(source.RootElement.GetProperty("ResponseJson").GetString()!);
        Assert.Equal(42, response.RootElement.GetProperty("deliveryStatuses")[0].GetProperty("stopReason").GetInt32());
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Dated_http_reads_find_historical_and_future_duplicates_with_clean_current_version()
    {
        var id = Guid.NewGuid();
        using var handler = new ReportHandler(id, versioned: true);
        using var http = new HttpClient(handler);
        var client = new SettlMeteringPointClient(http, new OfflineAuth(),
            Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid" }), NullLogger<SettlMeteringPointClient>.Instance);
        using var output = new StringWriter();
        Assert.Equal(0, await new ReportConsumer2DuplicateRunner(client) { ProgressOutput = TextWriter.Null }
            .RunAsync("featuretest", [], null, output));
        Assert.Equal(4, output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("\"No\"", output.ToString());
        Assert.Contains("2026-12-01", output.ToString());
        Assert.Contains(handler.Uris, uri => Uri.UnescapeDataString(uri.Query) == "?EffectiveDate=2026-01-01T00:00:00Z");
        Assert.Contains(handler.Uris, uri => Uri.UnescapeDataString(uri.Query) == "?EffectiveDate=2026-12-01T00:00:00Z");
        Assert.Equal(3, handler.Uris.Count(uri => uri.AbsolutePath.Contains("/v4/")));
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private sealed class OfflineAuth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fixture");
        public void InvalidateToken() { }
    }

    private sealed class ReportHandler(Guid id, bool inDelivery = false, bool missingIdentification = false, bool versioned = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = [];
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            Uris.Add(request.RequestUri!);
            Assert.Equal(HttpMethod.Get, request.Method);
            var json = request.RequestUri.AbsolutePath.Contains("/v4/", StringComparison.Ordinal)
                ? """{"id":"ID","identification":{"meteringPointIdentification":"571313100000000002"},"consumer":{"start":"2026-01-01T00:00:00Z","customerType":"Person","firstConsumerParty":{"name":{"fullName":"Fixture"}},"secondConsumerParty":{"name":{"fullName":"Fixture"},"nationalIdentificationNumber":"1234567890"}}}"""
                : """{"items":[{"id":"ID","meteringPointIdentification":"571313100000000002","inDelivery":false,"lastUpdated":"2026-10-01T10:00:00Z","mostRecentCompletedDeliveryPeriod":{"start":"2026-01-01T00:00:00Z","end":"2026-09-01T00:00:00Z"}}]}""";
            if (request.RequestUri.AbsolutePath.EndsWith("/deliveryStatuses", StringComparison.Ordinal))
                json = """{"deliveryStatuses":[{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":"9999-12-31T23:59:59Z"},"startReason":"FutureReason","stopReason":42}]}""";
            if (request.RequestUri.AbsolutePath.EndsWith("/history", StringComparison.Ordinal))
                json = """{"consumerDates":["2026-01-01T00:00:00Z"]}""";
            if (versioned)
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/history", StringComparison.Ordinal))
                    json = """{"consumerDates":["2026-01-01T00:00:00Z","2026-06-01T00:00:00Z","2026-12-01T00:00:00Z"]}""";
                else if (request.RequestUri.AbsolutePath.Contains("/v4/", StringComparison.Ordinal))
                {
                    var query = Uri.UnescapeDataString(request.RequestUri.Query);
                    var historical = query.Contains("2026-01-01");
                    var future = query.Contains("2026-12-01");
                    var start = historical ? "2026-01-01" : future ? "2026-12-01" : "2026-06-01";
                    var end = historical ? "2026-06-01T00:00:00Z" : future ? null : "2026-12-01T00:00:00Z";
                    json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id, identification = new { meteringPointIdentification = "fixture" },
                        consumer = new
                        {
                            start = start + "T00:00:00Z", end, customerType = "Person",
                            firstConsumerParty = new { name = new { fullName = "Fixture" } },
                            secondConsumerParty = new { name = new { fullName = historical || future ? "Fixture" : "Different" } }
                        }
                    });
                }
            }
            json = json.Replace("ID", id.ToString());
            if (missingIdentification) json = json.Replace("\"meteringPointIdentification\":\"571313100000000002\"", "\"meteringPointIdentification\":null");
            if (inDelivery) json = json.Replace("\"inDelivery\":false", "\"inDelivery\":true");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
