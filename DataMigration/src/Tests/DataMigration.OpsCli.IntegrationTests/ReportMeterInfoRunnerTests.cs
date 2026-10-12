using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Tests;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.IntegrationTests;

public class ReportMeterInfoRunnerTests(ProvisioningFixture fixture)
    : IClassFixture<ProvisioningFixture>
{
    [Fact]
    public async Task FixtureAccounts_MatchesGolden()
    {
        var loader = new XellentDomainLoader(fixture.Settl.XellentConnectionString);
        var runner = new ReportMeterInfoRunner(
            fixture.Settl.CustomerClient,
            fixture.Settl.MeteringPointClient,
            loader);

        var output = new StringWriter();
        var exitCode = await runner.RunAsync(new ReportMeterInfoRunOptions
        {
            ProfileName = "golden-test",
            Accounts = [FixTestAccounts.HappyPath, FixTestAccounts.AnnualConsumption],
            DataAreaId = "hol",
            ReferenceDate = new DateTime(2026, 5, 1),
            ShowAll = true,
        }, output);

        Assert.Equal(0, exitCode);
        CsvGoldenAsserter.AssertMatchesGolden(
            output.ToString(),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Goldens", "report-meter-info", "fixture-accounts.csv")));
    }
}
