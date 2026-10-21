using System.Net;
using System.Text;
using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.IntegrationTests;

public class ConsumerVersionCleanupHttpTests
{
    [Theory]
    [InlineData("cleared", 0)]
    [InlineData("retained-id", 1)]
    [InlineData("retained-name", 1)]
    [InlineData("wrong-version", 1)]
    [InlineData("write-error", 1)]
    public async Task Actual_cleanup_uses_report_selection_and_minimal_dated_patch(string outcome, int expectedExit)
    {
        using var handler = new Handler(outcome);
        using var http = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid", SubscriptionKey = "fixture" });
        var auth = new Auth();
        var read = new SettlMeteringPointClient(http, auth, options, NullLogger<SettlMeteringPointClient>.Instance);
        var write = new ConsumerVersionCleanup(http, auth, options, NullLogger<ConsumerVersionCleanup>.Instance);
        var logs = new List<string>();
        var runner = new FixConsumer2DuplicateRunner(read, logs.Add, write) { ProgressOutput = TextWriter.Null };
        Assert.Equal(0, await runner.RunAsync(new() { MeteringPointIds = [handler.Id.ToString()], DryRun = true }));
        Assert.Null(handler.Payload);
        Assert.Contains(logs, l => l.Contains("would blank"));
        Assert.Equal(expectedExit, await runner.RunAsync(new() { MeteringPointIds = [handler.Id.ToString()] }));
        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.Payload));
        var root = payload.RootElement;
        Assert.False(root.TryGetProperty("MarketUpdate", out _));
        Assert.Equal(2, root.EnumerateObject().Count());
        var patch = Assert.Single(root.GetProperty("MeteringPointPatches").EnumerateArray());
        Assert.False(patch.TryGetProperty("MarketUpdate", out _));
        Assert.Equal(3, patch.EnumerateObject().Count());
        Assert.Equal(handler.Id, patch.GetProperty("Id").GetGuid());
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), patch.GetProperty("PatchTime").GetDateTimeOffset());
        Assert.Equal("2026-01-01T00:00:00Z", patch.GetProperty("PatchTime").GetString());
        Assert.All(handler.DatedQueries, q => Assert.Equal("?EffectiveDate=2026-01-01T00:00:00Z", q));
        Assert.NotEmpty(handler.DatedQueries);
        var party = patch.GetProperty("ConsumerPatch").GetProperty("SecondConsumerParty");
        Assert.Equal("", party.GetProperty("NationalIdentificationNumber").GetString());
        Assert.All(party.GetProperty("Name").EnumerateObject(), p => Assert.Equal(JsonValueKind.Null, p.Value.ValueKind));
        Assert.Equal(1, handler.Writes);
        // The id check after the write uses Settl's dedicated national-id read.
        if (outcome is "cleared" or "retained-id") Assert.True(handler.NationalIdReads > 0);
        Assert.DoesNotContain("1234567890", string.Join("\n", logs));
        if (expectedExit == 0) Assert.Contains(logs, l => l.Contains("fixed - verified"));
        else Assert.DoesNotContain(logs, l => l.Contains("fixed - verified"));
        // Every failure here happens after the PATCH was sent, so it is a failed write, not a skip.
        Assert.Contains($"  Failed writes: {expectedExit}", logs);
        Assert.Contains("  Errors: 0", logs);
    }

    [Fact]
    public async Task Wrong_prewrite_version_never_sends_patch()
    {
        using var handler = new Handler("wrong-before");
        using var http = new HttpClient(handler);
        var writer = new ConsumerVersionCleanup(http, new Auth(), Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid" }), NullLogger<ConsumerVersionCleanup>.Instance);
        var result = await writer.BlankAsync(handler.Id, new(2026, 1, 1), default);
        Assert.Equal(ConsumerCleanupOutcome.NotSent, result.Outcome);
        Assert.Equal(0, handler.Writes);
    }

    [Theory]
    [InlineData("cleared", ConsumerCleanupOutcome.Fixed)]
    [InlineData("name-changed", ConsumerCleanupOutcome.Unverified)]
    [InlineData("retained-id", ConsumerCleanupOutcome.Unverified)]
    [InlineData("first-fictitious", ConsumerCleanupOutcome.NotSent)]
    public async Task Fictitious_cpr_2_clear_sends_only_the_cpr_and_keeps_the_name(string outcome, ConsumerCleanupOutcome expected)
    {
        using var handler = new FictitiousHandler(outcome);
        using var http = new HttpClient(handler);
        var options = Options.Create(new SettlApiOptions { BaseUrl = "https://offline.invalid", SubscriptionKey = "fixture" });
        var auth = new Auth();
        var read = new SettlMeteringPointClient(http, auth, options, NullLogger<SettlMeteringPointClient>.Instance);
        var write = new ConsumerVersionCleanup(http, auth, options, NullLogger<ConsumerVersionCleanup>.Instance);
        var logs = new List<string>();
        var runner = new FixConsumer2DuplicateRunner(read, logs.Add, write) { ProgressOutput = TextWriter.Null };
        Assert.Equal(expected is ConsumerCleanupOutcome.Fixed or ConsumerCleanupOutcome.NotSent ? 0 : 1,
            await runner.RunAsync(new() { MeteringPointIds = [handler.Id.ToString()], ClearFictitiousCpr2 = true }));
        if (expected == ConsumerCleanupOutcome.NotSent)
        {
            // The report rule already rejects a fictitious CPR 1, so nothing is selected.
            Assert.Equal(0, handler.Writes);
            return;
        }
        Assert.Equal(1, handler.Writes);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(handler.Payload));
        var patch = Assert.Single(payload.RootElement.GetProperty("MeteringPointPatches").EnumerateArray());
        Assert.Equal("2026-01-01T00:00:00Z", patch.GetProperty("PatchTime").GetString());
        var party = patch.GetProperty("ConsumerPatch").GetProperty("SecondConsumerParty");
        Assert.Equal("", party.GetProperty("NationalIdentificationNumber").GetString());
        // Settl replaces the whole party, so the name is sent back unchanged.
        Assert.Equal("Other", party.GetProperty("Name").GetProperty("FullName").GetString());
        Assert.Equal(2, party.EnumerateObject().Count());
        Assert.Contains($"  Failed writes: {(expected == ConsumerCleanupOutcome.Fixed ? 0 : 1)}", logs);
        Assert.DoesNotContain("1234567890", string.Join("\n", logs));
    }

    private sealed class FictitiousHandler(string outcome) : HttpMessageHandler
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal string? Payload { get; private set; }
        internal int Writes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Patch)
            {
                Payload = await request.Content!.ReadAsStringAsync(ct);
                Writes++;
                return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/history")) json = """{"consumerDates":["2026-01-01T00:00:00Z"]}""";
            else if (path.EndsWith("/deliveryStatuses")) json = """{"deliveryStatuses":[{"inDelivery":false,"period":{"start":"2025-12-01T00:00:00Z","end":"2026-01-01T00:00:00Z"},"startReason":"Na","stopReason":"Na"},{"inDelivery":true,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"MoveIn","stopReason":"Na"}]}""";
            else if (path.EndsWith("/nationalIdentificationNumber"))
                json = JsonSerializer.Serialize(new
                {
                    firstConsumerPartyNationalIdentificationNumber = outcome == "first-fictitious" ? "1111110000" : "1234567890",
                    secondConsumerPartyNationalIdentificationNumber = Writes == 0 || outcome == "retained-id" ? "1111110000" : ""
                });
            else
                json = JsonSerializer.Serialize(new
                {
                    id = Id, consumer = new
                    {
                        start = "2026-01-01T00:00:00Z", customerType = "Person",
                        firstConsumerParty = new { name = new { fullName = "Fixture" } },
                        secondConsumerParty = new { name = new { fullName = Writes > 0 && outcome == "name-changed" ? "" : "Other" } }
                    }
                });
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Auth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("offline");
        public void InvalidateToken() { }
    }

    private sealed class Handler(string outcome) : HttpMessageHandler
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal string? Payload { get; private set; }
        internal int Writes { get; private set; }
        internal List<string> DatedQueries { get; } = [];
        internal int NationalIdReads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("offline.invalid", request.RequestUri!.Host);
            if (request.RequestUri.Query.Contains("EffectiveDate")) lock (DatedQueries) DatedQueries.Add(Uri.UnescapeDataString(request.RequestUri.Query));
            if (request.Method == HttpMethod.Patch)
            {
                Assert.EndsWith("/api/v3/meteringPoints", request.RequestUri.AbsolutePath);
                Payload = await request.Content!.ReadAsStringAsync(ct);
                Writes++;
                return new(outcome == "write-error" ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { Content = new StringContent("{}") };
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            string json;
            if (request.RequestUri.AbsolutePath.EndsWith("/history")) json = """{"consumerDates":["2026-01-01T00:00:00Z"]}""";
            else if (request.RequestUri.AbsolutePath.EndsWith("/deliveryStatuses")) json = """{"deliveryStatuses":[{"inDelivery":false,"period":{"start":"2025-12-01T00:00:00Z","end":"2026-01-01T00:00:00Z"},"startReason":"Na","stopReason":"Na"},{"inDelivery":false,"period":{"start":"2026-01-01T00:00:00Z","end":null},"startReason":"MoveIn","stopReason":"Na"}]}""";
            else if (request.RequestUri.AbsolutePath.EndsWith("/nationalIdentificationNumber"))
            {
                // Settl's only source for national ids; the metering-point read below never carries them.
                NationalIdReads++;
                Assert.Equal("?EffectiveDate=2026-01-01T00:00:00Z", Uri.UnescapeDataString(request.RequestUri.Query));
                json = JsonSerializer.Serialize(new
                {
                    firstConsumerPartyNationalIdentificationNumber = "1234567890",
                    secondConsumerPartyNationalIdentificationNumber = Writes == 0 || outcome == "retained-id" ? "1234567890" : null
                });
            }
            else
            {
                Assert.Contains("/api/v4/meteringPoints/", request.RequestUri.AbsolutePath);
                if (Writes > 0) Assert.Contains("2026-01-01", Uri.UnescapeDataString(request.RequestUri.Query));
                json = JsonSerializer.Serialize(new
                {
                    id = Id, consumer = new
                    {
                        start = outcome == "wrong-before" || Writes > 0 && outcome == "wrong-version" ? "2026-02-01T00:00:00Z" : "2026-01-01T00:00:00Z",
                        customerType = "Person", marketUpdate = true,
                        firstConsumerParty = new { name = new { fullName = "Fixture" } },
                        secondConsumerParty = new { name = new { fullName = Writes == 0 || outcome == "retained-name" ? "Fixture" : "" } }
                    }
                });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
