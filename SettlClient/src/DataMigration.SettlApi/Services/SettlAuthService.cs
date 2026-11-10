using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.SettlApi.Services;

public class SettlAuthService : ISettlAuthService
{
    private readonly HttpClient _httpClient;
    private readonly SettlApiOptions _options;
    private readonly ILogger<SettlAuthService> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(5);

    public SettlAuthService(
        HttpClient httpClient,
        IOptions<SettlApiOptions> options,
        ILogger<SettlAuthService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
            return _cachedToken;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiry)
                return _cachedToken;

            _logger.LogDebug("Acquiring new access token from {TokenUrl}", _options.TokenUrl);
            return await AcquireTokenAsync(cancellationToken);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public void InvalidateToken()
    {
        _logger.LogDebug("Invalidating cached token");
        _cachedToken = null;
        _tokenExpiry = DateTime.MinValue;
    }

    private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        var credentials = _options.Credentials;

        var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl);

        // Use Basic Auth header if both ClientId and ClientSecret are provided
        // and Username is also provided (separate client + user credentials).
        // Otherwise, send client_id/client_secret in the form body.
        var hasBasicAuth = !string.IsNullOrEmpty(credentials.ClientId)
            && !string.IsNullOrEmpty(credentials.ClientSecret)
            && !string.IsNullOrEmpty(credentials.Username);

        if (hasBasicAuth)
        {
            var basicAuthValue = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuthValue);
        }

        // Add subscription key for APIM
        if (!string.IsNullOrEmpty(_options.SubscriptionKey))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _options.SubscriptionKey);
        }

        var formFields = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["scope"] = credentials.Scope
        };

        if (hasBasicAuth)
        {
            // Client credentials in Basic Auth header, user credentials in body
            formFields["username"] = credentials.Username;
            formFields["password"] = credentials.Password;
        }
        else
        {
            // All credentials in body (ClientId as username, ClientSecret as password)
            formFields["client_id"] = credentials.ClientId;
            formFields["username"] = !string.IsNullOrEmpty(credentials.Username)
                ? credentials.Username : credentials.ClientId;
            formFields["password"] = !string.IsNullOrEmpty(credentials.Password)
                ? credentials.Password : credentials.ClientSecret;
        }

        request.Content = new FormUrlEncodedContent(formFields);

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to acquire token. Status: {Status}, Response: {Response}",
                response.StatusCode, errorContent);
            throw new HttpRequestException($"Failed to acquire token: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<SettlTokenResponse>(content);

        if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            throw new InvalidOperationException("Token response was null or empty");

        _cachedToken = tokenResponse.AccessToken;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn) - TokenExpiryBuffer;

        _logger.LogDebug("Token acquired successfully. Expires in {ExpiresIn} seconds", tokenResponse.ExpiresIn);
        return _cachedToken;
    }
}
