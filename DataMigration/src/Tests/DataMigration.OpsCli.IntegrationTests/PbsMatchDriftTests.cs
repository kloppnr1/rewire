using DataMigration.Core.Reporting;
using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.XellentData;
using DataMigration.XellentData.Reporting;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// What the account-level PBS fallback actually changes, measured against live Xellent.
///
/// <para>Deliberately not a golden. Migrating a customer terminates their PBS agreement in Xellent,
/// so a snapshot of the reader's output goes stale with every wave — the file would turn red for
/// reasons that have nothing to do with the code, and a golden nobody trusts is a golden nobody
/// reads.</para>
///
/// <para>The delta is stable where the snapshot is not: rows carrying
/// <see cref="XellentPbsContract.PbsAgreementSourceContract"/> are exactly the rows the old rule
/// dropped to "no PBS in Xellent", whenever the run happens. The test writes them out as a report
/// and asserts the properties that must hold however the data moves.</para>
/// </summary>
public class PbsMatchDriftTests(SettlFeaturetestFixture fixture) : IClassFixture<SettlFeaturetestFixture>
{
    private const string DataAreaId = "hol";

    /// <summary>
    /// An account that pays by PBS through a contract other than the one owning its electricity
    /// metering point — the whole case for the fallback. Its metering-point contract 10288032
    /// carries <c>Pbstransactioncode = 0</c>, while contract 10065009 holds an active agreement
    /// (code 1) on a blank-category umbrella contract.
    ///
    /// <para>Not 947686 (the account named in <c>4dc57f2</c>): that one exists in production but
    /// not in AXDB50_TEST, and a pinned account the test database has never heard of makes this
    /// test assert nothing.</para>
    /// </summary>
    private const string AccountWithPbsOnAnotherContract = "100009";

    private static readonly string[] PinnedAccounts =
    [
        AccountWithPbsOnAnotherContract,
        // Same shape, so a single account going quiet does not silently empty the report.
        "100030",
        "100054",
        // No borrowing expected here — the delta must not simply be "every account".
        "403522",
        "446977",
    ];

    [Fact]
    public async Task The_borrowed_agreements_are_reported_and_hold_together()
    {
        // Maximally inclusive on purpose: no cutover date and terminated agreements included. Both
        // filters depend on which wave has run, and this test is about the fallback rule, not about
        // the calendar. Narrowing them would make the result drift with the migration schedule.
        var result = await XellentPbsContractReader.FetchAsync(
            fixture.XellentConnectionString, DataAreaId,
            PinnedAccounts.ToHashSet(StringComparer.Ordinal),
            includeTerminated: true, changeOfSupplierDate: null);

        var delta = result.Contracts.Where(c => c.PbsAgreementSourceContract is not null).ToList();

        await WriteDeltaReportAsync(delta);

        // A borrowed agreement that came from the row's own contract is not borrowed at all — it
        // would mean the fallback fired where nothing was missing.
        Assert.All(delta, c => Assert.NotEqual(c.ContractNumber, c.PbsAgreementSourceContract));
        Assert.All(delta, c => Assert.NotEqual(0, c.PbsAgreementNumber));

        // The agreement must come from the row's own main contract, and from nothing else. A
        // sibling contract's agreement is another contract's arrangement, and borrowing it would
        // report PBS for customers create migrated without any — the disagreement this fallback
        // exists to remove, pointing the other way. Also covers the same-account requirement:
        // Referencetomaincontract is read off the row itself.
        Assert.All(delta, c => Assert.Equal(c.Referencetomaincontract, c.PbsAgreementSourceContract));

        await AssertSourceContractsBelongToTheSameAccountAsync(delta);

        // Without this the test goes quietly green the day the fallback stops firing, and reports
        // nothing at all.
        Assert.Contains(delta, c => c.CustomerAccount == AccountWithPbsOnAnotherContract);
    }

    private async Task AssertSourceContractsBelongToTheSameAccountAsync(List<XellentPbsContract> delta)
    {
        if (delta.Count == 0) return;

        var sourceContracts = delta
            .Select(c => c.PbsAgreementSourceContract!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await using var db = new XellentDbContext(
            new DbContextOptionsBuilder<XellentDbContext>()
                .UseSqlServer(fixture.XellentConnectionString, sql => sql.UseCompatibilityLevel(110))
                .Options);

        var accountByContract = (await db.ExuContractTables
                .Where(c => c.Dataareaid == DataAreaId && sourceContracts.Contains(c.Contractnum))
                .Select(c => new { c.Contractnum, c.Custaccount })
                .ToListAsync())
            .ToDictionary(x => x.Contractnum.Trim(), x => x.Custaccount.Trim(), StringComparer.Ordinal);

        foreach (var row in delta)
        {
            Assert.True(
                accountByContract.TryGetValue(row.PbsAgreementSourceContract!, out var owner),
                $"Source contract {row.PbsAgreementSourceContract} does not exist in Xellent.");
            Assert.Equal(row.CustomerAccount, owner);
        }
    }

    /// <summary>
    /// The drift report itself: one line per metering point that gained a PBS agreement it did not
    /// have before. Written as an artefact rather than committed — it describes the database at the
    /// moment of the run, and a file in git would claim to describe it forever.
    /// </summary>
    private static async Task WriteDeltaReportAsync(List<XellentPbsContract> delta)
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "artifacts"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"pbs-fallback-delta_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var lines = new List<string>
        {
            "Kundenummer;Målepunkt;Egen kontrakt;PBS fra kontrakt;Commodity;PBS-aftale ID"
        };
        lines.AddRange(delta
            .OrderBy(c => c.CustomerAccount, StringComparer.Ordinal)
            .ThenBy(c => c.MeteringpointId, StringComparer.Ordinal)
            .Select(c => string.Join(';',
                c.CustomerAccount, c.MeteringpointId, c.ContractNumber,
                c.PbsAgreementSourceContract, c.PbsAgreementSourceDeliveryCategory,
                c.PbsAgreementNumber)));

        await File.WriteAllLinesAsync(path, lines);

        Console.WriteLine($"{delta.Count} målepunkter fik en PBS-aftale fra en anden kontrakt");
        Console.WriteLine($"  Drift-rapport: {path}");
    }
}
