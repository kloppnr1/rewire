using System.Globalization;
using System.Text;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>One parsed line of a reconcile CSV (the file <c>reconcile</c> writes), keeping only the
/// columns apply needs. Used by the <c>reconcile-apply</c> CLI to turn a reconcile result + selectors
/// into <see cref="ReconcileApplyTarget"/>s — the same shape the dashboard builds from its in-memory rows.</summary>
public sealed record ReconcileCsvRow(
    string Category,
    string RepresentativeAccount,
    string MemberAccounts,
    string Entity,
    string SubType,
    string EntityKey,
    string Field,
    string Cos,
    string Kind,
    string? IntendedJson = null);

/// <summary>
/// Parses a reconcile CSV and groups selected rows into per-representative
/// <see cref="ReconcileApplyTarget"/>s. The representative's COS (→ <c>refDate = COS-1</c> for the
/// scoped capture) is derived as the MIN date found across its rows' COS cells — an MP cell is a
/// single date, a Customer/Payer cell is "from .. to" whose <i>from</i> is the representative's min
/// COS, so the first date token of any cell yields the floor.
/// </summary>
public static class ReconcileApplySelection
{
    // Column order written by ReconcileRunner.WriteCsv.
    private const int ColCategory = 0, ColRep = 1, ColMembers = 3, ColEntity = 5,
        ColSubType = 6, ColEntityKey = 7, ColCos = 9, ColField = 10, ColKind = 13, ColIntendedJson = 14;
    private const int MinColumns = 14; // IntendedJson (col 14) is optional — absent in pre-carry CSVs.

    public static IReadOnlyList<ReconcileCsvRow> ParseCsv(string csvText)
    {
        var rows = new List<ReconcileCsvRow>();
        var records = SplitRecords(csvText);
        for (var i = 1; i < records.Count; i++) // skip header
        {
            var f = records[i];
            if (f.Count < MinColumns) continue;
            var intendedJson = f.Count > ColIntendedJson && !string.IsNullOrEmpty(f[ColIntendedJson]) ? f[ColIntendedJson] : null;
            rows.Add(new ReconcileCsvRow(
                f[ColCategory], f[ColRep], f[ColMembers], f[ColEntity],
                f[ColSubType], f[ColEntityKey], f[ColField], f[ColCos], f[ColKind], intendedJson));
        }
        return rows;
    }

    /// <summary>
    /// Groups rows by representative into apply targets. Each group's accounts come from the
    /// (identical) MemberAccounts cell; its COS is the min parsed date. A row whose representative has
    /// no parseable COS cannot be scoped and is dropped (recorded in <paramref name="droppedNoCos"/>).
    /// </summary>
    public static List<ReconcileApplyTarget> BuildTargets(
        IEnumerable<ReconcileCsvRow> rows, out int droppedNoCos)
    {
        droppedNoCos = 0;
        var targets = new List<ReconcileApplyTarget>();
        foreach (var group in rows.GroupBy(r => r.RepresentativeAccount, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(group.Key)) { droppedNoCos += group.Count(); continue; }

            DateOnly? minCos = null;
            foreach (var r in group)
                if (FirstDate(r.Cos) is { } d && (minCos == null || d < minCos))
                    minCos = d;

            if (minCos is not { } cos) { droppedNoCos += group.Count(); continue; }

            var accounts = group.Select(r => r.MemberAccounts).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "";
            var accountList = accounts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Fall back to the representative itself when MemberAccounts is blank (single-member group).
            if (accountList.Length == 0) accountList = [group.Key];

            var applyRows = group
                .Select(r => new ReconcileApplyRow(r.Entity, r.SubType, r.EntityKey, r.Field, r.Kind, r.IntendedJson))
                .ToList();

            targets.Add(new ReconcileApplyTarget(group.Key, accountList, cos, applyRows));
        }
        return targets;
    }

    /// <summary>The leading yyyy-MM-dd of a COS cell ("2026-05-01" or "2026-05-01 .. 2026-06-01"), or null.</summary>
    private static DateOnly? FirstDate(string cosCell)
    {
        if (string.IsNullOrWhiteSpace(cosCell) || cosCell.Length < 10) return null;
        return DateOnly.TryParseExact(cosCell[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Splits CSV text into records of fields, honouring double-quoted fields with "" escaping
    /// and ';' separators (the dialect ReconcileRunner.WriteCsv emits).</summary>
    private static List<List<string>> SplitRecords(string text)
    {
        var records = new List<List<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"': inQuotes = true; break;
                    case ';': fields.Add(sb.ToString()); sb.Clear(); break;
                    case '\r': break;
                    case '\n':
                        fields.Add(sb.ToString()); sb.Clear();
                        records.Add(fields); fields = [];
                        break;
                    default: sb.Append(c); break;
                }
            }
        }
        if (sb.Length > 0 || fields.Count > 0) { fields.Add(sb.ToString()); records.Add(fields); }
        return records;
    }
}
