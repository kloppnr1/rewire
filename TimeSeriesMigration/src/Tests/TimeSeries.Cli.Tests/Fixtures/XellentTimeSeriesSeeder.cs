using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TimeSeries.Cli.Tests.Fixtures;

/// <summary>
/// SQLite in-memory Xellent fixture seeded with EMS_TIMESERIES + EMS_TIMESERIESVALUES.
/// Sufficient for tests that hit RunPostAsync via MeteringPointId-mode (skips contract/agreement joins).
/// </summary>
public sealed class XellentTimeSeriesSeeder : IDisposable
{
    private const string DataAreaId = "hol";
    private const string DefaultDeliveryCategory = "El-ekstern";
    private static readonly DateTime VersionToExcl = new(2154, 12, 31, 23, 59, 59, 0);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<XellentDbContext> _options;
    private long _nextRecid = 5_000_000_001L;

    public XellentTimeSeriesSeeder()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public XellentDbContext CreateContext() => new(_options);

    private const string DefaultCompanyId = "tst";
    private const int DeliveryCeased = 5; // EMS_AgreementStatusebIX "Leverance ophørt"
    private static readonly DateTime DefaultAgreementStart = new(2000, 1, 1);

    /// <summary>
    /// Seed an hourly time series with measured points. The series belongs to <paramref name="companyId"/>,
    /// whose agreement on the metering point runs from <paramref name="agreementStart"/> (default 2000-01-01)
    /// to <paramref name="agreementEnd"/> (inclusive Danish date; default 1900-01-01 = no end).
    /// </summary>
    public void SeedHourlyTimeSeries(
        string meteringPoint,
        DateTime startUtc,
        int hours,
        decimal valuePerHour = 1.0m,
        string deliveryCategory = DefaultDeliveryCategory,
        string companyId = DefaultCompanyId,
        DateTime? agreementStart = null,
        DateTime? agreementEnd = null)
        => SeedTimeSeries(meteringPoint, startUtc, hours,
            DataMigration.Domain.TimeSeries.TimeResolution.Hour, TimeSpan.FromHours(1),
            valuePerHour, deliveryCategory, companyId, agreementStart, agreementEnd);

    /// <summary>Seed a quarter-hour time series with measured points (count = number of 15-min points).</summary>
    public void SeedQuarterTimeSeries(
        string meteringPoint,
        DateTime startUtc,
        int quarters,
        decimal valuePerQuarter = 0.25m,
        string deliveryCategory = DefaultDeliveryCategory,
        string companyId = DefaultCompanyId,
        DateTime? agreementStart = null,
        DateTime? agreementEnd = null)
        => SeedTimeSeries(meteringPoint, startUtc, quarters,
            DataMigration.Domain.TimeSeries.TimeResolution.Quarter, TimeSpan.FromMinutes(15),
            valuePerQuarter, deliveryCategory, companyId, agreementStart, agreementEnd);

    /// <summary>Seed a series with no delivery point/agreement for its company.</summary>
    public void SeedHourlyTimeSeriesWithoutAgreement(string meteringPoint, DateTime startUtc, int hours, string companyId)
        => SeedTimeSeries(meteringPoint, startUtc, hours,
            DataMigration.Domain.TimeSeries.TimeResolution.Hour, TimeSpan.FromHours(1),
            1.0m, DefaultDeliveryCategory, companyId, null, null, seedAgreement: false);

    private void SeedTimeSeries(
        string meteringPoint,
        DateTime startUtc,
        int pointCount,
        DataMigration.Domain.TimeSeries.TimeResolution resolution,
        TimeSpan step,
        decimal value,
        string deliveryCategory,
        string companyId,
        DateTime? agreementStart,
        DateTime? agreementEnd,
        bool seedAgreement = true)
    {
        using var ctx = CreateContext();

        if (seedAgreement)
            SeedAgreement(ctx, meteringPoint, deliveryCategory, companyId,
                agreementStart ?? DefaultAgreementStart, agreementEnd ?? new DateTime(1900, 1, 1));

        var ts = new EmsTimeseries
        {
            Recid = _nextRecid++,
            Dataareaid = DataAreaId,
            Meteringpoint = meteringPoint,
            Deliverycategory = deliveryCategory,
            Companyid = companyId,
            Quantitycodevalue = "Active energy",
            Timeresolution = (int)resolution,
            Name = string.Empty,
        };
        ctx.EmsTimeseries.Add(ts);
        ctx.SaveChanges();

        for (var i = 0; i < pointCount; i++)
        {
            ctx.EmsTimeseriesValues.Add(new EmsTimeseriesValues
            {
                Recid = _nextRecid++,
                Dataareaid = DataAreaId,
                Timeseriesrefrecid = ts.Recid,
                Versiontoexcl = VersionToExcl,
                Timeofvalue = startUtc + step * i,
                Value = value,
                Qualityofvalue = (int)DataMigration.Domain.TimeSeries.QualityOfValue.MachineApprovedReading,
            });
        }
        ctx.SaveChanges();
    }

    /// <summary>
    /// Seed an EXU_TIMEREADINGSPERIOD row on the (metering point, company) delivery point: read at
    /// <paramref name="seriesResolution"/> (0 = hourly, 1 = quarter-hourly) from <paramref name="start"/>
    /// to <paramref name="end"/> (inclusive Danish date; 1900-01-01 = no end).
    /// </summary>
    public void SeedReadingPeriod(string meteringPoint, string companyId, DateTime start, DateTime end, int seriesResolution,
        string deliveryCategory = DefaultDeliveryCategory)
    {
        using var ctx = CreateContext();
        var dp = ctx.ExuDelpoints.Single(d => d.Meteringpoint == meteringPoint && d.Companyid == companyId);
        ctx.ExuTimereadingsPeriods.Add(new ExuTimereadingsPeriod
        {
            Dataareaid = DataAreaId,
            Deliverycategory = deliveryCategory,
            Companyid = companyId,
            Attachmentnum = dp.Attachmentnum,
            Delpointnum = dp.Num,
            Startdate = start,
            Enddate = end,
            Seriesresolution = seriesResolution,
            Recid = _nextRecid++,
        });
        ctx.SaveChanges();
    }

    /// <summary>
    /// Seed an EXU_METERPOINTSTATUSPERIOD row on the (metering point, company) delivery point: the
    /// metering point has held <paramref name="status"/> since <paramref name="start"/> (5 = "Oprettet",
    /// no meter at the address yet; 6 = a meter that delivers).
    /// </summary>
    public void SeedMeterPointStatus(string meteringPoint, string companyId, int status, DateTime start,
        string deliveryCategory = DefaultDeliveryCategory)
    {
        using var ctx = CreateContext();
        var dp = ctx.ExuDelpoints.Single(d => d.Meteringpoint == meteringPoint && d.Companyid == companyId);
        ctx.ExuMeterPointStatusPeriods.Add(new ExuMeterPointStatusPeriod
        {
            Dataareaid = DataAreaId,
            Deliverycategory = deliveryCategory,
            Companyid = companyId,
            Attachmentnum = dp.Attachmentnum,
            Delpointnum = dp.Num,
            Startdate = start,
            Enddate = new DateTime(1900, 1, 1),
            Meterpointstatus = status,
            Recid = _nextRecid++,
        });
        ctx.SaveChanges();
    }

    /// <summary>
    /// Seed a contract for <paramref name="account"/> whose contract part points at the metering
    /// point's delivery point for <paramref name="companyId"/> (created with an open agreement if the
    /// series seeding has not already made one). The part runs <paramref name="partStart"/> (default
    /// 2000-01-01) to <paramref name="partEnd"/> (inclusive Danish date; default 1900-01-01 = no end).
    /// </summary>
    /// <param name="migratedOn">
    /// When set, the relationship was migrated with delivery starting on this date: the contract, and the
    /// agreement behind it, end the day before (and the agreement has status "Leverance ophørt").
    /// Overrides <paramref name="partEnd"/>.
    /// </param>
    public void SeedContract(
        string account, string meteringPoint, string companyId = DefaultCompanyId,
        DateTime? partStart = null, DateTime? partEnd = null, string deliveryCategory = DefaultDeliveryCategory,
        DateTime? migratedOn = null, int? agreementStatus = null, DateTime? agreementEnd = null)
    {
        using var ctx = CreateContext();
        SeedAgreement(ctx, meteringPoint, deliveryCategory, companyId, DefaultAgreementStart, new DateTime(1900, 1, 1));
        var dp = ctx.ExuDelpoints.Single(d => d.Meteringpoint == meteringPoint && d.Companyid == companyId && d.Deliverycategory == deliveryCategory);
        var contractNum = $"C{_nextRecid++}";
        var start = partStart ?? DefaultAgreementStart;
        var end = migratedOn is { } cos ? cos.Date.AddDays(-1) : partEnd ?? new DateTime(1900, 1, 1);
        if (migratedOn is not null || agreementStatus is not null || agreementEnd is not null)
        {
            var agreement = ctx.ExuAgreementTables.Single(a => a.Agreementnum == dp.Attachmentnum && a.Companyid == companyId);
            if (migratedOn is not null)
                agreement.Enddate = end;
            if (agreementEnd is { } ae)
                agreement.Enddate = ae;
            agreement.EmsAgreementstatusebix = agreementStatus ?? DeliveryCeased;
        }
        ctx.ExuContractTables.Add(new ExuContractTable
        {
            Contractnum = contractNum,
            Dataareaid = DataAreaId,
            Custaccount = account,
            Name = string.Empty,
            Companyid = companyId,
            Deliverycategory = deliveryCategory,
            Mobilepaysubscriptionagre20135 = string.Empty,
            Einvoiceeannum = string.Empty,
            Custpaymcode = string.Empty,
            Billingplannum = string.Empty,
            Contracttemplatenum = string.Empty,
            Contractstartdate = start,
            Contractenddate = end,
        });
        ctx.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Contractnum = contractNum,
            Dataareaid = DataAreaId,
            Instagreenum = dp.Attachmentnum,
            Deliverycategory = deliveryCategory,
            Companyid = companyId,
            Productnum = string.Empty,
            Amrcode = string.Empty,
            Contractpartsubreferenceid = string.Empty,
            Startdate = start,
            Enddate = end,
        });
        ctx.SaveChanges();
    }

    /// <summary>Make <paramref name="childMeteringPoint"/> a child of <paramref name="parentMeteringPoint"/> (both already seeded for the company).</summary>
    public void SeedChildRelation(string parentMeteringPoint, string childMeteringPoint, string companyId = DefaultCompanyId)
    {
        using var ctx = CreateContext();
        var parent = ctx.ExuDelpoints.Single(d => d.Meteringpoint == parentMeteringPoint && d.Companyid == companyId);
        var child = ctx.ExuDelpoints.Single(d => d.Meteringpoint == childMeteringPoint && d.Companyid == companyId);
        ctx.ExuDelpoint2delpointrela25505s.Add(new ExuDelpoint2delpointrela25505
        {
            Dataareaid = DataAreaId,
            Recid = _nextRecid++,
            Deliverycategory = parent.Deliverycategory,
            Companyid = companyId,
            Primaryattachmentnum = parent.Attachmentnum,
            Primarydelpointnum = parent.Num,
            Secondaryattachmentnum = child.Attachmentnum,
            Secondarydelpointnum = child.Num,
            Startdate = DefaultAgreementStart,
            Enddate = new DateTime(1900, 1, 1),
        });
        ctx.SaveChanges();
    }

    // One delivery point + agreement per (metering point, company, category); a second series for the
    // same company reuses it.
    private void SeedAgreement(
        XellentDbContext ctx, string meteringPoint, string deliveryCategory, string companyId,
        DateTime start, DateTime end)
    {
        if (ctx.ExuDelpoints.Any(d => d.Meteringpoint == meteringPoint && d.Companyid == companyId && d.Deliverycategory == deliveryCategory))
            return;

        var agreementNum = $"A{_nextRecid++}";
        ctx.ExuAgreementTables.Add(new ExuAgreementTable
        {
            Agreementnum = agreementNum,
            Dataareaid = DataAreaId,
            Companyid = companyId,
            Deliverycategory = deliveryCategory,
            Startdate = start,
            Enddate = end,
            EmsAgreementstatusebix = DeliveryCeased,
            Recid = _nextRecid++,
        });
        ctx.ExuDelpoints.Add(new ExuDelpoint
        {
            Num = "1",
            Dataareaid = DataAreaId,
            Deliverycategory = deliveryCategory,
            Attachmentnum = agreementNum,
            Companyid = companyId,
            Meteringpoint = meteringPoint,
            Extnum = string.Empty,
            Name = string.Empty,
            Custaccount = string.Empty,
            Streetnum = string.Empty,
            Countynum = string.Empty,
            Zipcodeid = string.Empty,
            Addressstreet = string.Empty,
            Gsrn = string.Empty,
            Houseletterstart = string.Empty,
            Apartment = string.Empty,
            Floor = string.Empty,
            Addrlocation = string.Empty,
            Addressid = string.Empty,
            EmsNetarea = string.Empty,
            Powerexchangearea = string.Empty,
            Recid = _nextRecid++,
        });
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
