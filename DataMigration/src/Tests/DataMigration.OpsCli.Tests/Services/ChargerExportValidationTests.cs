using DataMigration.OpsCli.Runners;

namespace DataMigration.OpsCli.Tests.Services;

public class ChargerExportValidationTests
{
    [Theory]
    [InlineData("571313103204219912", "—")]
    [InlineData("571313144500703972", "-")]
    public void Empty_MP_DAR_is_nonblocking_for_offline_export(string mp, string separator)
    {
        Assert.True(GenerateStateFromSettlRunner.IsEmptyDarError(
            $"MP {mp}: invalid DAR identifier '00000000-0000-0000-0000-000000000000' {separator} not a valid Guid [Xellent: Addressid / ExuAddressid]"));
    }

    [Theory]
    [InlineData("MP 123: invalid DAR identifier 'bad' — not a valid Guid [Xellent: Addressid / ExuAddressid]")]
    [InlineData("Customer 123: invalid DAR identifier '00000000-0000-0000-0000-000000000000' — not a valid Guid [Xellent: Addressid / ExuAddressid]")]
    [InlineData("Charger mapping failed")]
    public void Other_mapping_errors_remain_blocking(string error)
    {
        Assert.False(GenerateStateFromSettlRunner.IsEmptyDarError(error));
    }
}
