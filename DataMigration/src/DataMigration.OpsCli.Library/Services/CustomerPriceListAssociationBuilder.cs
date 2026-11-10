using DataMigration.Core;
using DataMigration.Migration.Services;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// One metering-point's contribution to the customer↔pricelist association set, projected from the
/// Xellent account→MP mapping. Decouples the builder from the full extraction tuple so it stays a
/// pure function of just the fields that drive association emission.
/// </summary>
public readonly record struct PriceListMpInput(
    string MeteringPointId,
    string ContractNum,
    string ProductNum,
    string AmrCode,
    DateTime ContractPartStartdate,
    DateTime ContractPartEnddate);

/// <summary>
/// Result of <see cref="CustomerPriceListAssociationBuilder.Build"/>: the collapsed association rows
/// plus the diagnostics the caller logs (Xellent rate codes / products that found no Settl match).
/// </summary>
public sealed record CustomerPriceListAssociationResult(
    List<CustomerPriceListAssociationRow> Rows,
    IReadOnlyList<(string AmrCode, string DgRateCode)> UnmatchedContracts,
    IReadOnlyList<(string Product, string? AmrCode, string? DgRateCode)> UnmatchedProducts,
    int GoForwardEmitted,
    int EmittedBeforeGrouping);

/// <summary>
/// Pure builder of customer↔pricelist associations from Xellent contract data — the create-true
/// "intended" set both <c>export-customer-pricelists</c> (→ Settl bulk-import Excel) and reconcile
/// (→ diff vs actual Settl) consume. Extracted from <c>ExportCustomerPriceListsRunner</c> so the
/// logic is shared and unit-testable; no I/O, every input is pre-fetched by the caller.
///
/// <para>One row per (representative customer × ProductElementCode × PriceList × contract-part
/// window). PriceList is resolved by translating the Xellent rate code (prefix "KLI " → "ST ") to a
/// Settl price-list <b>name</b> and looking that up via <paramref name="resolvePriceListId"/>; the
/// caller supplies that map (a manual CSV today, the Settl price-list catalog for reconcile).
/// Rows for the same (customer, ProductElementCode) collapse to one per price list with Start pinned
/// to <paramref name="changeOfSupplierDate"/> and the latest End winning — except where a go-forward
/// part (a future part purged from the live table, surviving only in the contract-part log) replaces
/// its predecessor, in which case that part's window wins.</para>
/// </summary>
public static class CustomerPriceListAssociationBuilder
{
    public static CustomerPriceListAssociationResult Build(
        IReadOnlyDictionary<string, List<PriceListMpInput>> mpsByRepresentative,
        IReadOnlyDictionary<string, DateTime> contractEndDates,
        IReadOnlyDictionary<(string ContractNum, string AmrCode, DateTime StartDate), string> deliveryRateCodes,
        IReadOnlyDictionary<(string ContractNum, string AmrCode, string ProductNum, DateTime StartDate), DateTime> contractPartLogEndDates,
        Func<string, string?> resolvePriceListId,
        ProductElementCodeResolver productElementResolver,
        IReadOnlySet<string> stillActiveOnCos,
        DateTime changeOfSupplierDate,
        DateTime referenceDate,
        string? env)
    {
        var dedup = new HashSet<(string Customer, string ProductElementCode, string PriceList, DateTime Start, DateTime End)>();
        var rows = new List<CustomerPriceListAssociationRow>();
        var emitted = 0;
        var unmatchedContracts = new List<(string AmrCode, string DgRateCode)>();
        var unmatchedProductKeys = new List<(string, string?, string?)>();
        var goForwardElements = new HashSet<(string CustomerNumber, string ProductElementCode)>();

        foreach (var (representative, mpList) in mpsByRepresentative)
        {
            if (stillActiveOnCos.Contains(representative))
                continue;
            var settlCustomerNumber = CustomerNumberPolicy.PadForEnv(representative, env, out _);
            foreach (var mp in mpList)
            {
                // Skip contracts still active at migration time: no end date (EndDateNull sentinel)
                // or end date after ReferenceDate (= ChangeOfSupplierDate - 1 day).
                var contractEndDate = contractEndDates.GetValueOrDefault(mp.ContractNum, MigrationConstants.EndDateNull);
                if (contractEndDate == MigrationConstants.EndDateNull || contractEndDate > referenceDate)
                    continue;

                var productNum = mp.ProductNum ?? string.Empty;
                deliveryRateCodes.TryGetValue((mp.ContractNum, mp.AmrCode, mp.ContractPartStartdate), out var dgRateCode);
                // Delivery-group rate codes ("Øst"/"Vest") are regions, not price lists.
                if (IsIgnoredDeliveryGroup(dgRateCode))
                    dgRateCode = null;
                var logKey = (mp.ContractNum, mp.AmrCode, productNum, mp.ContractPartStartdate);
                var endDate = contractPartLogEndDates.TryGetValue(logKey, out var logEndDate)
                    ? logEndDate
                    : mp.ContractPartEnddate;

                // No ProductElementCode mapping → exclude, surface in diagnostics instead.
                var productElementCode = productElementResolver.Resolve(productNum);
                if (string.IsNullOrEmpty(productElementCode))
                {
                    unmatchedProductKeys.Add((
                        productNum.Length == 0 ? "(blank)" : productNum,
                        mp.AmrCode,
                        dgRateCode));
                    continue;
                }

                // Resolve both match paths before emitting so dedup of identical ids is correct.
                string? amrPriceListId = null;
                if (!string.IsNullOrEmpty(mp.AmrCode) && resolvePriceListId(TranslateAmrcode(mp.AmrCode)) is { } amrId)
                    amrPriceListId = amrId;

                string? dgPriceListId = null;
                if (!string.IsNullOrEmpty(dgRateCode) && resolvePriceListId(TranslateAmrcode(dgRateCode)) is { } dgId)
                    dgPriceListId = dgId;

                void TryEmit(string priceListId)
                {
                    var key = (settlCustomerNumber, productElementCode, priceListId, mp.ContractPartStartdate, endDate);
                    if (!dedup.Add(key)) return;
                    rows.Add(new CustomerPriceListAssociationRow(
                        settlCustomerNumber, productElementCode, priceListId,
                        mp.ContractPartStartdate, endDate));
                    emitted++;
                }

                if (amrPriceListId != null && amrPriceListId == dgPriceListId)
                {
                    TryEmit(amrPriceListId);
                }
                else
                {
                    if (amrPriceListId != null) TryEmit(amrPriceListId);
                    if (dgPriceListId != null) TryEmit(dgPriceListId);
                }

                if (amrPriceListId == null && dgPriceListId == null && (!string.IsNullOrEmpty(mp.AmrCode) || !string.IsNullOrEmpty(dgRateCode)))
                    unmatchedContracts.Add((mp.AmrCode, dgRateCode ?? string.Empty));
            }
        }

        // --- Go-forward parts deleted at cutover (live only in EXU_CONTRACTPARTLOG) ---
        // Future contract parts (start >= COS) were purged from the live table in the cutover batch;
        // they survive only in the log. Emit price-list rows for the log-only future keys so their
        // add-ons (and correct end date) aren't lost. Same ProductElement: the go-forward part
        // replaces its predecessor (different rate code) — see the collapse below.
        var liveKeys = mpsByRepresentative.Values
            .SelectMany(list => list)
            .Select(mp => (mp.ContractNum, mp.AmrCode, mp.ProductNum ?? string.Empty, mp.ContractPartStartdate))
            .ToHashSet();
        var contractToRep = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rep, mpList) in mpsByRepresentative)
            foreach (var mp in mpList)
                if (!string.IsNullOrEmpty(mp.ContractNum))
                    contractToRep.TryAdd(mp.ContractNum, rep);

        var goForwardEmitted = 0;
        foreach (var (key, logEndDate) in contractPartLogEndDates)
        {
            var (contractNum, amrCode, productNum, startDate) = key;
            if (liveKeys.Contains(key)) continue;                       // covered by a live part
            if (startDate < changeOfSupplierDate) continue;             // only future go-forward parts
            if (!contractToRep.TryGetValue(contractNum, out var rep)) continue;  // contract not in scope
            if (stillActiveOnCos.Contains(rep)) continue;

            var goForwardProductElementCode = productElementResolver.Resolve(productNum);
            if (string.IsNullOrEmpty(goForwardProductElementCode)) continue;
            if (string.IsNullOrEmpty(amrCode) || resolvePriceListId(TranslateAmrcode(amrCode)) is not { } goForwardPriceListId)
                continue;

            var goForwardCustomerNumber = CustomerNumberPolicy.PadForEnv(rep, env, out _);
            var goForwardKey = (goForwardCustomerNumber, goForwardProductElementCode, goForwardPriceListId, startDate, logEndDate);
            if (!dedup.Add(goForwardKey)) continue;
            rows.Add(new CustomerPriceListAssociationRow(
                goForwardCustomerNumber, goForwardProductElementCode, goForwardPriceListId, startDate, logEndDate));
            goForwardElements.Add((goForwardCustomerNumber, goForwardProductElementCode));
            emitted++;
            goForwardEmitted++;
        }

        // Collapse per (customer, ProductElementCode): Settl allows one price list per product element
        // per period. Start is uniformly ChangeOfSupplierDate.
        // - Has a go-forward part (deleted future part): it replaces the predecessor → latest-ending
        //   row wins (the go-forward), so the add-on gets the go-forward window's end date.
        // - Otherwise: one row per price list, latest end date in each group.
        var ordered = rows
            .GroupBy(r => (r.CustomerNumber, r.ProductElementCode))
            .SelectMany(g =>
            {
                if (goForwardElements.Contains(g.Key))
                    return new[] { g.OrderByDescending(r => r.End).First() with { Start = changeOfSupplierDate } };
                return g.GroupBy(r => r.PriceListIdentifier)
                    .Select(pg => pg.OrderByDescending(r => r.End).First() with { Start = changeOfSupplierDate })
                    .ToArray();
            })
            .OrderBy(r => r.CustomerNumber, StringComparer.Ordinal)
            .ThenBy(r => r.ProductElementCode, StringComparer.Ordinal)
            .ThenBy(r => r.PriceListIdentifier, StringComparer.Ordinal)
            .ToList();

        return new CustomerPriceListAssociationResult(
            ordered, unmatchedContracts, unmatchedProductKeys, goForwardEmitted, emitted);
    }

    /// <summary>Xellent Amrcode prefix "KLI " maps to Settl pricelist name prefix "ST ".</summary>
    internal static string TranslateAmrcode(string amrcode) =>
        PriceListNameResolver.TranslateAmrcode(amrcode);

    /// <summary>Delivery-group rate codes naming a region ("Øst"/"Vest") are not price lists.</summary>
    internal static bool IsIgnoredDeliveryGroup(string? rateCode) =>
        !string.IsNullOrEmpty(rateCode)
        && (string.Equals(rateCode, "Øst", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rateCode, "Vest", StringComparison.OrdinalIgnoreCase));
}
