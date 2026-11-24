using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.Services;

/// <summary>One Settl customer and the requested metering points linked to it at the date asked for.</summary>
public sealed class CustomerLinkEntry
{
    public CustomerLinkCustomer? Customer { get; set; }
    public List<CustomerLinkMeteringPoint>? MeteringPoints { get; set; }
}

public sealed class CustomerLinkCustomer
{
    public Guid Id { get; set; }
}

public sealed class CustomerLinkMeteringPoint
{
    public Guid MeteringPointId { get; set; }
}

public interface ISettlCustomerLinkLookup
{
    /// <summary>
    /// The customers linked directly to the metering points at <paramref name="relationDate"/>.
    /// A metering point with no customer link at that date is left out of the answer.
    /// </summary>
    Task<(List<CustomerLinkEntry>? Items, string? Error)> FindCustomersForMeteringPointsAsync(
        IReadOnlyList<Guid> meteringPointIds, DateTime relationDate, CancellationToken cancellationToken);
}

/// <summary>Dated Customer↔MP lookup using the existing authenticated Settl transport.</summary>
public sealed class SettlCustomerLinkLookup(HttpClient http, ISettlAuthService auth,
    IOptions<SettlApiOptions> options, ILogger<SettlCustomerLinkLookup> logger)
    : SettlApiClientBase(http, auth, options, logger), ISettlCustomerLinkLookup
{
    public async Task<(List<CustomerLinkEntry>? Items, string? Error)> FindCustomersForMeteringPointsAsync(
        IReadOnlyList<Guid> meteringPointIds, DateTime relationDate, CancellationToken cancellationToken)
    {
        if (meteringPointIds.Count == 0) return ([], null);
        var url = $"{Options.BillingBaseUrl}/api/v2/customers/CustomerInfosForMeteringPoints";
        var body = new { RelationDate = relationDate, MeteringPointIds = meteringPointIds };
        var (items, error) = await SendAsync<List<CustomerLinkEntry>>(HttpMethod.Post, url, body, cancellationToken);
        return error is null ? (items ?? [], null) : (null, error);
    }
}
