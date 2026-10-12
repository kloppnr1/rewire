using DataMigration.SettlApi.Aggregates;
using Microsoft.Extensions.DependencyInjection;

namespace DataMigration.SettlApi.Extensions;

public static class SettlApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISettlContractLinesAggregator"/>.
    /// Prerequisites: <c>AddHttpClient&lt;ISettlContractClient, SettlContractClient&gt;()</c>
    /// and <c>AddHttpClient&lt;ISettlPriceListClient, SettlPriceListClient&gt;()</c> must be registered by the consumer.
    /// </summary>
    public static IServiceCollection AddSettlContractLinesAggregator(this IServiceCollection services)
    {
        services.AddScoped<ISettlContractLinesAggregator, SettlContractLinesAggregator>();
        return services;
    }
}
