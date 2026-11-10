using System.Data.Common;
using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The three delpoint-keyed lookups — reading interval, expected yearly consumption and billing
/// method — must ask on the whole key the tables are clustered on: data area, delivery category,
/// attachment number, company and delpoint number. The delpoint number alone is not selective in
/// Xellent (four distinct values across the whole table), so a query that only names it scans
/// millions of rows to hand back a handful.
///
/// <para>Two things are checked here, both without a database: that all five key columns end up in
/// the where-clause of each of the three queries, and that the answer is the same as it always was.
/// The seed carries a decoy row per table — same delpoint number, different attachment — which is
/// exactly the collision the loose filter used to drag home and then throw away in memory.</para>
/// </summary>
public class DelpointLookupKeyTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;
    private const string Account = "100";
    private const string Agreement = "A001";
    private const string OtherAgreement = "A999";
    private const string Delpoint = "1";
    private const string Mp = "571313100000000001";

    private static readonly DateTime Open = MigrationConstants.EndDateNull;

    private readonly SqliteConnection _connection;
    private readonly SqlCapture _sql = new();

    public DelpointLookupKeyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var seed = NewContext();
        seed.Database.EnsureCreated();

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
        });

        // Seriesresolution 1 → quarter-hourly. The decoy shares the delpoint number, starts later
        // and says hourly — it must never reach the answer.
        seed.ExuTimereadingsPeriods.AddRange(
            new ExuTimereadingsPeriod
            {
                Recid = 1,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Attachmentnum = Agreement,
                Deliverycategory = Category,
                Startdate = new DateTime(2021, 1, 1),
                Enddate = Open,
                Seriesresolution = 1,
            },
            new ExuTimereadingsPeriod
            {
                Recid = 2,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Attachmentnum = OtherAgreement,
                Deliverycategory = Category,
                Startdate = new DateTime(2025, 1, 1),
                Enddate = Open,
                Seriesresolution = 0,
            });

        seed.ExuBillingMethodPeriods.AddRange(
            new ExuBillingMethodPeriod
            {
                Recid = 1,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Attachmentnum = Agreement,
                Deliverycategory = Category,
                Startdate = new DateTime(2021, 1, 1),
                Enddate = Open,
                Billingmethod = 2,
            },
            new ExuBillingMethodPeriod
            {
                Recid = 2,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Attachmentnum = OtherAgreement,
                Deliverycategory = Category,
                Startdate = new DateTime(2025, 1, 1),
                Enddate = Open,
                Billingmethod = 1,
            });

        seed.ExuReadingTableExts.AddRange(
            new ExuReadingTableExt
            {
                Recid = 1,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Agreementnum = Agreement,
                Deliverycategory = Category,
                Readingdate = new DateTime(2025, 6, 1),
                Expectedconsumptionperyear = 4200m,
            },
            new ExuReadingTableExt
            {
                Recid = 2,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Agreementnum = OtherAgreement,
                Deliverycategory = Category,
                Readingdate = new DateTime(2025, 12, 1),
                Expectedconsumptionperyear = 9999m,
            });

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Theory]
    [InlineData("EXU_TIMEREADINGSPERIOD", "ATTACHMENTNUM")]
    [InlineData("EXU_BILLINGMETHODPERIOD", "ATTACHMENTNUM")]
    [InlineData("EXU_READINGTABLEEXT", "AGREEMENTNUM")]
    public async Task The_lookup_asks_on_the_whole_key(string table, string attachmentColumn)
    {
        _sql.Clear();
        await MapSingle();

        var where = WhereClauseFor(table);

        Assert.Contains("DATAAREAID", where);
        Assert.Contains("DELIVERYCATEGORY", where);
        Assert.Contains(attachmentColumn, where);
        Assert.Contains("COMPANYID", where);
        Assert.Contains("DELPOINTNUM", where);
    }

    /// <summary>
    /// The reading table has a column literally named ATTACHMENT as well. It is a different thing
    /// and matches nothing — the attachment we filter on is AGREEMENTNUM.
    /// </summary>
    [Fact]
    public async Task The_reading_table_is_keyed_on_the_agreement_number()
    {
        _sql.Clear();
        await MapSingle();

        var where = WhereClauseFor("EXU_READINGTABLEEXT");

        Assert.DoesNotContain("\"ATTACHMENT\"", where);
    }

    [Fact]
    public async Task The_row_belonging_to_another_attachment_stays_out_of_the_answer()
    {
        var mp = await MapSingle();

        // Quarter-hourly from the delpoint's own row, not hourly from the decoy that starts later.
        Assert.Equal(TimeSpan.FromMinutes(15), mp.Meter?.ReadingInterval);
        Assert.Equal(ExtractionMappings.MapBillingMethod(2), mp.Grid!.SettlementMethod);
        Assert.Equal(4200d, mp.EstimatedAnnualVolumeKwh);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private string WhereClauseFor(string table)
    {
        var statement = Assert.Single(_sql.Statements, s => s.Contains(table, StringComparison.OrdinalIgnoreCase));
        var where = statement.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
        Assert.True(where >= 0, $"No where-clause in the query against {table}: {statement}");
        return statement[where..].ToUpperInvariant();
    }

    private async Task<MeteringPoint> MapSingle()
    {
        using var context = NewContext();
        var options = new MapperOptions(new DateOnly(2026, 1, 1), Commodity.Electricity)
        {
            IncludeEstimatedAnnualVolume = true
        };
        var mapper = new MeteringPointMapper(context, new WarningCollector(), options, Area);

        var result = await mapper.MapAsync([Account]);
        return Assert.Single(result.MeteringPoints);
    }

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_sql)
            .Options);

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
