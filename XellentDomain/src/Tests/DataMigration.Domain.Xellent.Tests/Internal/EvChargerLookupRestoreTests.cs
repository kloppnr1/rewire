using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The charger lookup's electricity side under the contract-part restore, end to end against a real
/// (in-memory) database: both query shapes, the deleted-part resolution and the in-memory re-filter.
///
/// <para>Three accounts, each with one charger installation and one charger-product electricity part
/// the 2026-08-31 batch touched:</para>
/// <list type="bullet">
///   <item>100 — open-ended <c>V LadVariP</c> the batch ended. Restored, the charger links again.</item>
///   <item>200 — future <c>V LadVariP</c> the batch deleted outright (account 105332 in production).
///         Synthesised back, the charger links.</item>
///   <item>300 — <c>V LadVariP</c> that had already expired in May; the batch overwrote that date.
///         Restored to May, it stays out and the charger stays on the account.</item>
/// </list>
/// </summary>
public class EvChargerLookupRestoreTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Electricity = MigrationConstants.DeliveryCategoryElectricity;
    private const string ChargerProduct = "V LadVariP";

    private const string Mp100 = "571313100000000001";
    private const string Mp200 = "571313100000000002";
    private const string Mp300 = "571313100000000003";

    private static readonly DateTime Open = MigrationConstants.EndDateNull;
    private static readonly DateOnly Reference = new(2026, 9, 1);

    private static readonly DateTime WindowFrom = new(2026, 8, 31, 9, 13, 49);
    private static readonly DateTime WindowBefore = new(2026, 8, 31, 9, 59, 33);
    private static readonly DateTime BatchEndDate = new(2026, 8, 31);

    private static readonly string[] Accounts = ["100", "200", "300"];

    private readonly SqliteConnection _connection;
    private long _recid = 100;

    public EvChargerLookupRestoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        _connection.Open();

        using var seed = NewContext();
        seed.Database.EnsureCreated();

        AddStreet(seed);
        foreach (var (account, meteringPoint) in new[] { ("100", Mp100), ("200", Mp200), ("300", Mp300) })
        {
            AddContract(seed, $"C{account}", account, Electricity);
            AddContract(seed, $"K{account}", account, MigrationConstants.DeliveryCategoryCharge);
            AddAgreementWithMeteringPoint(seed, $"A{account}", account, meteringPoint);
            AddInstallation(seed, $"I{account}");
            AddPart(seed, $"K{account}", $"I{account}", MigrationConstants.DeliveryCategoryCharge, "V Lader",
                new DateTime(2026, 11, 1), Open);
        }

        // Live charger-product parts as the batch left them. 200's is gone — deleted.
        AddPart(seed, "C100", "A100", Electricity, ChargerProduct, new DateTime(2020, 1, 1), BatchEndDate);
        AddPart(seed, "C300", "A300", Electricity, ChargerProduct, new DateTime(2020, 1, 1), BatchEndDate);

        // The log: what each part looked like before the batch wrote.
        AddLog(seed, "C100", "A100", new DateTime(2020, 1, 1), Open,
            ContractPartRestore.ChangeTypeEndDateChanged, new DateTime(2026, 8, 31, 9, 20, 0));
        AddLog(seed, "C200", "A200", new DateTime(2026, 11, 1), Open,
            ContractPartRestore.ChangeTypePartDeleted, new DateTime(2026, 8, 31, 9, 30, 0));
        AddLog(seed, "C300", "A300", new DateTime(2020, 1, 1), new DateTime(2026, 5, 31),
            ContractPartRestore.ChangeTypeEndDateChanged, new DateTime(2026, 8, 31, 9, 21, 0));

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Left_off_every_charger_stays_on_its_account()
    {
        using var context = NewContext();
        var mapper = new XellentDomainMapper(context, new WarningCollector(), Options(restore: false), Area);

        var (byMp, byAccount) = await mapper.BuildEvChargerLookupsAsync(
            Accounts, ContractPartRestore.Empty, headerExemption: null, CancellationToken.None);

        Assert.Empty(byMp);
        Assert.Equal(Accounts, byAccount.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(byAccount.Values.SelectMany(c => c), c => Assert.False(c.AddressMatchesMeteringPoint));
    }

    [Fact]
    public async Task Turned_on_the_restored_and_the_synthesised_part_link_their_chargers_and_the_expired_one_does_not()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();
        var restore = await ContractPartRestore.CreateAsync(
            context, warnings, Options(restore: true), Area, Accounts);
        Assert.False(restore.IsEmpty);

        var mapper = new XellentDomainMapper(context, warnings, Options(restore: true), Area);

        var (byMp, byAccount) = await mapper.BuildEvChargerLookupsAsync(
            Accounts, restore, headerExemption: null, CancellationToken.None);

        var on100 = Assert.Single(byMp[Mp100]);
        var on200 = Assert.Single(byMp[Mp200]);
        Assert.Equal("I100", on100.InstallationNumber);
        Assert.Equal("I200", on200.InstallationNumber);
        Assert.False(byMp.ContainsKey(Mp300));

        // Both query shapes carry the delpoint street: the live (restored) row and the row
        // synthesised for the deleted part. Every installation sits at Fiktivvejewj 1.
        Assert.True(on100.AddressMatchesMeteringPoint);
        Assert.True(on200.AddressMatchesMeteringPoint);

        var left = Assert.Single(Assert.Single(byAccount).Value);
        Assert.Equal("I300", left.InstallationNumber);
        Assert.False(left.AddressMatchesMeteringPoint);
        Assert.Empty(warnings.GetWarnings());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static MapperOptions Options(bool restore) => restore
        ? new MapperOptions(Reference, Commodity.Electricity,
            RestorePartsChangedFrom: WindowFrom, RestorePartsChangedBefore: WindowBefore)
        : new MapperOptions(Reference, Commodity.Electricity);

    private void AddContract(XellentDbContext seed, string contract, string account, string category)
        => seed.ExuContractTables.Add(new ExuContractTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = contract,
            Custaccount = account,
            Deliverycategory = category,
            Name = "Contract",
            Contractstartdate = new DateTime(2020, 1, 1),
            Contractenddate = Open,
            Mobilepaysubscriptionagre20135 = "",
            Einvoiceeannum = "",
            Custpaymcode = "",
            Billingplannum = "",
            Contracttemplatenum = "",
            Custcontactpersonid = "",
        });

    /// <summary>The street every delivery point's street code (1234/751) resolves to.</summary>
    private void AddStreet(XellentDbContext seed)
        => seed.ExuStreets.Add(new ExuStreet
        {
            Recid = _recid++,
            Dataareaid = Area,
            Streetnum = "1234",
            Countynum = "751",
            Streetname = "Fiktivvejewj",
        });

    private void AddInstallation(XellentDbContext seed, string instnum)
        => seed.ExuInstallations.Add(new ExuInstallation
        {
            Recid = _recid++,
            Dataareaid = Area,
            Instnum = instnum,
            Zipcodeid = "8000",
            Instaddressformatted = "Fiktivvejewj  1, 8000 Testby",
        });

    private void AddAgreementWithMeteringPoint(XellentDbContext seed, string agreement, string account, string meteringPoint)
    {
        seed.ExuAgreementTables.Add(new ExuAgreementTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Agreementnum = agreement,
            Deliverycategory = Electricity,
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
            Instbillingname = "Fakturanavn",
        });

        seed.ExuDelpoints.Add(new ExuDelpoint
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Num = "1",
            Attachmentnum = agreement,
            Deliverycategory = Electricity,
            Custaccount = account,
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
            Delpointnum = "1",
            Attachmentnum = agreement,
            Deliverycategory = Electricity,
            Meterpointtypecode = "E17/Forb",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
        });
    }

    private void AddPart(
        XellentDbContext seed, string contract, string instAgreeNum, string category, string product,
        DateTime startDate, DateTime endDate)
        => seed.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = contract,
            Instagreenum = instAgreeNum,
            Deliverycategory = category,
            Productnum = product,
            Startdate = startDate,
            Enddate = endDate,
            Amrcode = "",
            Contractpartsubreferenceid = "",
        });

    private void AddLog(
        XellentDbContext seed, string contract, string agreement, DateTime partStart, DateTime loggedEndDate,
        string changeType, DateTime loggedAt)
        => seed.ExuContractPartLogs.Add(new ExuContractPartLog
        {
            Recid = _recid++,
            Dataareaid = Area,
            Contractnum = contract,
            Instagreenum = agreement,
            Deliverycategory = Electricity,
            Companyid = Company,
            Productnum = ChargerProduct,
            Amrcode = "",
            Deliverygroupnumratecode = "",
            Contractpartstartdate = partStart,
            Contractpartenddate = loggedEndDate,
            Errormsg = changeType,
            Logemplid = "",
            Logdatetime = loggedAt,
        });

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options);
}
