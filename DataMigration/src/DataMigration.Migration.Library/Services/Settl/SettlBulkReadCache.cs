using DataMigration.SettlApi.Clients;

namespace DataMigration.Migration.Services.Settl;

/// <summary>
/// A warmed set of bulk read-caches over the Settl clients: paginated snapshots of customers,
/// payers and metering points, served from memory in place of one existence-search per entity.
/// Each of the three is optional — a snapshot the run never reads is pure cost, and the client
/// for it is then handed back unwrapped.
///
/// <para><see cref="Runners.CreateRunner"/> builds one of these for itself when
/// <c>CreateOptions.BulkCacheReads</c> is on. It is public so a caller that runs the pipeline
/// <b>several times over one account set</b> — a whole-category migration split into batches —
/// can warm it once and hand the wrapped clients to every run with <c>BulkCacheReads = false</c>.
/// Otherwise each batch re-paginates the entire Settl customer, payer and metering-point set.</para>
///
/// <para><b>Reuse is safe across runs.</b> A dry-run creates nothing, so the snapshot cannot go
/// stale. A live run does create entities, but a cache miss falls through to a real search rather
/// than being answered "not found" — so an entity created by an earlier batch is still found, and
/// a duplicate create is not possible. That fall-through is the same mechanism that protects a
/// single long run, which can equally outlive its own snapshot.</para>
/// </summary>
public sealed class SettlBulkReadCache
{
    public ISettlCustomerClient CustomerClient { get; }
    public ISettlPayerClient PayerClient { get; }
    public ISettlMeteringPointClient MeteringPointClient { get; }
    public BulkReadCacheStats Stats { get; }

    private SettlBulkReadCache(
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient meteringPointClient,
        BulkReadCacheStats stats)
    {
        CustomerClient = customerClient;
        PayerClient = payerClient;
        MeteringPointClient = meteringPointClient;
        Stats = stats;
    }

    /// <summary>
    /// Wraps the given clients and preloads the snapshots in parallel. A failed preload leaves
    /// that decorator as a pass-through — an incomplete snapshot is never served as authoritative.
    /// </summary>
    /// <param name="preloadMeteringPoints">
    /// Water/heat metering points use a separate search endpoint that is not cached, so the
    /// market-v3 snapshot is only worth loading when the extract actually holds electricity MPs.
    /// </param>
    /// <param name="preloadCustomers">
    /// Off for a run that never looks a customer up — a metering-points-only run stops before the
    /// customer loop, so paginating every customer in the environment buys it nothing. The client
    /// handed back is then the caller's own, untouched: no half-warm decorator that could answer
    /// "not found" from an empty snapshot and provoke a duplicate create.
    /// </param>
    /// <param name="preloadPayers">Same, for payers.</param>
    public static async Task<SettlBulkReadCache> CreateAsync(
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient meteringPointClient,
        bool dryRun,
        string deliveryCategory,
        bool preloadMeteringPoints,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        bool preloadCustomers = true,
        bool preloadPayers = true)
    {
        var stats = new BulkReadCacheStats();

        var customerCache = preloadCustomers
            ? new BulkCachingCustomerClient(customerClient, dryRun, stats, log)
            : null;
        var payerCache = preloadPayers
            ? new BulkCachingPayerClient(payerClient, dryRun, stats, log)
            : null;
        var mpCache = preloadMeteringPoints
            ? new BulkCachingMeteringPointClient(meteringPointClient, dryRun, stats, log)
            : null;

        var preloads = new List<Task>();
        if (customerCache != null)
            preloads.Add(customerCache.PreloadAsync(cancellationToken));
        if (payerCache != null)
            preloads.Add(payerCache.PreloadAsync(cancellationToken));
        if (mpCache != null)
            preloads.Add(mpCache.PreloadAsync(deliveryCategory, cancellationToken));

        await Task.WhenAll(preloads);

        return new SettlBulkReadCache(
            customerCache ?? customerClient,
            payerCache ?? payerClient,
            mpCache ?? meteringPointClient,
            stats);
    }
}
