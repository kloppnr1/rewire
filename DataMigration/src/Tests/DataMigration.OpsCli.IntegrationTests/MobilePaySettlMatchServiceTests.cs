using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.OpsCli.Reporting;
using DataMigration.XellentData.Entities;

namespace DataMigration.OpsCli.IntegrationTests;

/// <summary>
/// The wiring of <see cref="MobilePaySettlMatchService"/>: the real typed metering-point client over
/// a fake Settl tenant, and the real Xellent queries against an in-memory Xellent. Read-only — the
/// service mutates nothing.
///
/// <para>Every production failure this command has had lived in exactly this wiring rather than in
/// the matching rules, and each one presented as an empty-looking run rather than an error:
/// a missing <c>SetDeliveryCategory</c> left the Settl pagination returning nothing, and a
/// contract-end filter dropped precisely the migrated customers (migrating ends the Xellent
/// contract). These tests assert the scan numbers, where both failures were visible first.</para>
///
/// <para>The tenant holds three migrated electricity metering points in delivery (change of supplier
/// 2026-09-01) and one that is not in delivery. Their Xellent contracts ended 2026-08-31. Two of the
/// three accounts hold an active MobilePay agreement; a fourth Xellent account holds one but was never
/// migrated, and must not be read at all.</para>
/// </summary>
public sealed class MobilePaySettlMatchServiceTests : IAsyncLifetime
{
    private const string DataAreaId = "hol";
    private static readonly DateOnly Cos = new(2026, 9, 1);

    private readonly FakeSettl _settl = new();
    private readonly FakeXellent _xellent = new();

    /// <summary>The scan as an operator would see it before any payment agreement is known.</summary>
    private MobilePayMatchResult _baseline = null!;

    private MobilePaySettlMatchService Service() =>
        new(_settl.MeteringPointClient, _xellent.ConnectionString);

    public async Task InitializeAsync()
    {
        var ended = Cos.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        (string Account, string Mp, string? MobilePay)[] migrated =
        [
            ("600001", "571313100000000501", "MP-AGR-0001"),
            ("600002", "571313100000000502", "MP-AGR-0002"),
            ("600003", "571313100000000503", null),
        ];
        foreach (var (account, mp, mobilePay) in migrated)
        {
            await _settl.SeedMeteringPointAsync(mp, Cos);
            var rows = XellentRows.For(new(account, "C" + account, mp, new(2019, 1, 1), ended));
            if (mobilePay != null) rows.OfType<ExuContractTable>().Single().Mobilepaysubscriptionagre20135 = mobilePay;
            _xellent.Add(rows);
        }
        // In Settl but not in delivery: out of scope for a handover.
        await _settl.SeedMeteringPointAsync("571313100000000504", Cos, inDelivery: false);
        _xellent.Add(XellentRows.For(new("600004", "C600004", "571313100000000504", new(2019, 1, 1), ended)));
        // A MobilePay customer Settl knows nothing about.
        var unmigrated = XellentRows.For(new("600005", "C600005", "571313100000000505", new(2019, 1, 1), XellentRows.Open));
        unmigrated.OfType<ExuContractTable>().Single().Mobilepaysubscriptionagre20135 = "MP-AGR-0005";
        _xellent.Add(unmigrated);

        _baseline = await Service().MatchAsync(DataAreaId, []);
    }

    public Task DisposeAsync()
    {
        _settl.Dispose();
        _xellent.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void The_scan_reads_the_Settl_environment()
    {
        // Zero scanned metering points is the signature of the delivery-category bug.
        Assert.NotNull(_baseline.Scan);
        Assert.Equal(3, _baseline.Scan!.SettlMeteringPointsScanned);
    }

    [Fact]
    public void Metering_points_pin_to_Xellent_accounts()
    {
        // The COS-1 pinning needs the Xellent contract that ended the day before change-of-supplier.
        // A filter that excludes ended contracts brings this to zero while the Settl count stays healthy.
        Assert.Equal(3, _baseline.Scan!.MigratedAccountsResolved);
    }

    [Fact]
    public void Only_migrated_customers_are_listed()
    {
        Assert.NotEmpty(_baseline.Rows);
        Assert.All(_baseline.Rows, r => Assert.True(r.SettlMeteringPointCount > 0,
            $"Row for account {r.Account} is listed but no Settl metering point resolves to it."));
        Assert.DoesNotContain(_baseline.Rows, r => r.Account == "600005");
    }

    [Fact]
    public void With_no_payment_agreements_every_listed_customer_is_missing_one()
    {
        // Handed an empty set of Settl payment agreements, every migrated customer holding an active
        // MobilePay agreement must be reported as losing it — none may quietly come back as matched.
        Assert.Equal(0, _baseline.Matched);
        Assert.Equal(0, _baseline.MatchedJoint);
        Assert.Equal(_baseline.Rows.Count, _baseline.NoSettlCustomerNumber);
        Assert.All(_baseline.Rows, r => Assert.Null(r.EnummerSettl));
    }

    [Fact]
    public void Settl_decides_the_population()
    {
        // Agreements are read for the accounts the scan resolved, so every agreement read becomes a row.
        Assert.Equal(2, _baseline.Scan!.AgreementsRead);
        Assert.Equal(_baseline.Scan!.AgreementsRead, _baseline.Rows.Count);
    }

    [Fact]
    public void Every_row_pins_to_a_migrated_contract()
    {
        Assert.All(_baseline.Rows, r =>
        {
            Assert.NotEqual("", r.MigratedContractNumber);
            Assert.NotNull(r.Cos);
        });
    }

    [Fact]
    public void An_agreement_on_the_migrated_contract_ends_the_day_before_change_of_supply()
    {
        Assert.Contains(_baseline.Rows, r => r.IsOnMigratedContract == true);
        Assert.All(
            _baseline.Rows.Where(r => r.IsOnMigratedContract == true),
            r => Assert.Equal(r.Cos!.Value.AddDays(-1), r.SourceContractEndDate));
    }
}
