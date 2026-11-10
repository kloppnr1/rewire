using System.Data.Common;
using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// ADDRESSZIPCODE's key is (Dataareaid, Countryregionid, Zipcode, County) — the same zip number
/// genuinely recurs across countries within one dataarea (confirmed for 'hol': 12 zips, every one
/// paired with a DK row, e.g. zip 1000 is both "Bruxelles, BE" and "København K, DK"). The legal
/// address fallback join in <see cref="CustomerMapper"/> matches on Zipcode+Dataareaid alone, so a
/// collision fanned every candidate city into the result with no country disambiguation — the pick
/// was whatever order SQL happened to return, not the account's real country.
///
/// <para>This is a separate defect from ff15a1e (which fixed the fallback's own resolved-address
/// country overriding the primary's): that fix is untouched and still applies when the fallback's
/// Addressid resolves to its own ADDRESS row. This suite covers the case where it does not, and the
/// zip lookup itself is ambiguous.</para>
/// </summary>
public class LegalAddressZipCollisionTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;

    private static readonly DateTime Open = MigrationConstants.EndDateNull;

    private readonly SqliteConnection _connection;
    private readonly SqlCapture _sql = new();

    public void Dispose() => _connection.Dispose();

    public LegalAddressZipCollisionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var seed = NewContext();
        seed.Database.EnsureCreated();
    }

    // ── Account 429529 mirror: zip collision + unresolved fallback Addressid ──────────────────

    [Fact]
    public async Task Zip_collision_resolves_to_the_primary_countrys_city_not_an_arbitrary_one()
    {
        using (var seed = NewContext())
        {
            SeedAccount(
                seed,
                account: "429529",
                partyId: "P429529",
                primaryStreet: "Rue Ernest Allard",
                primaryZip: "1000",
                primaryCity: "Bruxelles",
                primaryCountry: "BE",
                legalStreet: "Rue Ernest Allard",
                legalZip: "1000",
                // Unresolved: no ADDRESS row carries this EXU_ADDRESSID — the same fallthrough
                // ff15a1e already handles for the "no legal address row at all" case.
                legalAddressid: "unresolved-guid-429529");

            // The genuine collision: two ADDRESSZIPCODE rows for zip 1000 in 'hol', one Belgian,
            // one Danish — exactly the production data.
            seed.AddressZipCodes.AddRange(
                new AddressZipCode { Zipcode = "1000", County = "BE1", Countryregionid = "BE", Dataareaid = Area, City = "Bruxelles", Recid = 1 },
                new AddressZipCode { Zipcode = "1000", County = "DK1", Countryregionid = "DK", Dataareaid = Area, City = "København K", Recid = 2 });

            seed.SaveChanges();
        }

        var customer = await MapSingle("429529");

        Assert.Equal("Bruxelles", customer.Address.City);
        Assert.Equal("BE", customer.Address.CountryCode);
    }

    [Fact]
    public async Task Collision_but_no_primary_country_hint_falls_back_without_throwing()
    {
        using (var seed = NewContext())
        {
            SeedAccount(
                seed,
                account: "429530",
                partyId: "P429530",
                primaryStreet: "Rue Ernest Allard",
                primaryZip: "1000",
                primaryCity: "",
                primaryCountry: "", // no hint available
                legalStreet: "Rue Ernest Allard",
                legalZip: "1000",
                legalAddressid: "unresolved-guid-429530");

            seed.AddressZipCodes.AddRange(
                new AddressZipCode { Zipcode = "1000", County = "BE1", Countryregionid = "BE", Dataareaid = Area, City = "Bruxelles", Recid = 3 },
                new AddressZipCode { Zipcode = "1000", County = "DK1", Countryregionid = "DK", Dataareaid = Area, City = "København K", Recid = 4 });

            seed.SaveChanges();
        }

        // Must not throw, and must still produce a customer — an ambiguous zip is a data-quality
        // warning, not a hard failure.
        var customer = await MapSingle("429530");

        Assert.NotNull(customer.Address.City);
        Assert.False(string.IsNullOrEmpty(customer.Address.City));
    }

    // ── No collision: unchanged behaviour ──────────────────────────────────────────────────────

    [Fact]
    public async Task No_collision_behaves_exactly_as_before()
    {
        using (var seed = NewContext())
        {
            SeedAccount(
                seed,
                account: "500",
                partyId: "P500",
                primaryStreet: "Fiktivvejhp",
                primaryZip: "8000",
                primaryCity: "Aarhus",
                primaryCountry: "DK",
                legalStreet: "Fiktivvejhp",
                legalZip: "8000",
                legalAddressid: "unresolved-guid-500");

            seed.AddressZipCodes.Add(
                new AddressZipCode { Zipcode = "8000", County = "751", Countryregionid = "DK", Dataareaid = Area, City = "Aarhus", Recid = 5 });

            seed.SaveChanges();
        }

        var customer = await MapSingle("500");

        Assert.Equal("Aarhus", customer.Address.City);
        Assert.Equal("DK", customer.Address.CountryCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<Customer> MapSingle(string account)
    {
        using var context = NewContext();
        var options = new MapperOptions(new DateOnly(2026, 1, 1), Commodity.Electricity);
        var mapper = new CustomerMapper(context, new WarningCollector(), options, Area);

        var result = await mapper.MapAsync([account]);
        return Assert.Single(result.Customers);
    }

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_sql)
            .Options);

    /// <summary>
    /// Seeds one account with a primary CustTable/Address row and one El-ekstern delpoint whose
    /// legal contact (Contacttype 4) carries the given street/zip and an Addressid that resolves
    /// to no ADDRESS row (the "unresolved fallback" case this suite targets).
    /// </summary>
    private static void SeedAccount(
        XellentDbContext ctx,
        string account,
        string partyId,
        string primaryStreet,
        string primaryZip,
        string primaryCity,
        string primaryCountry,
        string legalStreet,
        string legalZip,
        string legalAddressid)
    {
        var contractNum = "C" + account;
        var agreement = "A" + account;
        var delpoint = "D" + account;
        var mp = "5713131" + account.PadLeft(11, '0');

        ctx.CustTables.Add(new CustTable
        {
            Accountnum = account,
            Dataareaid = Area,
            Name = "Test",
            Address = "",
            Street = "",
            City = "",
            Zipcode = "",
            Countryregionid = "",
            Phone = "",
            Cellularphone = "",
            Telefax = "",
            Email = "",
            Companyregnum = "",
            Identificationnumber = "",
            Namealias = "",
            ExuCprDisponent1 = "",
            Exualternativecustomernumber = "",
            ExuAdditionalname = "",
            ExuCprDisponent2 = "",
            ExuCvrDisponent1 = "",
            ExuCvrDisponent2 = "",
            Recid = long.Parse(account),
            Partyid = partyId,
            ExuTechnicgroup4 = "",
        });

        ctx.DirPartyAddressRelationships.Add(new DirPartyAddressRelationship
        {
            Recid = long.Parse(account),
            Dataareaid = Area,
            Partyid = partyId,
            Isprimary = 1,
            Validtodatetime = new DateTime(2154, 12, 31),
        });
        ctx.DirPartyAddressRelationshi1066s.Add(new DirPartyAddressRelationshi1066
        {
            Recid = long.Parse(account),
            Dataareaid = Area,
            Addressrecid = long.Parse(account),
            Partyaddressrelationshiprecid = long.Parse(account),
        });
        ctx.Addresses.Add(new XellentData.Entities.Address
        {
            Recid = long.Parse(account),
            Dataareaid = Area,
            Street = primaryStreet,
            FullAddress = primaryStreet,
            Zipcode = primaryZip,
            City = primaryCity,
            Countryregionid = primaryCountry,
            State = "",
            County = "",
            Addrtableid = MigrationConstants.AddressTableId,
        });

        ctx.ExuContractTables.Add(new ExuContractTable
        {
            Recid = long.Parse(account) + 100_000,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = contractNum,
            Custaccount = account,
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

        ctx.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Recid = long.Parse(account) + 200_000,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = contractNum,
            Instagreenum = agreement,
            Deliverycategory = Category,
            Productnum = "P1",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
            Amrcode = "",
            Contractpartsubreferenceid = "",
        });

        ctx.ExuAgreementTables.Add(new ExuAgreementTable
        {
            Recid = long.Parse(account) + 300_000,
            Dataareaid = Area,
            Companyid = Company,
            Agreementnum = agreement,
            Deliverycategory = Category,
            Startdate = new DateTime(2021, 3, 1),
            Enddate = Open,
            Instbillingname = "",
        });

        ctx.ExuDelpoints.Add(new ExuDelpoint
        {
            Recid = long.Parse(account) + 400_000,
            Dataareaid = Area,
            Companyid = Company,
            Num = delpoint,
            Attachmentnum = agreement,
            Deliverycategory = Category,
            Custaccount = account,
            Meteringpoint = mp,
            Extnum = "",
            Name = "",
            Streetnum = "",
            Countynum = "",
            Zipcodeid = legalZip,
            Addressstreet = "",
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

        ctx.ExuDelpointContactInfoTables.Add(new ExuDelpointContactInfoTable
        {
            Meteringpoint = mp,
            Contacttype = 4,
            Startdate = new DateTime(2021, 1, 1),
            Deliverycategory = Category,
            Companyid = Company,
            Dataareaid = Area,
            Enddate = Open,
            Streetname = legalStreet,
            Housenumber = "1",
            Apartment = "",
            Floor = "",
            Zipcode = legalZip,
            Addressid = legalAddressid,
            Name1 = "Legal contact",
            Name2 = "",
            Phone = null,
            Mobile = null,
            Email = null,
            Recid = long.Parse(account) + 500_000,
        });
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        public IReadOnlyList<string> Statements
        {
            get { lock (_statements) return [.. _statements]; }
        }

        public void Clear() { lock (_statements) _statements.Clear(); }

        private void Record(DbCommand command)
        {
            lock (_statements) _statements.Add(command.CommandText);
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
