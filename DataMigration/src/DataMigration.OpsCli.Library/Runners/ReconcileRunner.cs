using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;
using DataMigration.SettlClient;

namespace DataMigration.OpsCli.Runners;

/// <summary>
/// Read-only reconcile: rebuilds the <b>create-true intended payload</b> for a profile (via the
/// shared <see cref="CreateRunner"/> capture-mode) and diffs it 1:1 against actual Settl state,
/// grouped per representative account.
///
/// <para>Settl InDelivery MPs are the source-of-truth for <i>which</i> MPs and <i>when</i> (COS):
/// the runner does one Xellent load per distinct COS date at <c>refDate = COS-1</c> (merge is
/// date-independent, so representatives are stable across slices), keeps each slice's MPs, and
/// unions the slices into one create-true extract. Because the intended payload comes from the
/// exact same prep + builders as <c>create</c> (only CapturePlanAsync instead of MigrateAsync),
/// it cannot drift from what <c>create</c> would POST.</para>
///
/// <para>Coverage: customer/payer/billing are diffed property-for-property via
/// <see cref="JsonValueDiff"/>; MP master-data is diffed against the (richer) market-v3 list DTO
/// via an explicit projector; exemptions + customer/contract→MP link presence are checked; payer→MP
/// links are checked for presence AND correctness (an MP linked to a different payer than intended is
/// flagged FieldDiff, an extra Settl link MissingInXellent);
/// per-customer Settlement Group membership is checked against the same expected group(s) create
/// assigns (<see cref="SettlementGroupExpectationResolver"/>) — a missing membership is flagged
/// MissingInSettl (create would fix it on re-run), an extra membership (rep in a managed group its
/// contracts don't justify) is flagged Anomaly (create never removes — manual edit / stale state);
/// anomalies (&gt;1 contract per MP, Settl MP with no Xellent match, Xellent MP missing
/// in Settl) are flagged. An unmatched Settl MP is split by InDelivery status and Xellent presence:
/// InDelivery+inXellent → settl-indelivery-no-xellent-match; InDelivery+absent → settl-native-meteringpoint;
/// NotInDelivery+inXellent → settl-notindelivery-no-xellent-match; NotInDelivery+absent → settl-native-meteringpoint.
/// Deep MP master sections
/// beyond the projected fields are not covered by
/// the current Settl GET shape — logged, never silently claimed.</para>
/// </summary>
public sealed class ReconcileRunner
{
    private readonly SettlApiOptions _apiOptions;
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlPayerClient _payerClient;
    private readonly ISettlMeteringPointClient _mpClient;
    private readonly ISettlContractClient _contractClient;
    private readonly ISettlSettlementGroupClient _settlementGroupClient;
    private readonly string _connectionString;
    private readonly BillingExemptionConfig _exemption;
    // Kundegrupper der aldrig må merge. Kendsgerning om dataområdet, ikke om profilen — uden den
    // merger reconcile konti som create holdt adskilt, og rapporterer forskellen som drift.
    private readonly IReadOnlyList<string> _baseIgnoredGroups;
    private readonly Action<string> _log;
    // Optional price-list reconcile: when both are supplied, the scan also diffs each customer's
    // Settl price-list associations against the create-true intended set. Null client → skipped
    // (keeps the constructor backward-compatible for hosts that haven't wired it yet).
    private readonly ISettlPriceListClient? _priceListClient;
    private readonly IReadOnlyList<ProductElementMappingRule> _productElementMappings;

    // Mirrors ReconcileApplier.SerializerOptions so a carried IntendedJson round-trips back to the
    // same DTO the applier writes (every field present incl. nulls).
    private static readonly JsonSerializerOptions IntendedJsonOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public ReconcileRunner(
        SettlApiOptions apiOptions,
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient mpClient,
        ISettlContractClient contractClient,
        ISettlSettlementGroupClient settlementGroupClient,
        string connectionString,
        BillingExemptionConfig exemption,
        Action<string>? log = null,
        ISettlPriceListClient? priceListClient = null,
        IReadOnlyList<ProductElementMappingRule>? productElementMappings = null,
        IReadOnlyList<string>? baseIgnoredGroups = null)
    {
        _apiOptions = apiOptions;
        _customerClient = customerClient;
        _payerClient = payerClient;
        _mpClient = mpClient;
        _contractClient = contractClient;
        _settlementGroupClient = settlementGroupClient;
        _connectionString = connectionString;
        _exemption = exemption;
        _baseIgnoredGroups = baseIgnoredGroups ?? [];
        _log = log ?? Console.Error.WriteLine;
        _priceListClient = priceListClient;
        _productElementMappings = productElementMappings ?? [];
    }

    /// <summary>Optional progress callback for the slow Settl MP-pagination phase: (done, total) MPs.</summary>
    public Action<int, int>? OnProgress { get; set; }

    /// <summary>Max concurrent per-representative diffs (independent read-only Settl GETs).</summary>
    private const int DiffConcurrency = 12;

    /// <summary>Max concurrent per-COS Xellent loads + create-true captures (step 5). Each slice is a
    /// heavy DB load against the Xellent server (not a light Settl GET); historically kept below
    /// <see cref="DiffConcurrency"/>, now raised to the same level to speed up the scan.</summary>
    private const int SliceLoadConcurrency = 12;

    /// <summary>Danish wall-clock zone. Settl returns delivery-period starts in UTC (e.g. Danish
    /// midnight 2026-05-01 as "2026-04-30T22:00:00Z"); the COS must be the Danish calendar date, so
    /// the UTC instant is converted here before taking the date — otherwise COS is one day early.</summary>
    private static readonly TimeZoneInfo DkTz = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    /// <summary>
    /// CLI entry: run the env-wide reconcile scan and write the result as CSV. Thin wrapper over
    /// <see cref="ScanAsync"/> — the structured rows are the single source of truth.
    /// </summary>
    public async Task<int> RunAsync(ReconcileMappingOptions mapping, string? env, TextWriter output, CosPeriod cosPeriod = default, CancellationToken ct = default, CommodityFilter commodity = CommodityFilter.Electricity)
    {
        var rows = await ScanAsync(mapping, env, ct, cosPeriod, commodity: commodity);
        WriteCsv(output, rows);
        return 0;
    }

    /// <summary>
    /// CLI entry for the per-representative full-payload view: scan ONE customer's create payload and
    /// write the result as CSV. Thin wrapper over <see cref="ScanRepresentativeAsync"/>.
    /// </summary>
    public async Task<int> RunCustomerAsync(
        ReconcileMappingOptions mapping, string? env, IReadOnlyCollection<string> accounts, DateOnly cos,
        TextWriter output, CancellationToken ct = default)
    {
        var rows = await ScanRepresentativeAsync(mapping, env, accounts, cos, ct);
        WriteCsv(output, rows);
        return 0;
    }

    /// <summary>
    /// Runs the env-wide reconcile scan and returns the structured drift rows (uncategorised — call
    /// <see cref="DriftCategorizer.Categorize"/> to label each). Read-only; never writes to Settl.
    /// Driven entirely by env: all parent InDelivery MPs in Settl define the scope (or all parent MPs
    /// when <paramref name="includeNotInDelivery"/> is true), their COS finds the Xellent
    /// customers/contracts, and the per-dataarea <paramref name="mapping"/> supplies the create-true
    /// config a profile would otherwise carry. Streams progress via <see cref="OnProgress"/>.
    /// </summary>
    // Note: cosPeriod is intentionally placed AFTER ct so the pre-existing 3-arg call shape
    // ScanAsync(mapping, env, ct) — used by the dashboard consuming this via NuGet — keeps binding ct
    // positionally and stays source-compatible. cosPeriod defaults to unbounded; includeNotInDelivery
    // defaults to false (backward-compatible). commodity defaults to Electricity (backward-compatible).
    public async Task<List<ReconcileDriftRow>> ScanAsync(ReconcileMappingOptions mapping, string? env, CancellationToken ct = default, CosPeriod cosPeriod = default, bool includeNotInDelivery = false, CommodityFilter commodity = CommodityFilter.Electricity)
    {
        _log($"=== reconcile env-wide (env={env ?? "featuretest"}, dataarea={mapping.DataAreaId}" +
             $"{(includeNotInDelivery ? ", includeNotInDelivery=true" : "")}" +
             $"{(commodity != CommodityFilter.Electricity ? $", commodity={commodity}" : "")}) ===");
        if (!cosPeriod.IsUnbounded)
            _log($"COS selection window: {cosPeriod.From?.ToString("yyyy-MM-dd") ?? "(open)"} .. {cosPeriod.To?.ToString("yyyy-MM-dd") ?? "(open)"} (inclusive)");

        // 1) All parent MPs in Settl with a resolvable COS — env-wide pagination (no profile).
        //    Default: InDelivery=true only. When includeNotInDelivery=true: also MPs with
        //    InDelivery=false/null that have a MostRecentCompletedDeliveryPeriod.
        var inDeliveryByMpId = await LoadInDeliveryEnvWideAsync(cosPeriod, ct, includeNotInDelivery, commodity);
        _log($"Settl parent MPs selected (env): {inDeliveryByMpId.Count}");

        // 2) Resolve the owning Xellent account per MP, COS-scoped. An MP identification can move
        //    between accounts over time, so the resolver matches the contract whose end date is
        //    COS-1 (the day the prior supplier's contract ends, = the migrated account at COS). This
        //    pins each MP to the single account it belonged to at change-of-supplier, not every
        //    account it ever touched.
        //    Exception: no-history MPs (never in delivery, COS=today) have no change-of-supplier event,
        //    so COS-1=yesterday won't match any Xellent contract. The resolver uses a fallback for
        //    these: the most recent contract by Contractenddate.
        var cosByMpId = inDeliveryByMpId.ToDictionary(kv => kv.Key, kv => kv.Value.Cos, StringComparer.Ordinal);
        var noHistoryMpIds = new HashSet<string>(
            inDeliveryByMpId
                .Where(kv => kv.Value.Info.InDelivery != true && kv.Value.Info.MostRecentCompletedDeliveryPeriod == null)
                .Select(kv => kv.Key),
            StringComparer.Ordinal);
        var resolution = await MeteringPointAccountResolver.ResolveAsync(
            _connectionString, cosByMpId, mapping.DataAreaId, noHistoryMpIds, ct);
        var mpAccountPairs = resolution.Pairs.ToList();
        var xellentMpIdsSet = new HashSet<string>(resolution.XellentMpIds, StringComparer.Ordinal);

        // 2a) Water MPs have no delivery period in Settl; resolve their accounts from Xellent via
        //     the V{Instnum}01 → ExuContractPart.Instagreenum join. Also updates cosByMpId with the
        //     Xellent-derived COS (ContractPart.Startdate) and tracks each water account's delivery
        //     category so the domain load uses "Vand" instead of "El-ekstern".
        var waterDeliveryCategoryByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        var waterMpIds = inDeliveryByMpId.Keys
            .Where(id => id.StartsWith("V", StringComparison.OrdinalIgnoreCase) && id.Length >= 3)
            .ToList();
        if (waterMpIds.Count > 0)
        {
            var waterResolved = await MeteringPointAccountResolver.ResolveWaterAsync(
                _connectionString, waterMpIds, mapping.DataAreaId, ct);
            var resolvedWaterMpIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (mpId, account, cos, deliveryCategory) in waterResolved)
            {
                cosByMpId[mpId] = cos;
                // Also key by Xellent Instnum (mpId[1..^2] strips "V"-prefix and "01"-suffix from
                // "V{instnum}01") so ContractLink drift rows can look up COS by their EntityKey.
                if (mpId.Length >= 4 && mpId.StartsWith("V", StringComparison.OrdinalIgnoreCase) && mpId.EndsWith("01", StringComparison.Ordinal))
                    cosByMpId[mpId[1..^2]] = cos;
                xellentMpIdsSet.Add(mpId);
                mpAccountPairs.Add((mpId, account));
                waterDeliveryCategoryByAccount.TryAdd(account, deliveryCategory);
                resolvedWaterMpIds.Add(mpId);
            }
            // Remove water MPs that have no matching Xellent installation (Settl-native, not migrated).
            foreach (var mpId in waterMpIds.Where(id => !resolvedWaterMpIds.Contains(id)))
                inDeliveryByMpId.Remove(mpId);
            _log($"  [water] resolved {waterResolved.Count}/{waterMpIds.Count} MPs to {waterDeliveryCategoryByAccount.Count} accounts (Xellent COS applied)");
        }

        var xellentMpIds = (IReadOnlySet<string>)xellentMpIdsSet;
        var mpsByAccount = mpAccountPairs
            .GroupBy(p => p.Account, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.MpId).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        var resolvedMpCount = mpAccountPairs.Select(p => p.MpId).Distinct(StringComparer.Ordinal).Count();
        _log($"MP→account (COS-scoped) resolved: {resolvedMpCount}/{inDeliveryByMpId.Count} MPs → {mpsByAccount.Count} accounts");

        // 3) Derive each account's COS as the MIN COS over the MPs that resolved to it. An account has
        //    no single COS (its MPs can start delivery on different dates); the earliest is used so
        //    the whole account (merge + customer + payer + all its MPs) is reconstructed at one
        //    reference date, matching how create migrated the customer in a single pass.
        var accountCos = mpsByAccount.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Min(mp => cosByMpId[mp]),
            StringComparer.Ordinal);

        // 4) Group accounts by their (min-)COS. Accounts that must merge into one Settl customer
        //    (same CVR+Fingerprint, migrated in the same change-of-supplier event) share a COS and so
        //    land in the same group — which is what lets the load below resolve the correct (lowest)
        //    representative: AccountRepresentativeResolver only merges over the accounts handed to it,
        //    so all group members must be present together.
        var accountSlices = accountCos
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                Cos = g.Key,
                RefDate = CosSlicer.RefDateFor(g.Key),
                Accounts = g.Select(kv => kv.Key).ToArray(),
                MpIds = g.SelectMany(kv => mpsByAccount[kv.Key]).ToHashSet(StringComparer.Ordinal),
                // If ALL accounts in this slice are water accounts, use their delivery category
                // (e.g. "Vand") so the Xellent domain load fetches installation-based MPs instead
                // of electricity delpoints. Mixed slices (water + electricity) are unusual and fall
                // back to the mapping's default (electricity).
                DeliveryCategory = g.All(kv => waterDeliveryCategoryByAccount.ContainsKey(kv.Key))
                    && g.Any()
                    ? waterDeliveryCategoryByAccount[g.First().Key]
                    : mapping.DeliveryCategory,
            })
            .ToList();
        _log($"Distinct account-COS dates (Xellent loads): {accountSlices.Count}");

        var union = new Dictionary<string, UnionedPlan>(StringComparer.Ordinal);
        var capturedMpIds = new HashSet<string>(StringComparer.Ordinal);

        // Representative (padded CustomerNumber) → merge-group facts, built from each slice's create-true
        // AccountGroupings, keyed on the same padded form the rows carry:
        //   membersByRep     → comma-joined Xellent accountnums (incl. the representative)
        //   memberNamesByRep → the corresponding customer names, in the SAME order as membersByRep
        //   repNameByRep     → the representative customer's own name
        var membersByRep = new Dictionary<string, string>(StringComparer.Ordinal);
        var memberNamesByRep = new Dictionary<string, string>(StringComparer.Ordinal);
        var repNameByRep = new Dictionary<string, string>(StringComparer.Ordinal);

        // Representative (padded CustomerNumber) → expected Settlement Group name(s). Sourced from
        // report.CustomerSettlementGroups, which create populates via the SAME
        // SettlementGroupExpectationResolver — so the intended set cannot drift from what create assigns.
        var settlementGroupsByRep = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Price-list reconcile (optional): fetch the Settl price-list + product-element catalogs once
        // (price-list Name→Settl GUID, product-element Code→GUID), and accumulate the create-true
        // intended associations per representative across the COS slices. Skipped entirely when no
        // price-list client is wired (backward-compatible for hosts that haven't passed one).
        var priceListEnabled = _priceListClient != null;
        var priceListNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var priceListIdToName = new Dictionary<Guid, string>();
        var productElementCodeToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var intendedPriceListsByRep = new Dictionary<string, List<CustomerPriceListAssociationRow>>(StringComparer.Ordinal);
        if (priceListEnabled)
        {
            var plTask = _priceListClient!.ListPriceListsAsync("DK", ct);
            var peTask = _priceListClient!.ListProductElementsAsync("DK", ct);
            await Task.WhenAll(plTask, peTask);
            var (priceLists, plErr) = plTask.Result;
            var (productElements, peErr) = peTask.Result;
            if (plErr != null || peErr != null)
            {
                _log($"  Price-list reconcile disabled: catalog fetch failed (priceLists={plErr ?? "ok"}, productElements={peErr ?? "ok"})");
                priceListEnabled = false;
            }
            else
            {
                foreach (var pl in priceLists!.Where(p => !string.IsNullOrEmpty(p.Name)))
                {
                    priceListNameToId[pl.Name!] = pl.Id.ToString();
                    priceListIdToName[pl.Id] = pl.Name!;
                }
                foreach (var pe in productElements!.Where(p => !string.IsNullOrEmpty(p.ProductElementCode)))
                    productElementCodeToId[pe.ProductElementCode!] = pe.Id;
                _log($"  Price-list catalog: {priceListNameToId.Count} price list(s), {productElementCodeToId.Count} product element(s)");
            }
        }

        // 6) Identity maps and settlement-group membership paginate Settl independently of Xellent;
        //    start them as background Tasks now so they run concurrently with the Xellent slice loads
        //    below. With ~6000+ MPs in Settl the three paginations take 20-40 s combined — overlapping
        //    them with the Xellent DB work eliminates that wait from the critical path. Awaited after
        //    the union fold (before step 7).
        var managedGroupNames = mapping.Mappings?.SettlementGroup?.Values ?? Enumerable.Empty<string>();
        var customerIdTask = BuildCustomerIdMapAsync(ct);
        var payerIdTask = BuildPayerIdMapAsync(ct);
        var settlGroupTask = BuildSettlementGroupMembersAsync(managedGroupNames, ct);

        // 5) Per account-COS group: one create-true capture at refDate = COS-1 over the group's
        //    accounts (complete merge-groups), then diff exactly the MPs those accounts own.
        //
        //    The heavy work per slice — the Xellent load + create-true capture (+ optional price-list
        //    compute) — is independent across slices and dominates the run, so it fans out under a
        //    concurrency gate (mirror of step 7). The slices' in-memory results are then folded into
        //    the shared accumulators SEQUENTIALLY below: union representatives and MP sets are disjoint
        //    across slices (merge-groups share a COS by domain invariant, so a representative lands in
        //    exactly one slice), so the fold order does not affect the result.
        var slicesLoaded = 0;
        SliceCapture[] captures;
        using (var loadGate = new SemaphoreSlim(SliceLoadConcurrency))
        {
            captures = await Task.WhenAll(accountSlices.Select(async slice =>
            {
                await loadGate.WaitAsync(ct);
                try
                {
                    _log($"  COS {slice.Cos:yyyy-MM-dd} (refDate {slice.RefDate:yyyy-MM-dd}): {slice.Accounts.Length} accounts, {slice.MpIds.Count} MPs");

                    var options = CreateOptionsFactory.BuildFromMapping(
                        mapping, slice.Accounts, env, slice.RefDate.ToDateTime(TimeOnly.MinValue), _exemption,
                        _baseIgnoredGroups, slice.Cos,
                        deliveryCategoryOverride: slice.DeliveryCategory);

                    var (loadResult, _) = await MigrationDomainLoad.LoadAsync(_connectionString, options, ct);

                    var plans = new List<SettlMigrationPlan>();
                    var runner = new CreateRunner(
                        _apiOptions, _customerClient, _payerClient, _mpClient, _contractClient, _settlementGroupClient);
                    runner.OnLogLine = _ => { }; // capture-mode prep is read-only; suppress its chatter
                    var (_, report) = await runner.RunAsync(options, loadResult, ct, capturePlans: plans);

                    // Compute this slice's intended price-list associations into a slice-local dict; it is
                    // merged into the shared intendedPriceListsByRep in the sequential fold (no shared
                    // write under await).
                    Dictionary<string, List<CustomerPriceListAssociationRow>>? slicePriceLists = null;
                    if (priceListEnabled)
                    {
                        slicePriceLists = new Dictionary<string, List<CustomerPriceListAssociationRow>>(StringComparer.Ordinal);
                        await AccumulateIntendedPriceListsAsync(
                            slice.Accounts, loadResult, slice.Cos, slice.RefDate, env, mapping.DataAreaId,
                            mapping.LegacyTg4Merge, priceListNameToId, slicePriceLists, ct);
                    }

                    var done = Interlocked.Increment(ref slicesLoaded);
                    _log($"  ...loaded {done}/{accountSlices.Count} COS slices");
                    return new SliceCapture(slice.MpIds, plans, report, slicePriceLists);
                }
                finally { loadGate.Release(); }
            }));
        }

        foreach (var capture in captures)
        {
            foreach (var plan in capture.Plans)
                MergeSliceIntoUnion(union, plan, capture.MpIds, capturedMpIds);

            if (capture.PriceLists != null)
                foreach (var (rep, priceListRows) in capture.PriceLists)
                {
                    if (!intendedPriceListsByRep.TryGetValue(rep, out var list))
                        intendedPriceListsByRep[rep] = list = [];
                    list.AddRange(priceListRows);
                }

            // Capture this slice's merge-group membership + names: key on the padded representative (the
            // form the rows carry). Member accountnums and member names are emitted in the SAME sorted
            // order so the two columns line up positionally.
            foreach (var g in capture.Report.AccountGroupings)
            {
                var repKey = CustomerNumberPolicy.PadForEnv(g.RepresentativeAccount, env, out _);
                var sortedMembers = g.MemberAccounts
                    .Append(g.RepresentativeAccount)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(a => a, StringComparer.Ordinal)
                    .ToList();
                membersByRep[repKey] = string.Join(",", sortedMembers);
                memberNamesByRep[repKey] = string.Join(",", sortedMembers.Select(a => g.AccountNames.GetValueOrDefault(a, "")));
                repNameByRep[repKey] = g.AccountNames.GetValueOrDefault(g.RepresentativeAccount, "");
            }

            // Expected Settlement Group(s) per customer. report.CustomerSettlementGroups is keyed by
            // the (un-padded) representative AccountNumber and aggregates the whole merge cluster's
            // contracts — exactly the customer-level granularity create's AssignSettlementGroups uses.
            // Pad to match the row key (the padded representative CustomerNumber).
            foreach (var (account, groups) in capture.Report.CustomerSettlementGroups)
            {
                var repKey = CustomerNumberPolicy.PadForEnv(account, env, out _);
                settlementGroupsByRep[repKey] = groups;
            }
        }

        // 6) Identity maps and settlement-group membership were started before the slice loads and
        //    ran concurrently with the Xellent DB queries — await all three now.
        await Task.WhenAll(customerIdTask, payerIdTask, settlGroupTask);
        var customerIdByNumber = customerIdTask.Result;
        var payerIdByIdentification = payerIdTask.Result;
        var settlementGroupMembers = settlGroupTask.Result;

        // 7) Diff each unioned representative against actual Settl state. The remaining work is
        //    Settl-bound detail GETs (customer/payer/MP billing), independent and read-only per
        //    representative, so they run concurrently under a fixed gate instead of strictly
        //    sequentially. Per-representative progress is streamed — otherwise the run looks hung
        //    between the last COS slice and the final "Done." line.
        _log($"Diffing {union.Count} representatives against Settl (concurrency {DiffConcurrency})...");
        var diffed = 0;
        using var gate = new SemaphoreSlim(DiffConcurrency);
        var perRepRows = await Task.WhenAll(union.Values.Select(async up =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var repRows = await DiffRepresentativeAsync(up, inDeliveryByMpId, customerIdByNumber, payerIdByIdentification, settlementGroupsByRep, settlementGroupMembers, priceListEnabled, intendedPriceListsByRep, productElementCodeToId, priceListIdToName, ct, fullPayload: true);
                var done = Interlocked.Increment(ref diffed);
                OnProgress?.Invoke(done, union.Count);
                _log($"  ...diffed {done}/{union.Count} representatives");
                return repRows;
            }
            finally { gate.Release(); }
        }));
        var rows = perRepRows.SelectMany(r => r).ToList();

        // 8) Settl MP the create-true extract did not produce. Env-wide → a complete signal, not a
        //    profile-scope artifact. Split by InDelivery status and Xellent presence:
        //      InDelivery=true, in Xellent   → real drift (contract deleted/changed, or no contract
        //                                       ending at COS-1). settl-indelivery-no-xellent-match.
        //      InDelivery=true, not in Xellent → created directly in Settl, no migration origin.
        //                                         settl-native-meteringpoint (benign).
        //      InDelivery=false, in Xellent   → MP migrated from Xellent, supply never started in
        //                                        Settl (or left delivery). settl-notindelivery-no-xellent-match.
        //      InDelivery=false, not in Xellent → Settl-native not-in-delivery MP. settl-native-meteringpoint.
        foreach (var mpId in inDeliveryByMpId.Keys.Where(id => !capturedMpIds.Contains(id)))
        {
            var isInDelivery = inDeliveryByMpId[mpId].Info.InDelivery == true;
            var inXellent = xellentMpIds.Contains(mpId);
            var (reason, actual) = (isInDelivery, inXellent) switch
            {
                (true,  true)  => ("settl-indelivery-no-xellent-match",    "InDelivery in Settl"),
                (true,  false) => ("settl-native-meteringpoint",            "InDelivery in Settl (no Xellent origin)"),
                (false, true)  => ("settl-notindelivery-no-xellent-match",  "NotInDelivery in Settl"),
                (false, false) => ("settl-native-meteringpoint",            "NotInDelivery in Settl (no Xellent origin)"),
            };
            rows.Add(new ReconcileDriftRow("", "MeteringPoint", mpId, reason,
                null, actual, DriftKind.MissingInXellent));
        }

        // Payer identification → payer name (PayerInformation address Name1.FullName), for stamping
        // EntityName on Payer rows whose EntityKey is the payer identification.
        var payerNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var up in union.Values)
            foreach (var (pid, pdto) in up.Payers)
                payerNameById[pid] = pdto.PayerInformation?.PostalAddress?.Name1?.FullName ?? "";

        // Representative → (min, max) COS over ALL the representative's MPs (from union, so it covers
        // every MP the rep owns — not only those that produced a drift row). Customer/Payer rows have
        // no single COS; this period is stamped on them (and every row under the rep) so the list can
        // show "min .. max" instead of a blank COS cell.
        var cosRangeByRep = new Dictionary<string, (DateOnly Min, DateOnly Max)>(StringComparer.Ordinal);
        foreach (var up in union.Values)
        {
            var coss = up.MeteringPoints.Keys
                .Where(cosByMpId.ContainsKey)
                .Select(id => cosByMpId[id])
                .ToList();
            if (coss.Count > 0)
                cosRangeByRep[up.CustomerNumber] = (coss.Min(), coss.Max());
        }
        // Fallback for water/heat accounts where up.MeteringPoints is empty because the
        // domain MP Identification format doesn't match sliceMpIds: derive COS directly
        // from accountCos (already computed from the resolved water MP COS dates).
        foreach (var up in union.Values)
        {
            if (cosRangeByRep.ContainsKey(up.CustomerNumber)) continue;
            if (accountCos.TryGetValue(up.CustomerNumber, out var cos))
                cosRangeByRep[up.CustomerNumber] = (cos, cos);
        }

        // 8a) Settl customers that exist for non-representative (merged-away) Xellent accounts.
        //     These accounts were absorbed into a representative's merge group and should not have
        //     their own Settl customer — all their MPs and payers belong under the representative.
        //     Detected by cross-referencing every merge group's non-representative members against
        //     the env-wide customer id map built in step 6.
        foreach (var (repKey, membersStr) in membersByRep)
        {
            foreach (var member in membersStr.Split(','))
            {
                var padMember = CustomerNumberPolicy.PadForEnv(member, env, out _);
                if (padMember == repKey) continue; // the representative itself — not stale
                if (!customerIdByNumber.ContainsKey(padMember)) continue; // no Settl customer → nothing to report
                rows.Add(new ReconcileDriftRow(
                    repKey, "Customer", padMember, "merged-into-representative",
                    null, "Settl customer exists for merged-away account",
                    DriftKind.MissingInXellent));
            }
        }

        // 8b) Post-projection enrichment in one pass so the diff phase stays focused on field
        //     comparison. Facets:
        //       • COS  — stamped onto every MeteringPoint row from the env-wide cosByMpId map (keyed
        //         by MP identification = the EntityKey). Customer/Payer rows have no single COS → null;
        //         they instead carry the representative's CosFrom/CosTo period (see below).
        //       • CosFrom / CosTo — the representative's COS period (min/max over its MPs), stamped on
        //         every row under the representative so non-MP rows can render a period.
        //       • MemberAccounts / MemberNames / RepresentativeName — merge-group facts, stamped onto
        //         every row under the representative (representative-level). Rep-less anomaly rows
        //         (settl-indelivery-no-xellent-match / settl-native-meteringpoint) have no group → null.
        //       • EntityName — this row's own entity name: Customer→representative name, Payer→payer
        //         name, MeteringPoint→null (no name requested for MPs).
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var cos = r.Entity == "MeteringPoint" && cosByMpId.TryGetValue(r.EntityKey, out var c)
                ? (DateOnly?)c : null;
            membersByRep.TryGetValue(r.RepresentativeAccount, out var members);
            memberNamesByRep.TryGetValue(r.RepresentativeAccount, out var memberNames);
            repNameByRep.TryGetValue(r.RepresentativeAccount, out var repName);
            DateOnly? cosFrom = null, cosTo = null;
            if (cosRangeByRep.TryGetValue(r.RepresentativeAccount, out var range))
                (cosFrom, cosTo) = (range.Min, range.Max);
            var entityName = r.Entity switch
            {
                "Customer" => repName,
                "Payer" => payerNameById.GetValueOrDefault(r.EntityKey),
                _ => null,
            };
            rows[i] = r with
            {
                Cos = cos,
                CosFrom = cosFrom,
                CosTo = cosTo,
                MemberAccounts = members,
                MemberNames = memberNames,
                RepresentativeName = repName,
                EntityName = entityName,
            };
        }

        // 9) Drop rows that carry no actionable signal by construction. Filtered here, not in
        //    JsonValueDiff/the MP projector, so those stay normalisation-free (every real difference
        //    is still surfaced); the category is the authoritative selector. The dropped classes:
        //      • RepNullEmpty  — both sides empty ("— → —"); a null-vs-"" artifact.
        //      • RepOpenEnded  — both sides mean "no end date" (blank vs the 0001-01-01/9999
        //        sentinels). A real end-date difference is NOT RepOpenEnded (one side is a concrete
        //        date) and survives.
        //      • DeliveryFlag  — InDelivery can only ever be "False → True" here: reconcile selects
        //        only InDelivery=true MPs, so Actual is always True and the field is tautological.
        var nonActionable = new HashSet<string>
        {
            DriftCategorizer.RepNullEmpty, DriftCategorizer.RepOpenEnded, DriftCategorizer.DeliveryFlag,
        };
        var suppressed = rows.RemoveAll(r => nonActionable.Contains(DriftCategorizer.Categorize(r)));
        if (suppressed > 0)
            _log($"Suppressed {suppressed} non-actionable row(s) (null-vs-empty, open-ended dates, InDelivery runtime flag).");

        // 10) Drop historically-accepted drift: rows whose effective COS falls in [CosFrom, CosTo]
        //     AND all non-empty filters (Types, SubTypes, Kinds, Categories) match. Effective COS =
        //     row.Cos for MeteringPoint rows; row.CosFrom (representative's earliest COS) for
        //     Customer/Payer rows. Rows with no resolvable COS are never suppressed.
        if (mapping.HistoricalSuppressions is { Length: > 0 } historicalSuppressions)
        {
            var suppressedHistorical = rows.RemoveAll(r =>
            {
                var effectiveCos = r.Cos ?? r.CosFrom;
                if (effectiveCos is null) return false;
                var category = DriftCategorizer.Categorize(r);
                foreach (var hs in historicalSuppressions)
                {
                    if (hs.CosFrom.HasValue && effectiveCos.Value < hs.CosFrom.Value) continue;
                    if (hs.CosTo.HasValue && effectiveCos.Value > hs.CosTo.Value) continue;
                    if (hs.Types.Length > 0 && Array.IndexOf(hs.Types, r.Entity) < 0) continue;
                    // "null" is accepted as a sentinel for SubType="" (core entity fields with no subtype facet).
                    if (hs.SubTypes.Length > 0 && Array.IndexOf(hs.SubTypes, r.SubType) < 0
                        && !(r.SubType == "" && Array.IndexOf(hs.SubTypes, "null") >= 0)) continue;
                    if (hs.Kinds.Length > 0 && Array.IndexOf(hs.Kinds, r.Kind.ToString()) < 0) continue;
                    if (hs.Categories.Length > 0 && Array.IndexOf(hs.Categories, category) < 0) continue;
                    if (hs.EntityKeys.Length > 0 && Array.IndexOf(hs.EntityKeys, r.EntityKey) < 0) continue;
                    if (hs.RepresentativeAccounts.Length > 0 && Array.IndexOf(hs.RepresentativeAccounts, r.RepresentativeAccount) < 0) continue;
                    return true;
                }
                return false;
            });
            if (suppressedHistorical > 0)
            {
                var summary = string.Join(", ", historicalSuppressions.Select(hs =>
                {
                    var parts = new List<string> { $"[{hs.CosFrom?.ToString() ?? "∞"}..{hs.CosTo?.ToString() ?? "∞"}]" };
                    if (hs.Types.Length > 0) parts.Add(string.Join("|", hs.Types));
                    if (hs.SubTypes.Length > 0) parts.Add(string.Join("|", hs.SubTypes.Select(s => s == "" ? "null" : s)));
                    if (hs.Kinds.Length > 0) parts.Add(string.Join("|", hs.Kinds));
                    if (hs.Categories.Length > 0) parts.Add(string.Join("|", hs.Categories));
                    if (hs.EntityKeys.Length > 0) parts.Add(string.Join("|", hs.EntityKeys));
                    if (hs.RepresentativeAccounts.Length > 0) parts.Add("rep:" + string.Join("|", hs.RepresentativeAccounts));
                    return string.Join(" ", parts);
                }));
                _log($"Suppressed {suppressedHistorical} historically-accepted row(s) ({summary}).");
            }
        }

        var matchRows = rows.Count(r => r.Kind == DriftKind.Match);
        _log($"Done. {rows.Count - matchRows} drift rows + {matchRows} matched across {union.Count} representatives.");
        return rows;
    }

    /// <summary>
    /// Per-representative <b>full-payload</b> view: rebuilds the create-true intended payload for ONE
    /// already-migrated customer (its merge-group at <paramref name="cos"/>) via the same scoped
    /// capture <see cref="ReconcileApplier"/> uses — one Xellent load + <see cref="CreateRunner"/>
    /// capture at <c>refDate = COS-1</c> over <paramref name="accounts"/>, no env-wide pagination — and
    /// diffs it against Settl in full-payload mode: <b>every</b> compared field is returned, matched
    /// ones tagged <see cref="DriftKind.Match"/>, so the whole create payload is visible, not just
    /// drift. Read-only. <paramref name="accounts"/> is the representative's complete merge-group (the
    /// comma-joined MemberAccounts a reconcile row carries); the capture resolves which is the
    /// representative.
    /// </summary>
    public async Task<List<ReconcileDriftRow>> ScanRepresentativeAsync(
        ReconcileMappingOptions mapping, string? env,
        IReadOnlyCollection<string> accounts, DateOnly cos, CancellationToken ct = default)
    {
        // Billing/payment/exemption + MP master lookups are on electricity MPs (mirror ReconcileApplier).
        _mpClient.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var refDate = cos.AddDays(-1);
        _log($"=== reconcile customer full-payload (env={env ?? "featuretest"}, dataarea={mapping.DataAreaId}, " +
             $"accounts={accounts.Count}, cos={cos:yyyy-MM-dd}, refDate={refDate:yyyy-MM-dd}) ===");

        // 1) Scoped create-true capture — same prep + builders as env-wide reconcile / create,
        //    restricted to this representative's merge-group at its COS.
        var options = CreateOptionsFactory.BuildFromMapping(
            mapping, accounts.ToArray(), env, refDate.ToDateTime(TimeOnly.MinValue), _exemption,
            _baseIgnoredGroups, cos);
        var (loadResult, _) = await MigrationDomainLoad.LoadAsync(_connectionString, options, ct);
        var plans = new List<SettlMigrationPlan>();
        var createRunner = new CreateRunner(
            _apiOptions, _customerClient, _payerClient, _mpClient, _contractClient, _settlementGroupClient)
        {
            OnLogLine = _ => { }, // capture-mode prep is read-only; suppress its chatter
        };
        var (_, report) = await createRunner.RunAsync(options, loadResult, ct, capturePlans: plans);

        // 2) Union the captured plan(s), keeping ALL MPs the accounts own (the full-payload view is not
        //    restricted to currently-InDelivery MPs the way the env scan is).
        var union = new Dictionary<string, UnionedPlan>(StringComparer.Ordinal);
        var capturedMpIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var allMpIds = plan.MeteringPoints.Select(m => m.Identification).ToHashSet(StringComparer.Ordinal);
            MergeSliceIntoUnion(union, plan, allMpIds, capturedMpIds);
        }
        if (union.Count == 0)
        {
            _log("No create-true payload produced for the requested accounts.");
            return [];
        }

        // 3) Merge-group facts (members + names) + expected settlement groups, for row enrichment.
        var membersByRep = new Dictionary<string, string>(StringComparer.Ordinal);
        var memberNamesByRep = new Dictionary<string, string>(StringComparer.Ordinal);
        var repNameByRep = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in report.AccountGroupings)
        {
            var repKey = CustomerNumberPolicy.PadForEnv(g.RepresentativeAccount, env, out _);
            var sortedMembers = g.MemberAccounts
                .Append(g.RepresentativeAccount)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToList();
            membersByRep[repKey] = string.Join(",", sortedMembers);
            memberNamesByRep[repKey] = string.Join(",", sortedMembers.Select(a => g.AccountNames.GetValueOrDefault(a, "")));
            repNameByRep[repKey] = g.AccountNames.GetValueOrDefault(g.RepresentativeAccount, "");
        }
        var settlementGroupsByRep = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (account, groups) in report.CustomerSettlementGroups)
            settlementGroupsByRep[CustomerNumberPolicy.PadForEnv(account, env, out _)] = groups;

        // 4) Per-representative identity lookups (one Search each — no env-wide pagination).
        var customerIdByNumber = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var number in union.Keys)
        {
            var (info, _) = await _customerClient.SearchByCustomerNumberAsync(number, "DK", ct);
            if (info != null) customerIdByNumber[number] = info.Id;
        }
        var payerIdByIdentification = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var payerId in union.Values.SelectMany(u => u.Payers.Keys).Distinct(StringComparer.Ordinal))
        {
            var (info, _) = await _payerClient.SearchByPayerIdentificationAsync(payerId, "DK", ct);
            if (info != null) payerIdByIdentification[payerId] = info.Id;
        }

        // 5) Actual MP state for every MP in the payload (market-v3 list shape; carries the Settl GUID
        //    the billing GET needs). An MP absent from Settl is omitted → reported MissingInSettl.
        //    Water/heat MPs (IsElectricity=false) live on a separate commodity endpoint — use
        //    SearchWaterByIdentificationAsync for them (loads all water MPs into a local cache on first call).
        var isElectricityByMpId = plans
            .SelectMany(p => p.MeteringPoints)
            .GroupBy(m => m.Identification, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.All(m => m.IsElectricity), StringComparer.Ordinal);
        var inDelivery = new Dictionary<string, SettlMpWithCos>(StringComparer.Ordinal);
        foreach (var mpId in union.Values.SelectMany(u => u.MeteringPoints.Keys).Distinct(StringComparer.Ordinal))
        {
            var isElectricity = !isElectricityByMpId.TryGetValue(mpId, out var e) || e;
            var (info, _) = isElectricity
                ? await _mpClient.SearchByIdentificationAsync(mpId, ct)
                : await _mpClient.SearchWaterByIdentificationAsync(mpId, ct);
            if (info != null) inDelivery[mpId] = new SettlMpWithCos(mpId, info.Id, cos, info);
        }

        // 6) Settlement-group membership over the managed universe (mirror of env scan step 6b).
        var managedGroupNames = mapping.Mappings?.SettlementGroup?.Values ?? Enumerable.Empty<string>();
        var settlementGroupMembers = await BuildSettlementGroupMembersAsync(managedGroupNames, ct);

        // 7) Price-list catalog + create-true intended associations (optional; mirror of env scan).
        var priceListEnabled = _priceListClient != null;
        var priceListNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var priceListIdToName = new Dictionary<Guid, string>();
        var productElementCodeToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var intendedPriceListsByRep = new Dictionary<string, List<CustomerPriceListAssociationRow>>(StringComparer.Ordinal);
        if (priceListEnabled)
        {
            var plTask = _priceListClient!.ListPriceListsAsync("DK", ct);
            var peTask = _priceListClient!.ListProductElementsAsync("DK", ct);
            await Task.WhenAll(plTask, peTask);
            var (priceLists, plErr) = plTask.Result;
            var (productElements, peErr) = peTask.Result;
            if (plErr != null || peErr != null)
            {
                _log($"  Price-list reconcile disabled: catalog fetch failed (priceLists={plErr ?? "ok"}, productElements={peErr ?? "ok"})");
                priceListEnabled = false;
            }
            else
            {
                foreach (var pl in priceLists!.Where(p => !string.IsNullOrEmpty(p.Name)))
                {
                    priceListNameToId[pl.Name!] = pl.Id.ToString();
                    priceListIdToName[pl.Id] = pl.Name!;
                }
                foreach (var pe in productElements!.Where(p => !string.IsNullOrEmpty(p.ProductElementCode)))
                    productElementCodeToId[pe.ProductElementCode!] = pe.Id;
                await AccumulateIntendedPriceListsAsync(
                    accounts.ToArray(), loadResult, cos, refDate, env, mapping.DataAreaId,
                    mapping.LegacyTg4Merge, priceListNameToId, intendedPriceListsByRep, ct);
            }
        }

        // 8) Full-payload diff per representative.
        var rows = new List<ReconcileDriftRow>();
        foreach (var up in union.Values)
            rows.AddRange(await DiffRepresentativeAsync(
                up, inDelivery, customerIdByNumber, payerIdByIdentification, settlementGroupsByRep,
                settlementGroupMembers, priceListEnabled, intendedPriceListsByRep, productElementCodeToId,
                priceListIdToName, ct, fullPayload: true));

        // 9) Enrichment (mirror of env scan step 8b): stamp COS + merge-group facts + entity names. All
        //    MPs share the representative's COS in this view, so Cos/CosFrom/CosTo are that single date.
        var payerNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var up in union.Values)
            foreach (var (pid, pdto) in up.Payers)
                payerNameById[pid] = pdto.PayerInformation?.PostalAddress?.Name1?.FullName ?? "";

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            membersByRep.TryGetValue(r.RepresentativeAccount, out var members);
            memberNamesByRep.TryGetValue(r.RepresentativeAccount, out var memberNames);
            repNameByRep.TryGetValue(r.RepresentativeAccount, out var repName);
            var entityName = r.Entity switch
            {
                "Customer" => repName,
                "Payer" => payerNameById.GetValueOrDefault(r.EntityKey),
                _ => null,
            };
            rows[i] = r with
            {
                Cos = r.Entity == "MeteringPoint" ? cos : null,
                CosFrom = cos,
                CosTo = cos,
                MemberAccounts = members,
                MemberNames = memberNames,
                RepresentativeName = repName,
                EntityName = entityName,
            };
        }

        _log($"Done. {rows.Count} payload field(s) across {union.Count} representative(s) " +
             $"({rows.Count(r => r.Kind == DriftKind.Match)} matched).");
        return rows;
    }

    // ---- Settl MP load (env-wide) ----

    /// <summary>
    /// Env-wide Settl enumeration: paginate <c>FindMeteringPointsAsync</c> over the whole tenant,
    /// keeping parent MPs with a resolvable COS. Child MPs (Settl relations) are skipped, consistent
    /// with report-meteringpoints/report-drift.
    /// <para>By default only InDelivery=true MPs are included (<paramref name="includeNotInDelivery"/>
    /// =false). When <paramref name="includeNotInDelivery"/>=true, MPs with InDelivery!=true are also
    /// included, using <c>MostRecentCompletedDeliveryPeriod.Start</c> as their COS — useful for
    /// verifying field state on MPs that have left delivery since migration.</para>
    /// </summary>
    private Task<Dictionary<string, SettlMpWithCos>> LoadInDeliveryEnvWideAsync(
        CosPeriod cosPeriod, CancellationToken ct, bool includeNotInDelivery = false, CommodityFilter commodity = CommodityFilter.Electricity)
    {
        // Shared with payment-match: one definition of which MPs exist in the environment and
        // when they changed supplier. See SettlMeteringPointCosLoader.
        var loader = new SettlMeteringPointCosLoader(_mpClient, _log) { OnProgress = OnProgress };
        return loader.LoadAsync(cosPeriod, ct, includeNotInDelivery, commodity);
    }

    // ---- Identity maps (env-wide, one-time — replaces per-rep/per-payer Search round-trips) ----

    /// <summary>
    /// Paginates all customers once into a <c>CustomerNumber → Settl Guid</c> map. Replaces the
    /// per-representative <c>SearchByCustomerNumberAsync</c> round-trip with a single list scan.
    /// </summary>
    private async Task<Dictionary<string, Guid>> BuildCustomerIdMapAsync(CancellationToken ct)
    {
        const int pageSize = 1000;
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (items, _, err) = await _customerClient.FindCustomersAsync(pageSize, skip, ct);
            if (err != null)
                throw new InvalidOperationException($"Settl FindCustomers failed: {err}");
            if (items.Count == 0) break;
            foreach (var c in items)
                if (!string.IsNullOrEmpty(c.CustomerNumber))
                    map[c.CustomerNumber] = c.Id;
            skip += pageSize;
            if (items.Count < pageSize) break;
        }
        _log($"Customer id-map: {map.Count} customers");
        return map;
    }

    /// <summary>
    /// Paginates all payers once into a <c>PayerIdentification → Settl Guid</c> map. Replaces the
    /// per-payer <c>SearchByPayerIdentificationAsync</c> round-trip with a single list scan.
    /// </summary>
    private async Task<Dictionary<string, Guid>> BuildPayerIdMapAsync(CancellationToken ct)
    {
        const int pageSize = 1000;
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var skip = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var (items, _, err) = await _payerClient.FindPayersAsync(pageSize, skip, ct);
            if (err != null)
                throw new InvalidOperationException($"Settl FindPayers failed: {err}");
            if (items.Count == 0) break;
            foreach (var p in items)
                if (!string.IsNullOrEmpty(p.PayerIdentification))
                    map[p.PayerIdentification] = p.Id;
            skip += pageSize;
            if (items.Count < pageSize) break;
        }
        _log($"Payer id-map: {map.Count} payers");
        return map;
    }

    /// <summary>
    /// Resolves actual Settl membership for the migration's managed Settlement Groups: one env-wide
    /// <c>ListAllAsync</c> for the name→GUID map, then <c>ListCustomerIdsAsync</c> per managed group.
    /// Returns <c>groupName</c> (OrdinalIgnoreCase, matching create's lookup) → member customer-GUIDs.
    /// A name absent from the returned map means the group does not exist in Settl. Mirrors the
    /// lookup <c>SettlClient.AssignSettlementGroupsAsync</c> performs on the create side; resolving
    /// the full managed set (not just groups some rep expects) is what lets the diff detect extra
    /// memberships.
    /// </summary>
    private async Task<Dictionary<string, HashSet<Guid>>> BuildSettlementGroupMembersAsync(
        IEnumerable<string> managedGroupNames, CancellationToken ct)
    {
        const string countryCode = "DK";
        var result = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        var needed = managedGroupNames
            .Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (needed.Count == 0) return result;

        var (allGroups, listErr) = await _settlementGroupClient.ListAllAsync(countryCode, ct);
        if (allGroups == null)
            throw new InvalidOperationException($"Settl ListAllAsync (settlement groups) failed: {listErr}");

        var idByName = allGroups
            .Where(g => !string.IsNullOrEmpty(g.GroupName))
            .GroupBy(g => g.GroupName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        foreach (var name in needed)
        {
            if (!idByName.TryGetValue(name, out var groupId)) continue; // missing → absent from result
            var (members, memErr) = await _settlementGroupClient.ListCustomerIdsAsync(groupId, ct);
            if (members == null)
                throw new InvalidOperationException($"Settl ListCustomerIds (group '{name}') failed: {memErr}");
            result[name] = members;
        }
        _log($"Settlement-group membership: resolved {result.Count}/{needed.Count} expected group(s)");
        return result;
    }

    // ---- Union ----

    private static void MergeSliceIntoUnion(
        Dictionary<string, UnionedPlan> union, SettlMigrationPlan plan,
        HashSet<string> sliceMpIds, HashSet<string> capturedMpIds)
    {
        if (!union.TryGetValue(plan.CustomerNumber, out var up))
            union[plan.CustomerNumber] = up = new UnionedPlan(plan.CustomerNumber, plan.Customer);

        foreach (var payer in plan.Payers)
            up.Payers.TryAdd(payer.PayerInformation?.PayerIdentifier ?? plan.CustomerNumber, payer);

        foreach (var mp in plan.MeteringPoints.Where(m => sliceMpIds.Contains(m.Identification)))
        {
            up.MeteringPoints[mp.Identification] = mp;
            capturedMpIds.Add(mp.Identification);
        }

        foreach (var link in plan.CustomerMpLinks.Where(l => sliceMpIds.Contains(l.ExternalIdentifier ?? "")))
            up.CustomerLinks[link.ExternalIdentifier!] = link;
        foreach (var link in plan.PayerMpLinks.Where(l => sliceMpIds.Contains(l.Link.ExternalIdentifier ?? "")))
            up.PayerLinks[link.Link.ExternalIdentifier!] = link;
        // Contract links are not filtered by sliceMpIds: for water/heat MPs the national ID in the
        // plan (Xellent installation ID) differs from the Settl "V..."-format in sliceMpIds, so the
        // Contains check always misses. The plan is already scoped per customer — all its links belong
        // to this representative and must be included regardless of commodity.
        foreach (var link in plan.ContractMpLinks)
            up.ContractLinks.Add(link);
    }

    // ---- Intended price-list associations (per COS slice) ----

    /// <summary>
    /// Builds the create-true intended customer↔price-list associations for one COS slice's accounts
    /// and merges them into <paramref name="intendedByRep"/> (keyed by the padded representative
    /// CustomerNumber). Mirrors the export-customer-pricelists gather→build pipeline, including the
    /// still-active filter: representatives still active in Xellent on the COS date are skipped, because
    /// the export that produced Settl's actual associations skipped them too. Env-wide reconcile resolves
    /// its accounts from Settl InDelivery MPs, which include customers still active in Xellent (typically
    /// on a financial pool/spot product that did not end at cutover) — without this filter the builder
    /// emits price-list rows for those customers' expired contract parts, surfacing as false
    /// MissingInSettl. Resolves price-list names against the Settl catalog
    /// (<paramref name="priceListNameToId"/>) instead of a CSV.
    /// </summary>
    private Task AccumulateIntendedPriceListsAsync(
        string[] accounts, XellentLoadResult loadResult, DateOnly cos, DateOnly refDate, string? env,
        string dataAreaId, bool legacyTg4Merge,
        IReadOnlyDictionary<string, string> priceListNameToId,
        Dictionary<string, List<CustomerPriceListAssociationRow>> intendedByRep,
        CancellationToken ct)
        => ReconcilePriceListIntended.AccumulateAsync(
            _connectionString, _productElementMappings, accounts, loadResult, cos, refDate, env,
            dataAreaId, legacyTg4Merge, priceListNameToId, intendedByRep, ct);

    // ---- Price-list period helpers ----

    /// <summary>Settl's AssociationPeriod is a UTC instant (Danish midnight as e.g. "…T23:00:00Z");
    /// convert to the Danish calendar date before comparing — same rule as COS.</summary>
    private static DateOnly? DkDate(DateTimeOffset? instant)
        => instant.HasValue
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instant.Value.UtcDateTime, DkTz))
            : null;

    /// <summary>A date that means "no end" — the Xellent 1900 sentinel or Settl's 9999 far-future.</summary>
    private static bool IsOpenEnd(DateOnly? d) => d is null || d.Value.Year <= 1900 || d.Value.Year >= 9000;

    /// <summary>
    /// End dates are equivalent when both are open, or the intended <b>inclusive</b> end matches
    /// Settl's <b>exclusive</b> stored end. Settl persists a price-list association period as a
    /// half-open interval <c>[Start, End)</c>: the import takes the inclusive Xellent end date the
    /// export wrote (e.g. 2027-01-31) and stores the upper bound as the day after (2027-02-01). The
    /// intended side here is the raw inclusive end (<see cref="CustomerPriceListAssociationBuilder"/>
    /// does not add the day), so the equivalent actual is <c>intendedEnd + 1</c> — comparing them
    /// directly produced a systematic 1-day false positive on every association with a concrete end.
    /// </summary>
    internal static bool EndEquivalent(DateOnly intendedEnd, DateOnly? actualEnd)
        => IsOpenEnd(intendedEnd) ? IsOpenEnd(actualEnd) : actualEnd == intendedEnd.AddDays(1);

    private static string FormatPeriod(DateOnly? start, DateOnly? end)
    {
        var s = start?.ToString("yyyy-MM-dd") ?? "(none)";
        var e = IsOpenEnd(end) ? "(open)" : end!.Value.ToString("yyyy-MM-dd");
        return $"{s} .. {e}";
    }

    // ---- Diff one representative ----

    private async Task<List<ReconcileDriftRow>> DiffRepresentativeAsync(
        UnionedPlan up, Dictionary<string, SettlMpWithCos> inDelivery,
        IReadOnlyDictionary<string, Guid> customerIdByNumber,
        IReadOnlyDictionary<string, Guid> payerIdByIdentification,
        IReadOnlyDictionary<string, List<string>> settlementGroupsByRep,
        IReadOnlyDictionary<string, HashSet<Guid>> settlementGroupMembers,
        bool priceListEnabled,
        IReadOnlyDictionary<string, List<CustomerPriceListAssociationRow>> intendedPriceListsByRep,
        IReadOnlyDictionary<string, Guid> productElementCodeToId,
        IReadOnlyDictionary<Guid, string> priceListIdToName,
        CancellationToken ct,
        bool fullPayload = false)
    {
        var rep = up.CustomerNumber;

        // Full-payload (per-representative view): emit EVERY compared field, tagging matched ones
        // DriftKind.Match, so the whole create payload of an already-migrated customer is visible — not
        // only its drift. The env-wide scan leaves fullPayload false and behaves exactly as before
        // (only differing leaves, all FieldDiff). includeEqual stays intersection-only, so an
        // intended-only field is still not invented on the actual side.
        static DriftKind LeafKind(string? intended, string? actual) =>
            string.Equals(intended, actual, StringComparison.Ordinal) ? DriftKind.Match : DriftKind.FieldDiff;
        IEnumerable<ReconcileDriftRow> Compare(
            string entity, string key, string fieldPrefix, object? intended, object? actual,
            IReadOnlySet<string>? exclude, string subType) =>
            JsonValueDiff.Diff(intended, actual, exclude, includeEqual: fullPayload)
                .Select(d => new ReconcileDriftRow(
                    rep, entity, key, fieldPrefix + d.Path, d.Intended, d.Actual, LeafKind(d.Intended, d.Actual),
                    SubType: subType,
                    // Carry the round-trippable intended leaf for the Compare outputs reconcile-apply can
                    // write: billing rows, and customer/payer CORE rows (subType "" — written via the
                    // full-echo master PUT). The applier navigates the live entity to the row's Field path
                    // and sets it to this token. MP master rows use the projector (not Compare), so the
                    // remaining null case here is the (unused) MP path — left null.
                    IntendedJson: subType == "Billing"
                        || (subType.Length == 0 && (entity == "Customer" || entity == "Payer"))
                        ? d.IntendedJson : null));

        // ---- Phase 1: in-memory setup (no I/O) ----

        // Customer — identity resolved from the env-wide customer map (no per-rep Search round-trip).
        Guid? custGuid = customerIdByNumber.TryGetValue(rep, out var cid) ? cid : null;

        // Settl's customer/payer link GETs return MeteringPointId (real Settl GUID) with
        // ExternalIdentifier null, so map actual link GUIDs back to identifications via the InDelivery
        // lookup (identification → Settl GUID), then compare on identification. Shared by the customer-
        // and payer-link checks below.
        var guidToMpId = inDelivery.ToDictionary(kv => kv.Value.Id, kv => kv.Key);

        // Contract→MP link presence + end-date check. Water/heat profiles set OpenEnded=true so
        // the intended End is 9999-12-31 (open-ended sentinel). A concrete date on an existing link
        // is drift create cannot fix on re-run (create skips existing links). Dedup by
        // (ContractId, MpGuid) first — the same MP can appear in multiple COS slices.
        // Strips "V"-prefix and "01"-suffix from Settl water MP IDs (e.g. "V104229901" → "1042299")
        // to normalise to the Xellent Instnum stored as MeteringPointIdentification in the capture plan.
        // Electricity IDs (18-digit numerics) pass through unchanged.
        static string NormWaterId(string id) =>
            id.Length >= 4 && id.StartsWith("V", StringComparison.OrdinalIgnoreCase) && id.EndsWith("01", StringComparison.Ordinal)
                ? id[1..^2]
                : id;

        var contractLinksByContractId = up.ContractLinks
            .GroupBy(l => (l.ContractId, Key: l.Link.MeteringPointIdentification ?? l.Link.MeteringPointId.ToString()))
            .Select(g => g.First())
            .GroupBy(l => l.ContractId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(
                l => l.Link.MeteringPointIdentification ?? l.Link.MeteringPointId.ToString(),
                StringComparer.Ordinal));

        // ---- Phase 2: fire ALL Settl I/O in parallel ----
        // All GETs for this representative are independent — customer detail, price-list associations,
        // customer→MP links, all payer details, all payer→MP links, all MP billing+versions, and all
        // contract→MP links can be in-flight concurrently. This eliminates the sequential latency chain
        // that dominated the diff phase (previously: O(MPs × 2 + payers × 2 + contracts) serial GETs).

        Task<(CustomerResponse?, string?)>? customerTask = custGuid is { } custFetchId
            ? _customerClient.GetByIdAsync(custFetchId, ct) : null;

        Task<(List<CustomerPriceListDto>?, string?)>? priceListTask =
            priceListEnabled && custGuid is { } plFetchId
            ? _priceListClient!.GetCustomerPriceListsAsync(plFetchId, ct) : null;

        Task<(List<CustomerMeteringPointDto>?, string?)>? custLinksTask = custGuid is { } custLinksFetchId
            ? _customerClient.GetMeteringPointsAsync(custLinksFetchId, ct) : null;

        // Payer details — only for payers with a known Settl GUID.
        var payerDetailTasks = up.Payers
            .Where(kv => payerIdByIdentification.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key,
                kv => _payerClient.GetByIdAsync(payerIdByIdentification[kv.Key], null, ct));

        // Payer→MP links — only for payers with a known Settl GUID.
        var payerLinkTasks = up.Payers.Keys
            .Where(id => payerIdByIdentification.ContainsKey(id))
            .ToDictionary(id => id,
                id => _payerClient.GetMeteringPointsAsync(payerIdByIdentification[id], ct));

        // MP billing + versions — current GET and history GET fire in parallel per MP; version GETs
        // (when history has >1 entry) also fire in parallel. Only MPs present in Settl are fetched.
        var mpBillingTasks = up.MeteringPoints
            .Where(kv => inDelivery.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key,
                kv => LoadAllMpBillingAsync(inDelivery[kv.Key].Id, ct));

        // Contract→MP links — all contracts in parallel.
        var contractLinkTasks = contractLinksByContractId.ToDictionary(
            kv => kv.Key,
            kv => _contractClient.GetContractMeteringPointsAsync(kv.Key, ct));

        // Reverse index: every contract this representative's MPs are ACTUALLY linked to in Settl —
        // including contracts the intended plan never names. One GET per representative. Without it a
        // metering point hung on the wrong contract is invisible: the per-contract loop below only asks
        // about contracts the plan names, so the intended link surfaces as MissingInSettl while the wrong
        // link is silent. The endpoint returns only currently-active MP links, so ended (historic) links
        // are not resurrected as drift.
        Task<(List<CustomerContractWithMeteringPointsDto>? Contracts, string? Error)>? actualContractsTask =
            custGuid is { } ccFetchId ? _contractClient.GetCustomerMeteringPointContractsAsync(ccFetchId, ct) : null;

        // Await everything before processing.
        {
            var allTasks = new List<Task>();
            if (customerTask != null) allTasks.Add(customerTask);
            if (priceListTask != null) allTasks.Add(priceListTask);
            if (custLinksTask != null) allTasks.Add(custLinksTask);
            allTasks.AddRange(payerDetailTasks.Values);
            allTasks.AddRange(payerLinkTasks.Values);
            allTasks.AddRange(mpBillingTasks.Values);
            allTasks.AddRange(contractLinkTasks.Values);
            if (actualContractsTask != null) allTasks.Add(actualContractsTask);
            if (allTasks.Count > 0) await Task.WhenAll(allTasks);
        }

        // ---- Phase 3: process results into rows ----

        var rows = new List<ReconcileDriftRow>();

        // Customer
        if (custGuid is not { } custId)
        {
            rows.Add(new ReconcileDriftRow(rep, "Customer", rep, "*", "create payload", null, DriftKind.MissingInSettl,
                IntendedJson: JsonSerializer.Serialize(up.Customer, IntendedJsonOptions)));
        }
        else
        {
            var (actualCust, _) = customerTask!.Result;
            if (actualCust != null)
                rows.AddRange(Compare("Customer", rep, "", up.Customer, actualCust, ExcludesWithCustomer, ""));
        }

        // Price-list associations (optional). Diff the create-true intended set against the customer's
        // actual Settl associations on the (PriceList, ProductElement) key, under SubType "PriceList":
        //   • MISSING (MissingInSettl) — intended association absent in Settl (create/import would add).
        //   • EXTRA   (Anomaly)        — Settl association the intended set doesn't justify.
        //   • PERIOD  (FieldDiff)      — matched association whose validity window differs. Intended is
        //     (COS .. contract-part-end) — the same COS-collapsed window the import wrote; actual is
        //     Settl's AssociationPeriod (DK-converted). Open ends (1900/9999 sentinels) fold equal.
        if (priceListEnabled && custGuid is { } plCustId)
        {
            var (actualAssocs, plErr) = priceListTask!.Result;
            if (plErr == null)
            {
                var intended = intendedPriceListsByRep.GetValueOrDefault(rep) ?? [];
                // Intended (priceListGuid, productElementGuid) → display + intended window.
                var intendedByKey = new Dictionary<(Guid PriceList, Guid ProductElement), (string PeCode, string PlName, DateOnly Start, DateOnly End)>();
                foreach (var a in intended)
                {
                    if (!Guid.TryParse(a.PriceListIdentifier, out var plGuid)) continue;
                    if (!productElementCodeToId.TryGetValue(a.ProductElementCode, out var peGuid))
                    {
                        // Intended references a product element Settl's catalog doesn't know — it can
                        // never match an actual association, so it's missing-in-Settl by construction.
                        rows.Add(new ReconcileDriftRow(rep, "Customer", rep, a.ProductElementCode,
                            $"{priceListIdToName.GetValueOrDefault(plGuid, a.PriceListIdentifier)} — {FormatPeriod(DateOnly.FromDateTime(a.Start), DateOnly.FromDateTime(a.End))}", null,
                            DriftKind.MissingInSettl, SubType: "PriceList"));
                        continue;
                    }
                    intendedByKey[(plGuid, peGuid)] = (
                        a.ProductElementCode,
                        priceListIdToName.GetValueOrDefault(plGuid, a.PriceListIdentifier),
                        DateOnly.FromDateTime(a.Start), DateOnly.FromDateTime(a.End));
                }

                var actualByKey = new Dictionary<(Guid, Guid), CustomerPriceListDto>();
                foreach (var a in actualAssocs ?? [])
                    actualByKey[(a.PriceListId, a.ProductElementId)] = a;

                foreach (var (key, disp) in intendedByKey)
                {
                    if (!actualByKey.TryGetValue(key, out var actual))
                    {
                        rows.Add(new ReconcileDriftRow(rep, "Customer", rep, disp.PeCode,
                            $"{disp.PlName} — {FormatPeriod(disp.Start, disp.End)}", null, DriftKind.MissingInSettl, SubType: "PriceList"));
                        continue;
                    }
                    // Matched → compare the validity window (Start = COS, plus End).
                    var aStart = DkDate(actual.AssociationPeriod?.Start);
                    var aEnd = DkDate(actual.AssociationPeriod?.End);
                    if (aStart != disp.Start || !EndEquivalent(disp.End, aEnd))
                        rows.Add(new ReconcileDriftRow(rep, "Customer", rep, $"{disp.PeCode} ({disp.PlName})",
                            FormatPeriod(disp.Start, disp.End), FormatPeriod(aStart, aEnd),
                            DriftKind.FieldDiff, SubType: "PriceList"));
                    else if (fullPayload)
                        rows.Add(new ReconcileDriftRow(rep, "Customer", rep, $"{disp.PeCode} ({disp.PlName})",
                            FormatPeriod(disp.Start, disp.End), FormatPeriod(aStart, aEnd),
                            DriftKind.Match, SubType: "PriceList"));
                }

                foreach (var (key, a) in actualByKey)
                    if (!intendedByKey.ContainsKey(key))
                        rows.Add(new ReconcileDriftRow(rep, "Customer", rep,
                            a.ProductElementId.ToString(), null,
                            priceListIdToName.GetValueOrDefault(a.PriceListId, a.PriceListId.ToString()),
                            DriftKind.Anomaly, SubType: "PriceList"));
            }
        }

        // Settlement Group membership. Create adds the *customer* (not the MP) to each group its
        // contracts map to (SettlClient.AssignSettlementGroups). Two directions are checked, scoped to
        // the migration's managed group universe (settlementGroupMembers); memberships in groups
        // outside the settlement-group scheme are intentionally not considered.
        //   • MISSING (MissingInSettl) — an expected group the rep is NOT a member of, or that does
        //     not exist in Settl. This is drift create would fix on a re-run (create only ADDs).
        //   • EXTRA (Anomaly) — the rep IS a member of a managed group its contracts do NOT justify,
        //     so create would never assign it. Not fixable by re-running create (it never removes) —
        //     a manual-edit / mapping-churn / stale-older-run signal, so flagged as an Anomaly rather
        //     than MissingInSettl (which would falsely imply "create will fix this").
        // Skipped when the customer itself is missing (the Customer-missing row above already covers it).
        if (custGuid is { } groupCustId)
        {
            var expected = new HashSet<string>(
                settlementGroupsByRep.GetValueOrDefault(rep) ?? [], StringComparer.OrdinalIgnoreCase);

            foreach (var groupName in expected)
            {
                if (!settlementGroupMembers.TryGetValue(groupName, out var members))
                {
                    // Expected group does not exist in Settl — create's settlement-group preflight would error.
                    rows.Add(new ReconcileDriftRow(rep, "Customer", rep, groupName,
                        "group exists + member", "group missing in Settl", DriftKind.MissingInSettl, SubType: "SettlementGroup"));
                    continue;
                }
                if (!members.Contains(groupCustId))
                    rows.Add(new ReconcileDriftRow(rep, "Customer", rep, groupName,
                        "member", null, DriftKind.MissingInSettl, SubType: "SettlementGroup",
                        // The intended group IS this row's Field (the scan only emits a member-missing row
                        // for an expected group). Carry it so reconcile-apply trusts it without a capture.
                        IntendedJson: JsonSerializer.Serialize(groupName, IntendedJsonOptions)));
                else if (fullPayload)
                    rows.Add(new ReconcileDriftRow(rep, "Customer", rep, groupName,
                        "member", "member", DriftKind.Match, SubType: "SettlementGroup"));
            }

            foreach (var (groupName, members) in settlementGroupMembers)
                if (!expected.Contains(groupName) && members.Contains(groupCustId))
                    rows.Add(new ReconcileDriftRow(rep, "Customer", rep, groupName,
                        null, "member (create would not assign)", DriftKind.Anomaly, SubType: "SettlementGroup"));
        }

        // Payers
        foreach (var (payerId, payerDto) in up.Payers)
        {
            // Identity resolved from the env-wide payer map (no per-payer Search round-trip).
            if (!payerIdByIdentification.ContainsKey(payerId))
            {
                rows.Add(new ReconcileDriftRow(rep, "Payer", payerId, "*", "create payload", null, DriftKind.MissingInSettl,
                    IntendedJson: JsonSerializer.Serialize(payerDto, IntendedJsonOptions)));
                continue;
            }
            var (actualPayer, _) = payerDetailTasks[payerId].Result;
            if (actualPayer != null)
            {
                // Non-billing payer fields (PayerBilling excluded — diffed separately below).
                rows.AddRange(Compare("Payer", payerId, "", payerDto, actualPayer, ExcludesWithPayerBilling, ""));

                // Payer-billing fields. PayerBillingDto now models the full create-payload field
                // set (all e-invoice references included), so a typed diff covers every billing
                // field — no raw-JSON workaround needed. Both sides serialise with Never (null keys
                // present), so a value-vs-null difference surfaces under intersection, symmetric
                // with the MP-billing diffs below.
                // Exception: VatIdentificationNumber (create/PUT name) ↔ OrganizationIdentifier
                // (GET response name) — Settl's API has a field-name asymmetry here, so the typed
                // intersection diff silently drops both. Compared explicitly below.
                if (payerDto.PayerBilling != null || actualPayer.PayerBilling != null)
                {
                    rows.AddRange(Compare("Payer", payerId, "PayerBilling.", payerDto.PayerBilling, actualPayer.PayerBilling,
                        new HashSet<string>(JsonValueDiff.DefaultExcludes, StringComparer.OrdinalIgnoreCase) { "OrganizationIdentifier", "Start" }, "Billing"));

                    var intendedCvr = payerDto.PayerBilling?.VatIdentificationNumber;
                    var actualCvr = actualPayer.PayerBilling?.OrganizationIdentifier;
                    if (fullPayload || intendedCvr != actualCvr)
                        rows.Add(new ReconcileDriftRow(rep, "Payer", payerId, "PayerBilling.VatIdentificationNumber",
                            intendedCvr, actualCvr, LeafKind(intendedCvr, actualCvr), SubType: "Billing",
                            IntendedJson: intendedCvr != null ? JsonSerializer.Serialize(intendedCvr, IntendedJsonOptions) : null));
                }
            }
        }

        // Metering points: master (projector) + billing (JSON) + exemptions + existence
        foreach (var (mpId, planned) in up.MeteringPoints)
        {
            if (!inDelivery.TryGetValue(mpId, out var actualMp))
            {
                rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "exists", "expected from Xellent", null, DriftKind.MissingInSettl));
                continue;
            }

            // MP master uses an explicit projector (the actual market-v3 list shape only exposes ~8
            // field values, so a property-name diff against the intended MeteringPointDto can't align).
            // In full-payload mode every projected field is emitted (matched ones tagged Match); the
            // scan emits only the differing ones.
            foreach (var (field, intended, actual) in ProjectMpMaster(planned.Master, actualMp.Info))
                if (fullPayload || !string.Equals(intended, actual, StringComparison.Ordinal))
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, field, intended, actual, LeafKind(intended, actual)));

            // Billing + exemptions. LoadAllMpBillingAsync fired current GET + history GET in parallel
            // and pre-fetched any historical versions in parallel — result is already awaited above.
            var (billing, billingVersions, paymentVersions) = mpBillingTasks[mpId].Result;
            if (planned.Billing != null)
            {
                if (billing == null)
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "*", "create payload", null, DriftKind.MissingInSettl, SubType: "Billing",
                        IntendedJson: JsonSerializer.Serialize(planned.Billing, IntendedJsonOptions)));
                else
                {
                    // Multi-version: diff intended against EVERY historical BillingConfiguration and
                    // PaymentConfiguration version (the two timelines version independently). The
                    // current/latest version of each keeps the bare field path — byte-identical to the
                    // single-version scan — while historical versions are tagged "<Config>@<versionStart>."
                    // so reconcile-apply can PUT each version's own Start in place (a PUT carrying the
                    // version's own unchanged Start targets that entry in Settl, never the current one).
                    // Historical versions exclude Start + End (see HistoricalBillingVersionExcludes):
                    // those are period-management fields that always differ from intended and cannot be
                    // rewritten — a PUT with a different Start would split the timeline, not update the entry.
                    foreach (var (start, isCurrent, cfg) in billingVersions)
                    {
                        if (isCurrent && cfg == null)
                        {
                            // BillingConfiguration absent entirely — emit a single MissingInSettl row
                            // instead of per-field diffs that would show misleading 0001-01-01 sentinel
                            // values (default(DateOnly)) and mask the real root cause.
                            rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "BillingConfiguration",
                                "create payload", null, DriftKind.MissingInSettl, SubType: "Billing",
                                IntendedJson: JsonSerializer.Serialize(planned.Billing.BillingConfiguration, IntendedJsonOptions)));
                            continue;
                        }
                        rows.AddRange(Compare("MeteringPoint", mpId,
                            isCurrent ? "BillingConfiguration." : MpConfigVersionTag.Prefix("BillingConfiguration", start),
                            planned.Billing.BillingConfiguration, cfg,
                            isCurrent ? null : HistoricalBillingVersionExcludes, "Billing"));
                    }

                    foreach (var (start, isCurrent, cfg) in paymentVersions)
                    {
                        if (planned.Billing.PaymentConfiguration == null && cfg == null) continue;
                        if (isCurrent && cfg == null)
                        {
                            rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "PaymentConfiguration",
                                "create payload", null, DriftKind.MissingInSettl, SubType: "Billing",
                                IntendedJson: JsonSerializer.Serialize(planned.Billing.PaymentConfiguration, IntendedJsonOptions)));
                            continue;
                        }
                        rows.AddRange(Compare("MeteringPoint", mpId,
                            isCurrent ? "PaymentConfiguration." : MpConfigVersionTag.Prefix("PaymentConfiguration", start),
                            planned.Billing.PaymentConfiguration, cfg,
                            isCurrent ? null : HistoricalBillingVersionExcludes, "Billing"));
                    }
                }
            }
            foreach (var ex in planned.Exemptions)
            {
                var present = billing?.Exemptions?.Any(e => e.ProductElementId == ex.ProductElementId) == true;
                if (!present)
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, ex.ProductElementId.ToString(),
                        $"{ex.ExemptionPercentage}% exempt", null, DriftKind.MissingInSettl, SubType: "Exemption",
                        IntendedJson: JsonSerializer.Serialize(ex, IntendedJsonOptions)));
                else if (fullPayload)
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, ex.ProductElementId.ToString(),
                        $"{ex.ExemptionPercentage}% exempt", $"{ex.ExemptionPercentage}% exempt", DriftKind.Match, SubType: "Exemption"));
            }
        }

        // Customer→MP link presence.
        if (custGuid is { } cg)
        {
            var (custMps, _) = custLinksTask!.Result;
            var actualLinkedIds = (custMps ?? [])
                .Select(m => guidToMpId.GetValueOrDefault(m.MeteringPointId))
                .Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal)!;
            foreach (var mpId in up.CustomerLinks.Keys.Where(id => !actualLinkedIds.Contains(id)))
                rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "linked", "create payload", null, DriftKind.MissingInSettl, SubType: "CustomerLink",
                    IntendedJson: JsonSerializer.Serialize(up.CustomerLinks[mpId], IntendedJsonOptions)));
            if (fullPayload)
                foreach (var mpId in up.CustomerLinks.Keys.Where(actualLinkedIds.Contains))
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "linked", "create payload", "linked", DriftKind.Match, SubType: "CustomerLink"));
        }

        // Payer→MP link presence + correctness. Resolve which payer each in-scope MP is ACTUALLY linked
        // to in Settl (over the rep's own payers), then compare against the intended links. Three signals:
        //   • intended link absent from every one of the rep's payers       → MissingInSettl
        //   • MP linked to a DIFFERENT one of the rep's payers than intended → FieldDiff (re-link drift)
        //   • actual link the intended set does not contain                  → MissingInXellent (extra)
        // Only the rep's own payers are queried, so an MP linked to a payer outside the rep reads as
        // MissingInSettl — the missing link is still surfaced, just not attributed to the foreign payer.
        // An actual link to an MP outside the reconcile universe (not InDelivery → absent from guidToMpId)
        // is dropped, consistent with the customer-link check.
        var actualPayerByMpId = new Dictionary<string, string>(StringComparer.Ordinal);
        var actualPayerLinks = new HashSet<(string PayerId, string MpId)>();
        foreach (var payerId in up.Payers.Keys)
        {
            if (!payerIdByIdentification.ContainsKey(payerId)) continue;
            var (payerMps, _) = payerLinkTasks[payerId].Result;
            foreach (var m in payerMps ?? [])
            {
                var mpId = guidToMpId.GetValueOrDefault(m.MeteringPointId);
                if (mpId == null) continue;
                actualPayerByMpId[mpId] = payerId;
                actualPayerLinks.Add((payerId, mpId));
            }
        }

        foreach (var (mpId, planned) in up.PayerLinks)
        {
            var intendedPayer = planned.PayerIdentifier;
            var actualPayer = actualPayerByMpId.GetValueOrDefault(mpId);
            if (actualPayer == null)
                rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "linked",
                    $"payer {intendedPayer}", null, DriftKind.MissingInSettl, SubType: "PayerLink",
                    IntendedJson: JsonSerializer.Serialize(planned.Link, IntendedJsonOptions)));
            else if (!string.Equals(actualPayer, intendedPayer, StringComparison.Ordinal))
                rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "linked",
                    $"payer {intendedPayer}", $"payer {actualPayer}", DriftKind.FieldDiff, SubType: "PayerLink"));
            else if (fullPayload)
                rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "linked",
                    $"payer {intendedPayer}", $"payer {actualPayer}", DriftKind.Match, SubType: "PayerLink"));
        }

        // Extra actual payer→MP links the intended set does not contain. An MP intended to link to a
        // different payer is already reported as a re-link FieldDiff above (its key IS in PayerLinks), so
        // it is not double-counted here.
        foreach (var (payerId, mpId) in actualPayerLinks)
            if (!up.PayerLinks.ContainsKey(mpId))
                rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpId, "linked",
                    null, $"payer {payerId}", DriftKind.MissingInXellent, SubType: "PayerLink"));

        // Contract→MP link presence + end-date check.
        foreach (var (contractId, intendedByKey) in contractLinksByContractId)
        {
            var (actualMps, _) = contractLinkTasks[contractId].Result;
            var actualByKey = (actualMps ?? [])
                .GroupBy(m => NormWaterId(m.MeteringPointIdentification ?? m.MeteringPointId.ToString()), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            foreach (var (mpKey, planned) in intendedByKey)
            {
                var intendedEnd = planned.Link.End;
                var intendedStr = intendedEnd == MigrationConstants.MeteringPointEndDate ? "open-ended" : intendedEnd?.ToString("yyyy-MM-dd");

                // Normalise mpKey for the lookup: planned link uses Settl "V{instnum}01" format
                // (same as the domain MP identification), actualByKey normalised via NormWaterId.
                if (!actualByKey.TryGetValue(NormWaterId(mpKey), out var actual))
                {
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpKey,
                        $"ContractLink[{contractId}]", intendedStr, null, DriftKind.MissingInSettl, SubType: "ContractLink"));
                    continue;
                }

                var actualStr = actual.End.Year >= 9999 ? "open-ended" : actual.End.ToString("yyyy-MM-dd");
                if (fullPayload || intendedStr != actualStr)
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpKey,
                        $"ContractLink[{contractId}].End", intendedStr, actualStr,
                        LeafKind(intendedStr, actualStr), SubType: "ContractLink"));
            }
        }

        // Contract→MP links Settl holds that the plan does not intend → Anomaly (extra in Settl).
        // This is the "metering point on the wrong contract" signal: create's fallback to the
        // contract-part product name can land an MP on a real-but-unintended Settl contract, and that
        // contract is absent from the intended plan, so the loop above never even fetched it. The
        // intended link is already reported as MissingInSettl; this row is its counterpart, and
        // reconcile-apply deletes it (after the intended link has been added — see ApplyContractLinksAsync).
        if (actualContractsTask != null)
        {
            var intendedPairs = new HashSet<(Guid ContractId, string MpKey)>();
            foreach (var (cid2, intendedByKey) in contractLinksByContractId)
                foreach (var mpKey in intendedByKey.Keys)
                    intendedPairs.Add((cid2, NormWaterId(mpKey)));

            var (actualContracts, _) = actualContractsTask.Result;
            var now = DateTimeOffset.UtcNow;
            foreach (var contract in actualContracts ?? [])
            {
                foreach (var m in contract.MeteringPoints)
                {
                    var rawId = m.MeteringPointIdentification ?? "";
                    if (rawId.Length == 0) continue;
                    var mpKey = NormWaterId(rawId);
                    // Only MPs this reconcile universe covers. Water MPs are keyed by the Settl
                    // "V{instnum}01" form in the plan's MP set but by the bare Instnum in its contract
                    // links, so both spellings are accepted here.
                    if (!up.MeteringPoints.ContainsKey(mpKey) && !up.MeteringPoints.ContainsKey(rawId)) continue;
                    if (intendedPairs.Contains((contract.Id, mpKey))) continue;
                    // Already-ended link: history, not actionable drift — never propose deleting it.
                    if (m.End < now) continue;
                    var endLabel = m.End.Year >= 9999 ? "open-ended" : m.End.ToString("yyyy-MM-dd");
                    rows.Add(new ReconcileDriftRow(rep, "MeteringPoint", mpKey,
                        $"ContractLink[{contract.Id}]", null,
                        $"linked to '{contract.ContractName}' (end {endLabel})",
                        DriftKind.Anomaly, SubType: "ContractLink"));
                }
            }
        }

        return rows;
    }

    // ---- MP billing: parallel current + history fetch, parallel version GETs ----

    /// <summary>
    /// Fetches an MP's current billing and its full version history in parallel, then fetches any
    /// historical versions also in parallel. MPs with a single billing version cost one extra GET
    /// (the history call) beyond the current-billing GET; that overhead is hidden behind the parallel
    /// wall because both fire at the same time. MPs with N>1 versions save N sequential GET round-trips
    /// (one per version) by fetching all unique dates concurrently.
    /// </summary>
    private async Task<(
        BillingMeteringPointCurrentDto? Current,
        List<(DateOnly Start, bool IsCurrent, BillingConfigurationDto? Config)> BillingVersions,
        List<(DateOnly Start, bool IsCurrent, MeteringPointPaymentConfigurationDto? Config)> PaymentVersions)>
        LoadAllMpBillingAsync(Guid settlMpId, CancellationToken ct)
    {
        // Fire current-billing GET and history GET in parallel — both are unconditional.
        var currentTask = _mpClient.GetBillingByIdAsync(settlMpId, null, ct);
        var histTask = _mpClient.GetBillingHistoryAsync(settlMpId, ct);
        await Task.WhenAll(currentTask, histTask);
        var (current, _) = currentTask.Result;
        var (hist, _) = histTask.Result;

        if (current == null)
            return (null, [], []);

        var billingDates = (hist?.BillingConfigurationDates ?? []).OrderBy(d => d).ToList();
        var paymentDates = (hist?.PaymentConfigurationDates ?? []).OrderBy(d => d).ToList();

        // Build version lists. With ≤1 history date per timeline, return current alone (no extra GETs).
        // With >1 dates on either timeline, fetch all unique dates in parallel and extract sub-configs.
        // Deduping dates avoids a redundant GET when the same date appears in both timelines.
        List<(DateOnly, bool, BillingConfigurationDto?)> billingVersions;
        List<(DateOnly, bool, MeteringPointPaymentConfigurationDto?)> paymentVersions;

        if (billingDates.Count <= 1 && paymentDates.Count <= 1)
        {
            billingVersions = [(current.BillingConfiguration?.Start ?? default, true, current.BillingConfiguration)];
            paymentVersions = [(current.PaymentConfiguration?.Start ?? default, true, current.PaymentConfiguration)];
        }
        else
        {
            // Fetch all unique version dates in parallel; each GET returns the full billing DTO at that
            // effective date, from which both BillingConfiguration and PaymentConfiguration are extracted.
            var allDates = billingDates.Union(paymentDates).Distinct().ToList();
            var versionResults = await Task.WhenAll(allDates.Select(d => _mpClient.GetBillingByIdAsync(settlMpId, d, ct)));
            var versionByDate = allDates
                .Zip(versionResults, (d, r) => (d, dto: r.Item1))
                .ToDictionary(x => x.d, x => x.dto);

            var currentBillingStart = current.BillingConfiguration?.Start;
            billingVersions = billingDates.Count <= 1
                ? [(currentBillingStart ?? default, true, current.BillingConfiguration)]
                : billingDates.Select(d =>
                {
                    var cfg = versionByDate.GetValueOrDefault(d)?.BillingConfiguration;
                    var start = cfg?.Start ?? DateOnly.FromDateTime(d);
                    return (start, currentBillingStart != null && start == currentBillingStart, cfg);
                }).ToList();

            var currentPaymentStart = current.PaymentConfiguration?.Start;
            paymentVersions = paymentDates.Count <= 1
                ? [(currentPaymentStart ?? default, true, current.PaymentConfiguration)]
                : paymentDates.Select(d =>
                {
                    var cfg = versionByDate.GetValueOrDefault(d)?.PaymentConfiguration;
                    var start = cfg?.Start ?? DateOnly.FromDateTime(d);
                    return (start, currentPaymentStart != null && start == currentPaymentStart, cfg);
                }).ToList();
        }

        return (current, billingVersions, paymentVersions);
    }

    // ---- MP master projector (intended MeteringPointDto vs actual market-v3 MeteringPointInfoDto) ----

    private static IEnumerable<(string Field, string? Intended, string? Actual)> ProjectMpMaster(MeteringPointDto intended, MeteringPointInfoDto actual)
    {
        yield return ("Identification", intended.Identification?.MeteringPointIdentification, actual.MeteringPointIdentification);
        yield return ("InDelivery", intended.DeliveryStatus?.InDelivery.ToString(), actual.InDelivery?.ToString());
        yield return ("ConnectionStatus", intended.ConnectionInfo?.ConnectionStatus, actual.ConnectionStatus);
        yield return ("SettlementMethod", intended.SettlementMethod?.SettlementMethod, actual.SettlementMethodCode);
        yield return ("AdditionalInfo1", intended.AdditionalInfo1, actual.AdditionalInfo1);
        yield return ("MarketBalanceAreaId", intended.Grid?.MarketBalanceAreaId?.ToString(), actual.MarketBalanceAreaId?.ToString());
        yield return ("MeteringGridAreaId", intended.Grid?.MeteringGridAreaId?.ToString(), actual.MeteringGridAreaId?.ToString());
        // EstimatedAnnualVolume is emitted but the intended side is ALWAYS null: MapperOptions
        // .IncludeEstimatedAnnualVolume is off by default and nothing in DataMigration turns it on.
        // That is deliberate — the market overwrites EAV in Settl once delivery activates, so a
        // migrated value would not survive, and the Xellent lookup behind it is the most expensive
        // read in the metering-point map. Consequence: any MP carrying an EAV in Settl reports as
        // drift here, permanently and by design. DriftCategorizer has its own "EstimatedAnnualVolume"
        // bucket for exactly that noise.
        //
        // Do NOT write an integration test asserting that a mutated EAV is detected and then restored
        // by reconcile-apply. One existed (EavReconcileFlowTests, removed 2026-09-02) and could not
        // pass: apply has no intended value to restore, the only drift-free state is EAV absent,
        // UpdateEstimatedAnnualVolumeAsync cannot write null, and Settl has no delete for it.
        yield return ("EstimatedAnnualVolume",
            intended.MeterCharacteristics?.EstimatedAnnualVolume is { } iv ? iv.Value.ToString(CultureInfo.InvariantCulture) : null,
            actual.EstimatedAnnualVolume is { } av ? av.Value.ToString(CultureInfo.InvariantCulture) : null);
    }

    // Customer diff excludes Start (Address.Start and Owner.Start are version boundaries — customer has
    // /History + ?EffectiveDate= versioning identical to payer; a PUT with a different Start creates a new
    // version instead of updating the existing one).
    private static readonly IReadOnlySet<string> ExcludesWithCustomer =
        new HashSet<string>(JsonValueDiff.DefaultExcludes, StringComparer.OrdinalIgnoreCase) { "Start" };

    // Typed payer diff excludes the PayerBilling subtree (diffed separately for SubType tagging) and
    // Start (period boundary — same timeline-split risk as BillingConfiguration.Start; payer also has
    // /History + ?EffectiveDate= versioning so a PUT with a new Start creates a new version).
    private static readonly IReadOnlySet<string> ExcludesWithPayerBilling =
        new HashSet<string>(JsonValueDiff.DefaultExcludes, StringComparer.OrdinalIgnoreCase) { "PayerBilling", "Start" };

    // Historical billing/payment versions: exclude Start and End. These are period-management fields —
    // Start identifies the version boundary, End is set by Settl when the next version begins. Comparing
    // them against the intended (COS-date Start, null End) always produces false-positive DateStart drift
    // on every historical entry. apply cannot safely rewrite Start either (a PUT with a new Start creates
    // a new Settl version instead of updating the existing one). Content fields (Currency, JointInvoice,
    // ElectronicNumber, etc.) are still diffed.
    private static readonly IReadOnlySet<string> HistoricalBillingVersionExcludes =
        new HashSet<string>(JsonValueDiff.DefaultExcludes, StringComparer.OrdinalIgnoreCase) { "Start", "End" };

    // ---- CSV output ----

    private static void WriteCsv(TextWriter output, List<ReconcileDriftRow> rows)
    {
        output.WriteLine(string.Join(";", new[] { "Category", "RepresentativeAccount", "RepresentativeName", "MemberAccounts", "MemberNames", "Entity", "SubType", "EntityKey", "EntityName", "COS", "Field", "Intended", "Actual", "Kind", "IntendedJson" }.Select(Csv)));
        foreach (var r in rows
            .OrderBy(r => DriftCategorizer.Categorize(r), StringComparer.Ordinal)
            .ThenBy(r => r.RepresentativeAccount, StringComparer.Ordinal)
            .ThenBy(r => r.Entity, StringComparer.Ordinal)
            .ThenBy(r => r.SubType, StringComparer.Ordinal)
            .ThenBy(r => r.EntityKey, StringComparer.Ordinal)
            .ThenBy(r => r.Field, StringComparer.Ordinal))
        {
            output.WriteLine(string.Join(";", new[]
            {
                DriftCategorizer.Categorize(r), r.RepresentativeAccount, r.RepresentativeName ?? "", r.MemberAccounts ?? "", r.MemberNames ?? "", r.Entity, r.SubType, r.EntityKey, r.EntityName ?? "", CosCell(r), r.Field, r.Intended ?? "", r.Actual ?? "", r.Kind.ToString(), r.IntendedJson ?? ""
            }.Select(Csv)));
        }
    }

    /// <summary>
    /// The COS cell for a row: a MeteringPoint row shows its own single COS; any other row
    /// (Customer/Payer) shows the representative's COS period (<c>min .. max</c>), collapsed to a
    /// single date when min == max, blank when the representative has no COS. Mirrors what the
    /// dashboard renders from the same CosFrom/CosTo fields.
    /// </summary>
    internal static string CosCell(ReconcileDriftRow r)
    {
        if (r.Entity == "MeteringPoint")
            return r.Cos?.ToString("yyyy-MM-dd") ?? "";
        if (r.CosFrom is not { } from || r.CosTo is not { } to)
            return "";
        return from == to
            ? from.ToString("yyyy-MM-dd")
            : $"{from:yyyy-MM-dd} .. {to:yyyy-MM-dd}";
    }

    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    // ---- internal aggregation types ----

    /// <summary>One COS slice's create-true capture, computed concurrently in step 5 and folded into
    /// the shared accumulators sequentially afterwards. <paramref name="PriceLists"/> is null when
    /// price-list reconcile is disabled.</summary>
    private sealed record SliceCapture(
        HashSet<string> MpIds,
        List<SettlMigrationPlan> Plans,
        MigrationReport Report,
        Dictionary<string, List<CustomerPriceListAssociationRow>>? PriceLists);

    // SettlMpWithCos lives in Services/Reconcile/SettlMeteringPointCosLoader.cs — shared with payment-match.

    private sealed class UnionedPlan(string customerNumber, CreateCustomerDto customer)
    {
        public string CustomerNumber { get; } = customerNumber;
        public CreateCustomerDto Customer { get; } = customer;
        public Dictionary<string, CreatePayerDto> Payers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PlannedMeteringPoint> MeteringPoints { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, CustomerMeteringPointDto> CustomerLinks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, PlannedPayerMpLink> PayerLinks { get; } = new(StringComparer.Ordinal);
        public List<PlannedContractMpLink> ContractLinks { get; } = [];
    }
}
