using DataMigration.Core;
using DataMigration.Domain.Engros;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

public sealed class XellentDomainLoader : IXellentDomainLoader
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<string> _ignoredGroups;

    /// <param name="ignoredGroups">
    /// Kundegrupper (TechnicGroup4) der aldrig må merge. Påføres her frem for i hver enkelt runner:
    /// runnerne bygger deres MapperOptions hver for sig, og listen er en kendsgerning om
    /// dataområdet — den samme uanset hvilken kommando der læser. Uden den viser fx report andre
    /// merge-grupper end create rent faktisk laver.
    /// </param>
    public XellentDomainLoader(string connectionString, IReadOnlyList<string>? ignoredGroups = null)
    {
        _connectionString = connectionString;
        _ignoredGroups = ignoredGroups ?? [];
    }

    private XellentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(_connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        return new XellentDbContext(options);
    }

    public async Task<XellentLoadResult> LoadAsync(
        string[]? accounts, MapperOptions options, string dataAreaId, WarningCollector warnings, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var mapper = new XellentDomainMapper(context, warnings, WithIgnoredGroups(options), dataAreaId);
        return await mapper.LoadAsync(accounts, ct);
    }

    /// <summary>
    /// Lægger basens ignorerede grupper oveni kalderens egne. Union, ikke erstatning — en runner
    /// der selv sætter listen mister ikke basen, og påføring to gange ændrer intet.
    /// </summary>
    private MapperOptions WithIgnoredGroups(MapperOptions options) =>
        _ignoredGroups.Count == 0
            ? options
            : options with
            {
                IgnoredGroupNumbers = IgnoredGroupsLoader.ToSet(_ignoredGroups, options.IgnoredGroupNumbers)
            };

    public async Task<SettlementBasis?> LoadSettlementBasisAsync(
        string meteringPointId, DateTime periodStart, DateTime periodEnd, MapperOptions options, string dataAreaId, WarningCollector warnings, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        var mapper = new XellentDomainMapper(context, warnings, WithIgnoredGroups(options), dataAreaId);
        return await mapper.LoadSettlementBasisAsync(meteringPointId, periodStart, periodEnd, ct);
    }

    /// <summary>
    /// Resolves representatives for the given accounts, splitting the work into CVR-class portions
    /// when the list is too large for SQL Server to produce a query plan for the resolver's inlined
    /// <c>IN</c> list. See <see cref="RepresentativePortioning"/> for why the split is cut on CVR
    /// classes and where the size comes from.
    ///
    /// <para>A list that fits in one call takes exactly the call it always took — same query, same
    /// answer, no CVR read. Only a list above the cap pays for the extra lookup.</para>
    /// </summary>
    /// <param name="disableMerge">
    /// Every account resolves to itself. Off by default; see <see cref="IXellentDomainLoader"/> for
    /// when to set it. It also removes the reason for portioning: the CVR portions exist only so a
    /// merge group cannot be split across two calls, and with nothing merging there is no group to
    /// keep whole. The resolver's no-merge path is a single CustTable read that chunks its own
    /// account filter, so the query-plan wall is out of reach without any help from here.
    /// </param>
    public async Task<AccountRepresentativeResolution> ResolveRepresentativesAsync(
        string[] accounts, string dataAreaId, CancellationToken ct = default, bool disableMerge = false)
    {
        await using var context = CreateContext();
        var ignored = IgnoredGroupsLoader.ToSet(_ignoredGroups);

        Task<AccountRepresentativeResolution> ResolveAsync(string[] batch) =>
            AccountRepresentativeResolver.ResolveAsync(
                context, batch, dataAreaId, cancellationToken: ct, ignoredGroupNumbers: ignored,
                disableMerge: disableMerge);

        if (disableMerge || accounts.Length <= RepresentativePortioning.PortionSize)
            return await ResolveAsync(accounts);

        // No sibling pull-in: an empty accountsByCvr makes BuildCvrPortions pack exactly the accounts
        // it was given. That is what keeps the answer identical to the single call — the merge
        // universe stays the caller's own list, it is only cut into pieces no group can straddle.
        var cvrByAccount = await LoadCvrByAccountAsync(context, accounts, dataAreaId, ct);
        var portions = CosStatePlanner.BuildCvrPortions(
            accounts.Distinct(StringComparer.Ordinal),
            cvrByAccount,
            new Dictionary<string, List<string>>(StringComparer.Ordinal),
            RepresentativePortioning.PortionSize);

        var oversized = portions.Count(p => p.Accounts.Length > RepresentativePortioning.PortionSize);
        Console.WriteLine(
            $"  {accounts.Length} account(s) exceed the resolver's query-plan budget — resolving in "
            + $"{portions.Count} CVR-class portion(s) of at most {RepresentativePortioning.PortionSize}"
            + (oversized > 0 ? $"; {oversized} portion(s) hold a single CVR class larger than that and are resolved whole" : ""));

        // Sequential on one context: the resolver only projects, nothing is tracked, and running the
        // portions in parallel would trade a planner failure for connection pressure.
        var parts = new List<AccountRepresentativeResolution>(portions.Count);
        foreach (var portion in portions)
        {
            ct.ThrowIfCancellationRequested();
            parts.Add(await ResolveAsync(portion.Accounts));
        }
        return RepresentativePortioning.Merge(parts);
    }

    /// <summary>
    /// Raw <c>ExuCvrDisponent1</c> per account, chunked so this lookup cannot hit the same plan wall
    /// it exists to avoid. Untrimmed on purpose: the resolver's blank test is <c>IsNullOrEmpty</c>,
    /// so a spaces-only CVR still forms merge edges and must not be portioned apart.
    /// </summary>
    private static async Task<Dictionary<string, string>> LoadCvrByAccountAsync(
        XellentDbContext context, string[] accounts, string dataAreaId, CancellationToken ct)
    {
        var byTrimmed = new Dictionary<string, string>(StringComparer.Ordinal);
        var requested = accounts.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var chunk in requested.Chunk(2000))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await context.CustTables
                .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 })
                .ToListAsync(ct);
            foreach (var row in rows)
                byTrimmed[row.Accountnum.Trim()] = row.ExuCvrDisponent1 ?? string.Empty;
        }

        // Keyed on the account string the caller passed, not on the trimmed database value: the
        // portioning looks these up with the caller's own strings, and a miss would make the account
        // look CVR-less and let its merge group be split.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var account in requested)
            if (byTrimmed.TryGetValue(account.Trim(), out var cvr))
                result[account] = cvr;
        return result;
    }

    public async Task<MeteringPointXellentData?> LoadMeteringPointDataAsync(
        string identification, string dataAreaId, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        const string deliveryCategory = MigrationConstants.DeliveryCategoryElectricity;

        var delpoint = await context.ExuDelpoints
            .Where(d => d.Meteringpoint == identification
                && d.Dataareaid == dataAreaId
                && d.Deliverycategory == deliveryCategory)
            .Select(d => new { d.Num, d.Attachmentnum, d.Companyid, d.Powerexchangearea, d.EmsNetarea })
            .FirstOrDefaultAsync(ct);

        if (delpoint == null) return null;

        var meters = await context.ExuMeterTableTrades
            .Where(m => m.Delpointnum == delpoint.Num
                && m.Agreementnum == delpoint.Attachmentnum
                && m.Dataareaid == dataAreaId
                && m.Deliverycategory == deliveryCategory
                && m.Companyid == delpoint.Companyid
                && m.Enddate == MigrationConstants.EndDateNull)
            .Select(m => new { m.Meternum, m.Counterconstant, m.MeteringPointDhSubType })
            .ToListAsync(ct);

        var seriesResolution = await context.ExuTimereadingsPeriods
            .Where(t => t.Delpointnum == delpoint.Num
                && t.Attachmentnum == delpoint.Attachmentnum
                && t.Dataareaid == dataAreaId
                && t.Companyid == delpoint.Companyid
                && t.Deliverycategory == deliveryCategory)
            .OrderByDescending(t => t.Startdate)
            .Select(t => (int?)t.Seriesresolution)
            .FirstOrDefaultAsync(ct);

        var meterTypeCode = await context.EgsMeterpointTypecodeperiods
            .Where(m => m.Delpointnum == delpoint.Num
                && m.Attachmentnum == delpoint.Attachmentnum
                && m.Dataareaid == dataAreaId
                && m.Companyid == delpoint.Companyid
                && m.Deliverycategory == deliveryCategory
                && m.Enddate == MigrationConstants.EndDateNull)
            .Select(m => m.Meterpointtypecode)
            .FirstOrDefaultAsync(ct);

        var reading = await context.ExuReadingTableExts
            .Where(r => r.Delpointnum == delpoint.Num
                && r.Agreementnum == delpoint.Attachmentnum
                && r.Companyid == delpoint.Companyid
                && r.Dataareaid == dataAreaId
                && r.Deliverycategory == deliveryCategory)
            .OrderByDescending(r => r.Readingdate)
            .ThenByDescending(r => r.Recid)
            .FirstOrDefaultAsync(ct);

        var contractRef = await (
            from ag in context.ExuAgreementTables
            join cp in context.ExuContractPartTables
                on new { Agreementnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                equals new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid }
            join ct2 in context.ExuContractTables
                on new { cp.Contractnum, cp.Dataareaid, cp.Companyid }
                equals new { ct2.Contractnum, ct2.Dataareaid, ct2.Companyid }
            where ag.Agreementnum == delpoint.Attachmentnum
                && ag.Dataareaid == dataAreaId
                && ag.Companyid == delpoint.Companyid
            select ct2.Referencetomaincontract
        ).FirstOrDefaultAsync(ct);

        return new MeteringPointXellentData(
            MeteringPointType: ExtractionMappings.MapMeteringPointType(meterTypeCode),
            MeteringPointSubType: meters
                .Select(m => ExtractionMappings.MapMeteringPointSubTypeFromDhCode(m.MeteringPointDhSubType))
                .FirstOrDefault(s => s != null),
            MeterReadingOccurrence: seriesResolution.HasValue
                ? ExtractionMappings.MapMeterReadingOccurrence(seriesResolution)
                : null,
            EstimatedAnnualVolumeKwh: reading?.Expectedconsumptionperyear > 0
                ? (double)reading.Expectedconsumptionperyear
                : null,
            Meters: meters.Select(m => new XellentMeter(m.Meternum, (double)m.Counterconstant)).ToList(),
            JointInvoiceGroupId: contractRef,
            GridMarketBalanceArea: ExtractionMappings.MapPowerExchangeArea(delpoint.Powerexchangearea ?? ""),
            GridAreaCode: ExtractionMappings.MapGridArea(delpoint.EmsNetarea ?? ""));
    }

    public async Task<Dictionary<string, DateTime>> LookupContractEndDatesAsync(
        string[] contractNums, string dataAreaId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (contractNums.Length == 0) return result;

        await using var context = CreateContext();
        foreach (var chunk in contractNums.Chunk(2000))
        {
            var rows = await context.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.Contractnum))
                .Select(c => new { c.Contractnum, c.Contractenddate })
                .ToListAsync(ct);
            foreach (var row in rows)
                result.TryAdd(row.Contractnum, row.Contractenddate);
        }
        return result;
    }

    public async Task<Dictionary<string, string>> LookupContractAccountsAsync(
        string[] contractNums, string dataAreaId, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (contractNums.Length == 0) return result;

        await using var context = CreateContext();
        foreach (var chunk in contractNums.Chunk(2000))
        {
            var rows = await context.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId && chunk.Contains(c.Contractnum))
                .Select(c => new { c.Contractnum, c.Custaccount })
                .ToListAsync(ct);
            foreach (var row in rows)
                result.TryAdd(row.Contractnum, row.Custaccount);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<(string ContractNum, string AmrCode, DateTime StartDate), string>> LookupDeliveryGroupRateCodesAsync(
        string[] contractNums, string dataAreaId, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        return await XellentContractPartQueries.LookupDeliveryGroupRateCodesAsync(context, contractNums, dataAreaId, ct);
    }

    public async Task<IReadOnlyDictionary<(string ContractNum, string AmrCode, string ProductNum, DateTime StartDate), DateTime>> LookupContractPartLogEndDatesAsync(
        string[] contractNums, string dataAreaId, CancellationToken ct = default)
    {
        await using var context = CreateContext();
        return await XellentContractPartQueries.LookupContractPartLogEndDatesAsync(context, contractNums, dataAreaId, ct);
    }

    public async Task VerifyConnectionAsync(CancellationToken ct = default)
    {
        await using var context = CreateContext();
        await context.Database.CanConnectAsync(ct);
    }
}
