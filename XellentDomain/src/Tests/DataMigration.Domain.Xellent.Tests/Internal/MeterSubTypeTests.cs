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
/// The DataHub sub type sits on the meter trade row in Xellent as a number, and the domain is meant
/// to hand consumers the translated word instead. These tests seed one electricity metering point
/// and check what comes out the other end for each code, and for a meter row that says nothing.
/// </summary>
public class MeterSubTypeTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;
    private const string Account = "100";
    private const string Agreement = "A001";
    private const string Delpoint = "1";
    private const string Mp = "571313100000000001";

    private static readonly DateTime Open = MigrationConstants.EndDateNull;

    private readonly SqliteConnection _connection;

    public MeterSubTypeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var seed = NewContext();
        seed.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData(1, "Physical")]
    [InlineData(2, "Virtual")]
    [InlineData(3, "Calculated")]
    public async Task The_meter_carries_the_translated_sub_type(int dhSubType, string expected)
    {
        Seed(dhSubType);

        var mp = await MapSingle();

        Assert.Equal(expected, mp.Meter!.MeteringPointSubType);
    }

    [Fact]
    public async Task An_unknown_code_leaves_the_sub_type_empty()
    {
        // 0 is what an untouched Xellent row holds — there is nothing to translate.
        Seed(0);

        var mp = await MapSingle();

        Assert.Null(mp.Meter!.MeteringPointSubType);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private void Seed(int dhSubType)
    {
        using var seed = NewContext();

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
            MeteringPointDhSubType = dhSubType,
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
