using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Runners;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// Ensures <see cref="FixTestAccounts.All"/> are provisioned in Settl featuretest via
/// <see cref="CreateRunner"/>. Idempotent — <c>CreateRunner</c> already skips customers
/// that exist and counts them under <c>CustomersExisting</c>, so repeated runs are
/// cheap (a few GETs per account, no writes).
///
/// <para>First-ever run against a fresh featuretest WILL write customers, payers, MPs,
/// and billing configurations. This is the intended effect — fix-* tests can only run
/// against realistic data.</para>
///
/// <para>Use via <c>IClassFixture&lt;ProvisioningFixture&gt;</c> on test classes that
/// need the 4 curated accounts. Fixture construction is slow (one-shot Xellent read +
/// up to 4 Settl create-flows) — xunit shares it across the class's tests.</para>
/// </summary>
public class ProvisioningFixture : IAsyncLifetime
{
    private readonly SettlFeaturetestFixture _settl;

    public MigrationReport? LastReport { get; private set; }

    public ProvisioningFixture()
    {
        _settl = new SettlFeaturetestFixture();
    }

    public async Task InitializeAsync()
    {
        // Accounts with ended contracts need historical reference dates so CreateRunner
        // finds their active contract. Current accounts are provisioned with today's date.
        //   - InactivePastCutoff (468595): contract ended 2025-04-14 → ref date 2025-04-01
        //   - Representative (372328): contract ended 2023-07-12 → ref date 2023-07-01
        //   - NonRepresentative (372537) is also in the same group, but CreateRunner at
        //     input=[372328] at 2023-07-01 already materialises customer 372328 in Settl.
        //     We don't provision 372537 separately — it lives in Xellent but is merged
        //     into 372328 by the representation principle.
        var historicalInactive = new[] { FixTestAccounts.InactivePastCutoff };
        var historicalRepresentative = new[] { FixTestAccounts.Representative };
        var historicalAccounts = historicalInactive.Concat(historicalRepresentative)
            .Append(FixTestAccounts.NonRepresentative)
            .ToHashSet();
        var currentAccounts = FixTestAccounts.All
            .Where(a => !historicalAccounts.Contains(a))
            .ToArray();

        var missingCurrent = await FindMissingAsync(currentAccounts);
        var missingInactive = await FindMissingAsync(historicalInactive);
        var missingRepresentative = await FindMissingAsync(historicalRepresentative);

        if (missingCurrent.Count == 0 && missingInactive.Count == 0 && missingRepresentative.Count == 0)
            return;

        if (missingCurrent.Count > 0)
            await RunCreateAsync(missingCurrent.ToArray(), DateTime.Today, "current");

        if (missingInactive.Count > 0)
        {
            // InactivePastCutoff contract ended 2025-04-14 — use a date before that so
            // CreateRunner finds the active contract. A separate runner instance is required
            // because RunAsync disposes the XellentDbContext when _ownsContext = true.
            await RunCreateAsync(missingInactive.ToArray(), new DateTime(2025, 4, 1), "inactive");
        }

        if (missingRepresentative.Count > 0)
        {
            // Representative (372328) contract ended 2023-07-12 — use a date before that.
            await RunCreateAsync(missingRepresentative.ToArray(), new DateTime(2023, 7, 1), "representative");
        }
    }

    private async Task RunCreateAsync(string[] accounts, DateTime referenceDate, string label)
    {
        var createOptions = new CreateOptions
        {
            Accounts = accounts,
            DataAreaId = "hol",
            DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity,
            DryRun = false,
            ReferenceDate = referenceDate,
        };

        var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(_settl.XellentConnectionString,
                sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(dbOptions);
        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(referenceDate),
            Commodity: Commodity.Electricity);
        var mapper = new XellentDomainMapper(context, warnings, mapperOptions, createOptions.DataAreaId);
        var loadResult = await mapper.LoadAsync(accounts);

        var runner = new CreateRunner(
            _settl.ApiOptions,
            _settl.CustomerClient, _settl.PayerClient, _settl.MeteringPointClient,
            _settl.ContractClient, _settl.SettlementGroupClient, warnings);
        var (exitCode, report) = await runner.RunAsync(createOptions, loadResult);
        LastReport = report;
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"ProvisioningFixture: CreateRunner ({label}) failed for [{string.Join(", ", accounts)}]. " +
                $"Errors: {string.Join("; ", report.Errors.Take(3))}");
    }

    private async Task<List<string>> FindMissingAsync(IEnumerable<string> accounts)
    {
        var missing = new List<string>();
        foreach (var accountNum in accounts)
        {
            var (customer, _) = await _settl.CustomerClient.SearchByCustomerNumberAsync(accountNum);
            if (customer == null)
                missing.Add(accountNum);
        }
        return missing;
    }

    public Task DisposeAsync()
    {
        _settl.Dispose();
        return Task.CompletedTask;
    }

    public SettlFeaturetestFixture Settl => _settl;
}
