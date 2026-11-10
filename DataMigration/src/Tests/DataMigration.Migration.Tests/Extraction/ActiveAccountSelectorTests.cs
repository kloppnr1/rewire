using DataMigration.Core;
using DataMigration.Migration.Services;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Migration.Tests.Extraction;

/// <summary>
/// The all-active scope is resolved before extraction and then applied to the extraction's output.
/// So it has to see the restore window too — otherwise the extraction restores the contracts the
/// 2026-08-31 batch end-dated and this pre-filter cuts them straight back out.
/// </summary>
public sealed class ActiveAccountSelectorTests : IDisposable
{
    private const string DataArea = "hol";
    private const string Category = "El-ekstern";

    private static readonly DateTime BatchStamp = new(2026, 8, 31);
    private static readonly DateTime WindowFrom = new(2026, 8, 31, 9, 13, 49);
    private static readonly DateTime WindowBefore = new(2026, 8, 31, 9, 59, 33);
    private static readonly DateTime Cutover = new(2026, 9, 1);

    private readonly SqliteConnection _connection;
    private readonly XellentDbContext _context;

    public ActiveAccountSelectorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new XellentDbContext(
            new DbContextOptionsBuilder<XellentDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();

        // Still open-ended: active whatever the window says.
        Seed("A1", "C1", MigrationConstants.EndDateNull, Category);

        // End-dated by the batch, and the log names it — the case the exemption exists for.
        Seed("A2", "C2", BatchStamp, Category);
        Log("C2", WindowFrom.AddMinutes(1), emplId: null);

        // Also end-dated by the batch, but holds no El-ekstern part. Belongs to another commodity's
        // scope, not to this one.
        Seed("A3", "C3", BatchStamp, "Vand");
        Log("C3", WindowFrom.AddMinutes(2), emplId: null);

        // A genuine termination the batch never touched: no log row, so no exemption.
        Seed("A4", "C4", new DateTime(2026, 7, 1), Category);

        _context.SaveChanges();
    }

    [Fact]
    public async Task Without_a_window_only_the_accounts_active_on_their_own_merits_come_back()
    {
        var accounts = await Select(window: null);

        Assert.Equal(["A1"], accounts);
    }

    [Fact]
    public async Task With_the_window_the_accounts_the_batch_closed_come_back_too()
    {
        var accounts = await Select((WindowFrom, WindowBefore));

        Assert.Equal(["A1", "A2"], accounts);
    }

    /// <summary>
    /// The exemption names every contract the batch end-dated, across all delivery categories. A run
    /// migrating electricity must not grow a water account just because the same batch closed it.
    /// </summary>
    [Fact]
    public async Task An_exempted_contract_without_a_part_in_the_category_is_not_pulled_in()
    {
        var accounts = await Select((WindowFrom, WindowBefore));

        Assert.DoesNotContain("A3", accounts);
    }

    /// <summary>
    /// A change a person made is never undone — the same rule the part restore follows. So a named
    /// user on the log row leaves the contract filtered exactly as it is without the window.
    /// </summary>
    [Fact]
    public async Task A_contract_the_log_credits_to_a_named_user_stays_out()
    {
        Log("C4", WindowFrom.AddMinutes(3), emplId: "MKJ");
        await _context.SaveChangesAsync();

        var accounts = await Select((WindowFrom, WindowBefore));

        Assert.DoesNotContain("A4", accounts);
    }

    [Fact]
    public async Task A_log_row_outside_the_window_exempts_nothing()
    {
        var accounts = await Select((WindowBefore.AddHours(1), WindowBefore.AddHours(2)));

        Assert.Equal(["A1"], accounts);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Half_a_window_is_refused_rather_than_read_as_off(bool hasFrom, bool hasBefore)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActiveAccountSelector.SelectAsync(
                "Server=nowhere;Database=none;Integrated Security=true;",
                Category, Cutover, DataArea,
                cancellationToken: default,
                restorePartsChangedFrom: hasFrom ? WindowFrom : null,
                restorePartsChangedBefore: hasBefore ? WindowBefore : null));
    }

    private Task<string[]> Select((DateTime From, DateTime Before)? window) =>
        ActiveAccountSelector.SelectAsync(
            _context, Category, Cutover, DataArea, window, new WarningCollector());

    private void Seed(string account, string contractNum, DateTime headerEndDate, string category)
    {
        _context.ExuContractTables.Add(new ExuContractTable
        {
            Dataareaid = DataArea,
            Contractnum = contractNum,
            Custaccount = account,
            Name = account,
            Contractstartdate = new DateTime(2020, 1, 1),
            Contractenddate = headerEndDate,
            Companyid = "1",
            Deliverycategory = category,
            Mobilepaysubscriptionagre20135 = "",
            Einvoiceeannum = "",
            Custpaymcode = "",
            Billingplannum = "",
            Contracttemplatenum = "",
            Recid = contractNum.GetHashCode(),
        });

        _context.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Dataareaid = DataArea,
            Contractnum = contractNum,
            Instagreenum = contractNum + "-A",
            Deliverycategory = category,
            Companyid = "1",
            Productnum = "P1",
            Amrcode = "",
            Contractpartsubreferencetype = 0,
            Contractpartsubreferenceid = "",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = MigrationConstants.EndDateNull,
            Recid = contractNum.GetHashCode() + 1,
        });
    }

    private void Log(string contractNum, DateTime loggedAt, string? emplId)
    {
        _context.ExuContractPartLogs.Add(new ExuContractPartLog
        {
            Recid = _context.ExuContractPartLogs.Local.Count + 1,
            Dataareaid = DataArea,
            Contractnum = contractNum,
            Instagreenum = contractNum + "-A",
            Deliverycategory = Category,
            Companyid = "1",
            Amrcode = "",
            Productnum = "P1",
            Contractpartstartdate = new DateTime(2020, 1, 1),
            Contractpartenddate = BatchStamp,
            Errormsg = "Slutdato er ændret",
            Logemplid = emplId,
            Logdatetime = loggedAt,
        });
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
