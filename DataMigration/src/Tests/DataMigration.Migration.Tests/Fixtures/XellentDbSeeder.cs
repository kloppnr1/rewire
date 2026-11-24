using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.XellentData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Reads xellent.json seed data and creates a SQLite in-memory XellentDbContext
/// seeded with those exact rows. No connection to the real Xellent database.
/// </summary>
public sealed class XellentDbSeeder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SqliteConnection _connection;
    public XellentDbContext Context { get; }

    public XellentDbSeeder(string accountNumber, bool activateContracts = false)
    {
        var seedData = LoadRawSeed(accountNumber);
        if (activateContracts) ActivateContracts(seedData);

        // Keep connection open for lifetime of seeder — required for SQLite in-memory
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new XellentDbContext(options);
        Context.Database.EnsureCreated();

        // SQL Server uses case-insensitive collation, so "El-ekstern" == "El-Ekstern" in production.
        // SQLite uses case-sensitive comparison by default. Normalize delivery category to lowercase
        // so LINQ equality checks against profile delivery category ("El-ekstern") behave the same.
        NormalizeDeliveryCategory(seedData);

        if (seedData.CustTables.Count > 0) Context.CustTables.AddRange(seedData.CustTables);
        if (seedData.ExuContractTables.Count > 0) Context.ExuContractTables.AddRange(seedData.ExuContractTables);
        if (seedData.ExuContractPartTables.Count > 0) Context.ExuContractPartTables.AddRange(seedData.ExuContractPartTables);
        if (seedData.DirPartyAddressRelationships.Count > 0) Context.DirPartyAddressRelationships.AddRange(seedData.DirPartyAddressRelationships);
        if (seedData.DirPartyAddressRelationshi1066s.Count > 0) Context.DirPartyAddressRelationshi1066s.AddRange(seedData.DirPartyAddressRelationshi1066s);
        if (seedData.Addresses.Count > 0) Context.Addresses.AddRange(seedData.Addresses);
        if (seedData.ExuAgreementTables.Count > 0) Context.ExuAgreementTables.AddRange(seedData.ExuAgreementTables);
        if (seedData.ExuDelpoints.Count > 0) Context.ExuDelpoints.AddRange(seedData.ExuDelpoints);
        if (seedData.ExuDelpointContactInfoTables.Count > 0) Context.ExuDelpointContactInfoTables.AddRange(seedData.ExuDelpointContactInfoTables);
        if (seedData.AddressZipCodes.Count > 0) Context.AddressZipCodes.AddRange(seedData.AddressZipCodes);
        if (seedData.DirPartyECommunicationRela2608s.Count > 0) Context.DirPartyECommunicationRela2608s.AddRange(seedData.DirPartyECommunicationRela2608s);
        if (seedData.DirECommunicationAddresses.Count > 0) Context.DirECommunicationAddresses.AddRange(seedData.DirECommunicationAddresses);
        if (seedData.EgsMeterpointTypecodeperiods.Count > 0) Context.EgsMeterpointTypecodeperiods.AddRange(seedData.EgsMeterpointTypecodeperiods);
        if (seedData.ExuBillingMethodPeriods.Count > 0) Context.ExuBillingMethodPeriods.AddRange(seedData.ExuBillingMethodPeriods);
        if (seedData.ExuDelpoint2delpointrela25505s.Count > 0) Context.ExuDelpoint2delpointrela25505s.AddRange(seedData.ExuDelpoint2delpointrela25505s);
        if (seedData.ExuStreets.Count > 0) Context.ExuStreets.AddRange(seedData.ExuStreets);
        if (seedData.ExuMeterTableTrades.Count > 0) Context.ExuMeterTableTrades.AddRange(seedData.ExuMeterTableTrades);
        if (seedData.ExuGroupTables.Count > 0) Context.ExuGroupTables.AddRange(seedData.ExuGroupTables);
        if (seedData.ExuInstallations.Count > 0) Context.ExuInstallations.AddRange(seedData.ExuInstallations);
        if (seedData.ExuMeterJournalTrans.Count > 0) Context.ExuMeterJournalTrans.AddRange(seedData.ExuMeterJournalTrans);

        Context.SaveChanges();
    }

    public static XellentSeedData LoadRawSeed(string accountNumber)
    {
        var seedPath = Path.Combine(
            Path.GetDirectoryName(typeof(XellentDbSeeder).Assembly.Location)!,
            "Fixtures", "SeedData", accountNumber, "xellent.json");

        if (!File.Exists(seedPath))
            throw new FileNotFoundException($"Seed data not found: {seedPath}. Run 'generate-seed {accountNumber}' first.");

        var json = File.ReadAllText(seedPath);
        return JsonSerializer.Deserialize<XellentSeedData>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize seed data from {seedPath}");
    }

    /// <summary>
    /// Shifts contract and contract part dates forward so ended contracts appear active.
    /// Finds the latest real end date and applies a uniform offset to bring it to today + 1 year.
    /// Sentinel dates (EndDateNull = 1900-01-01) are left untouched.
    /// </summary>
    public static void ActivateContracts(XellentSeedData data)
    {
        var endDateNull = MigrationConstants.EndDateNull;

        // Find the latest real contract end date
        var maxContractEnd = data.ExuContractTables
            .Where(c => c.Contractenddate != endDateNull)
            .Select(c => c.Contractenddate)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        var maxPartEnd = data.ExuContractPartTables
            .Where(p => p.Enddate != endDateNull)
            .Select(p => p.Enddate)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        var maxEnd = maxContractEnd > maxPartEnd ? maxContractEnd : maxPartEnd;

        if (maxEnd == DateTime.MinValue || maxEnd >= DateTime.Now.Date.AddYears(1))
            return; // Already active or no real end dates

        var offset = DateTime.Now.Date.AddYears(1) - maxEnd;

        foreach (var c in data.ExuContractTables)
        {
            if (c.Contractenddate != endDateNull)
                c.Contractenddate += offset;
        }

        foreach (var p in data.ExuContractPartTables)
        {
            p.Startdate += offset;
            if (p.Enddate != endDateNull)
                p.Enddate += offset;
        }
    }

    private static void NormalizeDeliveryCategory(XellentSeedData data)
        => MultiAccountXellentDbSeeder.NormalizeDeliveryCategory(data);

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
