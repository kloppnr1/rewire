using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Tests.Services;

public class XellentChargeSubscriptionReaderTests
{
    [Theory]
    [InlineData("OAboEjeMEL", "Privat_Opladning_Eje")]
    [InlineData("OAboEje0", "Privat_Opladning_Eje")]
    [InlineData("OAboEjeUEL", "Privat_Opladning_Eje")]
    [InlineData("OAboLejMEL", "Privat_Opladning_Leje")]
    [InlineData("Gl.CLejMEL", "Privat_Opladning_Leje")]
    [InlineData("NY.CLejMEL", "Privat_Opladning_Leje")]
    [InlineData("O HCEjeML", "Privat_Opladning_Eje")]
    [InlineData("O HCEjeUL3", "Privat_Opladning_Eje")]
    [InlineData("O HCEjeUL4", "Privat_Opladning_Eje")]
    [InlineData("O HCEjeUL5", "Privat_Opladning_Eje")]
    [InlineData("O HCLejML", "Privat_Opladning_Leje")]
    public void Confirmed_subscription_decides_ownership(string product, string expected) =>
        Assert.Equal(expected, XellentChargeSubscriptionReader.SegmentFor([product]));

    [Fact]
    public void Several_installations_with_the_same_ownership_still_decide() =>
        Assert.Equal("Privat_Opladning_Eje",
            XellentChargeSubscriptionReader.SegmentFor(["OAboEjeMEL", "OAboEje0"]));

    [Fact]
    public void Email_hierarchy_handles_mixed_and_unusable_products()
    {
        Assert.Equal("Privat_Opladning_Eje", XellentChargeSubscriptionReader.SegmentFor(["OAboEjeMEL", "OAboLejMEL"]));
        Assert.Equal("Privat", XellentChargeSubscriptionReader.SegmentFor(["O HCEjeUL2"]));
        Assert.Equal("Privat_Opladning_Eje", XellentChargeSubscriptionReader.SegmentFor(["OAboEjeMEL", "O HCEjeUL2"]));
        Assert.Equal("Privat", XellentChargeSubscriptionReader.SegmentFor([]));
    }
}
