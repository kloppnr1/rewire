using System.Text.Json.Nodes;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Read-only Settl transport that returns responses as untyped JSON. Used where the whole Settl
/// object must survive (exports for another system), so a field our DTOs do not know is not lost.
/// GET only, plus a POST for search endpoints that read but take a body.
/// </summary>
public interface ISettlRawReader
{
    SettlApiOptions Settings { get; }

    Task<(JsonNode? Node, string? Error)> GetAsync(string url, CancellationToken ct);

    Task<(JsonNode? Node, string? Error)> PostSearchAsync(string url, object body, CancellationToken ct);

    /// <summary>
    /// Reads every page of a Settl list endpoint and returns the concatenated <c>Items</c>. Follows
    /// <c>NextUri</c> when Settl returns one, otherwise steps <c>Skip</c> by the page size.
    /// <paramref name="url"/> must already carry its query string, without Limit/Skip.
    /// </summary>
    Task<(JsonArray Items, string? Error)> GetAllItemsAsync(string url, int pageSize, CancellationToken ct);
}

public sealed class SettlRawReader(HttpClient http, ISettlAuthService auth,
    IOptions<SettlApiOptions> options, ILogger<SettlRawReader> logger)
    : SettlApiClientBase(http, auth, options, logger), ISettlRawReader
{
    private const int MaxPages = 1000;

    public SettlApiOptions Settings => Options;

    // A full production export makes hundreds of thousands of calls over most of an hour; one dropped
    // connection must not end the run. Waits 2, 4, 8, 16 and 32 seconds between attempts.
    private const int MaxAttempts = 6;

    public Task<(JsonNode? Node, string? Error)> GetAsync(string url, CancellationToken ct) =>
        WithRetryAsync(async () =>
        {
            var (content, error) = await GetRawAsync(url, ct);
            if (error != null) return (null, error);
            return string.IsNullOrWhiteSpace(content) ? (null, null) : (JsonNode.Parse(content, NodeOptions), null);
        }, ct);

    public Task<(JsonNode? Node, string? Error)> PostSearchAsync(string url, object body, CancellationToken ct) =>
        WithRetryAsync(async () =>
        {
            var (node, error) = await SendAsync<JsonNode>(HttpMethod.Post, url, body, ct);
            return error != null || node == null ? (node, error) : (JsonNode.Parse(node.ToJsonString(), NodeOptions), null);
        }, ct);

    /// <summary>
    /// Retries network failures, timeouts, 429 and 5xx. Anything else (404, 400) is an answer, not a
    /// glitch, and comes back at once. When the attempts run out the failure is returned as an error,
    /// never thrown, so the caller records it on the customer and carries on.
    /// </summary>
    private async Task<(JsonNode? Node, string? Error)> WithRetryAsync(
        Func<Task<(JsonNode? Node, string? Error)>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            string error;
            try
            {
                var result = await call();
                if (result.Error == null || !IsTransient(result.Error)) return result;
                error = result.Error;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && ex is HttpRequestException or TaskCanceledException or IOException)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (attempt >= MaxAttempts) return (null, $"{error} (gave up after {attempt} attempts)");
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
        }
    }

    private static bool IsTransient(string error) =>
        error.StartsWith("HTTP 429", StringComparison.Ordinal)
        || error.StartsWith("HTTP 5", StringComparison.Ordinal);

    // Settl mixes PascalCase and camelCase between APIs; lookups by name must not care.
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<(JsonArray Items, string? Error)> GetAllItemsAsync(string url, int pageSize, CancellationToken ct)
    {
        var all = new JsonArray();
        var separator = url.Contains('?') ? "&" : "?";
        string? next = $"{url}{separator}Limit={pageSize}&Skip=0";
        var skip = 0;

        for (var page = 0; next != null && page < MaxPages; page++)
        {
            var (node, error) = await GetAsync(next, ct);
            if (error != null) return (all, error);

            var items = node?["Items"] as JsonArray ?? node?["items"] as JsonArray;
            var count = items?.Count ?? 0;
            if (items != null)
                foreach (var item in items.ToList())
                {
                    items.Remove(item);
                    all.Add(item);
                }

            var nextUri = (node?["NextUri"] ?? node?["nextUri"])?.GetValue<string>();
            if (count == 0)
                next = null;
            else if (!string.IsNullOrEmpty(nextUri))
                next = nextUri;
            else if (count >= pageSize)
            {
                skip += pageSize;
                next = $"{url}{separator}Limit={pageSize}&Skip={skip}";
            }
            else
                next = null;
        }

        return (all, null);
    }
}
