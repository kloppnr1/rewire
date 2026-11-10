using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// Locks the joint-invoice singleton-demotion rule: a JointInvoiceGroupId is only kept when ≥2
/// migrated MPs share it. Single-member "groups" (an el-MP whose contract references a main that
/// has no second migrated el-MP — e.g. an old vand/varme umbrella) must be demoted to solo so
/// billing does not mark them JointInvoice=true. Regression guard for the Settl billing false-positive.
/// </summary>
public class JointInvoiceGroupTests
{
    private static MeteringPoint Mp(string id, string? jointGroupId) => new(
        Identification: id,
        InstallationAddress: new Address("Vej 1", null, null, null, "8900", "Randers", null, null, null, "DK", null),
        Commodity: Commodity.Electricity,
        Meter: null,
        ContactPoints: Array.Empty<ContactPoint>(),
        JointInvoiceGroupId: jointGroupId,
        ConnectionStatus: ConnectionStatus.Connected,
        InDelivery: false,
        Grid: null,
        Start: new DateOnly(2024, 1, 1));

    [Fact]
    public void SingleMemberGroup_IsDemotedToSolo()
    {
        var input = new[]
        {
            Mp("571000000000000001", "10069804"), // alone in group 10069804
            Mp("571000000000000002", null),        // already solo
        };

        var result = MeteringPointMapper.DemoteSingletonJointGroups(input);

        Assert.Null(result.Single(mp => mp.Identification == "571000000000000001").JointInvoiceGroupId);
        Assert.Null(result.Single(mp => mp.Identification == "571000000000000002").JointInvoiceGroupId);
    }

    [Fact]
    public void TwoMemberGroup_IsKept()
    {
        var input = new[]
        {
            Mp("571000000000000010", "10457529"),
            Mp("571000000000000011", "10457529"),
        };

        var result = MeteringPointMapper.DemoteSingletonJointGroups(input);

        Assert.All(result, mp => Assert.Equal("10457529", mp.JointInvoiceGroupId));
    }

    [Fact]
    public void MixedGroups_OnlySingletonsDemoted()
    {
        var input = new[]
        {
            Mp("571000000000000020", "10404506"), // singleton → demote
            Mp("571000000000000021", "10273617"), // 2-member → keep
            Mp("571000000000000022", "10273617"),
        };

        var result = MeteringPointMapper.DemoteSingletonJointGroups(input);

        Assert.Null(result.Single(mp => mp.Identification == "571000000000000020").JointInvoiceGroupId);
        Assert.Equal("10273617", result.Single(mp => mp.Identification == "571000000000000021").JointInvoiceGroupId);
        Assert.Equal("10273617", result.Single(mp => mp.Identification == "571000000000000022").JointInvoiceGroupId);
    }
}
