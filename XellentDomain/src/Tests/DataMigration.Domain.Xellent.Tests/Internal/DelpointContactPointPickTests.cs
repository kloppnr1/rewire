using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// A metering point can have several open <c>Contacttype=4</c> rows — 2.537 of them in dataarea
/// 'hol' have two or more sharing one start date, and 21 of those pairs differ in NAME2 alone
/// (same NAME1, same ADDRESSID, one carrying a branch name like "Sparekassen Ry Afdeling", the
/// other blank). The pick used to be positional over a query with no ORDER BY, so which of the two
/// won changed between runs of the same build.
///
/// <para>These tests pin the rule instead of re-running and hoping: a populated NAME2 wins, and
/// RECID decides whatever is left. Each case is fed in both insertion orders and must give the
/// same answer — that is the part that catches a pick sliding back to "whatever came first".</para>
///
/// <para>The sibling rows here differ in COMPANYID. That is not incidental: the entity's key is
/// (Dataareaid, Companyid, Deliverycategory, Meteringpoint, Contacttype, Startdate), so two rows
/// tied on start date have to differ somewhere in it, and COMPANYID is the only column left once
/// the mapper's own filters have fixed the others. The contact-info read does not filter on
/// COMPANYID and the pick's grouping key does not contain it, so both rows reach the pick.</para>
/// </summary>
public class DelpointContactPointPickTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;
    private const string Account = "100";
    private const string Agreement = "A001";
    private const string Delpoint = "1";
    private const string Mp = "571313100000000001";

    private const string OtherCompany = "02";

    private static readonly DateTime Open = MigrationConstants.EndDateNull;
    private static readonly DateTime Start = new(2021, 1, 1);

    private readonly SqliteConnection _connection;

    public DelpointContactPointPickTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var seed = NewContext();
        seed.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_name_beats_a_blank_one_whichever_order_the_rows_arrive_in(bool populatedFirst)
    {
        var blank = Contact(recid: 601, name2: "");
        var populated = Contact(recid: 602, name2: "Sparekassen Ry Afdeling", company: OtherCompany);

        await SeedAsync(populatedFirst ? [populated, blank] : [blank, populated]);

        var legal = await LoadLegalContact();
        Assert.Equal("Sparekassen Ry Afdeling", legal.SecondaryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_name_still_wins_when_it_sits_on_the_higher_recid(bool populatedFirst)
    {
        // The branch-name rule has to outrank the stable key, not just agree with it by luck.
        var populated = Contact(recid: 611, name2: "Brædstrup Afdeling", company: OtherCompany);
        var blank = Contact(recid: 610, name2: "");

        await SeedAsync(populatedFirst ? [populated, blank] : [blank, populated]);

        var legal = await LoadLegalContact();
        Assert.Equal("Brædstrup Afdeling", legal.SecondaryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_branch_names_are_settled_by_the_lowest_recid(bool highestFirst)
    {
        // The lower Recid sits on the company the query returns second, so a positional pick would
        // take the other one.
        var low = Contact(recid: 620, name2: "Hammel Stab", company: OtherCompany);
        var high = Contact(recid: 621, name2: "Århus City Afdeling");

        await SeedAsync(highestFirst ? [high, low] : [low, high]);

        var legal = await LoadLegalContact();
        Assert.Equal("Hammel Stab", legal.SecondaryName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_blank_branch_names_are_settled_by_the_lowest_recid(bool highestFirst)
    {
        // Nothing to prefer on NAME2, so the whole result — here the phone number that rides along
        // on the chosen row — must still be decided by the stable key.
        var low = Contact(recid: 630, name2: "", phone: "11111111", company: OtherCompany);
        var high = Contact(recid: 631, name2: "", phone: "41796158");

        await SeedAsync(highestFirst ? [high, low] : [low, high]);

        var legal = await LoadLegalContact();
        Assert.Null(legal.SecondaryName);
        var line = Assert.Single(legal.CommunicationLines!, l => l.Type == "Phone");
        Assert.Equal("11111111", line.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_newer_start_date_still_outranks_the_branch_name(bool newerFirst)
    {
        // The start-date rule is unchanged: NAME2 only decides between rows that already tie on it.
        var older = Contact(recid: 640, name2: "Sommerhus Glesborg", startdate: new DateTime(2019, 1, 1));
        var newer = Contact(recid: 641, name2: "", startdate: new DateTime(2023, 1, 1));

        await SeedAsync(newerFirst ? [newer, older] : [older, newer]);

        var legal = await LoadLegalContact();
        Assert.Null(legal.SecondaryName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A contact row that differs from its siblings only in the fields under test. Everything in
    /// the mapper's grouping key — start date, street, house number, floor, apartment, zip,
    /// Addressid — is identical, which is exactly the shape found in production.
    /// </summary>
    private static ExuDelpointContactInfoTable Contact(
        long recid,
        string name2,
        string? phone = null,
        DateTime? startdate = null,
        string company = Company) =>
        new()
        {
            Meteringpoint = Mp,
            Contacttype = 4,
            Startdate = startdate ?? Start,
            Deliverycategory = Category,
            Companyid = company,
            Dataareaid = Area,
            Enddate = Open,
            Streetname = "Fiktivvejhp",
            Housenumber = "1",
            Apartment = "",
            Floor = "",
            Zipcode = "8000",
            Addressid = "shared-legal-addressid",
            Name1 = "Legal contact",
            Name2 = name2,
            Phone = phone,
            Mobile = null,
            Email = null,
            Recid = recid,
        };

    private async Task SeedAsync(ExuDelpointContactInfoTable[] contacts)
    {
        using var seed = NewContext();
        SeedMeteringPointSkeleton(seed);

        foreach (var contact in contacts)
        {
            seed.ExuDelpointContactInfoTables.Add(contact);
            await seed.SaveChangesAsync();
        }

        seed.AddressZipCodes.Add(
            new AddressZipCode { Zipcode = "8000", County = "751", Countryregionid = "DK", Dataareaid = Area, City = "Aarhus", Recid = 1 });
        await seed.SaveChangesAsync();
    }

    private async Task<ContactPoint> LoadLegalContact()
    {
        var mp = await MapSingle();
        return Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Legal);
    }

    private static void SeedMeteringPointSkeleton(XellentDbContext seed)
    {
        seed.ExuContractTables.Add(new ExuContractTable
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = "C001",
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
        });

        seed.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = "C001",
            Instagreenum = Agreement,
            Deliverycategory = Category,
            Productnum = "P1",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
            Amrcode = "",
            Contractpartsubreferenceid = "",
        });

        seed.ExuAgreementTables.Add(new ExuAgreementTable
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Agreementnum = Agreement,
            Deliverycategory = Category,
            Startdate = new DateTime(2021, 3, 1),
            Enddate = Open,
            Instbillingname = "Fakturanavn",
        });

        seed.ExuDelpoints.Add(new ExuDelpoint
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Num = Delpoint,
            Attachmentnum = Agreement,
            Deliverycategory = Category,
            Custaccount = Account,
            Meteringpoint = Mp,
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
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Delpointnum = Delpoint,
            Attachmentnum = Agreement,
            Deliverycategory = Category,
            Meterpointtypecode = "E17/Forb",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
        });

        seed.ExuMeterTableTrades.Add(new ExuMeterTableTrade
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Delpointnum = Delpoint,
            Agreementnum = Agreement,
            Deliverycategory = Category,
            Meternum = "M001",
            Counterconstant = 1m,
            Startdate = new DateTime(2021, 1, 1),
            Enddate = Open,
            MeteringPointDhSubType = 1,
        });

        seed.SaveChanges();
    }

    private async Task<MeteringPoint> MapSingle()
    {
        using var context = NewContext();
        var options = new MapperOptions(new DateOnly(2026, 1, 1), Commodity.Electricity);
        var mapper = new MeteringPointMapper(context, new WarningCollector(), options, Area);

        var result = await mapper.MapAsync([Account]);
        return Assert.Single(result.MeteringPoints);
    }

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options);
}
