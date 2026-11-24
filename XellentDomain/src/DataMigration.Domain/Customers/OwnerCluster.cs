namespace DataMigration.Domain.Customers;

/// <summary>
/// A synthetic owner-customer wrapping ≥2 representative customers that share the same
/// organisational grouping. Lives one level above representatives — its members can have
/// different CVR (e.g. branches of a chain or municipal-group entities).
///
/// <para><see cref="GroupCode"/> is the source-system's stable group identifier
/// retained for traceability. The cluster grouping is determined by the source loader;
/// the resulting record is source-agnostic. Empty when no representative customers
/// share the same grouping.</para>
///
/// <para><see cref="RepresentativeAccountNumber"/> is the member account picked by the
/// loader to represent the cluster downstream (used as the owner-customer's key).</para>
/// </summary>
public sealed record OwnerCluster(
    string GroupCode,
    string OwnerName,
    IReadOnlyList<string> MemberAccountNumbers,
    string RepresentativeAccountNumber);
