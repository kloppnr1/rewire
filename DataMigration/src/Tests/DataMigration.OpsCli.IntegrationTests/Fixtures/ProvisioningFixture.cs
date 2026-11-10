using System.Text.RegularExpressions;
using DataMigration.Core;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.IntegrationTests.Fakes;

namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// <see cref="FixTestAccounts.HappyPath"/> migrated into a fake Settl tenant by the real
/// <see cref="CreateRunner"/>, from the account's anonymised Xellent snapshot. The reconcile flow
/// tests then scan, mutate, apply and scan again against the same tenant — all through the real
/// typed clients over <see cref="FakeSettlApi"/>.
///
/// <para>The snapshot's contracts are shifted to run until a year from now
/// (<c>XellentDbSeeder.ActivateContracts</c>), so today is a valid change of supplier, as it was
/// for the always-active account in the test copy of Xellent.</para>
///
/// <para>The Settl contracts the account links to are found the way an operator finds them: a dry
/// run against an empty tenant names every contract it needs and cannot find, and those names are
/// then added to the tenant before the real run.</para>
/// </summary>
public class ProvisioningFixture : IAsyncLifetime
{
    public static readonly ReconcileMappingOptions Mapping = new()
    {
        DataAreaId = "hol",
        DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
    };

    public static readonly DateOnly Cos = DateOnly.FromDateTime(DateTime.Today);
    public static readonly BillingExemptionConfig NoExemption = new(null, null, null, 0d);

    public FakeXellent Xellent { get; } = new([FixTestAccounts.HappyPath], activateContracts: true);
    public FakeSettl Settl { get; } = new();
    public string XellentConnectionString => Xellent.ConnectionString;
    public MigrationReport? LastReport { get; private set; }

    public async Task InitializeAsync()
    {
        Settl.Api.MarketBalanceAreas.Add((Guid.NewGuid(), "DK1"));
        Settl.Api.MarketBalanceAreas.Add((Guid.NewGuid(), "DK2"));

        var (_, _, warnings) = await RunCreateAsync(dryRun: true);
        foreach (var name in warnings.GetErrors()
                     .Select(e => Regex.Match(e, "^Contract '(?<name>.+)' not found"))
                     .Where(m => m.Success).Select(m => m.Groups["name"].Value).Distinct())
            Settl.Api.Contracts.Add(new FakeSettlApi.FakeContract(Guid.NewGuid(), name, "Electricity"));

        var (exitCode, report, liveWarnings) = await RunCreateAsync(dryRun: false);
        LastReport = report;
        if (exitCode != 0 || Settl.Api.Customers.Count == 0)
            throw new InvalidOperationException(
                $"ProvisioningFixture: create failed for {FixTestAccounts.HappyPath} (exit {exitCode}). " +
                $"Errors: {string.Join("; ", report.Errors.Concat(liveWarnings.GetErrors()).Take(5))}. " +
                $"Unknown fake routes: {string.Join(", ", Settl.Api.UnknownRoutes.Distinct().Take(5))}");
    }

    private async Task<(int ExitCode, MigrationReport Report, WarningCollector Warnings)> RunCreateAsync(bool dryRun)
    {
        var options = CreateOptionsFactory.BuildFromMapping(
            Mapping, [FixTestAccounts.HappyPath], env: null, Cos.AddDays(-1).ToDateTime(TimeOnly.MinValue),
            NoExemption, [], Cos, dryRun: dryRun);
        var (loadResult, warnings) = await MigrationDomainLoad.LoadAsync(Xellent.ConnectionString, options);
        var runner = new CreateRunner(
            Settl.ApiOptions, Settl.CustomerClient, Settl.PayerClient, Settl.MeteringPointClient,
            Settl.ContractClient, Settl.SettlementGroupClient, warnings)
        {
            OnLogLine = _ => { },
        };
        var (exitCode, report) = await runner.RunAsync(options, loadResult);
        return (exitCode, report, warnings);
    }

    public Task DisposeAsync()
    {
        Settl.Dispose();
        Xellent.Dispose();
        return Task.CompletedTask;
    }
}
