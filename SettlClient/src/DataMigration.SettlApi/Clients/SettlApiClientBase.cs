using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Clients;

public abstract class SettlApiClientBase
{
    protected readonly HttpClient HttpClient;
    protected readonly ISettlAuthService AuthService;
    protected readonly SettlApiOptions Options;
    protected readonly ILogger Logger;

    /// <summary>
    /// Wire-level options for Settl billing API (v2): PascalCase property names, no indentation,
    /// null properties omitted. Exposed publicly so integration tests can serialize request
    /// payloads with the exact same rules the clients use when POSTing.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Wire-level options for Settl market API (v3): camelCase property names, no indentation,
    /// null properties omitted. Used for electricity MP create + billing update.
    /// </summary>
    public static readonly JsonSerializerOptions CamelCaseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    protected SettlApiClientBase(
        HttpClient httpClient,
        ISettlAuthService authService,
        IOptions<SettlApiOptions> options,
        ILogger logger)
    {
        HttpClient = httpClient;
        AuthService = authService;
        Options = options.Value;
        Logger = logger;
    }

    protected Task<(T? Response, string? Error)> SendAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken) where T : class
        => SendAsync<T>(method, url, body, JsonOptions, cancellationToken);

    protected async Task<(T? Response, string? Error)> SendAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken) where T : class
    {
        var response = await SendAuthenticatedRequestAsync(method, url, body, jsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: {Method} {Url} -> {Error}", method, url, errorMessage);
            return (null, errorMessage);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
            return (null, null);

        try
        {
            return (JsonSerializer.Deserialize<T>(content, jsonOptions), null);
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Failed to deserialize response from {Url}: {Content}", url, content);
            return (null, $"Failed to deserialize response: {ex.Message}");
        }
    }

    protected Task<(bool Success, string? Error)> PostAsync(
        string url,
        object body,
        CancellationToken cancellationToken)
        => PostAsync(url, body, JsonOptions, cancellationToken);

    protected async Task<(bool Success, string? Error)> PostAsync(
        string url,
        object body,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, url, body, jsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: POST {Url} -> {Error}", url, errorMessage);
            return (false, errorMessage);
        }

        return (true, null);
    }

    protected async Task<(bool Success, string? Error)> PutAsync(
        string url,
        object body,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, url, body, jsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: PUT {Url} -> {Error}", url, errorMessage);
            return (false, errorMessage);
        }

        return (true, null);
    }

    /// <summary>
    /// GET der returnerer den rå response-body som tekst (ingen DTO-deserialisering).
    /// Bruges når kalderen vil round-trippe felter Settl returnerer men som vores
    /// DTOs ikke kender (typisk PUT-modify-PUT flows på endpoints uden partial update).
    /// </summary>
    protected async Task<(string? Content, string? Error)> GetRawAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, url, null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: GET {Url} -> {Error}", url, errorMessage);
            return (null, errorMessage);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return (content, null);
    }

    /// <summary>
    /// PUT der sender en præ-serialiseret JSON-body direkte (ingen object→JSON-konvertering).
    /// Modstykke til <see cref="GetRawAsync"/>.
    /// </summary>
    protected async Task<(bool Success, string? Error)> PutRawAsync(
        string url,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        var token = await AuthService.GetAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(Options.SubscriptionKey))
            request.Headers.Add("Ocp-Apim-Subscription-Key", Options.SubscriptionKey);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            AuthService.InvalidateToken();
            token = await AuthService.GetAccessTokenAsync(cancellationToken);
            request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(Options.SubscriptionKey))
                request.Headers.Add("Ocp-Apim-Subscription-Key", Options.SubscriptionKey);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            response = await HttpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: PUT {Url} -> {Error}", url, errorMessage);
            return (false, errorMessage);
        }

        return (true, null);
    }

    /// <summary>
    /// PATCH der sender en præ-serialiseret JSON-body direkte (ingen object→JSON-konvertering).
    /// Modstykke til <see cref="GetRawAsync"/> og <see cref="PutRawAsync"/> — bruges til Settl's
    /// bulk-PATCH-endpoints (fx v3 metering point patch), hvor der ikke findes noget PUT/PATCH
    /// på {id}-ruten, kun et array-baseret endpoint uden id i path'en.
    /// </summary>
    protected async Task<(bool Success, string? Error)> PatchRawAsync(
        string url,
        string jsonBody,
        CancellationToken cancellationToken)
    {
        var token = await AuthService.GetAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Patch, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(Options.SubscriptionKey))
            request.Headers.Add("Ocp-Apim-Subscription-Key", Options.SubscriptionKey);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            AuthService.InvalidateToken();
            token = await AuthService.GetAccessTokenAsync(cancellationToken);
            request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(Options.SubscriptionKey))
                request.Headers.Add("Ocp-Apim-Subscription-Key", Options.SubscriptionKey);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            response = await HttpClient.SendAsync(request, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: PATCH {Url} -> {Error}", url, errorMessage);
            return (false, errorMessage);
        }

        return (true, null);
    }

    protected async Task<(bool Success, string? Error)> DeleteWithBodyAsync(
        string url,
        object body,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, url, body, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: DELETE {Url} -> {Error}", url, errorMessage);
            return (false, errorMessage);
        }

        return (true, null);
    }

    protected async Task<(bool Success, string? Error)> DeleteAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Delete, url, null, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"HTTP {(int)response.StatusCode}: {errorContent}";
            Logger.LogWarning("API request failed: DELETE {Url} -> {Error}", url, errorMessage);
            return (false, errorMessage);
        }

        return (true, null);
    }

    private Task<HttpResponseMessage> SendAuthenticatedRequestAsync(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken,
        bool isRetry = false)
        => SendAuthenticatedRequestAsync(method, url, body, JsonOptions, cancellationToken, isRetry);

    private async Task<HttpResponseMessage> SendAuthenticatedRequestAsync(
        HttpMethod method,
        string url,
        object? body,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        bool isRetry = false)
    {
        var token = await AuthService.GetAccessTokenAsync(cancellationToken);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Add APIM subscription key
        if (!string.IsNullOrEmpty(Options.SubscriptionKey))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", Options.SubscriptionKey);
        }

        if (body != null)
        {
            var jsonBody = JsonSerializer.Serialize(body, jsonOptions);
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            Logger.LogDebug("Request body: {Body}", jsonBody);
        }

        Logger.LogDebug("HTTP {Method} {Url}", method, url);

        var response = await HttpClient.SendAsync(request, cancellationToken);

        Logger.LogDebug("HTTP Response: {StatusCode}", (int)response.StatusCode);

        // Handle 401 by invalidating token and retrying once
        if (response.StatusCode == HttpStatusCode.Unauthorized && !isRetry)
        {
            Logger.LogWarning("Received 401 Unauthorized, invalidating token and retrying");
            AuthService.InvalidateToken();
            return await SendAuthenticatedRequestAsync(method, url, body, jsonOptions, cancellationToken, isRetry: true);
        }

        return response;
    }
}
