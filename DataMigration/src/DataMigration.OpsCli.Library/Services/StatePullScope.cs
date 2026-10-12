using System.Text.Json;
using DataMigration.Domain.Xellent;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Services;

public sealed record StateScopeEntry(int Line, string Value)
{
    public bool IsMeteringPoint => Value.Length == 18;
}

public sealed record StatePullScope(string[] Requested, string[] MeteringPoints,
    string[] TargetAccounts, string[] SupportingAccounts)
{
    public Dictionary<string, string[]> CustomerMeteringPoints { get; init; } = [];
    public DateOnly? ReferenceDate { get; init; }

    public static StateScopeEntry[] Parse(IEnumerable<string> lines)
    {
        var result = new List<StateScopeEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var line = 0;
        foreach (var raw in lines)
        {
            line++;
            var value = raw.Trim();
            if (value.Length == 0) continue;
            if (value.Any(c => c is < '0' or > '9') || (value.Length > 6 && value.Length != 18))
                throw new ArgumentException($"Invalid scope entry at line {line}: expected a 1–6 digit customer account or 18 digit metering point.");
            if (seen.Add(value)) result.Add(new(line, value));
        }
        if (result.Count == 0) throw new ArgumentException("Scope file contains no targets.");
        return result.ToArray();
    }

    public bool SameRequest(StateScopeEntry[] entries) => Requested.Order().SequenceEqual(entries.Select(e => e.Value).Order());
    public bool SameResolution(StatePullScope other) => SameRequest(other.Requested.Select(x => new StateScopeEntry(0, x)).ToArray())
        && MeteringPoints.Order().SequenceEqual(other.MeteringPoints.Order())
        && TargetAccounts.Order().SequenceEqual(other.TargetAccounts.Order())
        && SupportingAccounts.Order().SequenceEqual(other.SupportingAccounts.Order())
        && ReferenceDate == other.ReferenceDate
        && CustomerMeteringPoints.Count == other.CustomerMeteringPoints.Count
        && CustomerMeteringPoints.All(kv => other.CustomerMeteringPoints.TryGetValue(kv.Key, out var points) && kv.Value.Order().SequenceEqual(points.Order()));

    public static async Task<StatePullScope> ResolveAsync(StateScopeEntry[] entries,
        XellentDbContext db, string dataArea, DateOnly date, IReadOnlyList<string> ignoredGroups, CancellationToken ct)
    {
        var accounts = entries.Where(e => !e.IsMeteringPoint).Select(e => e.Value).ToArray();
        var points = entries.Where(e => e.IsMeteringPoint).Select(e => e.Value).ToArray();
        var rows = new List<StateMpAccount>();
        foreach (var batch in entries.Chunk(1000))
        {
            var accountBatch = batch.Where(e => !e.IsMeteringPoint).Select(e => e.Value).ToArray();
            var pointBatch = batch.Where(e => e.IsMeteringPoint).Select(e => e.Value).ToArray();
            rows.AddRange(await (from c in db.ExuContractTables.AsNoTracking()
            join p in db.ExuContractPartTables.AsNoTracking() on new { c.Contractnum, c.Dataareaid, c.Companyid } equals new { p.Contractnum, p.Dataareaid, p.Companyid }
            join a in db.ExuAgreementTables.AsNoTracking() on new { Agreementnum = p.Instagreenum, p.Dataareaid, p.Companyid } equals new { a.Agreementnum, a.Dataareaid, a.Companyid }
            join d in db.ExuDelpoints.AsNoTracking() on new { Attachmentnum = a.Agreementnum, a.Dataareaid, a.Companyid } equals new { d.Attachmentnum, d.Dataareaid, d.Companyid }
            where c.Dataareaid == dataArea && p.Deliverycategory == DataMigration.Core.MigrationConstants.DeliveryCategoryElectricity && d.Deliverycategory == DataMigration.Core.MigrationConstants.DeliveryCategoryElectricity
                && (accountBatch.Contains(c.Custaccount) || pointBatch.Contains(d.Meteringpoint))
            select new StateMpAccount(d.Meteringpoint, c.Custaccount)).Distinct().ToListAsync(ct));
        }
        var missing = entries.Where(e => e.IsMeteringPoint ? !rows.Any(r => r.MeteringPoint == e.Value) : !rows.Any(r => r.Account == e.Value)).ToArray();
        if (missing.Length > 0) throw new ArgumentException("Unresolved scope entries at lines " + string.Join(", ", missing.Select(e => e.Line)) + ".");
        var targetAccounts = rows.Select(r => r.Account).Distinct().Order().ToArray();
        var cvrs = new HashSet<string>();
        foreach (var chunk in targetAccounts.Chunk(1000))
        {
            var batchCvrs = await db.CustTables.Where(c => c.Dataareaid == dataArea && chunk.Contains(c.Accountnum))
                .Select(c => c.ExuCvrDisponent1).Distinct().ToListAsync(ct);
            cvrs.UnionWith(batchCvrs.Where(c => !string.IsNullOrWhiteSpace(c))!);
        }
        var related = new List<string>();
        var accountCvrs = new Dictionary<string, string>();
        var accountsByCvr = new Dictionary<string, List<string>>();
        foreach (var chunk in cvrs.Chunk(1000))
        {
            var batch = await db.CustTables.Where(c => c.Dataareaid == dataArea && chunk.Contains(c.ExuCvrDisponent1))
                .Select(c => new { c.Accountnum, c.ExuCvrDisponent1 }).ToListAsync(ct);
            foreach (var c in batch)
            {
                related.Add(c.Accountnum);
                accountCvrs[c.Accountnum] = c.ExuCvrDisponent1!;
                if (!accountsByCvr.TryGetValue(c.ExuCvrDisponent1!, out var members)) accountsByCvr[c.ExuCvrDisponent1!] = members = [];
                members.Add(c.Accountnum);
            }
        }
        var map = new Dictionary<string, string>();
        foreach (var portion in CosStatePlanner.BuildCvrPortions(targetAccounts, accountCvrs, accountsByCvr, 15000))
        {
            var reps = await AccountRepresentativeResolver.ResolveAsync(db, portion.Accounts, dataArea,
                referenceDate: date, cancellationToken: ct, ignoredGroupNumbers: ignoredGroups.ToHashSet());
            foreach (var pair in reps.RepresentativeByAccount) map[pair.Key] = pair.Value;
        }
        var roots = targetAccounts.Select(a => map.GetValueOrDefault(a, a)).ToHashSet();
        var supporting = map.Where(kv => roots.Contains(kv.Value)).Select(kv => kv.Key)
            .Concat(targetAccounts).Distinct().Order().ToArray();
        return new(entries.Select(e => e.Value).Order().ToArray(), rows.Select(r => r.MeteringPoint).Distinct().Order().ToArray(), targetAccounts, supporting)
        { ReferenceDate = date, CustomerMeteringPoints = rows.GroupBy(r => r.Account).ToDictionary(g => g.Key, g => g.Select(r => r.MeteringPoint).Distinct().Order().ToArray()) };
    }
}
