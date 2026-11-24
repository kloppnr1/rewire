using System.Data.Common;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// The resolver reads its account filter in portions, because a whole data area's worth of accounts
/// in one inlined IN list is more than SQL Server will compile (error 8632 — "an expression services
/// limit has been reached"), joins or no joins.
///
/// <para>What has to hold: an account set larger than the limit is read in several statements, a
/// null filter is still read in one, and the answer is the same either way — including a merge
/// between two accounts that land in different portions, which only works because every row is
/// unioned before any edge is built.</para>
/// </summary>
public class AccountRepresentativeResolverChunkingTests : IDisposable
{
    private const string Area = "hol";

    /// <summary>Two portions' worth: the first and last account fall on either side of the cut.</summary>
    private const int AccountCount = XellentQueryLimits.ChunkSize + 500;

    private const string FirstAccount = "10000";
    private static readonly string LastAccount = (10000 + AccountCount - 1).ToString();

    private readonly SqliteConnection _connection;
    private readonly SqlCapture _sql = new();
    private readonly string[] _allAccounts;

    public AccountRepresentativeResolverChunkingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var seed = NewContext();
        seed.ChangeTracker.AutoDetectChangesEnabled = false;
        seed.Database.EnsureCreated();

        _allAccounts = Enumerable.Range(10000, AccountCount).Select(i => i.ToString()).ToArray();

        foreach (var account in _allAccounts)
        {
            var recid = long.Parse(account);

            // The first and the last account describe themselves identically, so they must merge —
            // and they sit in different portions. Everyone else stands alone on their own street.
            var shared = account == FirstAccount || account == LastAccount;

            seed.CustTables.Add(Cust(
                account,
                cvr: shared ? "36665030" : "",
                name: shared ? "Acme" : "Kunde " + account,
                email: shared ? "ubc@dp.example.dk" : "",
                partyId: "P" + account));

            AddAddress(
                seed,
                partyId: "P" + account,
                recid: recid,
                street: shared ? "Fiktivvejhp" : "Vej " + account,
                zip: "8000",
                city: "Aarhus");
        }

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_large_account_set_is_read_in_several_statements()
    {
        _sql.Clear();
        await Resolve(_allAccounts, disableMerge: true);

        // Two portions over the account count above — one statement each, never one giant IN list.
        Assert.Equal(2, _sql.Statements.Count(s => s.Contains("CUSTTABLE")));
    }

    [Fact]
    public async Task A_null_filter_is_still_a_single_unfiltered_read()
    {
        _sql.Clear();
        await Resolve(null, disableMerge: true);

        var custStatements = _sql.Statements.Where(s => s.Contains("CUSTTABLE")).ToList();
        Assert.Single(custStatements);
        Assert.DoesNotContain("ACCOUNTNUM\" IN", custStatements[0]);
    }

    [Fact]
    public async Task Reading_in_portions_returns_every_account()
    {
        var listed = await Resolve(_allAccounts, disableMerge: true);
        var unfiltered = await Resolve(null, disableMerge: true);

        Assert.Equal(AccountCount, listed.RepresentativeByAccount.Count);
        Assert.Equal(unfiltered.Accounts, listed.Accounts);
        Assert.Equal(AccountCount, listed.NameByAccount.Count);
    }

    [Fact]
    public async Task A_merge_across_the_portion_boundary_still_happens()
    {
        var result = await Resolve(_allAccounts, disableMerge: false);

        // The two identical accounts are read by different statements; the edge between them is
        // built after both portions have landed, so they still fold into the lowest accountnum.
        Assert.Equal(FirstAccount, result.RepresentativeByAccount[FirstAccount]);
        Assert.Equal(FirstAccount, result.RepresentativeByAccount[LastAccount]);
        Assert.Equal(1, result.MergedCount);
    }

    [Fact]
    public async Task The_portioned_answer_matches_the_unfiltered_one_with_the_merge_on()
    {
        var listed = await Resolve(_allAccounts, disableMerge: false);
        var unfiltered = await Resolve(null, disableMerge: false);

        Assert.Equal(unfiltered.Accounts, listed.Accounts);
        Assert.Equal(unfiltered.MergedCount, listed.MergedCount);
        Assert.Equal(
            unfiltered.RepresentativeByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            listed.RepresentativeByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal));
        Assert.Equal(
            unfiltered.FingerprintByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            listed.FingerprintByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_legacy_path_reads_in_portions_too()
    {
        _sql.Clear();
        var result = await Resolve(_allAccounts, disableMerge: false, legacyTg4Merge: true);

        Assert.Equal(2, _sql.Statements.Count(s => s.Contains("CUSTTABLE")));
        Assert.Equal(AccountCount, result.RepresentativeByAccount.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<AccountRepresentativeResolution> Resolve(
        string[]? accounts, bool disableMerge, bool legacyTg4Merge = false)
    {
        using var context = NewContext();
        return await AccountRepresentativeResolver.ResolveAsync(
            context,
            accounts,
            dataAreaId: Area,
            legacyTg4Merge: legacyTg4Merge,
            referenceDate: new DateOnly(2026, 1, 1),
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
            ExuTechnicgroup4 = "NORM",
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
