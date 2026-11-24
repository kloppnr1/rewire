namespace DataMigration.Domain.Xellent;

/// <summary>
/// One CVR+Fingerprint merge group as resolved by <see cref="XellentDomainMapper.LoadAsync"/>.
/// <see cref="RepresentativeAccount"/> is the winning (lowest) accountnum; the remaining
/// <see cref="MemberAccounts"/> map to the same Settl customer.
///
/// <para><see cref="GroupNum"/> is <c>ExuTechnicgroup4</c> — used by the owner-cluster
/// detection that wraps ≥2 representatives sharing a TG4 under a synthetic owner-customer.
/// Null when the account has no TG4 or the group is on the ignored list.</para>
/// </summary>
public sealed record AccountGrouping(
    string RepresentativeAccount,
    IReadOnlyList<string> MemberAccounts,
    string? GroupNum,
    string? GroupName,
    string? Cvr,
    IReadOnlyDictionary<string, string> AccountNames);
