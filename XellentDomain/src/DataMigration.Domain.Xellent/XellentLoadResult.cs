using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Loader-output from <see cref="XellentDomainMapper.LoadAsync"/>. Bundles the entities
/// that were loaded for the requested account-set: customers (each with their inline
/// contracts), the metering points referenced by those contracts, owner-clusters that
/// span representatives, and joint-invoice groups linking metering points.
///
/// <para>This is a return-shape type (loader plumbing), not a domain entity. Other
/// loaders implementing the same domain may return the same shape or their own.</para>
/// </summary>
public sealed record XellentLoadResult(
    IReadOnlyList<Customer> Customers,
    IReadOnlyList<MeteringPoint> MeteringPoints,
    IReadOnlyList<OwnerCluster> OwnerClusters,
    IReadOnlyList<JointInvoiceGroup> JointInvoiceGroups,
    DateOnly SnapshotDate,
    IReadOnlyDictionary<string, string?> MpOrderIdentifiers,
    IReadOnlyList<AccountGrouping> AccountGroupings,
    IReadOnlyDictionary<string, AccountFingerprint> FingerprintByRepresentative)
{
    /// <summary>
    /// Charger installations tied to the electricity metering point they charge from, keyed by that
    /// metering point's identification. The tie is made from the electricity side — a contract part
    /// with a charger product points at exactly one E17 consumption metering point — because the
    /// charger's own contract says nothing about where it belongs.
    ///
    /// <para>Empty for non-electricity loads. An account with chargers appears here only when its
    /// charger electricity products resolve to exactly one metering point; anything else lands in
    /// <see cref="EvChargersByAccount"/>.</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> EvChargersByMeteringPoint { get; init; }
        = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);

    /// <summary>
    /// Charger installations that could not be tied to one metering point, keyed by the account they
    /// hang under — either the account holds no charger electricity product at all, or its products
    /// point at more than one E17 metering point and picking one would be a guess. The ambiguous case
    /// is also reported through the warning collector.
    ///
    /// <para>They are kept rather than dropped: a charger nobody can place is exactly the row a human
    /// has to look at.</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> EvChargersByAccount { get; init; }
        = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);
}
