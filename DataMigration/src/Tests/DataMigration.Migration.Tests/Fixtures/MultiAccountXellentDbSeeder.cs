using DataMigration.Core;
using DataMigration.Migration.Runners;
using DataMigration.XellentData;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Migration.Tests.Fixtures;

/// <summary>
/// Seeds an in-memory SQLite XellentDbContext with data from multiple accounts.
/// Loads and merges xellent.json seed files for each account number provided.
/// </summary>
public sealed class MultiAccountXellentDbSeeder : IDisposable
{
    private readonly SqliteConnection _connection;
    public XellentDbContext Context { get; }

    public MultiAccountXellentDbSeeder(string[] accountNumbers, bool activateContracts = false)
    {
        var merged = Merge(accountNumbers.Select(XellentDbSeeder.LoadRawSeed));
        if (activateContracts) XellentDbSeeder.ActivateContracts(merged);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options;

        Context = new XellentDbContext(options);
        Context.Database.EnsureCreated();

        NormalizeDeliveryCategory(merged);

        if (merged.CustTables.Count > 0) Context.CustTables.AddRange(merged.CustTables);
        if (merged.ExuContractTables.Count > 0) Context.ExuContractTables.AddRange(merged.ExuContractTables);
        if (merged.ExuContractPartTables.Count > 0) Context.ExuContractPartTables.AddRange(merged.ExuContractPartTables);
        if (merged.DirPartyAddressRelationships.Count > 0) Context.DirPartyAddressRelationships.AddRange(merged.DirPartyAddressRelationships);
        if (merged.DirPartyAddressRelationshi1066s.Count > 0) Context.DirPartyAddressRelationshi1066s.AddRange(merged.DirPartyAddressRelationshi1066s);
        if (merged.Addresses.Count > 0) Context.Addresses.AddRange(merged.Addresses);
        if (merged.ExuAgreementTables.Count > 0) Context.ExuAgreementTables.AddRange(merged.ExuAgreementTables);
        if (merged.ExuDelpoints.Count > 0) Context.ExuDelpoints.AddRange(merged.ExuDelpoints);
        if (merged.ExuDelpointContactInfoTables.Count > 0) Context.ExuDelpointContactInfoTables.AddRange(merged.ExuDelpointContactInfoTables);
        if (merged.AddressZipCodes.Count > 0) Context.AddressZipCodes.AddRange(merged.AddressZipCodes);
        if (merged.DirPartyECommunicationRela2608s.Count > 0) Context.DirPartyECommunicationRela2608s.AddRange(merged.DirPartyECommunicationRela2608s);
        if (merged.DirECommunicationAddresses.Count > 0) Context.DirECommunicationAddresses.AddRange(merged.DirECommunicationAddresses);
        if (merged.EgsMeterpointTypecodeperiods.Count > 0) Context.EgsMeterpointTypecodeperiods.AddRange(merged.EgsMeterpointTypecodeperiods);
        if (merged.ExuBillingMethodPeriods.Count > 0) Context.ExuBillingMethodPeriods.AddRange(merged.ExuBillingMethodPeriods);
        if (merged.ExuDelpoint2delpointrela25505s.Count > 0) Context.ExuDelpoint2delpointrela25505s.AddRange(merged.ExuDelpoint2delpointrela25505s);
        if (merged.ExuStreets.Count > 0) Context.ExuStreets.AddRange(merged.ExuStreets);
        if (merged.ExuMeterTableTrades.Count > 0) Context.ExuMeterTableTrades.AddRange(merged.ExuMeterTableTrades);
        if (merged.ExuGroupTables.Count > 0) Context.ExuGroupTables.AddRange(merged.ExuGroupTables);
        if (merged.ExuInstallations.Count > 0) Context.ExuInstallations.AddRange(merged.ExuInstallations);
        if (merged.ExuMeterJournalTrans.Count > 0) Context.ExuMeterJournalTrans.AddRange(merged.ExuMeterJournalTrans);

        Context.SaveChanges();
    }

    public static XellentSeedData Merge(IEnumerable<XellentSeedData> items)
    {
        var merged = new XellentSeedData();
        foreach (var d in items)
        {
            merged.CustTables.AddRange(d.CustTables);
            merged.ExuContractTables.AddRange(d.ExuContractTables);
            merged.ExuContractPartTables.AddRange(d.ExuContractPartTables);
            merged.DirPartyAddressRelationships.AddRange(d.DirPartyAddressRelationships);
            merged.DirPartyAddressRelationshi1066s.AddRange(d.DirPartyAddressRelationshi1066s);
            merged.Addresses.AddRange(d.Addresses);
            merged.ExuAgreementTables.AddRange(d.ExuAgreementTables);
            merged.ExuDelpoints.AddRange(d.ExuDelpoints);
            merged.ExuDelpointContactInfoTables.AddRange(d.ExuDelpointContactInfoTables);
            merged.AddressZipCodes.AddRange(d.AddressZipCodes);
            merged.DirPartyECommunicationRela2608s.AddRange(d.DirPartyECommunicationRela2608s);
            merged.DirECommunicationAddresses.AddRange(d.DirECommunicationAddresses);
            merged.EgsMeterpointTypecodeperiods.AddRange(d.EgsMeterpointTypecodeperiods);
            merged.ExuBillingMethodPeriods.AddRange(d.ExuBillingMethodPeriods);
            merged.ExuDelpoint2delpointrela25505s.AddRange(d.ExuDelpoint2delpointrela25505s);
            merged.ExuStreets.AddRange(d.ExuStreets);
            merged.ExuMeterTableTrades.AddRange(d.ExuMeterTableTrades);
            merged.ExuGroupTables.AddRange(d.ExuGroupTables);
            merged.ExuInstallations.AddRange(d.ExuInstallations);
            merged.ExuMeterJournalTrans.AddRange(d.ExuMeterJournalTrans);
        }
        // Shared reference tables — deduplicate by PK to avoid tracking conflicts when merging accounts
        merged.DirPartyAddressRelationships = merged.DirPartyAddressRelationships.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.DirPartyAddressRelationshi1066s = merged.DirPartyAddressRelationshi1066s.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.Addresses = merged.Addresses.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.AddressZipCodes = merged.AddressZipCodes.DistinctBy(z => (z.Dataareaid, z.Countryregionid, z.Zipcode, z.County)).ToList();
        merged.DirPartyECommunicationRela2608s = merged.DirPartyECommunicationRela2608s.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.DirECommunicationAddresses = merged.DirECommunicationAddresses.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.ExuDelpoint2delpointrela25505s = merged.ExuDelpoint2delpointrela25505s.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.EgsMeterpointTypecodeperiods = merged.EgsMeterpointTypecodeperiods.DistinctBy(x => (x.Dataareaid, x.Deliverycategory, x.Companyid, x.Attachmentnum, x.Delpointnum, x.Startdate)).ToList();
        merged.ExuBillingMethodPeriods = merged.ExuBillingMethodPeriods.DistinctBy(x => (x.Dataareaid, x.Deliverycategory, x.Companyid, x.Attachmentnum, x.Delpointnum, x.Startdate)).ToList();
        merged.ExuStreets = merged.ExuStreets.DistinctBy(x => (x.Dataareaid, x.Streetnum, x.Countynum)).ToList();
        merged.ExuMeterTableTrades = merged.ExuMeterTableTrades.DistinctBy(x => (x.Dataareaid, x.Deliverycategory, x.Companyid, x.Agreementnum, x.Delpointnum, x.Startdate)).ToList();
        merged.ExuGroupTables = merged.ExuGroupTables.DistinctBy(x => (x.Dataareaid, x.Groupnum)).ToList();
        merged.ExuInstallations = merged.ExuInstallations.DistinctBy(x => (x.Dataareaid, x.Recid)).ToList();
        merged.ExuMeterJournalTrans = merged.ExuMeterJournalTrans.DistinctBy(x => (x.Dataareaid, x.Journalid, x.Linenum)).ToList();

        return merged;
    }

    /// <summary>
    /// Normalises seed delivery categories to the canonical PascalCase form from
    /// <see cref="MigrationConstants"/> (e.g. "El-Ekstern" → "El-ekstern", "vand" → "Vand").
    /// Real Xellent data has mixed casing; SQL Server's CI collation hides this in production,
    /// but SQLite is case-sensitive. Categories outside MigrationConstants pass through unchanged.
    /// </summary>
    public static void NormalizeDeliveryCategory(XellentSeedData data)
    {
        foreach (var r in data.ExuContractTables) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuContractPartTables) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuAgreementTables) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuDelpoints) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuDelpointContactInfoTables) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.EgsMeterpointTypecodeperiods) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuBillingMethodPeriods) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuDelpoint2delpointrela25505s) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuMeterTableTrades) r.Deliverycategory = Canonical(r.Deliverycategory);
        foreach (var r in data.ExuMeterJournalTrans) r.Deliverycategory = Canonical(r.Deliverycategory);
    }

    private static readonly string[] CanonicalCategories =
    [
        MigrationConstants.DeliveryCategoryElectricity,
        MigrationConstants.DeliveryCategoryWater,
        MigrationConstants.DeliveryCategoryHeating,
        MigrationConstants.DeliveryCategoryCharge,
    ];

    private static string Canonical(string value) =>
        Array.Find(CanonicalCategories, c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) ?? value;

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
