using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent.Tests.Internal;

/// <summary>
/// The header half of the restore: the batch end-dated the contract headers too, and those cannot be
/// restored — <c>EXU_CONTRACTTABLE</c> has no audit log. Affected contracts are instead let back in by
/// name, on the evidence of the part log.
///
/// <para>Five contracts, all end-dated, all with an open contract part so nothing but the header
/// decides, and a reference date three months after the batch — the shape a real post-incident run
/// has, and the one where the demo's "the header just squeaks past &gt;=" no longer saves anything:</para>
/// <list type="bullet">
///   <item>C001, C005 — batch's end date, blank-user log row in the window. Exempt.</item>
///   <item>C002 — batch's end date, but no log row at all. Not the batch's doing, stays filtered.</item>
///   <item>C003 — batch's end date, but the only log row is a named user's. Stays filtered.</item>
///   <item>C004 — log row in the window, but the header was ended weeks earlier on another date.
///         A genuine termination the batch happened to touch. Stays filtered.</item>
/// </list>
/// </summary>
public class ContractHeaderExemptionTests : IDisposable
{
    private const string Area = "hol";
    private const string Company = "01";
    private const string Category = MigrationConstants.DeliveryCategoryElectricity;
    private const string Account = "100";

    private static readonly DateTime Open = MigrationConstants.EndDateNull;

    /// <summary>Well after the batch — a run done in the weeks following the incident.</summary>
    private static readonly DateOnly Reference = new(2026, 12, 1);

    private static readonly DateTime WindowFrom = new(2026, 8, 31, 9, 13, 49);
    private static readonly DateTime WindowBefore = new(2026, 8, 31, 9, 59, 33);

    /// <summary>What the batch stamped on the headers it ended.</summary>
    private static readonly DateTime BatchStamp = new(2026, 8, 31);

    /// <summary>A genuine termination, weeks before the batch.</summary>
    private static readonly DateTime EarlierTermination = new(2026, 7, 15);

    private readonly SqliteConnection _connection;
    private long _recid = 100;

    public ContractHeaderExemptionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Pooling=False");
        _connection.Open();

        using var seed = NewContext();
        seed.Database.EnsureCreated();

        AddContract(seed, "C001", BatchStamp);
        AddContract(seed, "C002", BatchStamp);
        AddContract(seed, "C003", BatchStamp);
        AddContract(seed, "C004", EarlierTermination);
        AddContract(seed, "C005", BatchStamp);

        // C001, C004 and C005 were touched by the batch; C003 only by a person; C002 not at all.
        AddLog(seed, "C001", logEmplId: "", loggedAt: new DateTime(2026, 8, 31, 9, 20, 0));
        AddLog(seed, "C005", logEmplId: "", loggedAt: new DateTime(2026, 8, 31, 9, 21, 0));
        AddLog(seed, "C004", logEmplId: "", loggedAt: new DateTime(2026, 8, 31, 9, 22, 0));
        AddLog(seed, "C003", logEmplId: "maiw", loggedAt: new DateTime(2026, 8, 31, 9, 23, 0));

        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ── Working out what the batch stamped ────────────────────────────────────────────────────

    [Fact]
    public async Task With_no_window_there_is_no_exemption_to_derive()
    {
        using var context = NewContext();

        var exemption = await ContractHeaderExemption.DeriveAsync(
            context, new WarningCollector(), Options(restore: false), Area);

        Assert.Null(exemption);
    }

    [Fact]
    public async Task The_batch_end_date_is_read_off_the_data_not_hardcoded()
    {
        using var context = NewContext();

        var exemption = await ContractHeaderExemption.DeriveAsync(
            context, new WarningCollector(), Options(restore: true), Area);

        // Two contracts carry 2026-08-31 and one carries the earlier genuine termination, so the
        // stamp is the former — worked out from the rows, with no date in the code.
        Assert.NotNull(exemption);
        Assert.Equal(BatchStamp, exemption!.BatchEndDate);
    }

    [Fact]
    public async Task The_choice_of_end_date_is_reported()
    {
        using var context = NewContext();
        var warnings = new WarningCollector();

        await ContractHeaderExemption.DeriveAsync(context, warnings, Options(restore: true), Area);

        Assert.Contains(warnings.GetInfos(), i => i.Contains("2026-08-31") && i.Contains("2026-07-15"));
    }

    [Fact]
    public async Task The_window_and_the_options_way_in_derive_the_same_set()
    {
        using var context = NewContext();
        var fromOptions = new WarningCollector();
        var fromWindow = new WarningCollector();

        var viaOptions = await ContractHeaderExemption.DeriveAsync(
            context, fromOptions, Options(restore: true), Area);
        var viaWindow = await ContractHeaderExemption.DeriveAsync(
            context, fromWindow, WindowFrom, WindowBefore, Area);

        // Same rule, one implementation: same stamp, same contracts, same account grouping, and the
        // same info line — so a caller that has no MapperOptions cannot drift from the extraction.
        Assert.NotNull(viaOptions);
        Assert.NotNull(viaWindow);
        Assert.Equal(viaOptions!.BatchEndDate, viaWindow!.BatchEndDate);
        Assert.Equal(
            viaOptions.ContractNumbers.OrderBy(x => x, StringComparer.Ordinal),
            viaWindow.ContractNumbers.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(
            viaOptions.ContractNumbersByAccount[Account].OrderBy(x => x, StringComparer.Ordinal),
            viaWindow.ContractNumbersByAccount[Account].OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(fromOptions.GetInfos(), fromWindow.GetInfos());
    }

    // ── Who gets back in ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Left_off_every_end_dated_contract_stays_out()
    {
        var contracts = await MapContracts(restore: false);

        Assert.Empty(contracts);
    }

    [Fact]
    public async Task Only_the_contracts_the_log_names_come_back()
    {
        var contracts = await MapContracts(restore: true);

        Assert.Equal(
            new[] { "C001", "C005" },
            contracts.Select(c => c.Number).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_contract_with_no_log_row_is_filtered_exactly_as_before()
    {
        var contracts = await MapContracts(restore: true);

        // C002 carries the same end date as the exempted ones. The date alone is not evidence.
        Assert.DoesNotContain(contracts, c => c.Number == "C002");
    }

    [Fact]
    public async Task A_contract_only_a_person_touched_is_filtered()
    {
        var contracts = await MapContracts(restore: true);

        Assert.DoesNotContain(contracts, c => c.Number == "C003");
    }

    [Fact]
    public async Task A_genuine_termination_the_batch_touched_is_still_a_termination()
    {
        var contracts = await MapContracts(restore: true);

        // C004 has a log row in the window, but its header was ended on another date by someone
        // else. Exempting it would create a customer in Settl for a contract that really is over.
        Assert.DoesNotContain(contracts, c => c.Number == "C004");
    }

    // ── The other two consumers of the header rule ────────────────────────────────────────────

    [Fact]
    public async Task The_metering_points_of_an_exempted_contract_come_back_too()
    {
        using var context = NewContext();
        var exemption = await Exemption(context);
        var mapper = new MeteringPointMapper(context, new WarningCollector(), Options(restore: true), Area);

        var result = await mapper.MapAsync([Account], ContractPartRestore.Empty, exemption);

        Assert.Equal(
            new[] { MeteringPointFor("C001"), MeteringPointFor("C005") },
            result.MeteringPoints.Select(mp => mp.Identification).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task The_billing_exemption_template_survives_for_an_exempted_contract()
    {
        using var context = NewContext();
        var exemption = await Exemption(context);
        var mapper = new XellentDomainMapper(context, new WarningCollector(), Options(restore: true), Area);

        var templates = await mapper.BuildMpActiveContractTemplatesAsync(
            [Account], exemption, CancellationToken.None);

        // Without the exemption here the template is missing, and the create-flow silently stops
        // marking the metering point as billing-fee exempt.
        Assert.Equal(new[] { TemplateFor("C001") }, templates[MeteringPointFor("C001")]);
        Assert.DoesNotContain(MeteringPointFor("C002"), templates.Keys);
    }

    [Fact]
    public async Task Left_off_no_template_is_returned_either()
    {
        using var context = NewContext();
        var mapper = new XellentDomainMapper(context, new WarningCollector(), Options(restore: false), Area);

        var templates = await mapper.BuildMpActiveContractTemplatesAsync(
            [Account], headerExemption: null, CancellationToken.None);

        Assert.Empty(templates);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Contract>> MapContracts(bool restore)
    {
        using var context = NewContext();
        var exemption = restore ? await Exemption(context) : null;
        var mapper = new ContractMapper(context, new WarningCollector(), Options(restore), Area);

        return await mapper.MapAsync(
            [Account],
            new Dictionary<string, string>(StringComparer.Ordinal) { [Account] = Account },
            ContractPartRestore.Empty,
            exemption);
    }

    private static Task<ContractHeaderExemption?> Exemption(XellentDbContext context) =>
        ContractHeaderExemption.DeriveAsync(context, new WarningCollector(), Options(restore: true), Area);

    private static MapperOptions Options(bool restore) => restore
        ? new MapperOptions(Reference, Commodity.Electricity,
            RestorePartsChangedFrom: WindowFrom, RestorePartsChangedBefore: WindowBefore)
        : new MapperOptions(Reference, Commodity.Electricity);

    private static string MeteringPointFor(string contract) => $"5713131000000{contract[1..]}";

    private static string TemplateFor(string contract) => $"T{contract}";

    private void AddContract(XellentDbContext seed, string contractNum, DateTime endDate)
    {
        var agreement = $"A{contractNum[1..]}";

        seed.ExuContractTables.Add(new ExuContractTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Contractnum = contractNum,
            Custaccount = Account,
            Deliverycategory = Category,
            Name = contractNum,
            Contractstartdate = new DateTime(2020, 1, 1),
            Contractenddate = endDate,
            Mobilepaysubscriptionagre20135 = "",
            Einvoiceeannum = "",
            Custpaymcode = "",
            Billingplannum = "",
            Contracttemplatenum = TemplateFor(contractNum),
        });

        // Open-ended, so the part rule never has an opinion — only the header decides.
        seed.ExuContractPartTables.Add(new ExuContractPartTable
        {
            Recid = _recid++,
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

        seed.ExuAgreementTables.Add(new ExuAgreementTable
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Agreementnum = agreement,
            Deliverycategory = Category,
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
            Instbillingname = "Fakturanavn",
        });

        seed.ExuDelpoints.Add(new ExuDelpoint
        {
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Num = "1",
            Attachmentnum = agreement,
            Deliverycategory = Category,
            Custaccount = Account,
            Meteringpoint = MeteringPointFor(contractNum),
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
            Recid = _recid++,
            Dataareaid = Area,
            Companyid = Company,
            Delpointnum = "1",
            Attachmentnum = agreement,
            Deliverycategory = Category,
            Meterpointtypecode = "E17/Forb",
            Startdate = new DateTime(2020, 1, 1),
            Enddate = Open,
        });
    }

    private void AddLog(XellentDbContext seed, string contractNum, string logEmplId, DateTime loggedAt)
        => seed.ExuContractPartLogs.Add(new ExuContractPartLog
        {
            Recid = _recid++,
            Dataareaid = Area,
            Contractnum = contractNum,
            Instagreenum = $"A{contractNum[1..]}",
            Deliverycategory = Category,
            Companyid = Company,
            Productnum = "P1",
            Amrcode = "",
            Deliverygroupnumratecode = "",
            Contractpartstartdate = new DateTime(2020, 1, 1),
            Contractpartenddate = Open,
            Errormsg = ContractPartRestore.ChangeTypeEndDateChanged,
            Logemplid = logEmplId,
            Logdatetime = loggedAt,
        });

    private XellentDbContext NewContext() =>
        new(new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlite(_connection)
            .Options);
}
