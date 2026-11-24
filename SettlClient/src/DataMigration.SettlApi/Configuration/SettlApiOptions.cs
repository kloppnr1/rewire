namespace DataMigration.SettlApi.Configuration;

public class SettlApiOptions
{
    public const string SectionName = "SettlApi";

    public string BaseUrl { get; set; } = string.Empty;
    public string SubscriptionKey { get; set; } = string.Empty;
    public CredentialOptions Credentials { get; set; } = new();
    public MigrationOptions Migration { get; set; } = new();

    public Dictionary<string, string> MarketBaseUrlOverrides { get; set; } = new();

    public string TokenUrl => $"{BaseUrl}/security/connect/token";
    public string BillingBaseUrl => $"{BaseUrl}/internal-billing";
    public string MarketBaseUrl => $"{BaseUrl}/internal-market";
    public string MarketTenantBaseUrl => $"{BaseUrl}/market/tenant-api";
    public string WaterMeteringPointsUrl => $"{BaseUrl}/commodities/tenant-api/v1/water-metering-points";
    public string DebtorsBaseUrl => $"{BaseUrl}/debtors";

    public string GetMarketBaseUrl(string? deliveryCategory)
    {
        if (deliveryCategory != null
            && MarketBaseUrlOverrides.TryGetValue(deliveryCategory, out var overrideUrl))
            return overrideUrl;

        return MarketBaseUrl;
    }
}

public class CredentialOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Scope { get; set; } = "BIO_Client BIO_Api";
}

public class MigrationOptions
{
    public bool DryRun { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 120;
}
