using DataMigration.Migration.Runners;
using DataMigration.Migration.Tests.Fixtures;
using DataMigration.XellentData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.IntegrationTests.Fakes;

/// <summary>
/// A Xellent database in SQLite memory, reachable through a connection string. Code that only
/// takes a connection string (the runners, the readers, the resolvers) is pointed at it through
/// <see cref="XellentDbConnection.OverrideForTests"/>, so it runs its real queries against rows the
/// test put there.
///
/// <para>Seeds come from the anonymised snapshots in DataMigration.Migration.Tests
/// (<c>Fixtures/SeedData/&lt;account&gt;/xellent.json</c>) or are added by the test. SQLite compares
/// strings case-sensitively where SQL Server does not, so seeded delivery categories are
/// normalised the way the migration tests normalise them.</para>
/// </summary>
public sealed class FakeXellent : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDisposable _registration;

    public FakeXellent(IEnumerable<string>? seedAccounts = null, bool activateContracts = false)
    {
        ConnectionString = $"Server=fake-xellent;Database={Guid.NewGuid():N}";
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _registration = XellentDbConnection.OverrideForTests(ConnectionString, b => b.UseSqlite(_connection));

        using var context = NewContext();
        context.Database.EnsureCreated();

        var accounts = seedAccounts?.ToArray() ?? [];
        if (accounts.Length == 0) return;
        var merged = MultiAccountXellentDbSeeder.Merge(accounts.Select(XellentDbSeeder.LoadRawSeed));
        if (activateContracts) XellentDbSeeder.ActivateContracts(merged);
        MultiAccountXellentDbSeeder.NormalizeDeliveryCategory(merged);
        Seed(context, merged);
        context.SaveChanges();
    }

    /// <summary>Not a real server: only this process knows the name, and only while this object lives.</summary>
    public string ConnectionString { get; }

    public XellentDbContext NewContext() => new(new DbContextOptionsBuilder<XellentDbContext>()
        .UseXellent(ConnectionString).Options);

    /// <summary>Adds rows; string columns the test left unset become "" as in Xellent (no NULLs there).</summary>
    public void Add(params object[] entities)
    {
        using var context = NewContext();
        foreach (var entity in entities) context.Add(Defaults(entity));
        context.SaveChanges();
    }

    public static T Defaults<T>(T entity) where T : class
    {
        foreach (var property in entity.GetType().GetProperties()
                     .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.GetValue(entity) is null))
            property.SetValue(entity, "");
        return entity;
    }

    private static void Seed(XellentDbContext c, XellentSeedData d)
    {
        c.CustTables.AddRange(d.CustTables);
        c.ExuContractTables.AddRange(d.ExuContractTables);
        c.ExuContractPartTables.AddRange(d.ExuContractPartTables);
        c.DirPartyAddressRelationships.AddRange(d.DirPartyAddressRelationships);
        c.DirPartyAddressRelationshi1066s.AddRange(d.DirPartyAddressRelationshi1066s);
        c.Addresses.AddRange(d.Addresses);
        c.ExuAgreementTables.AddRange(d.ExuAgreementTables);
        c.ExuDelpoints.AddRange(d.ExuDelpoints);
        c.ExuDelpointContactInfoTables.AddRange(d.ExuDelpointContactInfoTables);
        c.AddressZipCodes.AddRange(d.AddressZipCodes);
        c.DirPartyECommunicationRela2608s.AddRange(d.DirPartyECommunicationRela2608s);
        c.DirECommunicationAddresses.AddRange(d.DirECommunicationAddresses);
        c.EgsMeterpointTypecodeperiods.AddRange(d.EgsMeterpointTypecodeperiods);
        c.ExuBillingMethodPeriods.AddRange(d.ExuBillingMethodPeriods);
        c.ExuDelpoint2delpointrela25505s.AddRange(d.ExuDelpoint2delpointrela25505s);
        c.ExuStreets.AddRange(d.ExuStreets);
        c.ExuMeterTableTrades.AddRange(d.ExuMeterTableTrades);
        c.ExuGroupTables.AddRange(d.ExuGroupTables);
        c.ExuInstallations.AddRange(d.ExuInstallations);
        c.ExuMeterJournalTrans.AddRange(d.ExuMeterJournalTrans);
    }

    public void Dispose()
    {
        _registration.Dispose();
        _connection.Dispose();
    }
}
