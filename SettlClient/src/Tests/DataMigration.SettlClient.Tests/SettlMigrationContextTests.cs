using DataMigration.SettlClient;

namespace DataMigration.SettlClient.Tests;

public class SettlMigrationContextTests
{
    [Fact]
    public void DryRun_context_has_DryRun_true()
    {
        var ctx = new SettlMigrationContext(
            DryRun: true,
            ChangeOfSupplierDate: DateTime.Today,
            MpContractLinks: [],
            ExemptionContext: null,
            SettlementGroupMap: null,
            OwnerByCustomerNumber: null);

        Assert.True(ctx.DryRun);
    }

    [Fact]
    public void NonDryRun_context_has_DryRun_false()
    {
        var ctx = new SettlMigrationContext(
            DryRun: false,
            ChangeOfSupplierDate: DateTime.Today,
            MpContractLinks: [],
            ExemptionContext: null,
            SettlementGroupMap: null,
            OwnerByCustomerNumber: null);

        Assert.False(ctx.DryRun);
    }
}
