using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Maps Settl metering-point identifications back to their owning Xellent customer account
/// (<c>ExuContractTable.Custaccount</c>) via the delpoint→agreement→contractpart→contract join.
/// This is the env-wide reconcile's missing link: the rest of the extraction stack is
/// account-keyed (it loads Xellent from an account list), but env-wide reconcile starts from the
/// Settl MP set and has no profile account list — so it must resolve accounts from MP ids first,
/// with a single raw query, before the per-COS domain load.
///
/// <para><b>COS-scoped:</b> an MP identification can move between accounts over time, so a date-
/// agnostic join would map an MP to every account it ever touched. Each MP is therefore pinned to
/// the account whose contract ends on <c>COS-1</c> — the day the prior supplier's Xellent contract
/// ends, i.e. exactly the account that was migrated at that change-of-supplier. The COS comes from
/// Settl (per MP); the date match is applied in memory per MP (each MP carries its own COS, so a
/// single SQL date predicate cannot serve the batch). Scoped to el-ekstern, the only delpoint-based
/// category — consistent with the rest of reconcile.</para>
/// </summary>
public static class MeteringPointAccountResolver
{
    private const string DeliveryCategory = MigrationConstants.DeliveryCategoryElectricity;

    // SQL Server caps parameters at 2100; keep each IN-batch well under that.
    //
    // NOT the cause of "the query processor ran out of internal resources and could not produce a
    // query plan". That failure was blamed on this constant first (2026-09-02) and it was wrong:
    // 1500 inlined values plan comfortably, and lowering it only makes the run slower. The
    // unplannable query is AccountRepresentativeResolver's, with the whole population's account
    // list in one IN clause. See docs/representative-resolver-scale-limit.md.
    private const int BatchSize = 1500;

    /// <summary>
    /// Resolution result: the COS-1-matched (MP, account) <see cref="Pairs"/>, plus
    /// <see cref="XellentMpIds"/> — every requested MP id that has <i>any</i> el-ekstern delpoint
    /// row in Xellent (regardless of contract/date). The two sets answer different questions: Pairs
    /// is "which account owns this MP at change-of-supplier"; XellentMpIds is "does this MP exist in
    /// Xellent at all". A Settl InDelivery MP absent from XellentMpIds was created directly in Settl
    /// (no Xellent origin) — distinct from one that exists in Xellent but the create-true extract
    /// could not reproduce (contract deleted/changed, or no contract ending at COS-1).
    /// </summary>
    /// <param name="BillingPlanByMpId">The COS-1-matched contract's <c>Billingplannum</c> per MP id —
    /// the Settlement Schedule the MP was migrated on. Same pinning as <see cref="Pairs"/> (and only
    /// populated for MPs that appear there), so schedule and owning account always come from one and
    /// the same contract. Used by <c>export-settlement-config</c>.</param>
    /// <param name="ContractTemplateByMpId">The COS-1-matched contract's <c>Contracttemplatenum</c>
    /// per MP id — the Xellent contract template ("skabelon") the MP was migrated on, which maps to a
    /// Settl Customer Segment. Same pinning and same contract row as <c>BillingPlanByMpId</c>, so
    /// schedule, segment and owning account can never come from different contracts.</param>
    /// <param name="FallbackResolvedMpIds">MP ids whose account came from the no-history fallback
    /// rather than a COS-1 match — the MP has no change-of-supplier event, so the account was
    /// inferred from whichever contract is open today. That is an inference, not a fact, and
    /// callers that put the result in front of a human should say so.</param>
    /// <param name="OwnerContractByMpId">The contract that decided the account, per MP id: its
    /// number and its <c>Contractenddate</c>. Same pinning and same row as <see cref="Pairs"/>, so
    /// the contract shown can never be a different one from the contract that picked the account.
    /// Reported so a human can check the pinning instead of taking it on trust — for a COS-1 match
    /// the end date is the MP's COS minus one day, and any other value means the row came from the
    /// fallback.</param>
    /// <param name="ProductNumsByMpId">The <c>Productnum</c>s on the owning contract's parts that were
    /// active on the same date the contract was pinned to, per MP id. A list, not a single value:
    /// <c>Productnum</c> is part of the contract-part key, so one contract carries several parts
    /// (fixed fee, energy, …) at once. Date-filtered because parts have their own validity, and an
    /// unfiltered list would report products that were never in delivery at cutover. Used by
    /// <c>export-settlement-config</c> for the template+product segment rule.</param>
    public sealed record MpAccountResolution(
        IReadOnlyList<(string MpId, string Account)> Pairs,
        IReadOnlySet<string> XellentMpIds,
        IReadOnlyDictionary<string, string> BillingPlanByMpId,
        IReadOnlyDictionary<string, string> ContractTemplateByMpId,
        IReadOnlySet<string> FallbackResolvedMpIds,
        IReadOnlyDictionary<string, OwnerContract> OwnerContractByMpId,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? ProductNumsByMpId = null,
        IReadOnlyDictionary<string, SettlementMeterPointTypeEvidence>? MeterPointTypesByMpId = null);

    /// <summary>The contract a metering point's account was resolved from.</summary>
    public sealed record OwnerContract(string ContractNumber, DateOnly EndDate);

    /// <summary>
    /// For each requested MP id resolves its owning account at change-of-supplier — the contract
    /// whose <c>Contractenddate</c> is the MP's <c>COS - 1</c> — and, independently, whether the MP
    /// exists in Xellent's el-ekstern delpoint table at all. <paramref name="cosByMpId"/> maps each
    /// MP id to its Settl COS date. <paramref name="companyId"/> optionally limits both tests to
    /// one Xellent company (FOR for invoice export). <paramref name="includeBlankCompany"/>
    /// includes older contracts whose company was not populated. See <see cref="MpAccountResolution"/>.
    /// </summary>
    public static async Task<MpAccountResolution> ResolveAsync(
        string connectionString,
        IReadOnlyDictionary<string, DateOnly> cosByMpId,
        string dataAreaId,
        IReadOnlySet<string>? noHistoryMpIds = null,
        CancellationToken ct = default,
        string? companyId = null,
        bool includeBlankCompany = false,
        bool includeMeterPointTypes = false)
    {
        var distinctIds = cosByMpId.Keys
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinctIds.Count == 0)
            return new MpAccountResolution(
                [],
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, OwnerContract>(StringComparer.Ordinal));

        var contextOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(contextOptions);

        var pairs = new List<(string, string)>();
        var xellentMpIds = new HashSet<string>(StringComparer.Ordinal);
        var billingPlanByMpId = new Dictionary<string, string>(StringComparer.Ordinal);
        var contractTemplateByMpId = new Dictionary<string, string>(StringComparer.Ordinal);
        var fallbackResolvedMpIds = new HashSet<string>(StringComparer.Ordinal);
        var ownerContractByMpId = new Dictionary<string, OwnerContract>(StringComparer.Ordinal);
        var productNumsByMpId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var meterPointTypesByMpId = new Dictionary<string, SettlementMeterPointTypeEvidence>(StringComparer.Ordinal);
        for (var offset = 0; offset < distinctIds.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = distinctIds.GetRange(offset, Math.Min(BatchSize, distinctIds.Count - offset));

            // Existence: which of the batch's MP ids have ANY el-ekstern delpoint row in Xellent —
            // independent of contract attachment or date. This is the "has Xellent origin" test; MP
            // ids absent from it are Settl-native (created directly in Settl).
            var existing = await context.ExuDelpoints
                .Where(d => d.Dataareaid == dataAreaId
                    && d.Deliverycategory == DeliveryCategory
                    && (companyId == null || d.Companyid == companyId
                        || (includeBlankCompany && d.Companyid == ""))
                    && batch.Contains(d.Meteringpoint))
                .Select(d => d.Meteringpoint)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in existing) xellentMpIds.Add(id);

            // Fetch all (MP, account, contract-end) candidates for the batch; pin to the COS-1
            // contract per MP in memory (each MP has its own COS, so the date filter is per-row).
            var rows = await (
                from c in context.ExuContractTables
                join cp in context.ExuContractPartTables
                    on new { c.Contractnum, c.Dataareaid, c.Companyid }
                    equals new { cp.Contractnum, cp.Dataareaid, cp.Companyid }
                join ag in context.ExuAgreementTables
                    on new { Agreementnum = cp.Instagreenum, cp.Dataareaid, cp.Companyid }
                    equals new { ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                join d in context.ExuDelpoints
                    on new { Attachmentnum = ag.Agreementnum, ag.Dataareaid, ag.Companyid }
                    equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
                where c.Dataareaid == dataAreaId
                    && (companyId == null || c.Companyid == companyId
                        || (includeBlankCompany && c.Companyid == ""))
                    && d.Deliverycategory == DeliveryCategory
                    && batch.Contains(d.Meteringpoint)
                select new { d.Meteringpoint, Delpointnum = d.Num, d.Attachmentnum, d.Dataareaid, d.Companyid, d.Deliverycategory, c.Custaccount, c.Contractnum, c.Contractstartdate, c.Contractenddate, c.Billingplannum, c.Contracttemplatenum, cp.Productnum, PartStart = cp.Startdate, PartEnd = cp.Enddate }
            ).Distinct().ToListAsync(ct);

            // One lookup instead of a scan per MP: the projection now fans out over contract parts,
            // so `rows` is several times longer than before and the fallback loop below would be
            // O(MPs x rows) over a 1500-id batch — a cost every consumer of this resolver pays.
            var rowsByMpId = rows
                .GroupBy(r => r.Meteringpoint, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            var batchPaired = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                // Owning account = the contract ending the day before delivery starts (COS - 1).
                var expectedEnd = cosByMpId[r.Meteringpoint].AddDays(-1);
                if (DateOnly.FromDateTime(r.Contractenddate) == expectedEnd)
                {
                    pairs.Add((r.Meteringpoint, r.Custaccount));
                    batchPaired.Add(r.Meteringpoint);
                    ownerContractByMpId.TryAdd(
                        r.Meteringpoint,
                        new OwnerContract(r.Contractnum, DateOnly.FromDateTime(r.Contractenddate)));
                    // TryAdd stays correct with Productnum in the projection: the contract-level
                    // values repeat identically across the contract's product rows.
                    if (!string.IsNullOrEmpty(r.Billingplannum))
                        billingPlanByMpId.TryAdd(r.Meteringpoint, r.Billingplannum);
                    if (!string.IsNullOrEmpty(r.Contracttemplatenum))
                        contractTemplateByMpId.TryAdd(r.Meteringpoint, r.Contracttemplatenum);

                    // Products are collected rather than first-wins — a contract has several at once
                    // — but ONLY from the contract that won the account, which is the one recorded in
                    // ownerContractByMpId. Two contracts can end on the same COS-1 date; pooling both
                    // sets of products under the first contract's template would hand the segment rule
                    // template+product pairs that exist on no contract at all. The fallback branch
                    // below anchors the same way.
                    if (ownerContractByMpId.TryGetValue(r.Meteringpoint, out var owner)
                        && string.Equals(owner.ContractNumber, r.Contractnum, StringComparison.Ordinal)
                        && IsPartActiveOn(r.PartStart, r.PartEnd, expectedEnd.ToDateTime(TimeOnly.MinValue)))
                    {
                        AddProduct(productNumsByMpId, r.Meteringpoint, r.Productnum);
                    }
                }
            }

            // Fallback for no-history MPs (never in delivery, COS=today): COS-1=yesterday won't match
            // because the MP has no change-of-supplier event. Instead find the contract valid as of
            // today: Contractstartdate <= today AND (Contractenddate = EndDateNull OR >= today).
            if (noHistoryMpIds is { Count: > 0 })
            {
                var endDateNull = new DateTime(1900, 1, 1);
                var today = DateTime.Today;
                foreach (var mpId in batch.Where(id => noHistoryMpIds.Contains(id) && !batchPaired.Contains(id)))
                {
                    // Ordered, not FirstOrDefault over an unordered result: an MP with several open
                    // contracts otherwise resolved to whichever row the database happened to return
                    // first, so two runs over the same data could name different accounts. Newest
                    // contract wins; contract number breaks a tie.
                    var open = (rowsByMpId.TryGetValue(mpId, out var mpRows) ? mpRows : [])
                        .Where(r => r.Contractstartdate <= today
                            && (r.Contractenddate == endDateNull || r.Contractenddate >= today))
                        .OrderByDescending(r => r.Contractstartdate)
                        .ThenBy(r => r.Custaccount, StringComparer.Ordinal)
                        .ThenBy(r => r.Contractnum, StringComparer.Ordinal)
                        .ToList();
                    var best = open.FirstOrDefault();
                    if (best is not null)
                    {
                        pairs.Add((mpId, best.Custaccount));
                        fallbackResolvedMpIds.Add(mpId);
                        ownerContractByMpId.TryAdd(
                            mpId,
                            new OwnerContract(best.Contractnum, DateOnly.FromDateTime(best.Contractenddate)));
                        if (!string.IsNullOrEmpty(best.Billingplannum))
                            billingPlanByMpId.TryAdd(mpId, best.Billingplannum);
                        if (!string.IsNullOrEmpty(best.Contracttemplatenum))
                            contractTemplateByMpId.TryAdd(mpId, best.Contracttemplatenum);
                        // All parts of the chosen contract, not just the row that won the ordering —
                        // the winner is one product row out of several on the same contract.
                        foreach (var part in open.Where(r =>
                            string.Equals(r.Contractnum, best.Contractnum, StringComparison.Ordinal)
                            && IsPartActiveOn(r.PartStart, r.PartEnd, today)))
                        {
                            AddProduct(productNumsByMpId, mpId, part.Productnum);
                        }
                    }
                }
            }
            if (includeMeterPointTypes)
            {
                try
                {
                    // Periods are joined by the complete delpoint key, then matched only to the
                    // chosen owning contract's delpoint. A later tenant's open period is no fallback.
                    var periods = await (
                        from d in context.ExuDelpoints
                        join t in context.EgsMeterpointTypecodeperiods
                            on new { Delpointnum = d.Num, d.Attachmentnum, d.Dataareaid, d.Companyid, d.Deliverycategory }
                            equals new { t.Delpointnum, t.Attachmentnum, t.Dataareaid, t.Companyid, t.Deliverycategory }
                        where d.Dataareaid == dataAreaId && d.Deliverycategory == DeliveryCategory
                            && batch.Contains(d.Meteringpoint)
                            && (companyId == null || d.Companyid == companyId || (includeBlankCompany && d.Companyid == ""))
                        select new { d.Meteringpoint, t.Delpointnum, t.Attachmentnum, t.Dataareaid, t.Companyid,
                            t.Deliverycategory, t.Startdate, t.Enddate, t.Meterpointtypecode }
                    ).Distinct().ToListAsync(ct);
                    foreach (var mp in batch.Where(ownerContractByMpId.ContainsKey))
                    {
                        var owner = ownerContractByMpId[mp];
                        var ownerKeys = rowsByMpId[mp].Where(r => r.Contractnum == owner.ContractNumber)
                            .Select(r => (r.Delpointnum, r.Attachmentnum, r.Dataareaid, r.Companyid, r.Deliverycategory)).ToHashSet();
                        var on = cosByMpId[mp].AddDays(-1);
                        meterPointTypesByMpId[mp] = SettlementMeterPointTypeEvidence.Resolve(
                            periods.Where(t => t.Meteringpoint == mp && ownerKeys.Contains(
                                (t.Delpointnum, t.Attachmentnum, t.Dataareaid, t.Companyid, t.Deliverycategory)))
                                .Select(t => new SettlementMeterPointTypePeriod(t.Meterpointtypecode,
                                    DateOnly.FromDateTime(t.Startdate), DateOnly.FromDateTime(t.Enddate))), on);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    foreach (var mp in batch.Where(ownerContractByMpId.ContainsKey))
                        meterPointTypesByMpId[mp] = new(null, "ReadFailed");
                }
            }
        }

        return new MpAccountResolution(
            pairs.Distinct().ToList(), xellentMpIds, billingPlanByMpId, contractTemplateByMpId,
            fallbackResolvedMpIds, ownerContractByMpId,
            productNumsByMpId.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value.ToList(),
                StringComparer.Ordinal), includeMeterPointTypes ? meterPointTypesByMpId : null);
    }

    /// <summary>
    /// Whether a contract part was in force on <paramref name="on"/>. Parts carry their own validity
    /// inside the contract's, so a product can have been replaced long before the contract ended —
    /// reporting it would name a product that was not in delivery at the date being asked about.
    /// <c>Enddate</c> uses Xellent's 1900-01-01 sentinel for "open", the same convention as the
    /// fallback branch above.
    /// </summary>
    private static bool IsPartActiveOn(DateTime start, DateTime end, DateTime on) =>
        start <= on && (end == new DateTime(1900, 1, 1) || end >= on);

    private static void AddProduct(
        Dictionary<string, SortedSet<string>> productsByMpId, string mpId, string? productNum)
    {
        if (string.IsNullOrWhiteSpace(productNum)) return;
        // Sorted + case-insensitive: Xellent spells the same product with varying casing, and the
        // output is read by a human comparing rows across customers.
        if (!productsByMpId.TryGetValue(mpId, out var products))
            productsByMpId[mpId] = products = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        products.Add(productNum);
    }

    /// <summary>
    /// Resolves owning Xellent accounts for water/installation-based MPs whose identification
    /// follows the <c>V{Instnum}01</c> pattern synthesised by the create pipeline. Unlike the
    /// electricity resolver, there is no COS-pinned contract-end date — water contracts do not
    /// switch suppliers. The most recent contract part per installation is used (highest Startdate),
    /// and its Startdate becomes the COS used for the Xellent domain load.
    /// </summary>
    public static async Task<IReadOnlyList<(string MpId, string Account, DateOnly Cos, string DeliveryCategory)>> ResolveWaterAsync(
        string connectionString,
        IReadOnlyCollection<string> waterMpIds,
        string dataAreaId,
        CancellationToken ct = default)
    {
        // Extract Instnum from "V{Instnum}01": strip leading 'V' (1 char) and trailing '01' (2 chars).
        var instNumByMpId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mpId in waterMpIds)
        {
            if (mpId.Length >= 4
                && mpId.StartsWith("V", StringComparison.OrdinalIgnoreCase)
                && mpId.EndsWith("01", StringComparison.Ordinal))
            {
                instNumByMpId[mpId] = mpId[1..^2];
            }
        }

        if (instNumByMpId.Count == 0)
            return [];

        var distinctInstNums = instNumByMpId.Values.Distinct(StringComparer.Ordinal).ToList();

        var contextOptions = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); })
            .Options;
        await using var context = new XellentDbContext(contextOptions);

        var result = new List<(string MpId, string Account, DateOnly Cos, string DeliveryCategory)>();

        for (var offset = 0; offset < distinctInstNums.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = distinctInstNums.GetRange(offset, Math.Min(BatchSize, distinctInstNums.Count - offset));

            var rows = await (
                from c in context.ExuContractTables
                join cp in context.ExuContractPartTables
                    on new { c.Contractnum, c.Dataareaid }
                    equals new { cp.Contractnum, cp.Dataareaid }
                where cp.Dataareaid == dataAreaId
                    && batch.Contains(cp.Instagreenum)
                select new { Instnum = cp.Instagreenum, c.Custaccount, cp.Startdate, cp.Deliverycategory }
            ).Distinct().ToListAsync(ct);

            // Per installation: pick the most recent (highest Startdate) contract part — that is the
            // currently active contract. Its Startdate becomes the reconcile COS reference date.
            var byInstnum = rows
                .GroupBy(r => r.Instnum, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(r => r.Startdate).First(),
                    StringComparer.Ordinal);

            foreach (var (mpId, instnum) in instNumByMpId.Where(kv => batch.Contains(kv.Value)))
            {
                if (!byInstnum.TryGetValue(instnum, out var row)) continue;
                result.Add((mpId, row.Custaccount, DateOnly.FromDateTime(row.Startdate), row.Deliverycategory));
            }
        }

        return result;
    }
}
