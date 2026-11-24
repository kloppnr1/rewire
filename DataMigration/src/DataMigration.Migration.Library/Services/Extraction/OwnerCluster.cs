namespace DataMigration.Migration.Services.Extraction;

/// <summary>
/// A set of post-merge representative accounts that should hang under a synthetic owner-customer.
///
/// <para>One cluster per unique TG4 where ≥2 distinct representative accounts share it.
/// Cluster key is the TG4 value; owner name is the TG4's GROUPNAME (fallback: TG4 itself).
/// Reps without a TG4 are not eligible. CVR can differ across cluster members — the
/// owner-customer's CVR is copied from the lowest member.</para>
/// </summary>
public record OwnerCluster(
    string ClusterKey,
    string OwnerName,
    List<string> MemberAccountnums,
    string LowestAccountnum);
