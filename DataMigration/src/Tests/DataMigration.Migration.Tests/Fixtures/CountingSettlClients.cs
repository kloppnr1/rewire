using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Tomme mock-klienter der tæller de paginerings-kald bulk-cachen bruger til at varme sit
/// snapshot. Et kald her betyder "kørslen hentede hele listen"; nul betyder "den lod være".
/// Interfacet er nævnt igen med vilje — så bliver det de tællende metoder her der bliver kaldt,
/// også når klienten ses gennem interfacet.
/// </summary>
public sealed class CountingCustomerClient : MockCustomerClient, ISettlCustomerClient
{
    public int FindCustomersCalls { get; private set; }

    public new Task<(List<CustomerInfoDto>, int, string?)> FindCustomersAsync(
        int limit = 1000, int skip = 0, CancellationToken ct = default)
    {
        FindCustomersCalls++;
        return base.FindCustomersAsync(limit, skip, ct);
    }
}

/// <inheritdoc cref="CountingCustomerClient"/>
public sealed class CountingPayerClient : MockPayerClient, ISettlPayerClient
{
    public int FindPayersCalls { get; private set; }

    public new Task<(List<PayerInfoDto>, int, string?)> FindPayersAsync(
        int limit = 100, int skip = 0, CancellationToken ct = default)
    {
        FindPayersCalls++;
        return base.FindPayersAsync(limit, skip, ct);
    }
}

/// <inheritdoc cref="CountingCustomerClient"/>
public sealed class CountingMeteringPointClient : MockMeteringPointClient, ISettlMeteringPointClient
{
    public int FindMeteringPointsCalls { get; private set; }

    public new Task<(List<MeteringPointInfoDto>, int, string?)> FindMeteringPointsAsync(
        int limit = 100, int skip = 0, bool doCount = false, CancellationToken ct = default)
    {
        FindMeteringPointsCalls++;
        return base.FindMeteringPointsAsync(limit, skip, doCount, ct);
    }
}
