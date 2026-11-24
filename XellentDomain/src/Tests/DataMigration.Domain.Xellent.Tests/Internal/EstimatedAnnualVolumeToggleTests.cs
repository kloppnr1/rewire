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
/// Holds the two sides of <c>IncludeEstimatedAnnualVolume</c> against each other on one data set:
/// left alone the reading table is never touched at all and the expected yearly consumption comes
/// out null, and asked for explicitly it is read and lands on the metering point — while everything
/// else on the metering point, settlement method and reading interval included, is the same either way.
/// </summary>
public class EstimatedAnnualVolumeToggleTests : IDisposable
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
    private readonly SqlCapture _sql = new();

    public EstimatedAnnualVolumeToggleTests()
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

        // The meter is what carries the reading interval — without it there is nothing to check.
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

        // Reading interval and settlement method: the two lookups that must stay unconditional.
        seed.ExuTimereadingsPeriods.Add(new ExuTimereadingsPeriod
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
        });

        seed.ExuBillingMethodPeriods.Add(new ExuBillingMethodPeriod
        {
            Recid = 1,
            Dataareaid = Area,
            Companyid = Company,
            Delpointnum = Delpoint,
            Attachmentnum = Agreement,
            Deliverycategory = Category,
            Startdate = new DateTime(2021, 1, 1),
            Enddate = Open,
            // Deliberately the value the meter-type-code fallback would NOT produce, so the test can
            // tell "the billing-method row was read" from "the fallback happened to agree".
            Billingmethod = 2,
        });

        // Two readings for the same delpoint — the later one is the one that counts.
        seed.ExuReadingTableExts.AddRange(
            new ExuReadingTableExt
            {
                Recid = 1,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Agreementnum = Agreement,
                Deliverycategory = Category,
                Readingdate = new DateTime(2024, 1, 1),
                Expectedconsumptionperyear = 1000m,
            },
            new ExuReadingTableExt
            {
                Recid = 2,
                Dataareaid = Area,
                Companyid = Company,
                Delpointnum = Delpoint,
                Agreementnum = Agreement,
                Deliverycategory = Category,
                Readingdate = new DateTime(2025, 6, 1),
                Expectedconsumptionperyear = 4200m,
            });

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ── Default: nobody pays for it unless they ask ───────────────────────────────────────────

    [Fact]
    public void The_option_is_off_unless_someone_asks_for_it()
    {
        var options = new MapperOptions(new DateOnly(2026, 1, 1), Commodity.Electricity);

        Assert.False(options.IncludeEstimatedAnnualVolume);
    }

    [Fact]
    public async Task By_default_the_reading_table_is_never_queried()
    {
        _sql.Clear();
        await MapSingle();

        Assert.DoesNotContain(_sql.Statements, s => s.Contains("EXU_READINGTABLEEXT"));

        // …while the two lookups that may not be skipped are still issued.
        Assert.Contains(_sql.Statements, s => s.Contains("EXU_BILLINGMETHODPERIOD"));
        Assert.Contains(_sql.Statements, s => s.Contains("EXU_TIMEREADINGSPERIOD"));
    }

    [Fact]
    public async Task By_default_the_expected_yearly_consumption_comes_out_null()
    {
        var mp = await MapSingle();

        Assert.Null(mp.EstimatedAnnualVolumeKwh);
    }

    // ── Asked for explicitly ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Asked_for_the_latest_expected_yearly_consumption_lands_on_the_metering_point()
    {
        var mp = await MapSingle(includeEstimatedAnnualVolume: true);

        Assert.Equal(4200d, mp.EstimatedAnnualVolumeKwh);
    }

    [Fact]
    public async Task Asked_for_the_reading_table_is_queried()
    {
        _sql.Clear();
        await MapSingle(includeEstimatedAnnualVolume: true);

        Assert.Contains(_sql.Statements, s => s.Contains("EXU_READINGTABLEEXT"));
    }

    // ── The volume is the only difference between the two ─────────────────────────────────────

    [Fact]
    public async Task The_volume_is_all_that_the_flag_changes()
    {
        var included = await MapSingle(includeEstimatedAnnualVolume: true);
        var byDefault = await MapSingle();

        Assert.Null(byDefault.EstimatedAnnualVolumeKwh);

        // The expected volume is the only difference — everything else is field for field the same.
        Assert.Equal(included with { EstimatedAnnualVolumeKwh = null }, byDefault);
    }

    [Fact]
    public async Task By_default_settlement_method_and_reading_interval_are_unchanged()
    {
        var included = await MapSingle(includeEstimatedAnnualVolume: true);
        var skipped = await MapSingle();

        // Billingmethod 2 comes off the billing-method row, not off the meter type code fallback.
        Assert.Equal(included.Grid!.SettlementMethod, skipped.Grid!.SettlementMethod);
        Assert.Equal(
            ExtractionMappings.MapBillingMethod(2),
            skipped.Grid!.SettlementMethod);
        Assert.NotEqual(
            ExtractionMappings.MapSettlementMethodKind("E17/Forb"),
            skipped.Grid!.SettlementMethod);

        // Seriesresolution 1 → quarter-hourly.
        Assert.Equal(TimeSpan.FromMinutes(15), skipped.Meter?.ReadingInterval);
        Assert.Equal(included.Meter?.ReadingInterval, skipped.Meter?.ReadingInterval);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No argument means the property is not set at all — that is the point: the default-path tests
    /// must exercise what a caller who never heard of the flag gets, not a hand-written "false".
    /// </summary>
    private async Task<MeteringPoint> MapSingle(bool? includeEstimatedAnnualVolume = null)
    {
        using var context = NewContext();
        var reference = new DateOnly(2026, 1, 1);
        var options = includeEstimatedAnnualVolume is { } include
            ? new MapperOptions(reference, Commodity.Electricity) { IncludeEstimatedAnnualVolume = include }
            : new MapperOptions(reference, Commodity.Electricity);
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
