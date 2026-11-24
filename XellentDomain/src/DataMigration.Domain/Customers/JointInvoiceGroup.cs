namespace DataMigration.Domain.Customers;

/// <summary>
/// A group of metering points that are billed together on one invoice. Identified by
/// <see cref="Id"/>, which equals the master contract's number — the contract that
/// owns the joint-billing relationship. Member MPs reference this group via
/// <c>MeteringPoint.JointInvoiceGroupId</c>.
/// </summary>
public sealed record JointInvoiceGroup(
    string Id,
    string MasterContractNumber,
    IReadOnlyList<string> MemberMeteringPointIds);
