using DataMigration.Migration.Services.Settl;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using Moq;

namespace DataMigration.Migration.Tests;

/// <summary>
/// Locks the bulk read-cache decorator behaviour that fronts the per-entity Settl
/// existence-searches during <c>create</c>: snapshot completeness across pages, dry-run
/// trust-the-snapshot (miss = not-found, no round-trip) vs. live fall-through on a miss, the
/// link-GET short-circuit for non-existing Guids, and graceful degradation when the bulk load
/// fails.
/// </summary>
public class BulkCachingSettlClientsTests
{
    private static CustomerInfoDto Cust(string num, Guid id) => new() { Id = id, CustomerNumber = num };
    private static PayerInfoDto Payer(string ident, Guid id) => new() { Id = id, PayerIdentification = ident };

    // Wires a Find* mock to return `total`-sized data sliced into pages keyed on the skip arg,
    // simulating a real paginated endpoint. `reportedTotal` lets a test exercise either the
    // TotalCount guard (>0) or the empty-page termination (0).
    private static void SetupPagedCustomers(Mock<ISettlCustomerClient> mock, List<CustomerInfoDto> all, int pageSize, int reportedTotal)
    {
        mock.Setup(c => c.FindCustomersAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int limit, int skip, CancellationToken _) =>
                (all.Skip(skip).Take(pageSize).ToList(), reportedTotal, (string?)null));
    }

    [Fact]
    public async Task DryRun_serves_hit_and_miss_from_snapshot_without_per_entity_calls()
    {
        var g1 = Guid.NewGuid();
        var inner = new Mock<ISettlCustomerClient>(MockBehavior.Strict);
        SetupPagedCustomers(inner, [Cust("100", g1), Cust("200", Guid.NewGuid())], pageSize: 1000, reportedTotal: 2);

        var stats = new BulkReadCacheStats();
        var cache = new BulkCachingCustomerClient(inner.Object, dryRun: true, stats, log: null);
        await cache.PreloadAsync(CancellationToken.None);

        var (hit, _) = await cache.SearchByCustomerNumberAsync("100");
        var (miss, _) = await cache.SearchByCustomerNumberAsync("999");

        Assert.Equal(g1, hit!.Id);
        Assert.Null(miss);
        // Strict mock with no SearchByCustomerNumberAsync setup => any fall-through would throw.
        inner.Verify(c => c.SearchByCustomerNumberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Live_miss_falls_through_to_inner_search()
    {
        var inner = new Mock<ISettlCustomerClient>();
        SetupPagedCustomers(inner, [Cust("100", Guid.NewGuid())], pageSize: 1000, reportedTotal: 1);
        inner.Setup(c => c.SearchByCustomerNumberAsync("999", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((CustomerInfoDto?)null, (string?)null));

        var cache = new BulkCachingCustomerClient(inner.Object, dryRun: false, new BulkReadCacheStats(), log: null);
        await cache.PreloadAsync(CancellationToken.None);

        await cache.SearchByCustomerNumberAsync("100"); // hit — no fall-through
        await cache.SearchByCustomerNumberAsync("999"); // miss — must fall through

        inner.Verify(c => c.SearchByCustomerNumberAsync("999", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        inner.Verify(c => c.SearchByCustomerNumberAsync("100", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Link_get_short_circuits_for_non_existing_guid_but_passes_through_existing()
    {
        var existing = Guid.NewGuid();
        var inner = new Mock<ISettlCustomerClient>();
        SetupPagedCustomers(inner, [Cust("100", existing)], pageSize: 1000, reportedTotal: 1);
        inner.Setup(c => c.GetMeteringPointsAsync(existing, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<CustomerMeteringPointDto>(), (string?)null));

        var stats = new BulkReadCacheStats();
        var cache = new BulkCachingCustomerClient(inner.Object, dryRun: true, stats, log: null);
        await cache.PreloadAsync(CancellationToken.None);

        var fabricated = Guid.NewGuid(); // a "would-create" dry-run Guid — not in the snapshot
        var (links, _) = await cache.GetMeteringPointsAsync(fabricated);
        await cache.GetMeteringPointsAsync(existing);

        Assert.Empty(links!);
        inner.Verify(c => c.GetMeteringPointsAsync(fabricated, It.IsAny<CancellationToken>()), Times.Never);
        inner.Verify(c => c.GetMeteringPointsAsync(existing, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Failed_bulk_load_degrades_to_pass_through()
    {
        var inner = new Mock<ISettlCustomerClient>();
        inner.Setup(c => c.FindCustomersAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<CustomerInfoDto>(), 0, "boom"));
        inner.Setup(c => c.SearchByCustomerNumberAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Cust("100", Guid.NewGuid()), (string?)null));

        var cache = new BulkCachingCustomerClient(inner.Object, dryRun: true, new BulkReadCacheStats(), log: null);
        await cache.PreloadAsync(CancellationToken.None); // must not throw

        // Even in dry-run, a failed snapshot must fall through rather than answer "not found".
        var (result, _) = await cache.SearchByCustomerNumberAsync("100");
        Assert.NotNull(result);
        inner.Verify(c => c.SearchByCustomerNumberAsync("100", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pagination_walks_all_pages_via_total_count_guard()
    {
        var all = Enumerable.Range(0, 5).Select(i => Payer($"p{i}", Guid.NewGuid())).ToList();
        var inner = new Mock<ISettlPayerClient>();
        // Pages of 2, total reported = 5 → loop must continue past short pages until count reached.
        inner.Setup(p => p.FindPayersAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int limit, int skip, CancellationToken _) => (all.Skip(skip).Take(2).ToList(), 5, (string?)null));

        var stats = new BulkReadCacheStats();
        var cache = new BulkCachingPayerClient(inner.Object, dryRun: true, stats, log: null);
        await cache.PreloadAsync(CancellationToken.None);

        Assert.Equal(5, stats.PayersLoaded);
        for (var i = 0; i < 5; i++)
        {
            var (hit, _) = await cache.SearchByPayerIdentificationAsync($"p{i}");
            Assert.NotNull(hit);
        }
    }

    [Fact]
    public async Task Pagination_terminates_on_empty_page_when_total_unknown()
    {
        var all = Enumerable.Range(0, 3).Select(i => Payer($"p{i}", Guid.NewGuid())).ToList();
        var inner = new Mock<ISettlPayerClient>();
        // total reported = 0 (server omits count) → only the empty page ends the walk.
        inner.Setup(p => p.FindPayersAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int limit, int skip, CancellationToken _) => (all.Skip(skip).Take(2).ToList(), 0, (string?)null));

        var stats = new BulkReadCacheStats();
        var cache = new BulkCachingPayerClient(inner.Object, dryRun: true, stats, log: null);
        await cache.PreloadAsync(CancellationToken.None);

        Assert.Equal(3, stats.PayersLoaded);
    }
}
