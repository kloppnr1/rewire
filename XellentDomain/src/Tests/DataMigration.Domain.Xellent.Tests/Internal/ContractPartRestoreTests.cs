using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The contract-part restore end to end against a real (in-memory) database: the log query, the
/// metering-point resolution and both mappers on one seeded data set.
///
/// <para>The fixture mirrors the 2026-08-31 incident in miniature. Five agreements, each with its own
/// metering point, and one contract:</para>
/// <list type="bullet">
///   <item>A001 — open-ended part the batch ended. Restored, and its metering point comes back.</item>
///   <item>A002 — future part the batch deleted outright. Synthesised back from the log.</item>
///   <item>A003 — part that had already expired in May; the batch overwrote that with its own date.
///         The restore puts the May date back and the part drops out again.</item>
///   <item>A004 — ended by a named user, not the batch. Never restored.</item>
///   <item>A005 — ended one second after the window closes. Outside the incident, so left alone.</item>
/// </list>
/// </summary>
public class ContractPartRestoreTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;
    private const string Account = "100";
    private const string Contract = "C001";

    /// <summary>The second delivery point under A002 — same agreement, same single log row.</summary>
    private const string SecondMeteringPointOnA002 = "571313100000000006";

    private const string ContactPersonId = "CP1";
    private const string ContactPersonName = "Anne Kontakt";

    private static readonly DateTime Open = MigrationConstants.EndDateNull;
    private static readonly DateOnly Reference = new(2026, 9, 1);

    /// <summary>The batch: first row 09:13:49, last 09:59:32, upper bound one second past the last.</summary>
    private static readonly DateTime WindowFrom = new(2026, 8, 31, 9, 13, 49);
    private static readonly DateTime WindowBefore = new(2026, 8, 31, 9, 59, 33);

    /// <summary>What the batch wrote into ENDDATE on the parts it ended.</summary>
    private static readonly DateTime BatchEndDate = new(2026, 8, 31);

    private readonly SqliteConnection _connection;
    private long _recid = 100;

    public ContractPartRestoreTests()
    {
        // Pooling off: Microsoft.Data.Sqlite pools ":memory:" connections and hands them back with
        // their contents intact, so the log table created below would already exist on the second
        // test in this class.
        _connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        _connection.Open();

        using var seed = NewContext();
        seed.Database.EnsureCreated();

        seed.ExuContractTables.Add(new ExuContractTable
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = Contract,
            Custaccount = Account,
            Deliverycategory = Category,
            Name = "Contract",
            Contractstartdate = new DateTime(2020, 1, 1),
            Contractenddate = Open,
            Mobilepaysubscriptionagre20135 = "",
            Einvoiceeannum = "",
            Custpaymcode = "",
            Billingplannum = "",
            Contracttemplatenum = "",
            // The contact person behind the per-MP OioUBL order identifier.
            Custcontactpersonid = ContactPersonId,
        });

        seed.ContactPersons.Add(new ContactPerson
        {
            Recid = 2,
            Dataareaid = Area,
            Contactpersonid = ContactPersonId,
            Custaccount = Account,
            Name = ContactPersonName,
        });

        for (var i = 1; i <= 5; i++)
            AddAgreementWithMeteringPoint(seed, $"A00{i}", $"57131310000000000{i}");

        // A002 covers two delivery points. One log row, two metering points — the shape roughly 118
        // of the incident's agreements have, and the one a per-agreement dictionary would collapse.
        AddDelpoint(seed, "A002", "2", SecondMeteringPointOnA002);

        // Live parts: everything the batch ended is still in the table with its new end date.
        AddPart(seed, "A001", "P1", BatchEndDate, new DateTime(2020, 1, 1));
        AddPart(seed, "A003", "P3", BatchEndDate, new DateTime(2020, 1, 1));
        AddPart(seed, "A004", "P4", BatchEndDate, new DateTime(2020, 1, 1));
        AddPart(seed, "A005", "P5", BatchEndDate, new DateTime(2020, 1, 1));
        // A002 has no live part — the batch deleted it.

        // The log. Two rows for A001: the earliest one in the window is the state to restore.
        AddLog(seed, "A001", "P1", new DateTime(2020, 1, 1), Open,
            ContractPartRestore.ChangeTypeEndDateChanged, "", new DateTime(2026, 8, 31, 9, 20, 0));
        AddLog(seed, "A001", "P1", new DateTime(2020, 1, 1), BatchEndDate,
            ContractPartRestore.ChangeTypeEndDateChanged, "", new DateTime(2026, 8, 31, 9, 40, 0));

        AddLog(seed, "A002", "P2", new DateTime(2026, 10, 1), Open,
            ContractPartRestore.ChangeTypePartDeleted, "", new DateTime(2026, 8, 31, 9, 30, 0));

        AddLog(seed, "A003", "P3", new DateTime(2020, 1, 1), new DateTime(2026, 5, 31),
            ContractPartRestore.ChangeTypeEndDateChanged, "", new DateTime(2026, 8, 31, 9, 21, 0));

        AddLog(seed, "A004", "P4", new DateTime(2020, 1, 1), Open,
            ContractPartRestore.ChangeTypeEndDateChanged, "maiw", new DateTime(2026, 8, 31, 9, 22, 0));

        AddLog(seed, "A005", "P5", new DateTime(2020, 1, 1), Open,
            ContractPartRestore.ChangeTypeEndDateChanged, "", WindowBefore);

        // A one-time-charge row inside the window: blank user, but not a part-level change. Its own
        // key, so it is the first row in the window for that key and has to be ignored on its type.
        AddLog(seed, "A001", "GEBYR", new DateTime(2020, 1, 1), Open,
            "Engangsydelse V Gebyr01 er slettet", "", new DateTime(2026, 8, 31, 9, 35, 0));

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ── The restore itself ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_window_the_log_is_never_read()
    {
        using var context = NewContext();

        var restore = await ContractPartRestore.CreateAsync(
            context, new WarningCollector(), Options(restore: false), Area, [Account]);

        Assert.True(restore.IsEmpty);
    }

    [Fact]
    public async Task The_earliest_change_in_the_window_is_the_one_restored()
    {
        var restore = await BuildRestore();

        var endDate = restore.RestoredEndDate(
            new ContractPartLogKey(Contract, "A001", Category, Company, "P1", "", new DateTime(2020, 1, 1)));

        // 09:20 logged the open end date; 09:40 logged what 09:20 had already written.
        Assert.Equal(Open, endDate);
    }

    [Fact]
    public async Task A_named_user_change_is_left_alone()
    {
        var restore = await BuildRestore();

        var endDate = restore.RestoredEndDate(
            new ContractPartLogKey(Contract, "A004", Category, Company, "P4", "", new DateTime(2020, 1, 1)));

        Assert.Null(endDate);
    }

    [Fact]
    public async Task A_change_one_second_past_the_window_is_left_alone()
    {
        var restore = await BuildRestore();

        var endDate = restore.RestoredEndDate(
            new ContractPartLogKey(Contract, "A005", Category, Company, "P5", "", new DateTime(2020, 1, 1)));

        Assert.Null(endDate);
    }

    [Fact]
    public async Task A_deleted_part_resolves_to_every_delivery_point_under_its_agreement()
    {
        var restore = await BuildRestore();

        // One log row, two delivery points. Keying this by agreement number, or taking the first
        // delivery point of the join, would silently lose the second metering point.
        var parts = restore.DeletedPartsFor(Category);

        Assert.Equal(
            new[] { "571313100000000002", SecondMeteringPointOnA002 },
            parts.Select(p => p.MeteringPoint).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.All(parts, p => Assert.Equal("P2", p.Key.ProductNum));
    }

    // ── What the mappers make of it ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Left_off_the_batch_leaves_the_contract_with_no_metering_points_at_all()
    {
        using var context = NewContext();
        var mapper = new ContractMapper(context, new WarningCollector(), Options(restore: false), Area);

        var contracts = await mapper.MapAsync([Account], Representatives());

        Assert.Empty(Assert.Single(contracts).MeteringPoints);
    }

    [Fact]
    public async Task Turned_on_the_restored_and_the_synthesised_part_come_back_and_nothing_else()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();
        var restore = await ContractPartRestore.CreateAsync(
            context, warnings, Options(restore: true), Area, [Account]);
        var mapper = new ContractMapper(context, warnings, Options(restore: true), Area);

        var contracts = await mapper.MapAsync([Account], Representatives(), restore);

        var assignments = Assert.Single(contracts).MeteringPoints
            .OrderBy(a => a.MeteringPointId, StringComparer.Ordinal)
            .ToList();

        // A002's single deleted part becomes one link per delivery point.
        Assert.Equal(
            new[] { "571313100000000001", "571313100000000002", SecondMeteringPointOnA002 },
            assignments.Select(a => a.MeteringPointId).ToArray());

        // The synthesised ones keep the dates the log recorded: a future part, open-ended.
        var synthesised = assignments[1];
        Assert.Equal(new DateOnly(2026, 10, 1), synthesised.Start);
        Assert.Equal("P2", synthesised.Product.Code);
    }

    [Fact]
    public async Task A_restored_end_date_that_is_still_in_the_past_drops_out_again()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();
        var restore = await ContractPartRestore.CreateAsync(
            context, warnings, Options(restore: true), Area, [Account]);
        var mapper = new ContractMapper(context, warnings, Options(restore: true), Area);

        var contracts = await mapper.MapAsync([Account], Representatives(), restore);

        // A003's part really did end in May 2026 — putting that date back must not revive it.
        Assert.DoesNotContain(
            Assert.Single(contracts).MeteringPoints,
            a => a.MeteringPointId == "571313100000000003");
    }

    [Fact]
    public async Task The_metering_points_follow_the_contract_links()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();
        var restore = await ContractPartRestore.CreateAsync(
            context, warnings, Options(restore: true), Area, [Account]);
        var mapper = new MeteringPointMapper(context, warnings, Options(restore: true), Area);

        var result = await mapper.MapAsync([Account], restore);

        Assert.Equal(
            new[] { "571313100000000001", "571313100000000002", SecondMeteringPointOnA002 },
            result.MeteringPoints.Select(mp => mp.Identification).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Left_off_no_metering_point_survives_the_batch()
    {
        using var context = NewContext();
        var mapper = new MeteringPointMapper(context, new WarningCollector(), Options(restore: false), Area);

        var result = await mapper.MapAsync([Account]);

        Assert.Empty(result.MeteringPoints);
    }

    // ── Order identifiers (the OioUBL invoice reference) ──────────────────────────────────────

    [Fact]
    public async Task Left_off_the_batch_takes_every_order_identifier_with_it()
    {
        using var context = NewContext();
        var mapper = new XellentDomainMapper(context, new WarningCollector(), Options(restore: false), Area);

        var identifiers = await mapper.BuildMpOrderIdentifiersAsync(
            [Account], ContractPartRestore.Empty, headerExemption: null, CancellationToken.None);

        // Every part is ended, so no metering point is reached at all.
        Assert.Empty(identifiers);
    }

    [Fact]
    public async Task A_deleted_parts_metering_points_keep_their_order_identifier()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();
        var restore = await ContractPartRestore.CreateAsync(
            context, warnings, Options(restore: true), Area, [Account]);
        var mapper = new XellentDomainMapper(context, warnings, Options(restore: true), Area);

        var identifiers = await mapper.BuildMpOrderIdentifiersAsync(
            [Account], restore, headerExemption: null, CancellationToken.None);

        // Both delivery points behind the deleted part, not just the first.
        Assert.Equal(ContactPersonName, identifiers["571313100000000002"]);
        Assert.Equal(ContactPersonName, identifiers[SecondMeteringPointOnA002]);

        // And the restored live part's own metering point.
        Assert.Equal(ContactPersonName, identifiers["571313100000000001"]);
    }

    [Fact]
    public async Task A_part_that_is_expired_even_after_the_restore_gets_no_order_identifier()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();
        var restore = await ContractPartRestore.CreateAsync(
            context, warnings, Options(restore: true), Area, [Account]);
        var mapper = new XellentDomainMapper(context, warnings, Options(restore: true), Area);

        var identifiers = await mapper.BuildMpOrderIdentifiersAsync(
            [Account], restore, headerExemption: null, CancellationToken.None);

        // A003 ended in May for real; the widened cutoff let its row through the query and the
        // re-filter has to throw it out again, or it would claim a name it has no right to.
        Assert.DoesNotContain("571313100000000003", identifiers.Keys);
        Assert.DoesNotContain("571313100000000004", identifiers.Keys);
        Assert.DoesNotContain("571313100000000005", identifiers.Keys);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<ContractPartRestore> BuildRestore()
    {
        using var context = NewContext();
        return await ContractPartRestore.CreateAsync(
            context, new WarningCollector(), Options(restore: true), Area, [Account]);
    }

    private static MapperOptions Options(bool restore) => restore
        ? new MapperOptions(Reference, Commodity.Electricity,
            RestorePartsChangedFrom: WindowFrom, RestorePartsChangedBefore: WindowBefore)
        : new MapperOptions(Reference, Commodity.Electricity);

    private static Dictionary<string, string> Representatives() =>
        new(StringComparer.Ordinal) { [Account] = Account };

    private void AddAgreementWithMeteringPoint(XellentDbContext seed, string agreement, string meteringPoint)
    {
        seed.ExuAgreementTables.Add(new ExuAgreementTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Agreementnum = agreement,
            Deliverycategory = Category,
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
            Instbillingname = "Fakturanavn",
        });

        AddDelpoint(seed, agreement, "1", meteringPoint);
    }

    private void AddDelpoint(XellentDbContext seed, string agreement, string delpointNum, string meteringPoint)
    {
        seed.ExuDelpoints.Add(new ExuDelpoint
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Num = delpointNum,
            Attachmentnum = agreement,
            Deliverycategory = Category,
            Custaccount = Account,
            Meteringpoint = meteringPoint,
            Extnum = "",
            Name = "",
            Streetnum = "1234",
            Countynum = "751",
            Zipcodeid = "8000",
            Addressstreet = "Fiktivvejhp",
            Gsrn = "",
            Housenumstart = 1,
            Houseletterstart = "",
            Apartment = "",
            Floor = "",
            Addrlocation = "",
            Addressid = "",
            EmsNetarea = "",
            Powerexchangearea = "",
        });

        seed.EgsMeterpointTypecodeperiods.Add(new EgsMeterpointTypecodeperiod
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Delpointnum = delpointNum,
            Attachmentnum = agreement,
            Deliverycategory = Category,
            Meterpointtypecode = "E17/Forb",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
        });
    }

    private void AddPart(XellentDbContext seed, string agreement, string product, DateTime endDate, DateTime startDate)
        => seed.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = Contract,
            Instagreenum = agreement,
            Deliverycategory = Category,
            Productnum = product,
            Startdate = startDate,
            Enddate = endDate,
            Amrcode = "",
            Contractpartsubreferenceid = "",
        });

    private void AddLog(
        XellentDbContext seed,
        string agreement,
        string product,
        DateTime partStart,
        DateTime loggedEndDate,
        string changeType,
        string logEmplId,
        DateTime loggedAt)
        => seed.ExuContractPartLogs.Add(new ExuContractPartLog
        {
            Recid = _recid++,
            Dataareaid = Area,
            Contractnum = Contract,
            Instagreenum = agreement,
            Deliverycategory = Category,
            Companyid = Company,
            Productnum = product,
            Amrcode = "",
            Deliverygroupnumratecode = "",
            Contractpartstartdate = partStart,
            Contractpartenddate = loggedEndDate,
            Errormsg = changeType,
            Logemplid = logEmplId,
            Logdatetime = loggedAt,
        });

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options);
}
