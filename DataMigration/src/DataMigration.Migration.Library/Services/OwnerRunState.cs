namespace DataMigration.Migration.Services;

/// <summary>The owner-customer a cluster resolved to: Settl id and CustomerNumber.</summary>
public readonly record struct ResolvedOwner(Guid OwnerId, string CustomerNumber);

/// <summary>
/// What a batched run remembers about its owner-customers between batches.
///
/// <para>Without it, every batch that meets a cluster resolves it from scratch. In a live run
/// that means creating a second owner-customer for a cluster the previous batch already gave
/// one; in a dry-run it means asking Settl for the first vacant number from 900000 and being
/// told 900000 every time, because a dry-run creates nothing — five unrelated clusters then
/// come back with the same number.</para>
///
/// <para>Held by the caller for the length of the run and handed to each batch through
/// <c>CreateOptions.OwnerState</c>, the same way the Settl clients are. Batches run one after
/// another, so this is deliberately not synchronised.</para>
/// </summary>
public sealed class OwnerRunState
{
    private readonly Dictionary<string, ResolvedOwner> _byCluster = new(StringComparer.Ordinal);

    /// <summary>
    /// The run's number allocator, kept alive across batches so numbers keep counting upwards
    /// instead of restarting at 900000. Created on first use by the resolver.
    /// </summary>
    internal OwnerNumberAllocator? Allocator { get; set; }

    public bool TryGet(string clusterKey, out ResolvedOwner owner) =>
        _byCluster.TryGetValue(clusterKey, out owner);

    public void Record(string clusterKey, ResolvedOwner owner) => _byCluster[clusterKey] = owner;

    /// <summary>Clusters that have been given an owner so far, for reporting.</summary>
    public IReadOnlyDictionary<string, ResolvedOwner> Owners => _byCluster;
}
