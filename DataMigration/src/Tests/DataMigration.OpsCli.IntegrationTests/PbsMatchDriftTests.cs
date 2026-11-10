using DataMigration.Core.Reporting;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.XellentData.Entities;
using DataMigration.XellentData.Reporting;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// What the account-level PBS fallback changes, measured on the real reader query against an
/// in-memory Xellent built to hold every shape the rule distinguishes.
///
/// <para>The rows carrying <see cref="XellentPbsContract.PbsAgreementSourceContract"/> are exactly the
/// rows the old rule dropped to "no PBS in Xellent". The test writes them out as a report and asserts
/// the properties that must hold however the data looks.</para>
/// </summary>
public sealed class PbsMatchDriftTests : IDisposable
{
    private const string DataAreaId = "hol";

    /// <summary>
    /// Pays by PBS through a contract other than the one owning its electricity metering point: the
    /// metering-point contract carries <c>Pbstransactioncode = 0</c>, while its main contract — a
    /// blank-category umbrella contract — holds an active agreement (code 1).
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

    private readonly FakeXellent _xellent = new();

    public PbsMatchDriftTests()
    {
        var open = XellentRows.Open;
        var n = 0;
        foreach (var account in new[] { "100009", "100030", "100054" })
        {
            n++;
            var main = $"1006500{n}";
            _xellent.Add(XellentRows.For(new(account, $"1028803{n}", $"57131310000000030{n}", new(2018, 1, 1), open, MainContract: main)));
            _xellent.Add(UmbrellaWithPbs(account, main, agreement: 7000 + n));
            // A sibling contract with its own agreement: another contract's arrangement, never borrowed.
            _xellent.Add(UmbrellaWithPbs(account, $"1009900{n}", agreement: 8000 + n));
        }
        // Accounts paying PBS on their own metering-point contract: nothing to borrow.
        foreach (var (account, i) in new[] { ("403522", 1), ("446977", 2) })
        {
            var rows = XellentRows.For(new(account, $"1050000{i}", $"57131310000000040{i}", new(2018, 1, 1), open));
            var contract = rows.OfType<ExuContractTable>().Single();
            contract.Pbsagreementnum = 9000 + i;
            contract.Pbstransactioncode = 1;
            contract.Pbstransdate = new DateTime(2019, 1, 1);
            _xellent.Add(rows);
        }
    }

    public void Dispose() => _xellent.Dispose();

    private static ExuContractTable UmbrellaWithPbs(string account, string contract, int agreement) => new()
    {
        Contractnum = contract, Dataareaid = DataAreaId, Custaccount = account, Companyid = "for",
        Deliverycategory = "", Contractstartdate = new(2018, 1, 1), Contractenddate = XellentRows.Open,
        Pbsagreementnum = agreement, Pbstransactioncode = 1, Pbstransdate = new(2019, 1, 1),
        Mobilepayagreementcanceldate = XellentRows.Open,
    };

    [Fact]
    public async Task The_borrowed_agreements_are_reported_and_hold_together()
    {
        // Maximally inclusive on purpose: no cutover date and terminated agreements included. This
        // test is about the fallback rule, not about the calendar.
        var result = await XellentPbsContractReader.FetchAsync(
            _xellent.ConnectionString, DataAreaId,
            PinnedAccounts.ToHashSet(StringComparer.Ordinal),
            includeTerminated: true, changeOfSupplierDate: null);

        var delta = result.Contracts.Where(c => c.PbsAgreementSourceContract is not null).ToList();

        await WriteDeltaReportAsync(delta);

        // A borrowed agreement that came from the row's own contract is not borrowed at all — it
        // would mean the fallback fired where nothing was missing.
        Assert.All(delta, c => Assert.NotEqual(c.ContractNumber, c.PbsAgreementSourceContract));
        Assert.All(delta, c => Assert.NotEqual(0, c.PbsAgreementNumber));

        // The agreement must come from the row's own main contract, and from nothing else. A
        // sibling contract's agreement is another contract's arrangement. Also covers the
        // same-account requirement: Referencetomaincontract is read off the row itself.
        Assert.All(delta, c => Assert.Equal(c.Referencetomaincontract, c.PbsAgreementSourceContract));

        await AssertSourceContractsBelongToTheSameAccountAsync(delta);

        // Without this the test goes quietly green the day the fallback stops firing.
        Assert.Contains(delta, c => c.CustomerAccount == AccountWithPbsOnAnotherContract);
        // And the accounts paying on their own contract borrow nothing.
        Assert.DoesNotContain(delta, c => c.CustomerAccount is "403522" or "446977");
    }

    private async Task AssertSourceContractsBelongToTheSameAccountAsync(List<XellentPbsContract> delta)
    {
        if (delta.Count == 0) return;

        var sourceContracts = delta
            .Select(c => c.PbsAgreementSourceContract!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await using var db = _xellent.NewContext();

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
    /// have before. Written as an artefact rather than committed.
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
