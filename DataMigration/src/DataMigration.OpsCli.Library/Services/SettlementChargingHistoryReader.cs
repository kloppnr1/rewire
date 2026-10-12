using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.OpsCli.Services.ContractPartHistoryRestoration;

namespace DataMigration.OpsCli.Services;

/// <summary>Settlement-only Charge_Abo history for the selected electricity accounts and COS-1 dates.</summary>
public static class SettlementChargingHistoryReader
{
    public sealed record Result(IReadOnlyDictionary<(string Account,DateOnly Date),IReadOnlyList<string>> Products,
        IReadOnlyList<SettlementConfigChargingPartEvidence> Evidence);

    public static async Task<Result> ReadAsync(string connectionString,
        IReadOnlyCollection<(string Account,DateOnly Date)> requests,string dataAreaId,CancellationToken ct = default)
    {
        if (requests.Count == 0) return new(new Dictionary<(string,DateOnly),IReadOnlyList<string>>(),[]);
        var options = new DbContextOptionsBuilder<XellentDbContext>().UseSqlServer(connectionString,
            sql => { sql.UseCompatibilityLevel(110); sql.CommandTimeout(120); }).Options;
        await using var context = new XellentDbContext(options);
        return await ReadAsync(context,requests,dataAreaId,ct);
    }

    public static async Task<Result> ReadAsync(XellentDbContext context,
        IReadOnlyCollection<(string Account,DateOnly Date)> requests,string dataAreaId,CancellationToken ct = default)
    {
        var rows = new List<SettlementContractPartResolver.PartRow>();
        foreach (var batch in requests.Select(r => r.Account).Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            rows.AddRange(await (
                from c in context.ExuContractTables
                // Charge_Abo header and part company IDs can differ; preserve the existing source join.
                join p in context.ExuContractPartTables on new { c.Contractnum,c.Dataareaid }
                    equals new { p.Contractnum,p.Dataareaid }
                where c.Dataareaid == dataAreaId && batch.Contains(c.Custaccount) && p.Deliverycategory == "Charge_Abo"
                select new SettlementContractPartResolver.PartRow("",c.Custaccount,c.Contractnum,c.Contractstartdate,
                    c.Contractenddate,c.Billingplannum,c.Contracttemplatenum,p.Companyid,p.Instagreenum,p.Deliverycategory,
                    p.Productnum,p.Amrcode,p.Startdate,p.Enddate,p.Contractpartsubreferencetype,p.Contractpartsubreferenceid,
                    "Live",null,p.Recid,null)).ToListAsync(ct));
            rows.AddRange(await (
                from c in context.ExuContractTables
                join p in context.ExuContractPartLogs on new { c.Contractnum,c.Dataareaid }
                    equals new { p.Contractnum,p.Dataareaid }
                where c.Dataareaid == dataAreaId && batch.Contains(c.Custaccount) && p.Deliverycategory == "Charge_Abo"
                select new SettlementContractPartResolver.PartRow("",c.Custaccount,c.Contractnum,c.Contractstartdate,
                    c.Contractenddate,c.Billingplannum,c.Contracttemplatenum,p.Companyid,p.Instagreenum,p.Deliverycategory,
                    p.Productnum,p.Amrcode,p.Contractpartstartdate,p.Contractpartenddate,0,"",
                    "ContractPartLog",p.Logdatetime,p.Recid,null) { ChangeType = p.Errormsg,LogEmployeeId = p.Logemplid }).ToListAsync(ct));
        }
        return Fold(requests,rows);
    }

    public static Result Fold(IReadOnlyCollection<(string Account,DateOnly Date)> requests,
        IReadOnlyCollection<SettlementContractPartResolver.PartRow> rows)
    {
        var products = new Dictionary<(string,DateOnly),IReadOnlyList<string>>();
        var evidence = new List<SettlementConfigChargingPartEvidence>();
        var byAccount = rows.Where(r => r.DeliveryCategory == "Charge_Abo").GroupBy(r => r.Account,StringComparer.Ordinal)
            .ToDictionary(g => g.Key,g => g.Distinct().ToArray());
        foreach (var request in requests.Distinct())
        {
            var day = request.Date.ToDateTime(TimeOnly.MinValue);
            var cutoff = day.AddDays(1);
            var activeProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ambiguous = false;
            foreach (var part in (byAccount.GetValueOrDefault(request.Account) ?? []).GroupBy(Key))
            {
                var live = part.Where(r => r.Source == "Live").ToArray();
                var logs = part.Where(r => r.Source == "ContractPartLog").ToArray();
                bool RelevantLog(SettlementContractPartResolver.PartRow r) => r.LoggedAt is null || r.LoggedAt >= cutoff
                    || IsChargingMigrationChange(r.LoggedAt,r.LogEmployeeId);
                var relevantLogs = logs.Where(RelevantLog).ToArray();
                // A known end/deletion log cannot move a future start back into COS-1.
                // Missing change types and start changes cannot establish that exclusion safely.
                var future = part.All(r => r.PartStart.Date > day)
                    && relevantLogs.All(r => !string.IsNullOrWhiteSpace(r.ChangeType) && r.ChangeType != StartChangedChangeType);
                var restored = RestoreParts(live,logs,Key,r => r.ChangeType,r => r.LoggedAt,
                    r => (long?)r.RecordId,r => r.PartEnd,r => r.PartStart,(r,end) => r with { PartEnd = end },
                    r => IsChargingMigrationChange(r.LoggedAt,r.LogEmployeeId),cutoff);
                var affectsDate = !future && (live.Concat(relevantLogs).Any(r => Active(r,day) || InvalidPeriod(r))
                    || relevantLogs.Any(r => r.ChangeType == StartChangedChangeType));
                string? problem = null;
                if (affectsDate)
                {
                    if (relevantLogs.Any(r => string.IsNullOrWhiteSpace(r.ChangeType) || r.LoggedAt is null || r.RecordId <= 0
                        || IsChargingMigrationWindow(r.LoggedAt) && r.LogEmployeeId is null))
                        problem = "Missing log change type, employee or ordering evidence";
                    else if (relevantLogs.GroupBy(r => (r.LoggedAt,r.RecordId)).Any(g => g.Distinct().Count() > 1)
                        || live.Length == 0 && relevantLogs.Count(r => IsChargingMigrationChange(r.LoggedAt,r.LogEmployeeId)
                            && r.ChangeType == DeletedPartChangeType) > 1)
                        problem = "Conflicting ordered contract-part log records";
                    else if (restored.Ambiguous.Contains(part.Key)) problem = "Ambiguous contract-part restoration";
                    else if (live.Length == 0 && relevantLogs.Length > 0 && restored.Parts.Count == 0)
                        problem = "No supported restoration for a date-relevant logged-only part";
                    else if (restored.Parts.Any(InvalidPeriod)) problem = "Invalid restored contract-part period";
                }
                var partAmbiguous = problem is not null;
                ambiguous |= partAmbiguous;
                var active = !future && !partAmbiguous ? restored.Parts.Where(r => Active(r,day)).ToArray() : [];
                foreach (var r in active.Where(r => !string.IsNullOrWhiteSpace(r.Product))) activeProducts.Add(r.Product);
                restored.Evidence.TryGetValue(part.Key,out var endEvidence);
                var status = future ? "ExcludedFuture" : partAmbiguous ? "Uncertain" : active.Length == 0 ? "NotActive"
                    : live.Length == 0 ? "RestoredDeletion" : endEvidence?.Source == "ContractPartLog" ? "RestoredEnd" : "Live";
                foreach (var r in part.OrderBy(r => r.Source == "Live" ? 0 : 1).ThenBy(r => r.LoggedAt).ThenBy(r => r.RecordId))
                    evidence.Add(new(request.Account,request.Date,r.ContractNumber,r.Company,r.Agreement,r.Product,
                        r.PartStart,r.PartEnd,r.Source,r.LoggedAt,r.Source == "ContractPartLog" ? r.RecordId : null,partAmbiguous,
                        r.ChangeType,r.LogEmployeeId,status,restored.Parts.FirstOrDefault()?.PartEnd,
                        endEvidence?.Source == "ContractPartLog" ? endEvidence.RecordId : null,endEvidence?.LoggedAt,
                        active.Any(a => a.Source == r.Source && a.RecordId == r.RecordId),problem));
            }
            // Omitted keys are failed histories; successful empty reads have an explicit empty list.
            if (!ambiguous) products[request] = activeProducts.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return new(products,evidence);
    }

    private readonly record struct PartKey(string Account,string Contract,string Company,string Agreement,
        string Product,string Amr,DateTime Start);
    private static PartKey Key(SettlementContractPartResolver.PartRow r) => new(r.Account,r.ContractNumber,
        r.Company.Trim().ToUpperInvariant(),r.Agreement,r.Product.ToUpperInvariant(),r.AmrCode.ToUpperInvariant(),r.PartStart);
    private static bool OpenEnd(DateTime end) => end.Year is 1900 or >= 9999;
    private static bool Active(SettlementContractPartResolver.PartRow r,DateTime day) =>
        r.PartStart.Date <= day && (OpenEnd(r.PartEnd) || r.PartEnd.Date >= day);
    private static bool InvalidPeriod(SettlementContractPartResolver.PartRow r) => !OpenEnd(r.PartEnd) && r.PartEnd < r.PartStart;
}
