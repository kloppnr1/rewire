using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.Migration.Services.Extraction;
using DataMigration.Migration.Tests.Fixtures;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Tests.Extraction;

/// <summary>
/// What a batched run has to remember about its owner-customers. Each batch used to resolve every
/// cluster it met from scratch, so a cluster spread over four batches got four owner-customers in
/// Settl and — in a dry-run, where nothing is created and 900000 keeps reading as vacant — clusters
/// collided on the same number.
/// </summary>
public class OwnerRunStateTests
{
    private static CreateCustomerDto Customer(string account) => new()
    {
        Id = Guid.NewGuid(),
        CustomerNumber = account,
        VatIdentificationNumber = "40626581",
    };

    private static OwnerCluster Cluster(string key, params string[] members) =>
        new(ClusterKey: key, OwnerName: $"Klynge {key}", MemberAccountnums: [.. members], LowestAccountnum: members[0]);

    private static async Task<(List<OwnerCustomerInfo> Owners, List<CreateCustomerDto> Customers)> RunBatchAsync(
        OwnerRunState? state, List<OwnerCluster> clusters, params string[] accountsInBatch)
    {
        var customers = accountsInBatch.Select(Customer).ToList();
        var owners = new List<OwnerCustomerInfo>();
        var resolver = new OwnerClusterResolver(
            new Fixtures.MockCustomerClient(), new WarningCollector(), _ => { }, state);

        await resolver.ApplyAsync(
            customers, clusters, new Dictionary<Guid, string>(), owners, dryRun: true, CancellationToken.None);

        return (owners, customers);
    }

    /// <summary>
    /// Two batches, one cluster. The second batch must land on the owner the first one made —
    /// same id, same number — not make its own.
    /// </summary>
    [Fact]
    public async Task A_cluster_met_twice_keeps_the_owner_the_first_batch_gave_it()
    {
        var state = new OwnerRunState();
        var clusters = new List<OwnerCluster> { Cluster("NSDades", "239246", "239250", "239254") };

        var first = await RunBatchAsync(state, clusters, "239246", "239250");
        var second = await RunBatchAsync(state, clusters, "239254");

        var a = Assert.Single(first.Owners);
        var b = Assert.Single(second.Owners);
        Assert.Equal(a.OwnerId, b.OwnerId);
        Assert.Equal(a.CustomerNumber, b.CustomerNumber);
    }

    /// <summary>Different clusters get different numbers, even across batches.</summary>
    [Fact]
    public async Task Numbers_keep_counting_upwards_across_batches()
    {
        var state = new OwnerRunState();
        var first = await RunBatchAsync(
            state, [Cluster("NSDades", "239246", "239250")], "239246", "239250");
        var second = await RunBatchAsync(
            state, [Cluster("HCPremium", "512001", "512002")], "512001", "512002");

        Assert.Equal("900000", Assert.Single(first.Owners).CustomerNumber);
        Assert.Equal("900001", Assert.Single(second.Owners).CustomerNumber);
    }

    /// <summary>
    /// Without the shared memory — a run that resolves its own clusters — every batch starts over
    /// at 900000. This is the behaviour that made two unrelated clusters share a number.
    /// </summary>
    [Fact]
    public async Task Without_shared_memory_every_batch_starts_over()
    {
        var first = await RunBatchAsync(null, [Cluster("NSDades", "239246", "239250")], "239246", "239250");
        var second = await RunBatchAsync(null, [Cluster("HCPremium", "512001", "512002")], "512001", "512002");

        Assert.Equal("900000", Assert.Single(first.Owners).CustomerNumber);
        Assert.Equal("900000", Assert.Single(second.Owners).CustomerNumber);
    }

    /// <summary>
    /// A batch holding a single member of a run-level cluster must still link it. The two-member
    /// rule belongs to the cluster, and the cluster already passed it on the whole scope.
    /// </summary>
    [Fact]
    public async Task A_batch_holding_one_member_of_a_run_level_cluster_still_links_it()
    {
        var state = new OwnerRunState();
        var clusters = new List<OwnerCluster> { Cluster("NSDades", "239246", "239250", "239254") };

        var (owners, customers) = await RunBatchAsync(state, clusters, "239254");

        Assert.Single(owners);
        Assert.NotNull(Assert.Single(customers).Owner);
    }

    /// <summary>
    /// A run that derives its own clusters keeps the old rule: one member is not worth an
    /// owner-customer, because there is no wider scope that says otherwise.
    /// </summary>
    [Fact]
    public async Task A_run_without_shared_memory_still_skips_a_one_member_cluster()
    {
        var (owners, customers) = await RunBatchAsync(
            null, [Cluster("NSDades", "239246", "239250", "239254")], "239254");

        Assert.Empty(owners);
        Assert.Null(Assert.Single(customers).Owner);
    }
}
