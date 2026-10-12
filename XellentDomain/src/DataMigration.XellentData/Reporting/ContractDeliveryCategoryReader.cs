using Microsoft.EntityFrameworkCore;

namespace DataMigration.XellentData.Reporting;

/// <summary>
/// Which commodity a Xellent contract delivers, read from its contract parts. The contract header's
/// own <c>Deliverycategory</c> is empty on water/heat contracts, so the parts are the only source.
///
/// <para>Only parts that were in force count. A contract that carried sewage until 2018 does not
/// deliver sewage today, and reporting it as "Kloak/Vand/Varme" states something untrue about the
/// contract. Verified against production 2026-07-30: contracts 10067406 and 10067704 both hold
/// Kloak parts ended 2018-12-31 alongside open Vand and Varme parts.</para>
///
/// <para>"In force" is measured at the contract's own end date, not at today. Migrating a customer
/// to Settl ends their Xellent contract and its parts with it, so measuring at today would blank
/// the category for exactly the migrated customers a handover report is about.</para>
///
/// <para>Single source of this rule: both the domain mapper (for
/// <c>MobilePaySubscription.SourceDeliveryCategory</c>) and
/// <see cref="XellentMobilePayContractReader"/> call in here rather than writing the query twice.</para>
/// </summary>
public static class ContractDeliveryCategoryReader
{
    // AX sentinel for "no date set" — the part or contract never ends.
    private static readonly DateTime EndDateNull = new(1900, 1, 1);

    // SQL Server caps parameters at 2100; keep each IN-batch well under that.
    private const int BatchSize = 1500;

    /// <summary>
    /// Returns <c>contract number → commodities</c>, several joined with "/" when a contract really
    /// does deliver more than one (water and heat commonly share a contract). Contracts with no
    /// in-force part are absent from the dictionary rather than mapped to an empty string.
    /// </summary>
    public static async Task<Dictionary<string, string>> LoadAsync(
        XellentDbContext db,
        string dataAreaId,
        IReadOnlyCollection<string> contractNumbers,
        CancellationToken ct = default)
    {
        if (contractNumbers.Count == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

        var contracts = contractNumbers.Distinct(StringComparer.Ordinal).ToList();
        var reference = await LoadReferenceDatesAsync(db, dataAreaId, contracts, ct);

        var inForce = new List<(string Contract, string Category)>();
        foreach (var batch in Batches(contracts))
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.ExuContractPartTables
                .Where(cp => cp.Dataareaid == dataAreaId
                    && batch.Contains(cp.Contractnum)
                    && cp.Deliverycategory != "")
                .Select(cp => new { cp.Contractnum, cp.Deliverycategory, cp.Enddate })
                .Distinct()
                .ToListAsync(ct);

            foreach (var part in page)
            {
                var contract = part.Contractnum.Trim();
                // Fall back to today when the contract number is missing from the header query —
                // an orphaned part still describes a live commodity if it has not ended.
                var at = reference.TryGetValue(contract, out var date) ? date : DateTime.Today;
                if (IsInForce(part.Enddate, at))
                    inForce.Add((contract, part.Deliverycategory.Trim()));
            }
        }

        return inForce
            .GroupBy(r => r.Contract, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => string.Join("/", g.Select(r => r.Category)
                                       .Distinct(StringComparer.Ordinal)
                                       .OrderBy(c => c, StringComparer.Ordinal)),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// A part is in force when it never ends, or ends no earlier than the point we measure at.
    /// </summary>
    public static bool IsInForce(DateTime partEndDate, DateTime at) =>
        partEndDate == EndDateNull || partEndDate >= at;

    /// <summary>
    /// The date each contract is measured at: its own end date when it has ended, otherwise today.
    /// </summary>
    private static async Task<Dictionary<string, DateTime>> LoadReferenceDatesAsync(
        XellentDbContext db, string dataAreaId, List<string> contracts, CancellationToken ct)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var batch in Batches(contracts))
        {
            ct.ThrowIfCancellationRequested();
            var page = await db.ExuContractTables
                .Where(c => c.Dataareaid == dataAreaId && batch.Contains(c.Contractnum))
                .Select(c => new { c.Contractnum, c.Contractenddate })
                .ToListAsync(ct);

            foreach (var contract in page)
                result[contract.Contractnum.Trim()] = contract.Contractenddate == EndDateNull
                    ? DateTime.Today
                    : contract.Contractenddate;
        }
        return result;
    }

    private static IEnumerable<List<string>> Batches(List<string> items)
    {
        for (var offset = 0; offset < items.Count; offset += BatchSize)
            yield return items.GetRange(offset, Math.Min(BatchSize, items.Count - offset));
    }
}
