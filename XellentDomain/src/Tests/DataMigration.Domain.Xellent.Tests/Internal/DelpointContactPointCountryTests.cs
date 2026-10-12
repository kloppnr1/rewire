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
/// <see cref="MeteringPointMapper.LoadDelpointContactPointsAsync"/> reads the same kind of record
/// as <see cref="CustomerMapper.FetchLegalAddressFallbacksAsync"/> — EXU_DELPOINTCONTACTINFOTABLE,
/// Contacttype 4 — but scoped per metering point instead of per account, to build the Legal/Technical
/// <see cref="ContactPoint"/> pair attached to a metering point.
///
/// <para>Before this fix it always passed <c>CountryCode: null</c> into
/// <see cref="AddressMapper.ResolveCustomerAddress"/>, so it fell straight through to the module's
/// DK default regardless of what country the legal contact actually sits in — the same bug already
/// fixed for the account-scoped fallback in <see cref="CustomerMapper"/>. It also joined
/// ADDRESSZIPCODE on Zipcode+Dataareaid alone, exposed to the same cross-country zip collisions
/// (12 known collisions in dataarea 'hol', e.g. zip 1000 = both Bruxelles/BE and København K/DK).</para>
///
/// <para>This does NOT touch <c>ResolveDelpointAddress</c> — the metering point's own physical
/// installation address, which is genuinely always Danish. This is the separate legal-contact
/// record: who the correspondent is, unrelated to where the meter sits.</para>
/// </summary>
public class DelpointContactPointCountryTests : IDisposable
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

    public DelpointContactPointCountryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var seed = NewContext();
        seed.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_genuinely_foreign_legal_contact_keeps_its_own_country()
    {
        using (var seed = NewContext())
        {
            SeedMeteringPointSkeleton(seed);

            // The contact's own Addressid resolves to a real ADDRESS row in Belgium.
            seed.Addresses.Add(new XellentData.Entities.Address
            {
                Recid = 900,
                Dataareaid = Area,
                Street = "Rue Ernest Allard",
                FullAddress = "Rue Ernest Allard",
                Zipcode = "1000",
                City = "Bruxelles",
                Countryregionid = "BE",
                State = "",
                County = "",
                Addrtableid = MigrationConstants.AddressTableId,
                ExuAddressid = "addr-legal-be",
            });

            seed.ExuDelpointContactInfoTables.Add(new ExuDelpointContactInfoTable
            {
                Meteringpoint = Mp,
                Contacttype = 4,
                Startdate = new DateTime(2021, 1, 1),
                Deliverycategory = Category,
                Companyid = Company,
                Dataareaid = Area,
                Enddate = Open,
                Streetname = "Rue Ernest Allard",
                Housenumber = "1",
                Apartment = "",
                Floor = "",
                Zipcode = "1000",
                Addressid = "addr-legal-be",
                Name1 = "Legal contact",
                Name2 = "",
                Phone = null,
                Mobile = null,
                Email = null,
                Recid = 501,
            });

            seed.AddressZipCodes.Add(
                new AddressZipCode { Zipcode = "1000", County = "BE1", Countryregionid = "BE", Dataareaid = Area, City = "Bruxelles", Recid = 1 });

            seed.SaveChanges();
        }

        var mp = await MapSingle();

        var legal = Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Legal);
        Assert.Equal("BE", legal.Address.CountryCode);
        Assert.Equal("Bruxelles", legal.Address.City);

        var technical = Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Technical);
        Assert.Equal("BE", technical.Address.CountryCode);
    }

    [Fact]
    public async Task Zip_collision_resolves_to_the_contacts_own_country_not_an_arbitrary_one()
    {
        using (var seed = NewContext())
        {
            SeedMeteringPointSkeleton(seed);

            seed.Addresses.Add(new XellentData.Entities.Address
            {
                Recid = 901,
                Dataareaid = Area,
                Street = "Rue Ernest Allard",
                FullAddress = "Rue Ernest Allard",
                Zipcode = "1000",
                City = "Bruxelles",
                Countryregionid = "BE",
                State = "",
                County = "",
                Addrtableid = MigrationConstants.AddressTableId,
                ExuAddressid = "addr-legal-be-2",
            });

            seed.ExuDelpointContactInfoTables.Add(new ExuDelpointContactInfoTable
            {
                Meteringpoint = Mp,
                Contacttype = 4,
                Startdate = new DateTime(2021, 1, 1),
                Deliverycategory = Category,
                Companyid = Company,
                Dataareaid = Area,
                Enddate = Open,
                Streetname = "Rue Ernest Allard",
                Housenumber = "1",
                Apartment = "",
                Floor = "",
                Zipcode = "1000",
                Addressid = "addr-legal-be-2",
                Name1 = "Legal contact",
                Name2 = "",
                Phone = null,
                Mobile = null,
                Email = null,
                Recid = 502,
            });

            // The genuine collision: two ADDRESSZIPCODE rows for zip 1000 in 'hol', one Belgian,
            // one Danish — the zip lookup alone cannot tell them apart; the contact's own resolved
            // country (BE, via Addressid above) must be the tie-breaker.
            seed.AddressZipCodes.AddRange(
                new AddressZipCode { Zipcode = "1000", County = "BE1", Countryregionid = "BE", Dataareaid = Area, City = "Bruxelles", Recid = 2 },
                new AddressZipCode { Zipcode = "1000", County = "DK1", Countryregionid = "DK", Dataareaid = Area, City = "København K", Recid = 3 });

            seed.SaveChanges();
        }

        var mp = await MapSingle();

        var legal = Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Legal);
        Assert.Equal("BE", legal.Address.CountryCode);
        Assert.Equal("Bruxelles", legal.Address.City);
    }

    [Fact]
    public async Task No_collision_and_no_own_address_falls_back_to_dk_as_before()
    {
        using (var seed = NewContext())
        {
            SeedMeteringPointSkeleton(seed);

            // No ADDRESS row for this Addressid — the "unresolved fallback" fallthrough. There is
            // no better signal than the zip lookup, and it is unambiguous here, so DK stands.
            seed.ExuDelpointContactInfoTables.Add(new ExuDelpointContactInfoTable
            {
                Meteringpoint = Mp,
                Contacttype = 4,
                Startdate = new DateTime(2021, 1, 1),
                Deliverycategory = Category,
                Companyid = Company,
                Dataareaid = Area,
                Enddate = Open,
                Streetname = "Fiktivvejhp",
                Housenumber = "1",
                Apartment = "",
                Floor = "",
                Zipcode = "8000",
                Addressid = "unresolved-legal-addressid",
                Name1 = "Legal contact",
                Name2 = "",
                Phone = null,
                Mobile = null,
                Email = null,
                Recid = 503,
            });

            seed.AddressZipCodes.Add(
                new AddressZipCode { Zipcode = "8000", County = "751", Countryregionid = "DK", Dataareaid = Area, City = "Aarhus", Recid = 4 });

            seed.SaveChanges();
        }

        var mp = await MapSingle();

        var legal = Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Legal);
        Assert.Equal("DK", legal.Address.CountryCode);
        Assert.Equal("Aarhus", legal.Address.City);
    }

    [Fact]
    public async Task Blank_legal_addressid_falls_through_to_zip_country_not_dk()
    {
        using (var seed = NewContext())
        {
            SeedMeteringPointSkeleton(seed);

            // dbo.ADDRESS carries ~140k rows with EXU_ADDRESSID = '' in production, spanning every
            // country. A blank Addressid here must never join against any of them — it must fall
            // straight through to the ZipCountryCode fallback (Winnemark/DE, zip 24398), not DK.
            seed.Addresses.Add(new XellentData.Entities.Address
            {
                Recid = 910,
                Dataareaid = Area,
                Street = "Irgendeine Str",
                FullAddress = "Irgendeine Str",
                Zipcode = "24398",
                City = "Winnemark",
                Countryregionid = "DE",
                State = "",
                County = "",
                Addrtableid = MigrationConstants.AddressTableId,
                ExuAddressid = "", // blank id, like the ~140k unresolved DAR rows in production
            });

            seed.ExuDelpointContactInfoTables.Add(new ExuDelpointContactInfoTable
            {
                Meteringpoint = Mp,
                Contacttype = 4,
                Startdate = new DateTime(2021, 1, 1),
                Deliverycategory = Category,
                Companyid = Company,
                Dataareaid = Area,
                Enddate = Open,
                Streetname = "Irgendeine Str",
                Housenumber = "1",
                Apartment = "",
                Floor = "",
                Zipcode = "24398",
                Addressid = "", // DAR sync hasn't caught up with a freshly changed legal address
                Name1 = "Legal contact",
                Name2 = "",
                Phone = null,
                Mobile = null,
                Email = null,
                Recid = 504,
            });

            seed.AddressZipCodes.Add(
                new AddressZipCode { Zipcode = "24398", County = "", Countryregionid = "DE", Dataareaid = Area, City = "Winnemark", Recid = 5 });

            seed.SaveChanges();
        }

        var mp = await MapSingle();

        var legal = Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Legal);
        Assert.Equal("DE", legal.Address.CountryCode);
        Assert.Equal("Winnemark", legal.Address.City);
    }

    [Fact]
    public async Task Blank_legal_addressid_with_many_blank_address_rows_does_not_leak_a_random_country()
    {
        using (var seed = NewContext())
        {
            SeedMeteringPointSkeleton(seed);

            // Several unrelated blank-Addressid rows in different countries — simulating the real
            // ~140k-row fan-out at a scale a test can seed. None of them must ever be picked: the
            // join must simply not fire on a blank key, whatever else shares that blank key.
            seed.Addresses.AddRange(
                new XellentData.Entities.Address
                {
                    Recid = 920, Dataareaid = Area, Street = "A", FullAddress = "A", Zipcode = "1000",
                    City = "Bruxelles", Countryregionid = "BE", State = "", County = "",
                    Addrtableid = MigrationConstants.AddressTableId, ExuAddressid = "",
                },
                new XellentData.Entities.Address
                {
                    Recid = 921, Dataareaid = Area, Street = "B", FullAddress = "B", Zipcode = "2000",
                    City = "Oslo", Countryregionid = "NO", State = "", County = "",
                    Addrtableid = MigrationConstants.AddressTableId, ExuAddressid = "",
                },
                new XellentData.Entities.Address
                {
                    Recid = 922, Dataareaid = Area, Street = "C", FullAddress = "C", Zipcode = "3000",
                    City = "Stockholm", Countryregionid = "SE", State = "", County = "",
                    Addrtableid = MigrationConstants.AddressTableId, ExuAddressid = "",
                });

            seed.ExuDelpointContactInfoTables.Add(new ExuDelpointContactInfoTable
            {
                Meteringpoint = Mp,
                Contacttype = 4,
                Startdate = new DateTime(2021, 1, 1),
                Deliverycategory = Category,
                Companyid = Company,
                Dataareaid = Area,
                Enddate = Open,
                Streetname = "Irgendeine Str",
                Housenumber = "1",
                Apartment = "",
                Floor = "",
                Zipcode = "24398",
                Addressid = "",
                Name1 = "Legal contact",
                Name2 = "",
                Phone = null,
                Mobile = null,
                Email = null,
                Recid = 505,
            });

            seed.AddressZipCodes.Add(
                new AddressZipCode { Zipcode = "24398", County = "", Countryregionid = "DE", Dataareaid = Area, City = "Winnemark", Recid = 6 });

            seed.SaveChanges();
        }

        var mp = await MapSingle();

        var legal = Assert.Single(mp.ContactPoints, c => c.Type == ContactPointType.Legal);
        Assert.Equal("DE", legal.Address.CountryCode);
        Assert.Equal("Winnemark", legal.Address.City);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

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
