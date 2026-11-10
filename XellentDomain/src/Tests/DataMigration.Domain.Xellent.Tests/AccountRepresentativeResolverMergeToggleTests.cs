using System.Data.Common;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// Locks the two sides of <c>disableMerge</c> against each other on one and the same data set:
/// with the merge on nothing may change, and with it off the fingerprint — and the primary-address
/// lookup behind it — must be gone entirely.
/// </summary>
public class AccountRepresentativeResolverMergeToggleTests : IDisposable
{
    private const string Area = "hol";

    private readonly SqliteConnection _connection;
    private readonly SqlCapture _sql = new();

    public AccountRepresentativeResolverMergeToggleTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var seed = NewContext();
        seed.Database.EnsureCreated();

        // Two accounts describing themselves identically (same CVR, name, address, email) and a
        // third under the same CVR at another address. 400 has no CVR at all.
        seed.CustTables.AddRange(
            Cust("100", cvr: "36665030", name: "Acme", email: "ubc@dp.example.dk", partyId: "P100"),
            Cust("200", cvr: "36665030", name: "Acme", email: "ubc@dp.example.dk", partyId: "P200"),
            Cust("300", cvr: "36665030", name: "Acme", email: "ubc@dp.example.dk", partyId: "P300"),
            Cust("400", cvr: "", name: "Solo", email: "", partyId: "P400"));

        AddAddress(seed, partyId: "P100", recid: 1, street: "Fiktivvejhp", zip: "8000", city: "Aarhus");
        AddAddress(seed, partyId: "P200", recid: 2, street: "Fiktivvejhp", zip: "8000", city: "Aarhus");
        AddAddress(seed, partyId: "P300", recid: 3, street: "Fiktivvejbin", zip: "8900", city: "Randers");
        AddAddress(seed, partyId: "P400", recid: 4, street: "Fiktivvejbvo", zip: "8600", city: "Silkeborg");

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ── Merge on: unchanged, field for field ──────────────────────────────────────────────────

    [Fact]
    public async Task With_merge_the_fingerprint_is_computed_for_every_mergeable_account()
    {
        var result = await Resolve(disableMerge: false);

        Assert.Equal(
            new[] { "100", "200", "300" },
            result.FingerprintByAccount.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // Same name, address and email → same fingerprint. The other address is its own.
        Assert.Equal(
            result.FingerprintByAccount["100"].Fingerprint,
            result.FingerprintByAccount["200"].Fingerprint);
        Assert.NotEqual(
            result.FingerprintByAccount["100"].Fingerprint,
            result.FingerprintByAccount["300"].Fingerprint);

        var fp = result.FingerprintByAccount["100"];
        Assert.Equal("Acme", fp.Name);
        Assert.Equal("Fiktivvejhp", fp.Street);
        Assert.Equal("8000", fp.PostalCode);
        Assert.Equal("Aarhus", fp.City);
        Assert.Equal("ubc@dp.example.dk", fp.Email);
    }

    [Fact]
    public async Task With_merge_the_matching_pair_resolves_to_the_lowest_accountnum()
    {
        var result = await Resolve(disableMerge: false);

        Assert.Equal("100", result.RepresentativeByAccount["100"]);
        Assert.Equal("100", result.RepresentativeByAccount["200"]);
        Assert.Equal("300", result.RepresentativeByAccount["300"]);
        Assert.Equal("400", result.RepresentativeByAccount["400"]);
        Assert.Equal(1, result.MergedCount);
        Assert.Equal(new[] { "100", "300", "400" }, result.Accounts.ToArray());
    }

    // ── Merge off ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_merge_no_fingerprint_is_computed()
    {
        var result = await Resolve(disableMerge: true);

        Assert.Empty(result.FingerprintByAccount);
    }

    [Fact]
    public async Task Without_merge_every_account_stands_alone_but_names_still_come_along()
    {
        var withMerge = await Resolve(disableMerge: false);
        var withoutMerge = await Resolve(disableMerge: true);

        // The merge itself is the only difference: 200 keeps itself instead of folding into 100.
        Assert.Equal("100", withoutMerge.RepresentativeByAccount["100"]);
        Assert.Equal("200", withoutMerge.RepresentativeByAccount["200"]);
        Assert.Equal("300", withoutMerge.RepresentativeByAccount["300"]);
        Assert.Equal("400", withoutMerge.RepresentativeByAccount["400"]);
        Assert.Equal(0, withoutMerge.MergedCount);
        Assert.Equal(new[] { "100", "200", "300", "400" }, withoutMerge.Accounts.ToArray());

        // Everything read off CustTable itself is identical on both paths.
        Assert.Equal(
            withMerge.NameByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray(),
            withoutMerge.NameByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Without_merge_an_ignored_customer_group_still_stands_alone()
    {
        var ignored = new HashSet<string>(["EXCL"], StringComparer.OrdinalIgnoreCase);

        var withMerge = await Resolve(disableMerge: false, ignoredGroupNumbers: ignored);
        var withoutMerge = await Resolve(disableMerge: true, ignoredGroupNumbers: ignored);

        // TG4 is read on both paths — the ignore list must bite the same way.
        Assert.Equal("100", withMerge.RepresentativeByAccount["100"]);
        Assert.Equal("200", withMerge.RepresentativeByAccount["200"]);
        Assert.Equal("200", withoutMerge.RepresentativeByAccount["200"]);
    }

    // ── The address lookup is really gone ─────────────────────────────────────────────────────

    [Fact]
    public async Task Without_merge_the_address_tables_are_never_queried()
    {
        _sql.Clear();
        await Resolve(disableMerge: true);

        Assert.DoesNotContain(_sql.Statements, s => s.Contains("DIRPARTYADDRESSRELATIONSHIP"));
        Assert.DoesNotContain(_sql.Statements, s => s.Contains("DIRPARTYADDRESSRELATIONSHI1066"));
        Assert.DoesNotContain(_sql.Statements, s => s.Contains("\"ADDRESS\""));
        Assert.DoesNotContain(_sql.Statements, s => s.Contains("LEFT JOIN"));

        // …and the columns that survive still come from CustTable.
        Assert.Contains(_sql.Statements, s => s.Contains("CUSTTABLE") && s.Contains("EXU_TECHNICGROUP4"));
    }

    [Fact]
    public async Task With_merge_the_address_tables_are_queried()
    {
        _sql.Clear();
        await Resolve(disableMerge: false);

        Assert.Contains(_sql.Statements, s => s.Contains("DIRPARTYADDRESSRELATIONSHIP"));
        Assert.Contains(_sql.Statements, s => s.Contains("LEFT JOIN"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<AccountRepresentativeResolution> Resolve(
        bool disableMerge, IReadOnlySet<string>? ignoredGroupNumbers = null)
    {
        using var context = NewContext();
        return await AccountRepresentativeResolver.ResolveAsync(
            context,
            accounts: null,
            dataAreaId: Area,
            referenceDate: new DateOnly(2026, 1, 1),
            ignoredGroupNumbers: ignoredGroupNumbers,
            disableMerge: disableMerge);
    }

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_sql)
            .Options);

    private static CustTable Cust(string account, string cvr, string name, string email, string partyId) =>
        new()
        {
            Accountnum = account,
            Dataareaid = Area,
            Name = name,
            Address = "",
            Street = "",
            City = "",
            Zipcode = "",
            Countryregionid = "",
            Phone = "",
            Cellularphone = "",
            Telefax = "",
            Email = email,
            Companyregnum = "",
            Identificationnumber = "",
            Namealias = "",
            ExuCprDisponent1 = "",
            Exualternativecustomernumber = "",
            ExuAdditionalname = "",
            ExuCprDisponent2 = "",
            ExuCvrDisponent1 = cvr,
            ExuCvrDisponent2 = "",
            Recid = long.Parse(account),
            Partyid = partyId,
            // 100 sits in an ignorable customer group; the others do not.
            ExuTechnicgroup4 = account == "100" ? "EXCL" : "NORM",
        };

    private static void AddAddress(
        XellentDbContext ctx, string partyId, long recid, string street, string zip, string city)
    {
        ctx.DirPartyAddressRelationships.Add(new DirPartyAddressRelationship
        {
            Recid = recid,
            Dataareaid = Area,
            Partyid = partyId,
            Isprimary = 1,
            Validtodatetime = new DateTime(2154, 12, 31),
        });
        ctx.DirPartyAddressRelationshi1066s.Add(new DirPartyAddressRelationshi1066
        {
            Recid = recid,
            Dataareaid = Area,
            Addressrecid = recid,
            Partyaddressrelationshiprecid = recid,
        });
        ctx.Addresses.Add(new Address
        {
            Recid = recid,
            Dataareaid = Area,
            Street = street,
            FullAddress = street,
            Zipcode = zip,
            City = city,
            Countryregionid = "DK",
            State = "",
            County = "",
            Addrtableid = DataMigration.Core.MigrationConstants.AddressTableId,
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
