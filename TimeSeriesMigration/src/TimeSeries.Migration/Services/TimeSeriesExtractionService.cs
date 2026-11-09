using DataMigration.Core;
using DataMigration.Domain.TimeSeries;
using DataMigration.XellentData;
using TimeSeries.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace TimeSeries.Cli.Services;

public class TimeSeriesExtractionService
{
    private readonly XellentDbContext _context;
    private readonly string _deliveryCategory;
    private readonly HashSet<string>? _meteringPoints;
    private readonly bool _includeChildMeteringPoints;
    private const string DataAreaId = "hol";
    private static readonly DateTime TimeSeriesVersionToExcl = new(2154, 12, 31, 23, 59, 59, 0);
    private static readonly TimeZoneInfo DanishTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    public TimeSeriesExtractionService(
        XellentDbContext context,
        string? deliveryCategory = null,
        string[]? meteringPoints = null,
        bool includeChildMeteringPoints = false)
    {
        _context = context;
        _deliveryCategory = deliveryCategory ?? "El-ekstern";
        _meteringPoints = meteringPoints?.ToHashSet();
        _includeChildMeteringPoints = includeChildMeteringPoints;
    }

    public async Task<string[]> GetAllMeteringPointsAsync(string[] accountNumbers)
    {
        var allMeteringPoints = await (
            from cust in _context.CustTables
            join contract in _context.ExuContractTables on new { cust.Accountnum, cust.Dataareaid }
                equals new { Accountnum = contract.Custaccount, contract.Dataareaid }
            join contractPart in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { contractPart.Contractnum, contractPart.Dataareaid }
            join agreement in _context.ExuAgreementTables on new { Agreementnum = contractPart.Instagreenum, contractPart.Dataareaid, contractPart.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
            where cust.Dataareaid == DataAreaId
                && accountNumbers.Contains(cust.Accountnum)
                && contract.Deliverycategory == _deliveryCategory
                && delpoint.Deliverycategory == _deliveryCategory
            select delpoint.Meteringpoint
        ).Distinct().ToListAsync();

        var childMeteringPoints = await QueryChildMeteringPointsAsync(allMeteringPoints);
        return allMeteringPoints.Except(childMeteringPoints).ToArray();
    }

    /// <summary>
    /// A metering point an account holds (or held) in Xellent, with the contract's period and the
    /// agreement the contract's part points at.
    /// </summary>
    /// <param name="End">Day after the contract's last day (exclusive); null when the contract has no end.</param>
    /// <param name="AgreementNum">EXU_AGREEMENTTABLE.AGREEMENTNUM behind the contract part.</param>
    /// <param name="AgreementStatus">EXU_AGREEMENTTABLE.EMS_AGREEMENTSTATUSEBIX (5 = "Leverance ophørt").</param>
    /// <param name="AgreementLastDay">The agreement's ENDDATE as stored (inclusive); null when open (1900-01-01).</param>
    public sealed record AccountMeteringPoint(
        string Account, string MeteringPoint, DateOnly Start, DateOnly? End,
        string AgreementNum = "", int AgreementStatus = 0, DateOnly? AgreementLastDay = null)
    {
        /// <summary>EMS_AgreementStatusebIX value for "Leverance ophørt" (delivery ceased).</summary>
        public const int DeliveryCeased = 5;

        public bool Covers(DateOnly day) => Start <= day && (End is null || day < End);
    }

    /// <summary>
    /// The metering points among these that are children of another metering point in Xellent (relation
    /// table). Queried in chunks to stay under SQL Server's parameter limit.
    /// </summary>
    public async Task<HashSet<string>> GetChildMeteringPointsAsync(IEnumerable<string> meteringPoints)
    {
        var children = new HashSet<string>();
        foreach (var chunk in meteringPoints.Distinct().Chunk(1000))
            children.UnionWith(await QueryChildMeteringPointsAsync(chunk.ToList()));
        return children;
    }

    /// <summary>
    /// The children (Xellent relation table) of each of these metering points; one entry per metering
    /// point asked for, empty when it has none. Queried in chunks to stay under SQL Server's parameter limit.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GetChildrenByParentAsync(IEnumerable<string> parents)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var chunk in parents.Distinct().Chunk(1000))
        {
            foreach (var parent in chunk)
                result[parent] = [];
            var pairs = await (
                from parentDp in _context.ExuDelpoints
                join rel in _context.ExuDelpoint2delpointrela25505s
                    on new { Num = parentDp.Num, Attachmentnum = parentDp.Attachmentnum, parentDp.Dataareaid, parentDp.Deliverycategory, parentDp.Companyid }
                    equals new { Num = rel.Primarydelpointnum, Attachmentnum = rel.Primaryattachmentnum, rel.Dataareaid, rel.Deliverycategory, rel.Companyid }
                join childDp in _context.ExuDelpoints
                    on new { Num = rel.Secondarydelpointnum, Attachmentnum = rel.Secondaryattachmentnum, rel.Dataareaid, rel.Deliverycategory, rel.Companyid }
                    equals new { childDp.Num, childDp.Attachmentnum, childDp.Dataareaid, childDp.Deliverycategory, childDp.Companyid }
                where parentDp.Dataareaid == DataAreaId
                    && parentDp.Deliverycategory == _deliveryCategory
                    && chunk.Contains(parentDp.Meteringpoint)
                select new { Parent = parentDp.Meteringpoint, Child = childDp.Meteringpoint }
            ).Distinct().ToListAsync();
            foreach (var pair in pairs)
                result[pair.Parent].Add(pair.Child);
        }
        return result;
    }

    /// <summary>A reading period: when Xellent read the metering point at one resolution ("Hour"/"Quarter").
    /// <paramref name="End"/> is the day after the last day (exclusive); null when open.</summary>
    public sealed record ReadingPeriod(string Resolution, DateOnly Start, DateOnly? End);

    /// <summary>
    /// Every reading period (EXU_TIMEREADINGSPERIOD) of these metering points in this delivery category,
    /// per metering point. Queried in chunks to stay under SQL Server's parameter limit.
    /// </summary>
    public async Task<Dictionary<string, List<ReadingPeriod>>> GetReadingPeriodsAsync(IEnumerable<string> meteringPoints)
    {
        var result = new Dictionary<string, List<ReadingPeriod>>(StringComparer.Ordinal);
        foreach (var chunk in meteringPoints.Distinct().Chunk(1000))
        {
            var delpoints = await _context.ExuDelpoints
                .Where(d => d.Dataareaid == DataAreaId && d.Deliverycategory == _deliveryCategory && chunk.Contains(d.Meteringpoint))
                .Select(d => new { d.Meteringpoint, d.Num, d.Attachmentnum, d.Companyid })
                .Distinct()
                .ToListAsync();
            if (delpoints.Count == 0)
                continue;
            var nums = delpoints.Select(d => d.Num).Distinct().ToList();
            var attachments = delpoints.Select(d => d.Attachmentnum).Distinct().ToList();
            var periods = await _context.ExuTimereadingsPeriods
                .Where(t => t.Dataareaid == DataAreaId && t.Deliverycategory == _deliveryCategory
                    && nums.Contains(t.Delpointnum) && attachments.Contains(t.Attachmentnum))
                .Select(t => new { t.Delpointnum, t.Attachmentnum, t.Companyid, t.Startdate, t.Enddate, t.Seriesresolution })
                .ToListAsync();
            var mpByDelpoint = delpoints
                .GroupBy(d => (d.Num, d.Attachmentnum, d.Companyid))
                .ToDictionary(g => g.Key, g => g.First().Meteringpoint);
            foreach (var p in periods.Where(p => mpByDelpoint.ContainsKey((p.Delpointnum, p.Attachmentnum, p.Companyid))))
            {
                var mp = mpByDelpoint[(p.Delpointnum, p.Attachmentnum, p.Companyid)];
                var (start, end) = ToAgreementPeriod(p.Startdate, p.Enddate);
                if (!result.TryGetValue(mp, out var list))
                    result[mp] = list = [];
                list.Add(new ReadingPeriod(ToResolutionName(p.Seriesresolution), start, end));
            }
        }
        return result;
    }

    /// <summary>
    /// The XML of every incoming DataHub message Xellent logged for this metering point, as received.
    /// EMS_ZDATAHUBLOG (METERINGPOINT, MESSAGEDIRECTION 1 = incoming) → EMS_ZDATAHUBLOGMESSAGES
    /// (ZEVENTQUEUEREFRECID) → EXU_ZEVENTQUEUEDATATABLE.XMLDATA. MP 571313104200130218: the
    /// DK_NotifyMasterDataMeteringPoint with Occurrence 2025-12-08T23:00:00Z is event 5713304140.
    /// These tables are not in the XellentData model, so the query is written out.
    /// </summary>
    public Task<List<string>> GetIncomingDataHubMessagesAsync(string meteringPoint) =>
        _context.Database.SqlQuery<string>($"""
            SELECT d.XMLDATA AS Value
            FROM EMS_ZDATAHUBLOG l
            JOIN EMS_ZDATAHUBLOGMESSAGES m ON m.DATAHUBLOGID = l.ID AND m.DATAAREAID = l.DATAAREAID
            JOIN EXU_ZEVENTQUEUEDATATABLE d ON d.ZEVENTQUEUEREFRECID = m.ZEVENTQUEUEREFRECID AND d.DATAAREAID = m.DATAAREAID
            WHERE l.DATAAREAID = {DataAreaId} AND l.MESSAGEDIRECTION = 1 AND l.METERINGPOINT = {meteringPoint}
            """).ToListAsync();

    /// <summary>A metering point's subtype in Xellent: EXU_METERTABLETRADE.METERINGPOINTDHSUBTYPE (1 = physical,
    /// 2 = virtual, 3 = calculated) on its latest agreement. <paramref name="SubType"/> is null when that agreement
    /// has no meter row.</summary>
    public sealed record MeteringPointSubType(string Agreement, int? SubType);

    /// <summary>
    /// The subtype of each of these metering points in this delivery category: the delivery point whose agreement
    /// starts last, and on it the meter row (EXU_METERTABLETRADE) that starts last. A metering point with no delivery
    /// point is left out. Queried in chunks to stay under SQL Server's parameter limit.
    /// </summary>
    public async Task<Dictionary<string, MeteringPointSubType>> GetMeteringPointSubTypesAsync(IEnumerable<string> meteringPoints)
    {
        var result = new Dictionary<string, MeteringPointSubType>(StringComparer.Ordinal);
        foreach (var chunk in meteringPoints.Distinct().Chunk(1000))
        {
            var delpoints = await (
                from dp in _context.ExuDelpoints
                join agreement in _context.ExuAgreementTables
                    on new { Agreementnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
                    equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                where dp.Dataareaid == DataAreaId && dp.Deliverycategory == _deliveryCategory && chunk.Contains(dp.Meteringpoint)
                select new { dp.Meteringpoint, dp.Num, dp.Attachmentnum, dp.Companyid, AgreementStart = agreement.Startdate }
            ).Distinct().ToListAsync();
            if (delpoints.Count == 0)
                continue;
            var latest = delpoints
                .GroupBy(d => d.Meteringpoint)
                .Select(g => g.OrderByDescending(d => d.AgreementStart).ThenByDescending(d => d.Attachmentnum, StringComparer.Ordinal).First())
                .ToList();
            var attachments = latest.Select(d => d.Attachmentnum).Distinct().ToList();
            var meters = await _context.ExuMeterTableTrades
                .Where(m => m.Dataareaid == DataAreaId && m.Deliverycategory == _deliveryCategory && attachments.Contains(m.Agreementnum))
                .Select(m => new { m.Agreementnum, m.Delpointnum, m.Companyid, m.Startdate, m.MeteringPointDhSubType })
                .ToListAsync();
            foreach (var dp in latest)
            {
                var meter = meters
                    .Where(m => m.Agreementnum == dp.Attachmentnum && m.Delpointnum == dp.Num && m.Companyid == dp.Companyid)
                    .OrderByDescending(m => m.Startdate)
                    .FirstOrDefault();
                result[dp.Meteringpoint] = new MeteringPointSubType(dp.Attachmentnum, meter?.MeteringPointDhSubType);
            }
        }
        return result;
    }

    /// <summary>
    /// The metering point's status in Xellent right now (EXU_METERPOINTSTATUSPERIOD on its delivery
    /// point): the status of the period with the latest start. Null when Xellent records none.
    /// </summary>
    public async Task<int?> GetMeterPointStatusAsync(string meteringPoint)
    {
        var periods = await (
            from dp in _context.ExuDelpoints
            join period in _context.ExuMeterPointStatusPeriods
                on new { dp.Dataareaid, dp.Deliverycategory, dp.Companyid, dp.Attachmentnum, Delpointnum = dp.Num }
                equals new { period.Dataareaid, period.Deliverycategory, period.Companyid, period.Attachmentnum, period.Delpointnum }
            where dp.Dataareaid == DataAreaId
                && dp.Deliverycategory == _deliveryCategory
                && dp.Meteringpoint == meteringPoint
            select new { period.Startdate, period.Meterpointstatus }
        ).ToListAsync();
        return periods.Count == 0
            ? null
            : periods.OrderByDescending(p => p.Startdate).First().Meterpointstatus;
    }

    /// <summary>
    /// Every contract on the metering points in this delivery category, whichever account holds it —
    /// used to see who held an MP on a given day.
    /// </summary>
    public async Task<List<AccountMeteringPoint>> GetMeteringPointContractsAsync(IEnumerable<string> meteringPoints)
    {
        var rows = new List<AccountMeteringPoint>();
        foreach (var chunk in meteringPoints.Distinct().Chunk(1000))
            rows.AddRange(await QueryContractsAsync(accounts: null, chunk));
        return rows;
    }

    // contract → part → agreement → delivery point, filtered by accounts or by metering points.
    private async Task<List<AccountMeteringPoint>> QueryContractsAsync(string[]? accounts, string[]? meteringPoints)
    {
        var page = await (
            from contract in _context.ExuContractTables
            join contractPart in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { contractPart.Contractnum, contractPart.Dataareaid }
            join agreement in _context.ExuAgreementTables on new { Agreementnum = contractPart.Instagreenum, contractPart.Dataareaid, contractPart.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
            where contract.Dataareaid == DataAreaId
                && (accounts == null || accounts.Contains(contract.Custaccount))
                && (meteringPoints == null || meteringPoints.Contains(delpoint.Meteringpoint))
                && contract.Deliverycategory == _deliveryCategory
                && delpoint.Deliverycategory == _deliveryCategory
            // The contract header's dates, not the part's: ownership is a contract fact, and parts can
            // start later than their contract (seen on MP 571313113163401655: contract 2024-01-19,
            // first part 2024-02-19).
            select new
            {
                contract.Custaccount,
                delpoint.Meteringpoint,
                Startdate = contract.Contractstartdate,
                Enddate = contract.Contractenddate,
                agreement.Agreementnum,
                AgreementStatus = agreement.EmsAgreementstatusebix,
                AgreementEnd = agreement.Enddate
            }
        ).Distinct().ToListAsync();

        return page
            .Select(r =>
            {
                var (start, end) = ToAgreementPeriod(r.Startdate, r.Enddate);
                DateOnly? agreementLastDay = r.AgreementEnd.Date == MigrationConstants.EndDateNull
                    ? null
                    : DateOnly.FromDateTime(r.AgreementEnd);
                return new AccountMeteringPoint(r.Custaccount, r.Meteringpoint, start, end,
                    r.Agreementnum, r.AgreementStatus, agreementLastDay);
            })
            .ToList();
    }

    public async Task<List<MeteringPointTimeSeriesDto>> ExtractTimeSeriesAsync(string[] accountNumbers)
    {
        var toDate = DateTime.Now;
        var fromDate = toDate.AddYears(-3);
        return await ExtractTimeSeriesAsync(accountNumbers, fromDate, toDate);
    }

    public async Task<List<MeteringPointTimeSeriesDto>> ExtractTimeSeriesAsync(
        string[] accountNumbers,
        DateTime startDate,
        DateTime? endDate)
    {
        // Indkommende start/end er DK-lokale kalenderdatoer (CLI-args eller resolved revision
        // tolket som DK midnat). EMS_TIMESERIESVALUES.TIMEOFVALUE er UTC, så vinduet konverteres
        // til UTC inden SQL-sammenligningen — ellers medtages 1-2 timer ekstra (DST-afhængigt).
        var fromDate = ToDanishUtc(startDate);
        var toDate = ToDanishUtc(endDate ?? DateTime.Now);
        var versionToExcl = TimeSeriesVersionToExcl;

        var meteringPoints = _meteringPoints?.ToList() ?? new List<string>();

        // Step 2: Get child metering points using relationship table (only if enabled)
        var childMeteringPoints = new List<(string ChildMeteringPoint, string ParentMeteringPoint)>();

        if (_includeChildMeteringPoints)
        {
            var parentDelpoints = await (
                from dp in _context.ExuDelpoints
                where meteringPoints.Contains(dp.Meteringpoint)
                    && dp.Dataareaid == DataAreaId
                    && dp.Deliverycategory == _deliveryCategory
                select new
                {
                    dp.Meteringpoint,
                    dp.Num,
                    dp.Attachmentnum,
                    dp.Deliverycategory,
                    dp.Companyid
                }
            ).ToListAsync();

            foreach (var parent in parentDelpoints)
            {
                var childRelations = await (
                    from rel in _context.ExuDelpoint2delpointrela25505s
                    join childDp in _context.ExuDelpoints on new
                    {
                        Num = rel.Secondarydelpointnum,
                        Attachmentnum = rel.Secondaryattachmentnum,
                        rel.Dataareaid,
                        rel.Deliverycategory,
                        rel.Companyid
                    }
                    equals new
                    {
                        childDp.Num,
                        childDp.Attachmentnum,
                        childDp.Dataareaid,
                        childDp.Deliverycategory,
                        childDp.Companyid
                    }
                    where rel.Dataareaid == DataAreaId
                        && rel.Primarydelpointnum == parent.Num
                        && rel.Primaryattachmentnum == parent.Attachmentnum
                        && rel.Deliverycategory == parent.Deliverycategory
                        && rel.Companyid == parent.Companyid
                    select new
                    {
                        ChildMeteringPoint = childDp.Meteringpoint,
                        ParentMeteringPoint = parent.Meteringpoint
                    }
                ).ToListAsync();

                childMeteringPoints.AddRange(childRelations.Select(c => (
                    c.ChildMeteringPoint,
                    c.ParentMeteringPoint
                )));
            }
        }

        // Step 3: For each metering point, query timeseries data
        var result = new List<MeteringPointTimeSeriesDto>();

        foreach (var meteringPoint in meteringPoints)
        {
            var dto = await ExtractSingleMeteringPointAsync(meteringPoint, null, fromDate, toDate, versionToExcl);
            result.Add(dto);
        }

        // Step 4: Process child metering points
        foreach (var (childMeteringPoint, parentMeteringPoint) in childMeteringPoints)
        {
            var dto = await ExtractSingleMeteringPointAsync(childMeteringPoint, parentMeteringPoint, fromDate, toDate, versionToExcl);
            result.Add(dto);
        }

        return result;
    }

    /// <summary>
    /// The same segments as <see cref="ExtractTimeSeriesAsync(string[], DateTime, DateTime?)"/> for one
    /// metering point, counted instead of read: per segment the number of points on or after
    /// <paramref name="deliveryBoundary"/> and the first of them, plus how many fall before it. A few
    /// small aggregate queries instead of every point — what a dry run needs.
    /// </summary>
    public Task<MeteringPointTimeSeriesDto> CountTimeSeriesAsync(
        string meteringPoint, DateTime startDate, DateTime endDate, DateTimeOffset? deliveryBoundary) =>
        ExtractSingleMeteringPointAsync(meteringPoint, null, ToDanishUtc(startDate), ToDanishUtc(endDate),
            TimeSeriesVersionToExcl, countOnly: true, boundaryUtc: deliveryBoundary?.UtcDateTime);

    private async Task<MeteringPointTimeSeriesDto> ExtractSingleMeteringPointAsync(
        string meteringPoint, string? parentMeteringPoint,
        DateTime fromDate, DateTime toDate, DateTime versionToExcl,
        bool countOnly = false, DateTime? boundaryUtc = null)
    {
        // Each EMS_TIMESERIES row belongs to one company (COMPANYID). The period it covers is not on
        // the series itself but on the company's agreement for this metering point:
        //   EMS_TIMESERIES.METERINGPOINT + COMPANYID = EXU_DELPOINT.METERINGPOINT + COMPANYID
        //   EXU_DELPOINT.ATTACHMENTNUM = EXU_AGREEMENTTABLE.AGREEMENTNUM
        // At a company switch Xellent can keep writing the old company's series past the switch
        // (MP 571313113160561482: 'meh' runs to 2024-01-01 22:00 UTC, 'for' starts 2023-12-31 23:00
        // UTC). Only the series whose agreement covers a period may supply that period's points,
        // so one segment is emitted per (series, agreement period) and points outside every
        // agreement period of their series are left out.
        var headers = await (
            from ts in _context.EmsTimeseries
            where ts.Dataareaid == DataAreaId
                && ts.Meteringpoint == meteringPoint
                && ts.Deliverycategory == _deliveryCategory
            select new { ts.Recid, ts.Companyid, ts.Timeresolution, ts.Quantitycodevalue }
        ).ToListAsync();

        var companies = headers.Select(h => h.Companyid).Distinct().ToList();
        var agreementPeriods = await (
            from dp in _context.ExuDelpoints
            join agreement in _context.ExuAgreementTables
                on new { Agreementnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            where dp.Dataareaid == DataAreaId
                && dp.Meteringpoint == meteringPoint
                && dp.Deliverycategory == _deliveryCategory
                && companies.Contains(dp.Companyid)
            select new { dp.Num, dp.Attachmentnum, dp.Companyid, agreement.Startdate, agreement.Enddate }
        ).Distinct().ToListAsync();

        // Within an agreement, EXU_TIMEREADINGSPERIOD records when the metering point was read hourly
        // (SERIESRESOLUTION 0) or quarter-hourly (1). A series only covers the parts of its
        // agreement read at its own resolution — that is where an Hour series hands over to a
        // Quarter series under the same company.
        var attachments = agreementPeriods.Select(a => a.Attachmentnum).Distinct().ToList();
        var readingPeriods = attachments.Count == 0 ? [] : await (
            from trp in _context.ExuTimereadingsPeriods
            where trp.Dataareaid == DataAreaId
                && trp.Deliverycategory == _deliveryCategory
                && companies.Contains(trp.Companyid)
                && attachments.Contains(trp.Attachmentnum)
            select new { trp.Delpointnum, trp.Attachmentnum, trp.Companyid, trp.Startdate, trp.Enddate, trp.Seriesresolution }
        ).ToListAsync();

        // An agreement whose start is after its end (seen on MPs 571313113140168212 and 571313174111946987:
        // the start was moved to a later takeover/migration date, the end left at the old customer's last
        // day) keeps its end and gets an open start; the caller's window start sets the lower bound.
        var agreementsByCompany = agreementPeriods
            .Select(a => (a.Companyid, a.Num, a.Attachmentnum, Period: ToAgreementPeriod(a.Startdate, a.Enddate)))
            .Select(a => a.Period.End is { } end && end <= a.Period.Start ? a with { Period = (DateOnly.MinValue, end) } : a)
            .GroupBy(a => a.Companyid)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.Period.Start).ToList());
        List<(DateOnly Start, DateOnly? End)> PeriodsOf(string companyId) =>
            agreementsByCompany.TryGetValue(companyId, out var list) ? list.Select(a => a.Period).ToList() : [];

        // The resolutions a company has a series for.
        HashSet<string> SeriesResolutionsOf(string companyId) =>
            headers.Where(h => h.Companyid == companyId).Select(h => MapTimeResolution(h.Timeresolution)).ToHashSet();

        // The agreement periods of the series' company, narrowed to the reading periods at the series'
        // resolution. An agreement with no reading periods is used whole. A reading period at a resolution
        // the company has no series for counts as the series' own: the values are always taken at the
        // resolution they are stored in. ReadingResolution carries the outgoing conversion decision.
        List<(DateOnly Start, DateOnly? End, string? ReadingResolution)> PeriodsFor(string companyId, string resolution)
        {
            if (!agreementsByCompany.TryGetValue(companyId, out var agreements))
                return [];
            var withSeries = SeriesResolutionsOf(companyId);
            bool Serves(int seriesResolution) =>
                ToResolutionName(seriesResolution) == resolution
                || !withSeries.Contains(ToResolutionName(seriesResolution));
            var result = new List<(DateOnly Start, DateOnly? End, string? ReadingResolution)>();
            foreach (var agreement in agreements)
            {
                var reads = readingPeriods
                    .Where(r => r.Companyid == companyId && r.Attachmentnum == agreement.Attachmentnum && r.Delpointnum == agreement.Num)
                    .ToList();
                if (reads.Count == 0)
                {
                    result.Add((agreement.Period.Start, agreement.Period.End, null));
                    continue;
                }
                foreach (var read in reads.Where(r => Serves(r.Seriesresolution)))
                {
                    var readPeriod = ToAgreementPeriod(read.Startdate, read.Enddate);
                    var start = readPeriod.Start > agreement.Period.Start ? readPeriod.Start : agreement.Period.Start;
                    DateOnly? end = (readPeriod.End, agreement.Period.End) switch
                    {
                        (null, var e) => e,
                        (var e, null) => e,
                        (DateOnly a, DateOnly b) => a < b ? a : b
                    };
                    if (end is null || end > start)
                        result.Add((start, end, ToResolutionName(read.Seriesresolution)));
                }
            }
            // Merge only equal reading resolutions: outgoing conversion must retain each dated boundary.
            var merged = new List<(DateOnly Start, DateOnly? End, string? ReadingResolution)>();
            foreach (var p in result.OrderBy(p => p.Start))
            {
                if (merged.Count > 0 && merged[^1].ReadingResolution == p.ReadingResolution
                    && (merged[^1].End is null || p.Start <= merged[^1].End))
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, last.End is null || p.End is null ? null : (p.End > last.End ? p.End : last.End), last.ReadingResolution);
                    continue;
                }
                merged.Add(p);
            }
            return merged;
        }
        bool OverlapsWindow((DateOnly Start, DateOnly? End) p) =>
            DayStartUtc(p.Start) < toDate
            && (p.End is not { } end || ToDanishUtc(end.ToDateTime(TimeOnly.MinValue)) > fromDate);

        // A series is read only when one of its agreement periods overlaps the window. Series with
        // no agreement at all are still read, so points without a home are reported, not lost.
        var recids = headers
            .Where(h => PeriodsOf(h.Companyid) is var periods && (periods.Count == 0 || periods.Any(OverlapsWindow)))
            .Select(h => h.Recid)
            .ToList();

        // What Xellent says the metering point was read as inside the window, and the stretches it was
        // read at a resolution the company has no series for. Values stored there (in a series of the
        // other resolution) have the wrong resolution — a known Xellent issue: MP 571313185621006160 is
        // read quarter-hourly from 2023-04-26 but has only an hourly series, with hourly values to 2025.
        var readResolutionsInWindow = new SortedSet<string>(StringComparer.Ordinal);
        var unserved = new List<(long Recid, string Company, string SeriesResolution, string ReadAs, DateOnly Start, DateOnly? End, DateTime FromUtc, DateTime ToUtc)>();
        foreach (var (company, agreements) in agreementsByCompany)
        {
            var seriesOfCompany = headers.Where(h => h.Companyid == company).ToList();
            var resolutionsWithSeries = seriesOfCompany.Select(h => MapTimeResolution(h.Timeresolution)).ToHashSet();
            foreach (var agreement in agreements)
            {
                foreach (var read in readingPeriods.Where(r => r.Companyid == company && r.Attachmentnum == agreement.Attachmentnum && r.Delpointnum == agreement.Num))
                {
                    var readPeriod = ToAgreementPeriod(read.Startdate, read.Enddate);
                    var start = readPeriod.Start > agreement.Period.Start ? readPeriod.Start : agreement.Period.Start;
                    DateOnly? end = (readPeriod.End, agreement.Period.End) switch
                    {
                        (null, var e) => e,
                        (var e, null) => e,
                        (DateOnly a, DateOnly b) => a < b ? a : b
                    };
                    var startUtc = DayStartUtc(start);
                    var fromUtc = startUtc > fromDate ? startUtc : fromDate;
                    var toUtc = end is { } e2 && ToDanishUtc(e2.ToDateTime(TimeOnly.MinValue)) is var endUtc && endUtc < toDate ? endUtc : toDate;
                    if (fromUtc >= toUtc)
                        continue;
                    var readAs = ToResolutionName(read.Seriesresolution);
                    if (resolutionsWithSeries.Contains(readAs))
                    {
                        readResolutionsInWindow.Add(readAs);
                        continue;
                    }
                    // Keep the recorded reading resolution even when source values use another one.
                    readResolutionsInWindow.Add(readAs);
                    foreach (var series in seriesOfCompany)
                        unserved.Add((series.Recid, company, MapTimeResolution(series.Timeresolution), readAs, start, end, fromUtc, toUtc));
                }
            }
        }
        async Task<List<string>> WrongResolutionAsync(Func<long, DateTime, DateTime, Task<int>> countValues)
        {
            var found = new List<string>();
            foreach (var u in unserved)
            {
                var count = await countValues(u.Recid, u.FromUtc, u.ToUtc);
                if (count > 0)
                    found.Add($"TS {u.Recid}/{u.Company} is {u.SeriesResolution}, its {count} values are {u.SeriesResolution}, " +
                              $"TimeReadingPeriod is {u.ReadAs} " +
                              $"({u.Start:yyyy-MM-dd} → {(u.End is { } e ? e.AddDays(-1).ToString("yyyy-MM-dd") : "open")})");
            }
            return found;
        }

        if (countOnly)
            return await CountSegmentsAsync();

        var values = await (
            from tsv in _context.EmsTimeseriesValues
            where tsv.Dataareaid == DataAreaId
                && recids.Contains(tsv.Timeseriesrefrecid)
                && tsv.Versiontoexcl == versionToExcl
                && tsv.Timeofvalue >= fromDate
                && tsv.Timeofvalue < toDate
            select new { tsv.Timeseriesrefrecid, tsv.Timeofvalue, tsv.Value, tsv.Qualityofvalue }
        ).ToListAsync();
        var valuesBySeries = values.ToLookup(v => v.Timeseriesrefrecid);

        // EMS_TIMESERIESVALUES.TIMEOFVALUE lagres som UTC i Xellent (verificeret mod AXDB50_TEST
        // 2026-05-20: MP 571313115242231955, fall-back døgn 2024-10-27 har 25 ubrudte UTC-timer
        // 21:00→04:00 uden gentaget 02:00 — havde det været lokal dansk tid ville fall-back-timen
        // optræde to gange). Stempler skal derfor sendes uændret som UTC til Settl — ingen
        // tidszone-konvertering eller DST-behandling.
        var segments = new List<TimeSeriesSegmentDto>();
        var seriesWithoutAgreement = new List<string>();
        foreach (var header in headers)
        {
            var seriesValues = valuesBySeries[header.Recid].ToList();
            if (seriesValues.Count == 0)
                continue;

            if (PeriodsOf(header.Companyid).Count == 0)
            {
                seriesWithoutAgreement.Add($"{header.Recid}/{header.Companyid}");
                continue;
            }

            var resolution = MapTimeResolution(header.Timeresolution);
            var periods = PeriodsFor(header.Companyid, resolution);
            if (periods.Count == 0)
            {
                seriesWithoutAgreement.Add($"{header.Recid}/{header.Companyid} (no {resolution} reading period)");
                continue;
            }

            foreach (var period in periods)
            {
                var periodStartUtc = DayStartUtc(period.Start);
                DateTime? periodEndUtc = period.End is { } end ? ToDanishUtc(end.ToDateTime(TimeOnly.MinValue)) : null;

                var points = seriesValues
                    .Where(v => v.Timeofvalue >= periodStartUtc && (periodEndUtc is null || v.Timeofvalue < periodEndUtc))
                    .OrderBy(v => v.Timeofvalue)
                    .Select(v => new TimeSeriesDto
                    {
                        TimeOfValue = new DateTimeOffset(DateTime.SpecifyKind(v.Timeofvalue, DateTimeKind.Utc), TimeSpan.Zero),
                        Value = v.Value,
                        QuantityCodeValue = header.Quantitycodevalue,
                        TimeResolution = resolution,
                        QualityOfValue = MapQualityOfValue(v.Qualityofvalue)
                    })
                    .ToList();
                if (points.Count == 0)
                    continue;

                segments.Add(new TimeSeriesSegmentDto
                {
                    TimeResolution = resolution,
                    ReadingResolution = period.ReadingResolution,
                    CompanyId = header.Companyid,
                    PeriodStart = period.Start,
                    PeriodEnd = period.End,
                    Points = points
                });
            }
        }

        return new MeteringPointTimeSeriesDto
        {
            MeteringPointId = meteringPoint,
            ParentMeteringPointId = parentMeteringPoint,
            Unit = "kWh",
            Segments = segments.OrderBy(s => s.Points[0].TimeOfValue).ToList(),
            SeriesWithoutAgreement = seriesWithoutAgreement,
            ReadingResolutionsInWindow = readResolutionsInWindow.ToList(),
            ValuesWithWrongResolution = await WrongResolutionAsync((recid, from, to) =>
                Task.FromResult(valuesBySeries[recid].Count(v => v.Timeofvalue >= from && v.Timeofvalue < to))),
        };

        // The count-only twin of the loop above: the same series, periods and checks, with each
        // (series, period) asked for its counts instead of its points.
        async Task<MeteringPointTimeSeriesDto> CountSegmentsAsync()
        {
            var counted = new List<(TimeSeriesSegmentDto Segment, DateTime FirstPoint)>();
            var withoutAgreement = new List<string>();
            var beforeBoundary = 0;
            IQueryable<DataMigration.XellentData.Entities.EmsTimeseriesValues> ValuesOf(long recid, DateTime from, DateTime to) =>
                _context.EmsTimeseriesValues.Where(v => v.Dataareaid == DataAreaId
                    && v.Timeseriesrefrecid == recid
                    && v.Versiontoexcl == versionToExcl
                    && v.Timeofvalue >= from
                    && v.Timeofvalue < to);

            foreach (var header in headers.Where(h => recids.Contains(h.Recid)))
            {
                if (PeriodsOf(header.Companyid).Count == 0)
                {
                    if (await ValuesOf(header.Recid, fromDate, toDate).AnyAsync())
                        withoutAgreement.Add($"{header.Recid}/{header.Companyid}");
                    continue;
                }

                var resolution = MapTimeResolution(header.Timeresolution);
                var periods = PeriodsFor(header.Companyid, resolution);
                if (periods.Count == 0)
                {
                    if (await ValuesOf(header.Recid, fromDate, toDate).AnyAsync())
                        withoutAgreement.Add($"{header.Recid}/{header.Companyid} (no {resolution} reading period)");
                    continue;
                }

                foreach (var period in periods)
                {
                    var periodStartUtc = DayStartUtc(period.Start);
                    var from = periodStartUtc > fromDate ? periodStartUtc : fromDate;
                    var to = period.End is { } end && ToDanishUtc(end.ToDateTime(TimeOnly.MinValue)) is var periodEndUtc && periodEndUtc < toDate
                        ? periodEndUtc
                        : toDate;
                    if (from >= to)
                        continue;

                    // No boundary: everything counts. 1900-01-01 rather than DateTime.MinValue, which SQL Server's datetime cannot hold.
                    var boundary = boundaryUtc ?? new DateTime(1900, 1, 1);
                    var agg = await ValuesOf(header.Recid, from, to)
                        .GroupBy(_ => 1)
                        .Select(g => new
                        {
                            Total = g.Count(),
                            First = g.Min(v => v.Timeofvalue),
                            Included = g.Count(v => v.Timeofvalue >= boundary),
                            FirstIncluded = g.Min(v => v.Timeofvalue >= boundary ? (DateTime?)v.Timeofvalue : null),
                        })
                        .FirstOrDefaultAsync();
                    if (agg == null || agg.Total == 0)
                        continue;

                    string? conversionError = null;
                    if (resolution == "Hour" && period.ReadingResolution == "Quarter" && agg.Included > 0)
                    {
                        var invalid = await ValuesOf(header.Recid, from, to)
                            .Where(v => v.Timeofvalue >= boundary && v.Value % 0.00004m != 0m)
                            .OrderBy(v => v.Timeofvalue)
                            .Select(v => new { v.Timeofvalue, v.Value })
                            .FirstOrDefaultAsync();
                        if (invalid != null)
                            conversionError = OutgoingTimeSeries.PrecisionError(
                                new DateTimeOffset(DateTime.SpecifyKind(invalid.Timeofvalue, DateTimeKind.Utc)), invalid.Value);
                    }

                    beforeBoundary += agg.Total - agg.Included;
                    counted.Add((new TimeSeriesSegmentDto
                    {
                        TimeResolution = resolution,
                        ReadingResolution = period.ReadingResolution,
                        CompanyId = header.Companyid,
                        PeriodStart = period.Start,
                        PeriodEnd = period.End,
                        CountedPoints = agg.Included,
                        ConversionError = conversionError,
                        FirstCountedPoint = agg.FirstIncluded is { } fi
                            ? new DateTimeOffset(DateTime.SpecifyKind(fi, DateTimeKind.Utc), TimeSpan.Zero)
                            : null,
                    }, agg.First));
                }
            }

            return new MeteringPointTimeSeriesDto
            {
                MeteringPointId = meteringPoint,
                ParentMeteringPointId = parentMeteringPoint,
                Unit = "kWh",
                Segments = counted.OrderBy(s => s.FirstPoint).Select(s => s.Segment).ToList(),
                SeriesWithoutAgreement = withoutAgreement,
                CountedPointsBeforeBoundary = beforeBoundary,
                ReadingResolutionsInWindow = readResolutionsInWindow.ToList(),
                ValuesWithWrongResolution = await WrongResolutionAsync((recid, from, to) => ValuesOf(recid, from, to).CountAsync()),
            };
        }
    }

    // Agreement and reading-period dates are Danish calendar days with an inclusive end; 1900-01-01
    // (or the 2154-12-31 far-future marker) means no end. Returned as [Start, End) so End is the
    // first day no longer covered.
    private static (DateOnly Start, DateOnly? End) ToAgreementPeriod(DateTime startDate, DateTime endDate)
    {
        var start = DateOnly.FromDateTime(startDate);
        DateOnly? end = endDate.Date == MigrationConstants.EndDateNull || endDate.Date >= MigrationConstants.EndDateFuture
            ? null
            : DateOnly.FromDateTime(endDate).AddDays(1);
        return (start, end);
    }

    private async Task<HashSet<string>> QueryChildMeteringPointsAsync(List<string> meteringPoints)
    {
        var childIds = await (
            from parentDp in _context.ExuDelpoints
            join rel in _context.ExuDelpoint2delpointrela25505s
                on new { Num = parentDp.Num, Attachmentnum = parentDp.Attachmentnum, parentDp.Dataareaid, parentDp.Deliverycategory, parentDp.Companyid }
                equals new { Num = rel.Primarydelpointnum, Attachmentnum = rel.Primaryattachmentnum, rel.Dataareaid, rel.Deliverycategory, rel.Companyid }
            join childDp in _context.ExuDelpoints
                on new { Num = rel.Secondarydelpointnum, Attachmentnum = rel.Secondaryattachmentnum, rel.Dataareaid, rel.Deliverycategory, rel.Companyid }
                equals new { childDp.Num, childDp.Attachmentnum, childDp.Dataareaid, childDp.Deliverycategory, childDp.Companyid }
            where parentDp.Dataareaid == DataAreaId
                && parentDp.Deliverycategory == _deliveryCategory
                && meteringPoints.Contains(parentDp.Meteringpoint)
            select childDp.Meteringpoint
        ).Distinct().ToListAsync();

        return childIds.ToHashSet();
    }

    // Danish midnight of the day as UTC; DateOnly.MinValue (an open start) stays DateTime.MinValue.
    private static DateTime DayStartUtc(DateOnly day) =>
        day == DateOnly.MinValue ? DateTime.MinValue : ToDanishUtc(day.ToDateTime(TimeOnly.MinValue));

    private static DateTime ToDanishUtc(DateTime dkLocal)
    {
        if (dkLocal.Kind == DateTimeKind.Utc) return dkLocal;
        var unspecified = DateTime.SpecifyKind(dkLocal, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, DanishTimeZone);
    }

    // EXU_TIMEREADINGSPERIOD.SERIESRESOLUTION: 1 = quarter-hourly, anything else hourly
    // (same reading as XellentDomain's MeteringPointMapper).
    private static string ToResolutionName(int seriesResolution)
        => seriesResolution == 1 ? TimeResolution.Quarter.ToString() : TimeResolution.Hour.ToString();

    private static string MapTimeResolution(int timeResolution)
        => Enum.IsDefined(typeof(TimeResolution), timeResolution)
            ? ((TimeResolution)timeResolution).ToString()
            : "Unknown";

    private static string MapQualityOfValue(int qualityOfValue)
        => qualityOfValue == (int)QualityOfValue.MachineApprovedReading
            ? "Maskinelt indlæst, godkendt"
            : "Ukendt";
}
