using DataMigration.Core;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

/// <summary>Settlement-only dated contract links, including pre-change states in ContractPartLog.</summary>
public static class SettlementContractPartResolver
{
    private const int BatchSize = 1500;

    public sealed record PartRow(string MeteringPoint, string Account, string ContractNumber,
        DateTime ContractStart, DateTime ContractEnd, string BillingPlan, string Template,
        string Company, string Agreement, string DeliveryCategory, string Product, string AmrCode,
        DateTime PartStart, DateTime PartEnd, int SubReferenceType, string SubReferenceId,
        string Source, DateTime? LoggedAt, long RecordId, string? DelpointNumber = null)
    {
        public string? ChangeType { get; init; }
        public string? LogEmployeeId { get; init; }
    }

    private sealed record TypePeriod(string MeteringPoint, string DelpointNumber, string Company,
        string Agreement, string Category, string Code, DateTime Start, DateTime End);

    public sealed record Result(MeteringPointAccountResolver.MpAccountResolution Resolution,
        IReadOnlyList<SettlementConfigContractPartEvidence> Evidence,
        IReadOnlySet<string> ConflictingMeteringPoints);

    public static async Task<Result> ResolveAsync(string connectionString,
        IReadOnlyDictionary<string, DateOnly> dates, string dataAreaId, CancellationToken ct = default)
    {
        if (dates.Count == 0) return Fold(dates, [], new HashSet<string>());
        var options = new DbContextOptionsBuilder<XellentDbContext>()
            .UseSqlServer(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(120); }).Options;
        await using var context = new XellentDbContext(options);
        return await ResolveAsync(context, dates, dataAreaId, ct);
    }

    public static async Task<Result> ResolveAsync(XellentDbContext context,
        IReadOnlyDictionary<string, DateOnly> dates, string dataAreaId, CancellationToken ct = default)
    {
        var rows = new List<PartRow>();
        var periods = new List<TypePeriod>();
        var typeReadFailures = new HashSet<string>(StringComparer.Ordinal);
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in dates.Keys.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            existing.UnionWith(await context.ExuDelpoints
                .Where(d => d.Dataareaid == dataAreaId && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && batch.Contains(d.Meteringpoint)).Select(d => d.Meteringpoint).Distinct().ToListAsync(ct));
            rows.AddRange(await (
                from c in context.ExuContractTables
                join p in context.ExuContractPartTables on new { c.Contractnum, c.Dataareaid, c.Companyid }
                    equals new { p.Contractnum, p.Dataareaid, p.Companyid }
                join a in context.ExuAgreementTables on new { Agreementnum = p.Instagreenum, p.Dataareaid, p.Companyid, p.Deliverycategory }
                    equals new { a.Agreementnum, a.Dataareaid, a.Companyid, a.Deliverycategory }
                join d in context.ExuDelpoints on new { Attachmentnum = a.Agreementnum, a.Dataareaid, a.Companyid, a.Deliverycategory }
                    equals new { d.Attachmentnum, d.Dataareaid, d.Companyid, d.Deliverycategory }
                where c.Dataareaid == dataAreaId && p.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity && batch.Contains(d.Meteringpoint)
                select new PartRow(d.Meteringpoint, c.Custaccount, c.Contractnum, c.Contractstartdate, c.Contractenddate,
                    c.Billingplannum, c.Contracttemplatenum, c.Companyid, p.Instagreenum, p.Deliverycategory,
                    p.Productnum, p.Amrcode, p.Startdate, p.Enddate, p.Contractpartsubreferencetype,
                    p.Contractpartsubreferenceid, "Live", null, p.Recid, d.Num)).ToListAsync(ct));
            rows.AddRange(await (
                from c in context.ExuContractTables
                join p in context.ExuContractPartLogs on new { c.Contractnum, c.Dataareaid, c.Companyid }
                    equals new { p.Contractnum, p.Dataareaid, p.Companyid }
                join a in context.ExuAgreementTables on new { Agreementnum = p.Instagreenum, p.Dataareaid, p.Companyid, p.Deliverycategory }
                    equals new { a.Agreementnum, a.Dataareaid, a.Companyid, a.Deliverycategory }
                join d in context.ExuDelpoints on new { Attachmentnum = a.Agreementnum, a.Dataareaid, a.Companyid, a.Deliverycategory }
                    equals new { d.Attachmentnum, d.Dataareaid, d.Companyid, d.Deliverycategory }
                where c.Dataareaid == dataAreaId && p.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                    && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity && batch.Contains(d.Meteringpoint)
                select new PartRow(d.Meteringpoint, c.Custaccount, c.Contractnum, c.Contractstartdate, c.Contractenddate,
                    c.Billingplannum, c.Contracttemplatenum, c.Companyid, p.Instagreenum, p.Deliverycategory,
                    p.Productnum, p.Amrcode, p.Contractpartstartdate, p.Contractpartenddate, 0,
                    "", "ContractPartLog", p.Logdatetime, p.Recid, d.Num)).ToListAsync(ct));
            try
            {
                periods.AddRange(await (
                    from d in context.ExuDelpoints
                    join t in context.EgsMeterpointTypecodeperiods
                        on new { Delpointnum = d.Num, d.Attachmentnum, d.Dataareaid, d.Companyid, d.Deliverycategory }
                        equals new { t.Delpointnum, t.Attachmentnum, t.Dataareaid, t.Companyid, t.Deliverycategory }
                    where d.Dataareaid == dataAreaId && d.Deliverycategory == MigrationConstants.DeliveryCategoryElectricity
                        && batch.Contains(d.Meteringpoint)
                    select new TypePeriod(d.Meteringpoint, t.Delpointnum, t.Companyid, t.Attachmentnum,
                        t.Deliverycategory, t.Meterpointtypecode, t.Startdate, t.Enddate)).Distinct().ToListAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { typeReadFailures.UnionWith(batch); }
        }
        var result = Fold(dates, rows, existing);
        var types = new Dictionary<string, SettlementMeterPointTypeEvidence>(StringComparer.Ordinal);
        var evidenceByMp = result.Evidence.GroupBy(e => e.MeteringPointIdentification).ToDictionary(g => g.Key,g => g.ToList());
        var rowsByMp = rows.GroupBy(r => r.MeteringPoint).ToDictionary(g => g.Key,g => g.ToList());
        var periodsByMp = periods.GroupBy(t => t.MeteringPoint).ToDictionary(g => g.Key,g => g.ToList());
        foreach (var (mp, owner) in result.Resolution.OwnerContractByMpId)
        {
            if (typeReadFailures.Contains(mp)) { types[mp] = new(null,"ReadFailed"); continue; }
            var evidence = evidenceByMp.GetValueOrDefault(mp) ?? [];
            var ownerKeys = (rowsByMp.GetValueOrDefault(mp) ?? []).Where(r => r.ContractNumber == owner.ContractNumber
                    && evidence.Any(e => e.ProductNum == r.Product && e.PartStart == r.PartStart && e.Source == r.Source
                        && e.Account == r.Account && e.Company == r.Company && e.Agreement == r.Agreement
                        && e.DeliveryCategory == r.DeliveryCategory && e.DelpointNumber == r.DelpointNumber
                        && e.PartEnd == r.PartEnd
                        && (e.LogRecordId is null || e.LogRecordId == r.RecordId)))
                .Select(r => (r.DelpointNumber, r.Company, r.Agreement, r.DeliveryCategory)).ToHashSet();
            types[mp] = SettlementMeterPointTypeEvidence.Resolve((periodsByMp.GetValueOrDefault(mp) ?? []).Where(t =>
                    ownerKeys.Contains((t.DelpointNumber,t.Company,t.Agreement,t.Category)))
                .Select(t => new SettlementMeterPointTypePeriod(t.Code,DateOnly.FromDateTime(t.Start),DateOnly.FromDateTime(t.End))),
                dates[mp].AddDays(-1));
        }
        return result with { Resolution = result.Resolution with { MeterPointTypesByMpId = types } };
    }

    // The log stores the state BEFORE a change. The first change after COS-1 therefore
    // gives that day's state. Older snapshots must not resurrect parts deleted before COS-1.
    public static (IReadOnlyList<PartRow> Rows, bool Ambiguous) SelectStates(IEnumerable<PartRow> rows, DateOnly reference)
    {
        var day = reference.ToDateTime(TimeOnly.MinValue);
        var selected = new List<PartRow>();
        var ambiguous = false;
        foreach (var part in rows.GroupBy(r => (r.ContractNumber,r.Company,r.Agreement,r.DeliveryCategory,
            Product: r.Product.ToUpperInvariant(), Amr: r.AmrCode.ToUpperInvariant(),r.PartStart)))
        {
            var live = part.Where(r => r.Source == "Live").Distinct().ToList();
            var logs = part.Where(r => r.Source == "ContractPartLog" && r.LoggedAt >= day.AddDays(1))
                .OrderBy(r => r.LoggedAt).ThenBy(r => r.RecordId).ToList();
            var collision = logs.Count > 0 && (live.Select(r => (r.SubReferenceType,r.SubReferenceId)).Distinct().Count() > 1
                || live.Count == 0 && logs.Select(r => r.PartEnd).Distinct().Count() > 1);
            ambiguous |= collision;
            IEnumerable<PartRow> states = collision ? logs.Concat(live) : logs.Count > 0 ? logs.Take(1) : live;
            selected.AddRange(collision ? states : states.Where(r => r.PartStart <= day
                && (r.PartEnd.Date == new DateTime(1900,1,1) || r.PartEnd.Date >= day)));
        }
        return (selected,ambiguous);
    }

    public static Result Fold(IReadOnlyDictionary<string, DateOnly> dates, IReadOnlyCollection<PartRow> rows,
        IReadOnlySet<string> existing)
    {
        var pairs = new List<(string MpId, string Account)>();
        var plans = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = new Dictionary<string, MeteringPointAccountResolver.OwnerContract>(StringComparer.Ordinal);
        var products = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var evidence = new List<SettlementConfigContractPartEvidence>();
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mp in rows.Where(r => dates.ContainsKey(r.MeteringPoint)).GroupBy(r => r.MeteringPoint, StringComparer.Ordinal))
        {
            var reference = dates[mp.Key].AddDays(-1);
            var day = reference.ToDateTime(TimeOnly.MinValue);
            var state = SelectStates(mp,reference);
            if (state.Ambiguous) conflicts.Add(mp.Key);
            var candidates = state.Rows.Where(r => r.ContractStart.Date <= day && r.ContractEnd.Date == day)
                .Distinct().ToList();
            evidence.AddRange(candidates.Select(r => new SettlementConfigContractPartEvidence(r.MeteringPoint,
                r.Account, r.ContractNumber, r.Product, reference, r.PartStart, r.PartEnd, r.Source,
                r.LoggedAt, r.Source == "ContractPartLog" ? r.RecordId : null,
                r.Company,r.Agreement,r.DeliveryCategory,r.DelpointNumber)));
            if (candidates.Select(r => (r.Account, r.Company, r.ContractNumber, r.BillingPlan, r.Template)).Distinct().Count() > 1)
                conflicts.Add(mp.Key);
            if (conflicts.Contains(mp.Key) || candidates.Count == 0 || string.IsNullOrWhiteSpace(candidates[0].Account)) continue;
            var owner = candidates[0];
            pairs.Add((mp.Key, owner.Account));
            plans[mp.Key] = owner.BillingPlan;
            templates[mp.Key] = owner.Template;
            owners[mp.Key] = new(owner.ContractNumber, DateOnly.FromDateTime(owner.ContractEnd));
            products[mp.Key] = candidates.Select(r => r.Product).Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new(new(pairs, existing, plans, templates, new HashSet<string>(), owners, products), evidence, conflicts);
    }
}
