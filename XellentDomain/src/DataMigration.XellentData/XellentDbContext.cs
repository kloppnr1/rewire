using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData;

public class XellentDbContext : DbContext
{
    public XellentDbContext(DbContextOptions<XellentDbContext> options)
        : base(options)
    {
    }

    public DbSet<CustTable> CustTables { get; set; } = null!;
    public DbSet<ExuContractTable> ExuContractTables { get; set; } = null!;
    public DbSet<ExuContractPartTable> ExuContractPartTables { get; set; } = null!;
    public DbSet<ExuAgreementTable> ExuAgreementTables { get; set; } = null!;
    public DbSet<ExuDelpoint> ExuDelpoints { get; set; } = null!;
    public DbSet<Address> Addresses { get; set; } = null!;
    public DbSet<ExuStreet> ExuStreets { get; set; } = null!;
    public DbSet<AddressZipCode> AddressZipCodes { get; set; } = null!;
    public DbSet<EgsMeterpointTypecodeperiod> EgsMeterpointTypecodeperiods { get; set; } = null!;
    public DbSet<ExuBillingMethodPeriod> ExuBillingMethodPeriods { get; set; } = null!;
    public DbSet<ExuDelpointContactInfoTable> ExuDelpointContactInfoTables { get; set; } = null!;
    public DbSet<ExuDelpoint2delpointrela25505> ExuDelpoint2delpointrela25505s { get; set; } = null!;
    public DbSet<DirPartyAddressRelationship> DirPartyAddressRelationships { get; set; } = null!;
    public DbSet<DirPartyAddressRelationshi1066> DirPartyAddressRelationshi1066s { get; set; } = null!;
    public DbSet<CustInvoiceJour> CustInvoiceJours { get; set; } = null!;
    public DbSet<DocuRef> DocuRefs { get; set; } = null!;
    public DbSet<ExuDelpointPriceElementR25046> ExuDelpointPriceElements { get; set; } = null!;
    public DbSet<ExuGroupTable> ExuGroupTables { get; set; } = null!;
    public DbSet<ExuMeterTableTrade> ExuMeterTableTrades { get; set; } = null!;
    public DbSet<ExuInstallation> ExuInstallations { get; set; } = null!;
    public DbSet<ExuUtilityInstallation> ExuUtilityInstallations { get; set; } = null!;
    public DbSet<ExuMeterJournalTrans> ExuMeterJournalTrans { get; set; } = null!;
    public DbSet<DirECommunicationAddress> DirECommunicationAddresses { get; set; } = null!;
    public DbSet<DirPartyECommunicationRela2608> DirPartyECommunicationRela2608s { get; set; } = null!;
    public DbSet<ExuReadingTableExt> ExuReadingTableExts { get; set; } = null!;
    public DbSet<ExuTimereadingsPeriod> ExuTimereadingsPeriods { get; set; } = null!;
    public DbSet<ExuPriceElementTable> ExuPriceElementTables { get; set; } = null!;
    public DbSet<ExuPriceElementRates> ExuPriceElementRates { get; set; } = null!;
    public DbSet<ExuPriceElementCheck> ExuPriceElementChecks { get; set; } = null!;
    public DbSet<ExuPriceElementCheckData> ExuPriceElementCheckData { get; set; } = null!;
    public DbSet<ExuRateTable> ExuRateTables { get; set; } = null!;
    public DbSet<ExuProductExtendTable> ExuProductExtendTables { get; set; } = null!;
    public DbSet<ExuFlexBhistoryTable> ExuFlexBhistoryTables { get; set; } = null!;
    public DbSet<ExuFlexBhistoryLine> ExuFlexBhistoryLines { get; set; } = null!;
    public DbSet<InventTable> InventTables { get; set; } = null!;
    public DbSet<ContactPerson> ContactPersons { get; set; } = null!;
    public DbSet<EmsTimeseries> EmsTimeseries { get; set; } = null!;
    public DbSet<EmsTimeseriesValues> EmsTimeseriesValues { get; set; } = null!;
    public DbSet<ExuContractPartLog> ExuContractPartLogs { get; set; } = null!;
    public DbSet<ExuMeterPointStatusPeriod> ExuMeterPointStatusPeriods { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CustTable>()
            .HasKey(c => new { c.Dataareaid, c.Accountnum });

        modelBuilder.Entity<ExuContractTable>()
            .HasKey(c => new { c.Dataareaid, c.Contractnum });

        modelBuilder.Entity<ExuContractPartTable>()
            .HasKey(c => new { c.Dataareaid, c.Contractnum, c.Instagreenum, c.Deliverycategory, c.Startdate, c.Companyid, c.Productnum, c.Amrcode, c.Contractpartsubreferencetype, c.Contractpartsubreferenceid });

        modelBuilder.Entity<ExuAgreementTable>()
            .HasKey(a => new { a.Dataareaid, a.Agreementnum, a.Companyid });

        modelBuilder.Entity<ExuDelpoint>()
            .HasKey(d => new { d.Dataareaid, d.Deliverycategory, d.Companyid, d.Attachmentnum, d.Num });

        modelBuilder.Entity<Address>()
            .HasKey(a => new { a.Dataareaid, a.Recid });

        modelBuilder.Entity<ExuStreet>()
            .HasKey(s => new { s.Dataareaid, s.Streetnum, s.Countynum });

        modelBuilder.Entity<AddressZipCode>()
            .HasKey(a => new { a.Dataareaid, a.Countryregionid, a.Zipcode, a.County });

        modelBuilder.Entity<EgsMeterpointTypecodeperiod>()
            .HasKey(m => new { m.Dataareaid, m.Deliverycategory, m.Companyid, m.Attachmentnum, m.Delpointnum, m.Startdate });

        modelBuilder.Entity<ExuBillingMethodPeriod>()
            .HasKey(b => new { b.Dataareaid, b.Deliverycategory, b.Companyid, b.Attachmentnum, b.Delpointnum, b.Startdate });

        modelBuilder.Entity<ExuMeterPointStatusPeriod>()
            .HasKey(m => new { m.Dataareaid, m.Deliverycategory, m.Companyid, m.Attachmentnum, m.Delpointnum, m.Startdate });

        modelBuilder.Entity<ExuDelpointContactInfoTable>()
            .HasKey(c => new { c.Dataareaid, c.Companyid, c.Deliverycategory, c.Meteringpoint, c.Contacttype, c.Startdate });

        modelBuilder.Entity<ExuDelpoint2delpointrela25505>()
            .HasKey(r => new { r.Dataareaid, r.Recid });

        modelBuilder.Entity<ExuDelpointPriceElementR25046>()
            .HasKey(p => new { p.Dataareaid, p.Deliverycategory, p.Companyid, p.Attachmentnum, p.Delpointnum, p.Partychargetypeid });

        modelBuilder.Entity<ExuMeterTableTrade>()
            .HasKey(m => new { m.Dataareaid, m.Deliverycategory, m.Companyid, m.Agreementnum, m.Delpointnum, m.Startdate });

        modelBuilder.Entity<ExuInstallation>()
            .HasKey(i => new { i.Dataareaid, i.Instnum });

        modelBuilder.Entity<ExuUtilityInstallation>()
            .HasKey(u => new { u.Instnum, u.Dataareaid, u.Deliverycategory });

        modelBuilder.Entity<DirECommunicationAddress>()
            .HasKey(a => new { a.Dataareaid, a.Recid });

        modelBuilder.Entity<DirPartyECommunicationRela2608>()
            .HasKey(r => new { r.Dataareaid, r.Recid });

        modelBuilder.Entity<ExuReadingTableExt>()
            .HasKey(r => r.Recid);

        modelBuilder.Entity<ExuTimereadingsPeriod>()
            .HasKey(t => new { t.Dataareaid, t.Deliverycategory, t.Companyid, t.Attachmentnum, t.Delpointnum, t.Startdate });

        modelBuilder.Entity<ExuContractPartLog>()
            .HasKey(l => l.Recid);
    }
}
