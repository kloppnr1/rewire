using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// xunit class fixture that wires <see cref="DataMigration.SettlApi"/> clients against
/// Settl featuretest. Reads config from <c>testsettings.json</c> + user-secrets on the
/// <c>DataMigration.OpsCli.IntegrationTests</c> project.
///
/// <para>Does not provision any customers or mutate featuretest on construction. Test
/// classes control provisioning explicitly.</para>
/// </summary>
public class SettlFeaturetestFixture : IDisposable
{
    public IServiceProvider Services { get; }
    public SettlApiOptions ApiOptions { get; }
    public string XellentConnectionString { get; }

    public ISettlCustomerClient CustomerClient => Services.GetRequiredService<ISettlCustomerClient>();
    public ISettlPayerClient PayerClient => Services.GetRequiredService<ISettlPayerClient>();
    public ISettlMeteringPointClient MeteringPointClient => Services.GetRequiredService<ISettlMeteringPointClient>();
    public ISettlContractClient ContractClient => Services.GetRequiredService<ISettlContractClient>();
    public ISettlSettlementGroupClient SettlementGroupClient => Services.GetRequiredService<ISettlSettlementGroupClient>();

    public SettlFeaturetestFixture()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("testsettings.json", optional: false)
            .AddUserSecrets<SettlFeaturetestFixture>(optional: true);
        var config = builder.Build();

        XellentConnectionString = config.GetConnectionString("TestConnection")
            ?? throw new InvalidOperationException("TestConnection missing in testsettings.json");

        // Reshape: if SettlApi:Environments:featuretest:* exists (shared with OpsCli's
        // user-secrets), flatten it into SettlApi:* so SettlApiOptions binds cleanly.
        var envSection = config.GetSection("SettlApi:Environments:featuretest");
        if (envSection.Exists())
        {
            builder.AddInMemoryCollection(
                envSection.GetChildren().SelectMany(FlattenSection("SettlApi")));
            config = builder.Build();
        }

        var settlSection = config.GetSection(SettlApiOptions.SectionName);
        ApiOptions = settlSection.Get<SettlApiOptions>()
            ?? throw new InvalidOperationException("SettlApi section missing in testsettings.json");

        if (string.IsNullOrWhiteSpace(ApiOptions.BaseUrl) || !ApiOptions.BaseUrl.Contains("featuretest", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to run integration tests against non-featuretest BaseUrl '{ApiOptions.BaseUrl}'. " +
                "These tests mutate the target environment; only featuretest is permitted.");

        if (string.IsNullOrWhiteSpace(ApiOptions.SubscriptionKey) || string.IsNullOrWhiteSpace(ApiOptions.Credentials.ClientSecret))
            throw new InvalidOperationException(
                "Settl credentials missing. Run `dotnet user-secrets set` on DataMigration.OpsCli.IntegrationTests " +
                "for SettlApi:SubscriptionKey and SettlApi:Credentials:*.");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.Configure<SettlApiOptions>(opts =>
        {
            opts.BaseUrl = ApiOptions.BaseUrl;
            opts.SubscriptionKey = ApiOptions.SubscriptionKey;
            opts.Credentials = ApiOptions.Credentials;
            opts.Migration = ApiOptions.Migration;
        });
        services.AddHttpClient<ISettlAuthService, SettlAuthService>();
        services.AddHttpClient<ISettlCustomerClient, SettlCustomerClient>();
        services.AddHttpClient<ISettlPayerClient, SettlPayerClient>();
        services.AddHttpClient<ISettlMeteringPointClient, SettlMeteringPointClient>();
        services.AddHttpClient<ISettlContractClient, SettlContractClient>();
        services.AddHttpClient<ISettlSettlementGroupClient, SettlSettlementGroupClient>();

        Services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        (Services as IDisposable)?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Func<IConfigurationSection, IEnumerable<KeyValuePair<string, string?>>> FlattenSection(string prefix)
    {
        return section =>
        {
            var children = section.GetChildren().ToList();
            if (children.Count == 0)
                return [new KeyValuePair<string, string?>(prefix + ":" + section.Key, section.Value)];
            return children.SelectMany(FlattenSection(prefix + ":" + section.Key));
        };
    }
}
