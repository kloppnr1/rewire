using DataMigration.OpsCli.IntegrationTests.Fixtures;
using DataMigration.OpsCli.Reporting;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// The wiring of <see cref="MobilePaySettlMatchService"/> against a live Settl environment and a
/// live Xellent database. Read-only — the service mutates nothing.
///
/// <para>Every production failure this command has had lived in exactly this wiring rather than in
/// the matching rules, and each one presented as an empty-looking run rather than an error:
/// a missing <c>SetDeliveryCategory</c> left the Settl pagination returning nothing, and a
/// contract-end filter dropped precisely the migrated customers (migrating ends the Xellent
/// contract). Unit tests over the classifier cannot see either. These tests assert the two scan
/// numbers instead, because that is where both failures were visible first.</para>
///
/// <para>The scan is an environment-wide pagination over Settl, so it runs once for the whole class
/// and the assertions read the same result.</para>
///
/// <para><b>Scope:</b> these tests cover the wiring only. No migrated customer in featuretest holds
/// an active MobilePay agreement, so the row list is empty here and row-level assertions would pass
/// by describing nothing. The matching rules are tested as facts in
/// <c>MobilePayRowBuilderTests</c> instead.</para>
/// </summary>
public class MobilePaySettlMatchServiceTests(SettlFeaturetestFixture settl)
    : IClassFixture<SettlFeaturetestFixture>, IAsyncLifetime
{
    private const string DataAreaId = "hol";

    /// <summary>The scan as an operator would see it before any payment agreement is known.</summary>
    private MobilePayMatchResult _baseline = null!;

    private MobilePaySettlMatchService Service() =>
        new(settl.MeteringPointClient, settl.XellentConnectionString);

    public async Task InitializeAsync() => _baseline = await Service().MatchAsync(DataAreaId, []);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void The_scan_reads_the_Settl_environment()
    {
        // Zero scanned metering points is the signature of the delivery-category bug: the market-v3
        // pagination is commodity-scoped and returns an empty page until it is set, after which
        // every single account looks unmigrated and the run finishes suspiciously fast.
        Assert.NotNull(_baseline.Scan);
        Assert.True(_baseline.Scan!.SettlMeteringPointsScanned > 0,
            "No metering points came back from Settl. Either the delivery category was not set on "
            + "the client before paginating, or the environment is genuinely empty.");
    }

    [Fact]
    public void Metering_points_pin_to_Xellent_accounts()
    {
        // The COS-1 pinning is the half of the chain Settl cannot verify on its own: it needs the
        // Xellent contract that ended the day before change-of-supplier. A connection to the wrong
        // Xellent database, or a filter that excludes ended contracts, brings this to zero while
        // the Settl count above stays healthy.
        Assert.True(_baseline.Scan!.MigratedAccountsResolved > 0,
            "No Settl metering point resolved to a Xellent account. The COS-1 pinning found nothing "
            + $"in '{DataAreaId}' — check which Xellent database the connection string points at.");
    }

    [Fact]
    public void Only_migrated_customers_are_listed()
    {
        // A listed row exists because Settl holds a metering point for its account. There is no
        // "not migrated" status left to check for — the population is what the scan resolved — so
        // the metering point count is what proves it.
        Assert.All(_baseline.Rows, r => Assert.True(r.SettlMeteringPointCount > 0,
            $"Row for account {r.Account} is listed but no Settl metering point resolves to it."));
    }

    [Fact]
    public void With_no_payment_agreements_every_listed_customer_is_missing_one()
    {
        // The finding the report exists for. Handed an empty set of Settl payment agreements, every
        // migrated customer holding an active MobilePay agreement must be reported as losing it —
        // none may quietly come back as matched.
        Assert.Equal(0, _baseline.Matched);
        Assert.Equal(0, _baseline.MatchedJoint);
        Assert.Equal(_baseline.Rows.Count, _baseline.NoSettlCustomerNumber);
        Assert.All(_baseline.Rows, r => Assert.Null(r.EnummerSettl));
    }

    [Fact]
    public void Settl_decides_the_population()
    {
        // Agreements are read for the accounts the scan resolved, so every agreement read becomes a
        // row — nothing is fetched only to be discarded, and nothing is reported about an account
        // the scan never looked at. Reading the whole data area instead produced a surplus the
        // output then labelled "not migrated" without grounds. Holds at zero, which is what
        // featuretest gives: no migrated customer there holds a MobilePay agreement.
        Assert.Equal(_baseline.Scan!.AgreementsRead, _baseline.Rows.Count);
    }

    [Fact]
    public void Every_row_pins_to_a_migrated_contract()
    {
        // The scan reads only metering points in delivery, and every one of those has a
        // change-of-supplier event to pin against — so every row must name the contract it pinned to.
        Assert.All(_baseline.Rows, r =>
        {
            Assert.NotEqual("", r.MigratedContractNumber);
            Assert.NotNull(r.Cos);
        });
    }

    [Fact]
    public void An_agreement_on_the_migrated_contract_ends_the_day_before_change_of_supply()
    {
        // Only for rows that sit on the migrated contract. A row on another contract is precisely
        // the leftover case, and its own end date is expected to be earlier — asserting COS-1 for
        // every row would make the leftovers look like defects.
        Assert.All(
            _baseline.Rows.Where(r => r.IsOnMigratedContract == true),
            r => Assert.Equal(r.Cos!.Value.AddDays(-1), r.SourceContractEndDate));
    }
}
