using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DataMigration.Core;
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
/// Writes selected reconcile drift rows back to Settl. The intended value is the create-true payload
/// — rebuilt here via a <b>scoped</b> capture (one Xellent load + <see cref="CreateRunner"/> capture
/// per selected representative, at <c>refDate = COS-1</c> over the merge-group's accounts), NOT a
/// re-run of the env-wide reconcile scan. That reproduces exactly what env-wide reconcile produced
/// for the representative (same builders), but in seconds instead of minutes.
///
/// <para>Phase 1 covers the drift classes that have a Settl write API:</para>
/// <list type="bullet">
///   <item><b>Payer billing</b> (FieldDiff, <c>PayerBilling.*</c>) — re-validate against current
///     (<see cref="ISettlPayerClient.GetByIdAsync"/>), then write the drifted leaves via
///     <see cref="ISettlPayerClient.UpdatePayerBillingAsync"/>, which owns Settl's flat update shape
///     (leaving all other payer fields + the customer↔payer link verbatim).</item>
///   <item><b>MP billing</b> (FieldDiff, <c>BillingConfiguration.*</c> / <c>PaymentConfiguration.*</c>)
///     — <see cref="ISettlMeteringPointClient.UpdateBillingAsync"/>: carry the current config (so
///     Start/LifeTime/End stay → in-place update, no new historical period) and overwrite only the
///     drifted leaves.</item>
///   <item><b>MP exemption</b> (MissingInSettl) —
///     <see cref="ISettlMeteringPointClient.AddExemptionsAsync"/> (additive).</item>
///   <item><b>SettlementGroup membership</b> (Customer, MissingInSettl) —
///     <see cref="ISettlSettlementGroupClient.AddCustomersAsync"/> (additive). Only the
///     "member missing" variant (group exists, customer isn't in it) is applied; the
///     "group missing in Settl" variant is Skipped — neither create nor this applier creates groups.</item>
///   <item><b>Customer→MP link</b> (MeteringPoint, SubType CustomerLink, MissingInSettl) —
///     <see cref="ISettlCustomerClient.AddMeteringPointsAsync"/> (additive — mirrors create's step-4).</item>
///   <item><b>MP annual volume</b> (MeteringPoint, FieldDiff, <c>EstimatedAnnualVolume</c>) —
///     <see cref="ISettlMeteringPointClient.UpdateEstimatedAnnualVolumeAsync"/>: a full-echo master PUT
///     that touches only the master <c>MeterCharacteristics.EstimatedAnnualVolume</c> (the field create
///     writes and the scan reads), effective from today. NOT the annualConsumptions endpoint, which
///     writes a separate <c>HistoricAnnualConsumption</c> field nothing in reconcile reads. Other MP
///     master fields have no safe single-field API and stay Skipped.</item>
///   <item><b>Contract→MP link</b> (MeteringPoint, SubType ContractLink) — MissingInSettl posts the
///     intended link, FieldDiff deletes and re-links with the intended end date, and Anomaly (a link
///     Settl holds that the plan does not intend) is DELETED via
///     <see cref="ISettlContractClient.RemoveContractMeteringPointAsync"/>. The only Anomaly class with a
///     write path — everything else extra-in-Settl stays manual. Intended links are written before the
///     deletes, so a metering point is never left without a contract mid-run.</item>
///   <item><b>Missing whole entity</b> (Customer/Payer/MeteringPoint, SubType "", MissingInSettl) —
///     a scoped <see cref="CreateRunner"/> write-run over the merge-group (idempotent search-or-create);
///     the only create-true path to materialise an absent entity + its links.</item>
///   <item><b>Customer / payer core</b> (Customer/Payer, FieldDiff, SubType "") — a full-object
///     read-modify-write master PUT: customer via the wrapped v3 endpoint
///     (<see cref="ISettlCustomerClient.UpdateV3RawAsync"/>), payer via the flat v1 endpoint
///     (<see cref="ISettlPayerClient.UpdateRawAsync"/>, which echoes CustomerId → link-safe). Echoes the
///     current entity back verbatim and overwrites only the drifted leaves (carried as IntendedJson),
///     navigating the live JSON to each row's Field path. Verified non-destructive on featuretest.
///     Exception: <c>NationalIdentificationNumber</c> (CPR) is NOT in the v3 customer body — v3 moved it
///     to a dedicated sub-resource, so those rows route to
///     <see cref="ISettlCustomerClient.UpdateNationalIdentificationNumberV3Async"/> (flat single-field
///     PUT; cannot clear, so a drift toward empty is Skipped).</item>
/// </list>
///
/// <para>Overwrite policy: a drifted field is always set to intended (create-true wins), even when
/// Settl holds a different non-blank value. Each write re-validates first — the read-modify-write GET
/// compares current vs intended, and a field that already matches (Settl changed since the scan) is
/// skipped as <see cref="ApplyOutcome.NoLongerDrifts"/>, so a stale selection never clobbers.</para>
///
/// <para>PriceList rows are not written here (the price-list client is read-only); they are emitted
/// as a scoped re-import file by a separate path and reported <see cref="ApplyOutcome.Skipped"/> here.</para>
/// </summary>
public sealed class ReconcileApplier
{
    // Mirror of JsonValueDiff.SerializerOptions so the leaf names produced here line up exactly with
    // the Field paths the diff emitted (PascalCase, every field present incl. nulls).
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly SettlApiOptions _apiOptions;
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlPayerClient _payerClient;
    private readonly ISettlMeteringPointClient _mpClient;
    private readonly ISettlContractClient _contractClient;
    private readonly ISettlSettlementGroupClient _settlementGroupClient;
    private readonly string _connectionString;
    private readonly BillingExemptionConfig _exemption;
    // Kundegrupper der aldrig må merge — se ReconcileRunner. Applieren kører scoped create-capture,
    // så den skal se samme liste, ellers skriver den en merge create aldrig ville have lavet.
    private readonly IReadOnlyList<string> _baseIgnoredGroups;
    private readonly Action<string> _log;
    // PriceList apply is optional: without a price-list client the catalog can't be resolved, so
    // selected PriceList rows are reported Skipped (the live writers still run).
    private readonly ISettlPriceListClient? _priceListClient;
    private readonly IReadOnlyList<ProductElementMappingRule> _productElementMappings;

    public ReconcileApplier(
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

    public async Task<ReconcileApplyResult> ApplyAsync(
        ReconcileMappingOptions mapping, ReconcileApplyRequest request, CancellationToken ct = default)
    {
        // Billing/payment-config/exemptions live on electricity MPs (mirror fix-payment-config).
        _mpClient.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);

        var results = new List<ReconcileApplyRowResult>();
        _log($"=== reconcile apply ({(request.DryRun ? "dry-run" : "HOT")}, env={request.Env ?? "featuretest"}): " +
             $"{request.Targets.Count} representative(s) ===");

        // PriceList prep: resolve the Settl price-list catalog once (Name→GUID), only when PriceList
        // rows are selected and a client is wired. Null map ⇒ PriceList rows reported Skipped.
        var priceListSelected = request.Targets.Any(t => t.Rows.Any(r => r.SubType == "PriceList"));
        Dictionary<string, string>? priceListNameToId = null;
        if (priceListSelected && _priceListClient != null)
        {
            var (priceLists, plErr) = await _priceListClient.ListPriceListsAsync("DK", ct);
            if (priceLists != null)
            {
                priceListNameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pl in priceLists.Where(p => !string.IsNullOrEmpty(p.Name)))
                    priceListNameToId[pl.Name!] = pl.Id.ToString();
                _log($"Price-list catalog: {priceListNameToId.Count} price list(s)");
            }
            else _log($"Price-list catalog fetch failed: {plErr} — PriceList rows will be Skipped");
        }
        var priceListRowsToEmit = new List<CustomerPriceListAssociationRow>();

        foreach (var target in request.Targets)
        {
            ct.ThrowIfCancellationRequested();
            // Log each row this target produced right after it's processed (the rep header line is
            // emitted inside ApplyTargetAsync first), so the stream carries live per-row detail —
            // entity/field/value/outcome — instead of only the per-rep summary + final tally. Captured
            // from the shared results list here so every ApplyTargetAsync return path (incl. early
            // NotFound) is covered without threading the logger into each writer.
            var firstRow = results.Count;
            await ApplyTargetAsync(mapping, request.Env, request.DryRun, target, results,
                priceListNameToId, priceListRowsToEmit, ct);
            for (var i = firstRow; i < results.Count; i++)
                _log(FormatRowLine(results[i]));
        }

        // Emit the PriceList re-import file (an artifact, not a Settl mutation — written in both modes
        // so the user can inspect/import it). De-duplicate identical rows across representatives.
        string? priceListCsvPath = null;
        if (priceListRowsToEmit.Count > 0)
        {
            priceListCsvPath = request.PriceListOutputPath
                ?? $"reconcile-apply-pricelists-{request.Env ?? "featuretest"}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
            var distinct = priceListRowsToEmit.Distinct().ToList();
            CustomerPriceListAssociationExcelWriter.Write(priceListCsvPath, distinct);
            _log($"PriceList: wrote {distinct.Count} association(s) to {priceListCsvPath} — re-import manually (no live write API)");
        }

        var result = new ReconcileApplyResult(results, priceListCsvPath);
        _log($"Done. Applied={result.Applied} WouldApply={result.WouldApply} " +
             $"NoLongerDrifts={result.NoLongerDrifts} NotFound={result.NotFound} " +
             $"Skipped={result.Skipped} Failed={result.Failed}");
        return result;
    }

    // One live log line per applied row. Writers store the value transition / skip reason in Detail
    // ("{Field} → {value}" for field writes); strip the leading "{Field} → " so the field isn't repeated,
    // and render value writes as "Field → value" vs notes/reasons as "(Outcome: detail)".
    private static string FormatRowLine(ReconcileApplyRowResult r)
    {
        var glyph = r.Outcome switch
        {
            ApplyOutcome.Applied => "✓",
            ApplyOutcome.WouldApply => "·",
            ApplyOutcome.NoLongerDrifts => "⊘",
            ApplyOutcome.NotFound => "?",
            ApplyOutcome.Skipped => "⊘",
            ApplyOutcome.Failed => "✗",
            _ => "•",
        };
        var key = string.IsNullOrEmpty(r.EntityKey) ? "" : $" {r.EntityKey}";
        var field = string.IsNullOrEmpty(r.Field) ? "" : $" {r.Field}";
        var valuePrefix = $"{r.Field} → ";
        if (!string.IsNullOrEmpty(r.Detail) && r.Field.Length > 0 && r.Detail.StartsWith(valuePrefix, StringComparison.Ordinal))
            return $"    {glyph} {r.Entity}{key} {r.Field} → {r.Detail[valuePrefix.Length..]} ({r.Outcome})";
        if (!string.IsNullOrEmpty(r.Detail))
            return $"    {glyph} {r.Entity}{key}{field} ({r.Outcome}: {r.Detail})";
        return $"    {glyph} {r.Entity}{key}{field} ({r.Outcome})";
    }

    private async Task ApplyTargetAsync(
        ReconcileMappingOptions mapping, string? env, bool dryRun,
        ReconcileApplyTarget target, List<ReconcileApplyRowResult> results,
        IReadOnlyDictionary<string, string>? priceListNameToId,
        List<CustomerPriceListAssociationRow> priceListRowsToEmit, CancellationToken ct)
    {
        var refDate = target.Cos.AddDays(-1);

        // The scoped capture (Xellent load + CreateRunner) is only needed for rows whose intended write
        // unit was NOT carried from the scan: billing rows (carried in round 2), PriceList/SettlementGroup
        // (re-resolved from loadResult), or any row from a pre-IntendedJson cache/CSV. A target whose
        // selected rows all carry IntendedJson skips it entirely — seconds → milliseconds.
        var needsCapture = target.Rows.Any(RowNeedsCapture);

        DataMigration.Domain.Xellent.XellentLoadResult? loadResult = null;
        SettlMigrationPlan? plan = null;
        if (needsCapture)
        {
            _log($"  rep {target.RepresentativeAccount}: scoped capture over {target.Accounts.Count} account(s) " +
                 $"at refDate {refDate:yyyy-MM-dd} ({target.Rows.Count} row(s) selected)");

            // Scoped create-true capture: same prep + builders as env-wide reconcile / create, restricted
            // to this representative's merge-group at its COS. No env-wide pagination → seconds.
            var options = CreateOptionsFactory.BuildFromMapping(
                mapping, target.Accounts.ToArray(), env, refDate.ToDateTime(TimeOnly.MinValue), _exemption,
                _baseIgnoredGroups, target.Cos);
            (loadResult, _) = await MigrationDomainLoad.LoadAsync(_connectionString, options, ct);
            var plans = new List<SettlMigrationPlan>();
            var runner = new CreateRunner(
                _apiOptions, _customerClient, _payerClient, _mpClient, _contractClient, _settlementGroupClient)
            {
                OnLogLine = _ => { }, // capture-mode prep is read-only; suppress its chatter
            };
            await runner.RunAsync(options, loadResult, ct, capturePlans: plans);

            plan = plans.FirstOrDefault(p => string.Equals(p.CustomerNumber, target.RepresentativeAccount, StringComparison.Ordinal))
                   ?? plans.FirstOrDefault();
            if (plan == null)
            {
                foreach (var row in target.Rows)
                    results.Add(Row(target, row, ApplyOutcome.NotFound, "representative not produced by capture"));
                return;
            }
        }
        else
        {
            _log($"  rep {target.RepresentativeAccount}: {target.Rows.Count} row(s) from carried intended (no capture)");
        }

        // Dispatch each selected row to the writer for its (Entity, SubType). Group so each entity is
        // written once (a payer/MP PUT carries all its drifted fields together).
        var payerBilling = new Dictionary<string, List<ReconcileApplyRow>>(StringComparer.Ordinal);
        var mpBilling = new Dictionary<string, List<ReconcileApplyRow>>(StringComparer.Ordinal);
        var mpExemptions = new Dictionary<string, List<ReconcileApplyRow>>(StringComparer.Ordinal);
        var mpAnnualVolume = new Dictionary<string, List<ReconcileApplyRow>>(StringComparer.Ordinal);
        var priceList = new List<ReconcileApplyRow>();
        var settlementGroups = new List<ReconcileApplyRow>();
        var customerLinks = new List<ReconcileApplyRow>();
        var contractLinks = new List<ReconcileApplyRow>();
        var missingEntities = new List<ReconcileApplyRow>();
        var customerCore = new List<ReconcileApplyRow>();
        var payerCore = new Dictionary<string, List<ReconcileApplyRow>>(StringComparer.Ordinal);

        foreach (var row in target.Rows)
        {
            switch (row)
            {
                // A Match row carries intended == actual by construction (full-payload view only) —
                // there is nothing to write. Guarded explicitly so it can never reach a live writer,
                // independent of the read-modify-write re-validation that would also no-op it.
                case { Kind: "Match" }:
                    results.Add(Row(target, row, ApplyOutcome.Skipped, "matched field (read-only)"));
                    break;
                case { Entity: "Payer", SubType: "Billing" }:
                    Add(payerBilling, row.EntityKey, row);
                    break;
                case { Entity: "MeteringPoint", SubType: "Billing" }:
                    Add(mpBilling, row.EntityKey, row);
                    break;
                case { Entity: "MeteringPoint", SubType: "Exemption", Kind: "MissingInSettl" }:
                    Add(mpExemptions, row.EntityKey, row);
                    break;
                // Customer→MP link the scan found missing (group exists in Settl, MP just isn't linked).
                // Additive — mirrors create's step-4 linking via the same AddMeteringPointsAsync API.
                case { Entity: "MeteringPoint", SubType: "CustomerLink", Kind: "MissingInSettl" }:
                    customerLinks.Add(row);
                    break;
                // Contract→MP link drift: wrong end date (FieldDiff), missing link (MissingInSettl) or a
                // link Settl holds that the plan does not intend (Anomaly → delete only). FieldDiff and
                // MissingInSettl need the captured plan to resolve the intended ContractMeteringPointDto;
                // the Anomaly has no intended link to post, so it only needs the link's own coordinates.
                case { Entity: "MeteringPoint", SubType: "ContractLink", Kind: "FieldDiff" or "MissingInSettl" or "Anomaly" }:
                    contractLinks.Add(row);
                    break;
                // Annual-volume drift on an MP's master data. Routed to the dedicated annual-consumptions
                // PUT (touches only the volume — no full master-data rewrite). Every other MeteringPoint
                // master FieldDiff field stays in the default Skipped branch (no safe single-field API).
                case { Entity: "MeteringPoint", SubType: "", Kind: "FieldDiff", Field: "EstimatedAnnualVolume" }:
                    Add(mpAnnualVolume, row.EntityKey, row);
                    break;
                // Customer / payer CORE field drift (name/address/identifier/contact). Written via a
                // full-object read-modify-write master PUT — customer = wrapped v3 PUT, payer = flat v1
                // PUT — that echoes the current entity back verbatim and overwrites only the drifted
                // leaves (preserving links + untouched fields). Verified non-destructive on featuretest.
                case { Entity: "Customer", SubType: "", Kind: "FieldDiff" }:
                    customerCore.Add(row);
                    break;
                case { Entity: "Payer", SubType: "", Kind: "FieldDiff" }:
                    Add(payerCore, row.EntityKey, row);
                    break;
                // Whole entity absent in Settl (customer/payer/MP the migration should have produced) —
                // reconciled by re-running create in write-mode over the merge-group (idempotent
                // search-or-create). Heavier than a single field write, but the only create-true path.
                case { Entity: "Customer" or "Payer" or "MeteringPoint", SubType: "", Kind: "MissingInSettl" }:
                    missingEntities.Add(row);
                    break;
                case { Entity: "Customer", SubType: "SettlementGroup", Kind: "MissingInSettl" }:
                    // Only the "member missing" variant is addable (the group exists, the customer
                    // just isn't in it). The "group missing in Settl" variant of MissingInSettl is
                    // rejected inside the writer (FindByName → not found → Skipped) — neither create
                    // nor this applier creates groups.
                    settlementGroups.Add(row);
                    break;
                case { SubType: "PriceList" }:
                    priceList.Add(row);
                    break;
                default:
                    // Skip reason sourced from the shared classifier so it can't drift from the dashboard's
                    // "can apply / cannot apply" flag (both call ReconcileApplyClassifier).
                    results.Add(Row(target, row, ApplyOutcome.Skipped, ReconcileApplyClassifier.Classify(row).Reason));
                    break;
            }
        }

        // Billing + exemption writers take a nullable plan: it's null when every row in the bucket carried
        // its IntendedJson (no capture), and each writer resolves intended per-row (IntendedJson first,
        // plan as fallback). loadResult! is non-null where used: PriceList/SettlementGroup always return
        // true from RowNeedsCapture, so a non-empty bucket implies the capture ran.
        // Missing whole entities first: a scoped create write-run reconciles them (and their links)
        // before the per-field writers below run, so a field write never targets an absent entity.
        if (missingEntities.Count > 0)
            await ApplyMissingEntitiesAsync(mapping, env, target, loadResult, refDate, missingEntities, dryRun, results, ct);
        foreach (var (payerId, rows) in payerBilling)
            await ApplyPayerBillingAsync(target, plan, payerId, rows, dryRun, results, ct);
        // One Settl read per MP, shared by the billing-config update (PUT), the exemption add (POST), and
        // the annual-consumptions update: an MP with several kinds of drift is searched (+ billing-GET'd)
        // once, not once per writer.
        var mpIds = new HashSet<string>(mpBilling.Keys, StringComparer.Ordinal);
        mpIds.UnionWith(mpExemptions.Keys);
        mpIds.UnionWith(mpAnnualVolume.Keys);
        foreach (var mpId in mpIds)
            await ApplyMeteringPointAsync(target, plan, mpId,
                mpBilling.GetValueOrDefault(mpId) ?? [], mpExemptions.GetValueOrDefault(mpId) ?? [],
                mpAnnualVolume.GetValueOrDefault(mpId) ?? [],
                dryRun, results, ct);
        if (customerLinks.Count > 0)
            await ApplyCustomerLinksAsync(target, customerLinks, dryRun, results, ct);
        if (contractLinks.Count > 0)
            await ApplyContractLinksAsync(target, plan, contractLinks, dryRun, results, ct);
        if (customerCore.Count > 0)
            await ApplyCustomerCoreAsync(target, customerCore, dryRun, results, ct);
        foreach (var (payerId, rows) in payerCore)
            await ApplyPayerCoreAsync(target, payerId, rows, dryRun, results, ct);
        if (priceList.Count > 0)
            await ApplyPriceListAsync(mapping, env, target, loadResult!, refDate, priceList,
                priceListNameToId, priceListRowsToEmit, results, ct);
        if (settlementGroups.Count > 0)
            await ApplySettlementGroupsAsync(mapping, env, target, loadResult, settlementGroups, dryRun, results, ct);
    }

    // A row forces the scoped capture when its intended write unit is not carried on the row. Exemptions
    // and billing rows carry their intended (→ capture only when IntendedJson is absent, e.g. a JSON-null
    // intended leaf or a pre-IntendedJson cache); PriceList/SettlementGroup still re-resolve from the
    // capture's loadResult. Match + out-of-scope rows never write, so they never force a capture.
    private static bool RowNeedsCapture(ReconcileApplyRow row) => row switch
    {
        { Kind: "Match" } => false,
        { Entity: "Payer", SubType: "Billing" } => row.IntendedJson == null,
        // Whole-config-missing MP billing rows now carry IntendedJson for dashboard display, but the
        // writer rejects their "*" field (Skipped) and never reads that JSON. Keep them forcing the
        // capture exactly as before IntendedJson was added, so the apply decision is unchanged.
        { Entity: "MeteringPoint", SubType: "Billing", Kind: "MissingInSettl" } => true,
        { Entity: "MeteringPoint", SubType: "Billing" } => row.IntendedJson == null,
        { Entity: "MeteringPoint", SubType: "Exemption", Kind: "MissingInSettl" } => row.IntendedJson == null,
        { Entity: "Customer", SubType: "SettlementGroup", Kind: "MissingInSettl" } => row.IntendedJson == null,
        // Annual volume's intended value is not carried on the row (master rows carry no IntendedJson),
        // so it always resolves from the capture's plan.
        { Entity: "MeteringPoint", SubType: "", Kind: "FieldDiff", Field: "EstimatedAnnualVolume" } => true,
        // Contract link intended payload (ContractMeteringPointDto) is not carried on the row;
        // always resolves from the capture's plan.ContractMpLinks. The Anomaly variant is the exception:
        // it only deletes a link Settl already holds, so it needs no intended payload and no capture.
        { Entity: "MeteringPoint", SubType: "ContractLink", Kind: "Anomaly" } => false,
        { Entity: "MeteringPoint", SubType: "ContractLink" } => true,
        // Missing whole entities are reconciled by a scoped create write-run, which needs the capture's
        // loadResult (the Xellent extract). The customer↔MP link writer needs neither (pure Settl lookups).
        { Entity: "Customer" or "Payer" or "MeteringPoint", SubType: "", Kind: "MissingInSettl" } => true,
        { SubType: "PriceList" } => true,
        _ => false,
    };

    // ---- Settlement-group membership (additive — add representative customer to existing group) ----

    private async Task ApplySettlementGroupsAsync(
        ReconcileMappingOptions mapping, string? env, ReconcileApplyTarget target,
        DataMigration.Domain.Xellent.XellentLoadResult? loadResult, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        var groupMap = mapping.Mappings?.SettlementGroup;
        if (groupMap is not { Count: > 0 })
        {
            AddAll(target, rows, results, ApplyOutcome.Skipped, "profile has no settlement-group mapping");
            return;
        }

        // Create-true intended: the groups create would assign this representative to. A carried row's
        // Field IS the intended group (the scan only emits a member-missing row for an expected group), so
        // it's trusted directly. Only uncarried rows (pre-IntendedJson cache) re-resolve from the capture's
        // loadResult — same resolver create uses — to reject a group create no longer intends.
        var intendedGroups = rows.Any(r => r.IntendedJson == null) && loadResult != null
            ? SettlementGroupExpectationResolver
                .ResolveByCustomer(loadResult.Customers, groupMap)
                .SelectMany(kv => kv.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // The representative's Settl customer GUID — EntityKey is the (env-padded) representative
        // customer number the scan compared, used as-is (mirrors the other writers' natural keys).
        var (customerInfo, _) = await _customerClient.SearchByCustomerNumberAsync(target.RepresentativeAccount, "DK", ct);
        if (customerInfo == null)
        {
            AddAll(target, rows, results, ApplyOutcome.NotFound, "representative customer not found in Settl");
            return;
        }

        foreach (var row in rows)
        {
            var groupName = row.Field;
            // Carried row → intended by construction; uncarried → must be in the re-resolved set.
            if (row.IntendedJson == null && intendedGroups?.Contains(groupName) != true)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "group no longer intended by create"));
                continue;
            }

            var (group, _) = await _settlementGroupClient.FindByNameAsync("DK", groupName, ct);
            if (group is not { IsActive: true })
            {
                // The "group missing in Settl" variant — neither create nor this applier creates groups.
                results.Add(Row(target, row, ApplyOutcome.Skipped, "group missing/inactive in Settl — not auto-creatable"));
                continue;
            }

            var (memberIds, listErr) = await _settlementGroupClient.ListCustomerIdsAsync(group.Id, ct);
            if (memberIds == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Failed, $"list members {listErr}"));
                continue;
            }
            if (memberIds.Contains(customerInfo.Id))
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }

            if (dryRun)
            {
                results.Add(Row(target, row, ApplyOutcome.WouldApply, $"add to group {group.Id}"));
                continue;
            }

            var (ok, addErr) = await _settlementGroupClient.AddCustomersAsync(
                group.Id,
                new AddSettlementGroupCustomersRequest
                {
                    Customers = [new SettlementGroupCustomerEntry { CustomerId = customerInfo.Id, IncludeCustomerSubsidiaries = false }],
                },
                ct);
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? $"added to group {group.Id}" : $"add customer {addErr}"));
        }
    }

    // ---- PriceList (re-import file artifact — no live write API) ----

    private async Task ApplyPriceListAsync(
        ReconcileMappingOptions mapping, string? env, ReconcileApplyTarget target,
        DataMigration.Domain.Xellent.XellentLoadResult loadResult, DateOnly refDate,
        List<ReconcileApplyRow> rows, IReadOnlyDictionary<string, string>? priceListNameToId,
        List<CustomerPriceListAssociationRow> priceListRowsToEmit,
        List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        if (_priceListClient == null || priceListNameToId == null)
        {
            AddAll(target, rows, results, ApplyOutcome.Skipped, "price-list catalog unavailable (client not wired)");
            return;
        }

        // Rebuild the representative's create-true intended associations from the scoped capture's
        // loadResult — same builder reconcile uses, so the emitted set matches the intended diff.
        var intendedByRep = new Dictionary<string, List<CustomerPriceListAssociationRow>>(StringComparer.Ordinal);
        await ReconcilePriceListIntended.AccumulateAsync(
            _connectionString, _productElementMappings, target.Accounts.ToArray(), loadResult,
            target.Cos, refDate, env, mapping.DataAreaId, mapping.EffectiveLegacyTg4Merge(target.Cos),
            priceListNameToId, intendedByRep, ct);

        var repRows = intendedByRep.GetValueOrDefault(target.RepresentativeAccount) ?? [];
        if (repRows.Count == 0)
        {
            AddAll(target, rows, results, ApplyOutcome.Skipped, "no intended associations for representative");
            return;
        }

        priceListRowsToEmit.AddRange(repRows);
        foreach (var row in rows)
            results.Add(Row(target, row, ApplyOutcome.Applied,
                $"{repRows.Count} association(s) queued for re-import file"));
    }

    // ---- Payer billing (compute drifted leaf patches; SettlClient owns the flat-shape write) ----

    private async Task ApplyPayerBillingAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan, string payerId, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        // Intended leaves come from each row's carried IntendedJson; only uncarried rows (JSON-null leaf /
        // pre-IntendedJson cache) fall back to the captured plan's PayerBilling.
        var planPayer = plan?.Payers.FirstOrDefault(p =>
            string.Equals(p.PayerInformation?.PayerIdentifier, payerId, StringComparison.Ordinal));

        var (info, _) = await _payerClient.SearchByPayerIdentificationAsync(payerId, "DK", ct);
        if (info == null)
        {
            AddAll(target, rows, results, ApplyOutcome.NotFound, "payer not found in Settl");
            return;
        }

        // Re-validation reads current via the typed GetByIdAsync; the flat-shape round-trip write lives in
        // SettlClient.UpdatePayerBillingAsync (we hand it only the drifted leaves to patch).
        var (current, getErr) = await _payerClient.GetByIdAsync(info.Id, null, ct);
        if (current?.PayerBilling == null)
        {
            AddAll(target, rows, results, ApplyOutcome.Failed, $"GET {getErr}");
            return;
        }
        var currentBilling = JsonSerializer.SerializeToNode(current.PayerBilling, SerializerOptions)!.AsObject();

        var intendedFromPlan = planPayer != null
            ? JsonSerializer.SerializeToNode(planPayer.PayerBilling, SerializerOptions)!.AsObject()
            : null;

        var patches = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var toWrite = new List<(ReconcileApplyRow Row, string Leaf, JsonNode? Value)>();
        foreach (var row in rows)
        {
            var leaf = LeafAfter(row.Field, "PayerBilling.");
            if (leaf == null || !ResolveIntendedLeaf(row, leaf, intendedFromPlan, out var wantedVal))
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "unrecognised or uncarried PayerBilling field"));
                continue;
            }
            var currentVal = currentBilling.ContainsKey(leaf) ? currentBilling[leaf] : null;
            if (JsonEqual(currentVal, wantedVal))
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }
            patches[leaf] = wantedVal?.DeepClone();
            toWrite.Add((row, leaf, wantedVal));
        }

        if (toWrite.Count == 0) return;

        if (dryRun)
        {
            foreach (var (row, leaf, val) in toWrite)
                results.Add(Row(target, row, ApplyOutcome.WouldApply, $"{leaf} → {Text(val)}"));
            return;
        }

        var (ok, putErr) = await _payerClient.UpdatePayerBillingAsync(info.Id, patches, ct);
        foreach (var (row, leaf, val) in toWrite)
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? $"{leaf} → {Text(val)}" : $"PUT {putErr}"));
    }

    // ---- MP orchestration (one search + one billing GET, shared by billing + exemption writers) ----

    private async Task ApplyMeteringPointAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan, string mpId,
        List<ReconcileApplyRow> billingRows, List<ReconcileApplyRow> exemptionRows,
        List<ReconcileApplyRow> annualVolumeRows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        // Resolve the MP and read its billing config ONCE; the writers reuse it. Billing, exemptions and
        // annual-consumptions are separate Settl write APIs (PUT billing-config vs POST exemptions vs PUT
        // annual-consumptions) and stay separate writes, but they no longer each re-search the same MP.
        var (existing, _) = await _mpClient.SearchByIdentificationAsync(mpId, ct);
        if (existing == null)
        {
            AddAll(target, billingRows.Concat(exemptionRows).Concat(annualVolumeRows), results, ApplyOutcome.NotFound, "MP not found in Settl");
            return;
        }

        var (current, getErr) = await _mpClient.GetBillingByIdAsync(existing.Id, null, ct);

        if (billingRows.Count > 0)
            await ApplyMpBillingAsync(target, plan, mpId, existing, current, getErr, billingRows, dryRun, results, ct);
        if (exemptionRows.Count > 0)
            await ApplyExemptionsAsync(target, plan, mpId, existing, current, exemptionRows, dryRun, results, ct);
        if (annualVolumeRows.Count > 0)
            await ApplyAnnualVolumeAsync(target, plan, mpId, existing, annualVolumeRows, dryRun, results, ct);
    }

    // ---- MP annual consumption (dedicated annual-consumptions PUT — touches only the volume) ----

    private async Task ApplyAnnualVolumeAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan, string mpId,
        MeteringPointInfoDto existing, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        // Intended volume comes from the captured create-true plan (master rows carry no IntendedJson);
        // current comes from the MP we already resolved. Read-modify-write: equal → NoLongerDrifts.
        var planMp = plan?.MeteringPoints.FirstOrDefault(m => string.Equals(m.Identification, mpId, StringComparison.Ordinal));
        var intended = planMp?.Master?.MeterCharacteristics?.EstimatedAnnualVolume?.Value;
        var currentVolume = existing.EstimatedAnnualVolume?.Value;

        foreach (var row in rows)
        {
            if (intended is not { } want)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "no intended annual volume in capture"));
                continue;
            }
            if (currentVolume is { } cur && Math.Abs(cur - want) < 0.0001)
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }
            if (dryRun)
            {
                results.Add(Row(target, row, ApplyOutcome.WouldApply, $"EstimatedAnnualVolume → {want}"));
                continue;
            }
            // EstimatedAnnualVolume lives on the master MeterCharacteristics (what create writes and the
            // scan reads) — NOT the annualConsumptions endpoint (a separate HistoricAnnualConsumption
            // field nothing in reconcile reads). It's periodised by MeterCharacteristics.Start, so write
            // it effective from today to make the corrected value the current reading.
            var effectiveStart = DateOnly.FromDateTime(DateTime.Today);
            var (ok, err) = await _mpClient.UpdateEstimatedAnnualVolumeAsync(existing.Id, want, effectiveStart, ct);
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? $"EstimatedAnnualVolume → {want}" : $"PUT {err}"));
        }
    }

    // ---- Customer→MP link (additive — mirrors create's step-4 linking) ----

    private async Task ApplyCustomerLinksAsync(
        ReconcileApplyTarget target, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        var (customerInfo, _) = await _customerClient.SearchByCustomerNumberAsync(target.RepresentativeAccount, "DK", ct);
        if (customerInfo == null)
        {
            AddAll(target, rows, results, ApplyOutcome.NotFound, "representative customer not found in Settl");
            return;
        }

        // Re-validate against the live link set so an already-linked MP reports NoLongerDrifts (a stale
        // selection never re-POSTs). AddMeteringPointsAsync is additive, but we skip explicitly for clarity.
        var (custMps, listErr) = await _customerClient.GetMeteringPointsAsync(customerInfo.Id, ct);
        if (custMps == null)
        {
            AddAll(target, rows, results, ApplyOutcome.Failed, $"list customer MPs {listErr}");
            return;
        }
        var linkedMpGuids = custMps.Select(m => m.MeteringPointId).ToHashSet();

        var toAdd = new List<(ReconcileApplyRow Row, CustomerMeteringPointDto Dto)>();
        foreach (var row in rows)
        {
            // EntityKey is the MP identification (natural key); resolve it to the Settl MP GUID.
            // Water/heat MPs live on a separate commodity endpoint — try electricity first (cheap null
            // return for misses), then fall back to the water search (loads all water MPs into cache).
            var (existing, _) = await _mpClient.SearchByIdentificationAsync(row.EntityKey, ct);
            var commodityType = "Electricity";
            if (existing == null)
            {
                (existing, _) = await _mpClient.SearchWaterByIdentificationAsync(row.EntityKey, ct);
                commodityType = "Water";
            }
            if (existing == null)
            {
                results.Add(Row(target, row, ApplyOutcome.NotFound, "MP not found in Settl"));
                continue;
            }
            if (linkedMpGuids.Contains(existing.Id))
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }
            toAdd.Add((row, new CustomerMeteringPointDto
            {
                ExternalIdentifier = row.EntityKey,
                CommodityType = commodityType,
                MeteringPointId = existing.Id,
                Start = target.Cos,
                End = MigrationConstants.MeteringPointEndDate,
            }));
        }

        if (toAdd.Count == 0) return;

        if (dryRun)
        {
            foreach (var (row, dto) in toAdd)
                results.Add(Row(target, row, ApplyOutcome.WouldApply, $"link MP {dto.MeteringPointId}"));
            return;
        }

        var req = new AddCustomerMeteringPointsRequest { CustomerMeteringPoints = toAdd.Select(t => t.Dto).ToList() };
        var (ok, addErr) = await _customerClient.AddMeteringPointsAsync(customerInfo.Id, req, ct);
        foreach (var (row, dto) in toAdd)
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? $"linked MP {dto.MeteringPointId}" : $"POST {addErr}"));
    }

    // ---- Contract→MP link (delete + re-link for wrong end-date; additive POST for missing link;
    //      delete-only for a link Settl holds that the plan does not intend) ----

    /// <summary>
    /// Applies every contract→MP link row for one representative. The two halves run in a deliberate
    /// order — see the comment inside.
    /// </summary>
    private async Task ApplyContractLinksAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan,
        List<ReconcileApplyRow> rows, bool dryRun,
        List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        // ORDER IS DELIBERATE: the intended links (MissingInSettl / FieldDiff) are written BEFORE the
        // delete-only Anomaly rows. A metering point on the wrong contract surfaces as a PAIR of rows —
        // the intended link missing, the wrong link extra — and both can be selected in the same run.
        // Add-then-delete leaves the MP on two contracts for the moment in between, which is exactly the
        // state it is already in, and if the add fails we stop with the original fault intact and nothing
        // destroyed. Delete-then-add would leave the MP on NO contract at all if the add then failed —
        // no billing, strictly worse than the fault we came to repair.
        var intendedRows = rows.Where(r => r.Kind != "Anomaly").ToList();
        var anomalyRows = rows.Where(r => r.Kind == "Anomaly").ToList();

        if (intendedRows.Count > 0)
        {
            if (plan == null)
                AddAll(target, intendedRows, results, ApplyOutcome.Skipped, "no captured plan for contract-link resolution");
            else
                await ApplyIntendedContractLinksAsync(target, plan, intendedRows, dryRun, results, ct);
        }

        if (anomalyRows.Count > 0)
            await DeleteUnintendedContractLinksAsync(target, plan, anomalyRows, dryRun, results, ct);
    }

    /// <summary>
    /// Deletes contract→MP links the intended plan does not contain (Kind = Anomaly). Delete only — an
    /// anomaly has no intended link to post, so nothing is re-linked afterwards. The plan is optional and
    /// used purely as a guard: if it turns out to intend this exact link after all, nothing is deleted.
    /// </summary>
    private async Task DeleteUnintendedContractLinksAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan,
        List<ReconcileApplyRow> rows, bool dryRun,
        List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        var byContract = new Dictionary<Guid, List<ReconcileApplyRow>>();
        foreach (var row in rows)
        {
            if (!TryParseContractId(row.Field, out var cid))
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "could not parse contract GUID from field"));
                continue;
            }
            if (!byContract.TryGetValue(cid, out var bucket))
                byContract[cid] = bucket = [];
            bucket.Add(row);
        }

        foreach (var (contractId, contractRows) in byContract)
        {
            var (actualMps, fetchErr) = await _contractClient.GetContractMeteringPointsAsync(contractId, ct);
            if (fetchErr != null)
            {
                AddAll(target, contractRows, results, ApplyOutcome.Failed, $"GET contract MPs failed: {fetchErr}");
                continue;
            }

            var actualByNatId = new Dictionary<string, ContractMeteringPointInfoDto>(StringComparer.Ordinal);
            foreach (var m in actualMps ?? [])
                if (m.MeteringPointIdentification != null)
                    actualByNatId[NormWaterMpId(m.MeteringPointIdentification!)] = m;

            foreach (var row in contractRows)
            {
                // Safety net: never delete a link the freshly-captured plan says create would make.
                var isIntended = plan?.ContractMpLinks.Any(l =>
                    l.ContractId == contractId &&
                    string.Equals(NormWaterMpId(l.Link.MeteringPointIdentification ?? ""),
                                  NormWaterMpId(row.EntityKey), StringComparison.Ordinal)) == true;
                if (isIntended)
                {
                    results.Add(Row(target, row, ApplyOutcome.Skipped, "capture says this contract link IS intended — not deleted"));
                    continue;
                }

                if (!actualByNatId.TryGetValue(NormWaterMpId(row.EntityKey), out var actual))
                {
                    results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts, "link no longer exists in Settl"));
                    continue;
                }

                if (dryRun)
                {
                    results.Add(Row(target, row, ApplyOutcome.WouldApply, $"delete unintended link to contract {contractId}"));
                    continue;
                }

                var (deleted, deleteErr) = await _contractClient.RemoveContractMeteringPointAsync(contractId, actual.Id, ct);
                results.Add(Row(target, row, deleted ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                    deleted ? $"deleted link to contract {contractId}" : $"DELETE contract link failed: {deleteErr}"));
            }
        }
    }

    /// <summary>Normalises a Settl water MP identification ("V104229901") to the Xellent Instnum
    /// ("1042299") the capture plan and drift rows key on. Electricity IDs pass through unchanged.</summary>
    private static string NormWaterMpId(string id) =>
        id.Length >= 4 && id.StartsWith("V", StringComparison.OrdinalIgnoreCase) && id.EndsWith("01", StringComparison.Ordinal)
            ? id[1..^2]
            : id;

    private async Task ApplyIntendedContractLinksAsync(
        ReconcileApplyTarget target, SettlMigrationPlan plan,
        List<ReconcileApplyRow> rows, bool dryRun,
        List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        // Group rows by contract GUID (parsed from the Field "ContractLink[{guid}](.End)?").
        // One GET per contract covers all its MP rows.
        var byContract = new Dictionary<Guid, List<ReconcileApplyRow>>();
        foreach (var row in rows)
        {
            if (!TryParseContractId(row.Field, out var cid))
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "could not parse contract GUID from field"));
                continue;
            }
            if (!byContract.TryGetValue(cid, out var bucket))
                byContract[cid] = bucket = [];
            bucket.Add(row);
        }

        foreach (var (contractId, contractRows) in byContract)
        {
            var (actualMps, fetchErr) = await _contractClient.GetContractMeteringPointsAsync(contractId, ct);
            if (fetchErr != null)
            {
                AddAll(target, contractRows, results, ApplyOutcome.Failed, $"GET contract MPs failed: {fetchErr}");
                continue;
            }
            // Water MP IDs are normalised ("V104229901" → "1042299") so EntityKey (Xellent Instnum) resolves.
            var actualByNatId = new Dictionary<string, ContractMeteringPointInfoDto>(StringComparer.Ordinal);
            foreach (var m in actualMps ?? [])
                if (m.MeteringPointIdentification != null)
                    actualByNatId[NormWaterMpId(m.MeteringPointIdentification!)] = m;

            foreach (var row in contractRows)
            {
                // Find intended link from capture plan (matched by national identification = row.EntityKey).
                var intended = plan.ContractMpLinks.FirstOrDefault(l =>
                    l.ContractId == contractId &&
                    string.Equals(l.Link.MeteringPointIdentification, row.EntityKey, StringComparison.Ordinal));

                if (intended == null)
                {
                    results.Add(Row(target, row, ApplyOutcome.Skipped, "intended contract link not found in capture plan"));
                    continue;
                }

                actualByNatId.TryGetValue(NormWaterMpId(row.EntityKey), out var actual);

                // Re-validate: check if already matches.
                if (actual != null)
                {
                    var actualStr = actual.End.Year >= 9999 ? "open-ended" : actual.End.ToString("yyyy-MM-dd");
                    var intendedStr = intended.Link.End == MigrationConstants.MeteringPointEndDate
                        ? "open-ended" : intended.Link.End?.ToString("yyyy-MM-dd");
                    if (row.Kind == "MissingInSettl" || actualStr == intendedStr)
                    {
                        results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts,
                            row.Kind == "MissingInSettl" ? "link now exists in Settl" : $"end date already {actualStr}"));
                        continue;
                    }
                }

                var action = row.Kind == "FieldDiff" ? "delete + re-link" : "add link";
                var endLabel = intended.Link.End == MigrationConstants.MeteringPointEndDate
                    ? "open-ended" : intended.Link.End?.ToString("yyyy-MM-dd");

                if (dryRun)
                {
                    results.Add(Row(target, row, ApplyOutcome.WouldApply, $"{action} contract {contractId} end → {endLabel}"));
                    continue;
                }

                // FieldDiff: DELETE the existing link before re-posting.
                if (row.Kind == "FieldDiff" && actual != null)
                {
                    var (deleted, deleteErr) = await _contractClient.RemoveContractMeteringPointAsync(contractId, actual.Id, ct);
                    if (!deleted)
                    {
                        results.Add(Row(target, row, ApplyOutcome.Failed, $"DELETE contract link failed: {deleteErr}"));
                        continue;
                    }
                }

                // Resolve MP GUID for the POST body.
                // FieldDiff: actual was found (after water-ID normalisation) — use its real GUID.
                // MissingInSettl: intended holds a real GUID for electricity (CapturePlanAsync resolved
                // it). For water the capture plan stores a fake Guid.NewGuid() because
                // SearchWaterByIdentificationAsync is keyed by "V{instnum}01" not by Xellent Instnum,
                // so we translate and search now to get the real water MP GUID.
                Guid postMpId;
                string? postMpNatId;
                if (actual != null)
                {
                    postMpId = actual.MeteringPointId;
                    postMpNatId = actual.MeteringPointIdentification;
                }
                else
                {
                    // Link was already gone before hot-run — look up the MP directly.
                    // Try electricity first (18-digit EntityKey), then water (V{key}01 format).
                    var (elecMp, _) = await _mpClient.SearchByIdentificationAsync(row.EntityKey, ct);
                    if (elecMp != null)
                    {
                        postMpId = elecMp.Id;
                        postMpNatId = row.EntityKey;
                    }
                    else
                    {
                        var maybeWaterSettleId = $"V{row.EntityKey}01";
                        var (waterMp, _) = await _mpClient.SearchWaterByIdentificationAsync(maybeWaterSettleId, ct);
                        if (waterMp != null)
                        {
                            postMpId = waterMp.Id;
                            postMpNatId = maybeWaterSettleId;
                        }
                        else
                        {
                            postMpId = intended.Link.MeteringPointId;
                            postMpNatId = intended.Link.MeteringPointIdentification;
                        }
                    }
                }

                var newLink = new ContractMeteringPointDto
                {
                    MeteringPointId = postMpId,
                    MeteringPointIdentification = postMpNatId,
                    Start = intended.Link.Start,
                    End = intended.Link.End,
                    Category = intended.Link.Category,
                };
                var req = new AddContractMeteringPointsRequest { ContractMeteringPoints = [newLink] };
                var (added, addErr) = await _contractClient.AddContractMeteringPointsAsync(contractId, req, ct);
                results.Add(Row(target, row, added ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                    added ? $"end → {endLabel}" : $"POST {addErr}"));
            }
        }
    }

    private static bool TryParseContractId(string field, out Guid contractId)
    {
        // Field format: "ContractLink[{guid}].End" (FieldDiff) or "ContractLink[{guid}]" (MissingInSettl).
        contractId = Guid.Empty;
        var open = field.IndexOf('[');
        var close = field.IndexOf(']');
        return open >= 0 && close > open && Guid.TryParse(field[(open + 1)..close], out contractId);
    }

    // ---- Customer core (full-echo wrapped v3 customer PUT — overwrite drifted leaves) ----

    private async Task ApplyCustomerCoreAsync(
        ReconcileApplyTarget target, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        var (info, _) = await _customerClient.SearchByCustomerNumberAsync(target.RepresentativeAccount, "DK", ct);
        if (info == null)
        {
            AddAll(target, rows, results, ApplyOutcome.NotFound, "representative customer not found in Settl");
            return;
        }

        // NationalIdentificationNumber (CPR) is the one customer-core leaf the v3 customer body does NOT
        // carry — v3 moved it to a dedicated sub-resource (flat single-field PUT). Route those rows there;
        // everything else goes through the full-echo master PUT below. (Before this, a CPR drift hit the
        // v3 master without that key and was silently Skipped as path-not-found.)
        var nationalIdRows = rows.Where(r => IsNationalIdField(r.Field)).ToList();
        var coreRows = rows.Where(r => !IsNationalIdField(r.Field)).ToList();

        await ApplyCustomerNationalIdAsync(target, info.Id, nationalIdRows, dryRun, results, ct);
        if (coreRows.Count == 0) return;

        // The customer UPDATE lives on v3 (the client's other reads are v2). Read the v3 master raw,
        // overwrite only the drifted leaves, echo the whole object back wrapped — preserves links +
        // untouched fields (verified non-destructive on featuretest).
        var (raw, getErr) = await _customerClient.GetRawByIdV3Async(info.Id, ct);
        if (raw == null)
        {
            AddAll(target, coreRows, results, ApplyOutcome.Failed, $"GET v3 customer {getErr}");
            return;
        }
        if (JsonNode.Parse(raw) is not JsonObject obj)
        {
            AddAll(target, coreRows, results, ApplyOutcome.Failed, "v3 customer response is not a JSON object");
            return;
        }

        var toWrite = ApplyCoreLeaves(target, obj, coreRows, results);
        if (toWrite.Count == 0) return;

        if (dryRun)
        {
            foreach (var (row, leafText) in toWrite)
                results.Add(Row(target, row, ApplyOutcome.WouldApply, leafText));
            return;
        }

        obj.Remove("Uri");
        obj.Remove("Links");
        var body = new JsonObject { ["Customer"] = obj, ["TransactionId"] = Guid.NewGuid().ToString() };
        var (ok, putErr) = await _customerClient.UpdateV3RawAsync(info.Id, body.ToJsonString(), ct);
        foreach (var (row, leafText) in toWrite)
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? leafText : $"PUT {putErr}"));
    }

    // ---- Customer national id (CPR) — dedicated v3 sub-resource, flat single-field PUT ----

    // v3 moved NationalIdentificationNumber out of the customer body into
    // PUT /api/v3/customers/{id}/national-identification-number (flat {TransactionId, value}, 204).
    // The endpoint requires a non-empty value — it cannot clear an existing CPR.
    private async Task ApplyCustomerNationalIdAsync(
        ReconcileApplyTarget target, Guid customerId, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            if (row.IntendedJson == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "no carried intended — re-run reconcile"));
                continue;
            }
            string? wanted;
            try { wanted = JsonNode.Parse(row.IntendedJson)?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "intended national id is not a string"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(wanted))
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "intended national id is empty — endpoint cannot clear"));
                continue;
            }

            var leafText = $"{row.Field} → {wanted}";
            if (dryRun)
            {
                results.Add(Row(target, row, ApplyOutcome.WouldApply, leafText));
                continue;
            }

            var (ok, err) = await _customerClient.UpdateNationalIdentificationNumberV3Async(customerId, wanted, ct);
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? leafText : $"PUT national-id {err}"));
        }
    }

    private static bool IsNationalIdField(string field)
        => string.Equals(field, "NationalIdentificationNumber", StringComparison.OrdinalIgnoreCase);

    // ---- Payer core (full-echo FLAT v1 payer PUT — overwrite drifted leaves, link preserved) ----

    private async Task ApplyPayerCoreAsync(
        ReconcileApplyTarget target, string payerId, List<ReconcileApplyRow> rows,
        bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        var (info, _) = await _payerClient.SearchByPayerIdentificationAsync(payerId, "DK", ct);
        if (info == null)
        {
            AddAll(target, rows, results, ApplyOutcome.NotFound, "payer not found in Settl");
            return;
        }

        // Payer core (PayerInformation: name/address/identifier) is written via a FLAT full-echo PUT:
        // echo the whole payer back (incl. CustomerId at top level → link preserved) and overwrite only
        // the drifted leaves. Verbatim echo is the key — a partial body resets CustomerId.
        var (raw, getErr) = await _payerClient.GetRawByIdAsync(info.Id, ct);
        if (raw == null)
        {
            AddAll(target, rows, results, ApplyOutcome.Failed, $"GET payer {getErr}");
            return;
        }
        if (JsonNode.Parse(raw) is not JsonObject obj)
        {
            AddAll(target, rows, results, ApplyOutcome.Failed, "payer response is not a JSON object");
            return;
        }

        var toWrite = ApplyCoreLeaves(target, obj, rows, results);
        if (toWrite.Count == 0) return;

        if (dryRun)
        {
            foreach (var (row, leafText) in toWrite)
                results.Add(Row(target, row, ApplyOutcome.WouldApply, leafText));
            return;
        }

        obj.Remove("Uri");
        obj.Remove("Links");
        obj["TransactionId"] = Guid.NewGuid().ToString();
        var (ok, putErr) = await _payerClient.UpdateRawAsync(info.Id, obj.ToJsonString(), ct);
        foreach (var (row, leafText) in toWrite)
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? leafText : $"PUT {putErr}"));
    }

    // Shared read-modify-write for customer/payer core: for each row, navigate the GET'd master object
    // to the drifted leaf (case-insensitive, so a PascalCase GET aligns with either casing), and — when
    // it still differs from the carried intended — overwrite it in place. Returns the rows actually
    // staged for the PUT (with their display text); records the per-row terminal outcomes for the rest.
    private List<(ReconcileApplyRow Row, string LeafText)> ApplyCoreLeaves(
        ReconcileApplyTarget target, JsonObject obj, List<ReconcileApplyRow> rows,
        List<ReconcileApplyRowResult> results)
    {
        var toWrite = new List<(ReconcileApplyRow, string)>();
        foreach (var row in rows)
        {
            // Start is a version boundary on both PayerInformation and PayerBilling (payer has /History
            // + ?EffectiveDate= versioning identical to billing MP). A PUT with a different Start creates
            // a new version rather than updating the existing entry — old version persists → infinite drift.
            var leaf = row.Field.Split('.').Last();
            if (leaf is "Start")
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "period boundary — Start cannot be rewritten (would split payer timeline)"));
                continue;
            }

            // Core rows carry their intended value as IntendedJson (scan emits it for Customer/Payer
            // core). An uncarried row (pre-IntendedJson cache) can't be written — there is no plan
            // fallback for core, so skip it with a re-run hint.
            if (row.IntendedJson == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "no carried intended — re-run reconcile"));
                continue;
            }
            var (parent, leafKey) = NavigateToLeaf(obj, row.Field);
            if (parent == null || leafKey == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, $"field path '{row.Field}' not found on the entity"));
                continue;
            }
            JsonNode? wanted;
            try { wanted = JsonNode.Parse(row.IntendedJson); }
            catch (JsonException) { results.Add(Row(target, row, ApplyOutcome.Skipped, "unparseable intended value")); continue; }

            var current = parent[leafKey];
            if (JsonEqual(current, wanted))
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }
            parent[leafKey] = wanted?.DeepClone();
            toWrite.Add((row, $"{row.Field} → {Text(wanted)}"));
        }
        return toWrite;
    }

    // Walk a dotted Field path to the (parent-object, real-leaf-key) it addresses, matching each
    // segment case-insensitively and returning the actual stored key so the overwrite uses the
    // existing casing. Returns (null, null) when any segment is missing or not an object — that row
    // is then Skipped (e.g. a v2-diff path with no v3 counterpart).
    private static (JsonObject? Parent, string? LeafKey) NavigateToLeaf(JsonObject root, string fieldPath)
    {
        var parts = fieldPath.Split('.');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var key = FindKey(current, parts[i]);
            if (key == null || current[key] is not JsonObject child) return (null, null);
            current = child;
        }
        var leaf = FindKey(current, parts[^1]);
        return leaf == null ? (null, null) : (current, leaf);
    }

    private static string? FindKey(JsonObject obj, string name)
    {
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }

    // ---- Missing whole entities (scoped create write-run over the merge-group — idempotent) ----

    private async Task ApplyMissingEntitiesAsync(
        ReconcileMappingOptions mapping, string? env, ReconcileApplyTarget target,
        DataMigration.Domain.Xellent.XellentLoadResult? loadResult, DateOnly refDate,
        List<ReconcileApplyRow> rows, bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        if (loadResult == null)
        {
            AddAll(target, rows, results, ApplyOutcome.Failed, "no capture loadResult for scoped create");
            return;
        }

        if (dryRun)
        {
            foreach (var row in rows)
                results.Add(Row(target, row, ApplyOutcome.WouldApply,
                    $"would run scoped create over {target.Accounts.Count} account(s)"));
            return;
        }

        // Same options + builders create uses, restricted to the merge-group, but DryRun=false so it
        // writes. search-or-create makes it idempotent: existing entities/links are matched, only the
        // genuinely-missing pieces are POSTed.
        var options = CreateOptionsFactory.BuildFromMapping(
            mapping, target.Accounts.ToArray(), env, refDate.ToDateTime(TimeOnly.MinValue), _exemption,
            _baseIgnoredGroups, target.Cos, dryRun: false);
        var runner = new CreateRunner(
            _apiOptions, _customerClient, _payerClient, _mpClient, _contractClient, _settlementGroupClient)
        {
            OnLogLine = line => _log($"      {line}"),
        };
        var (exitCode, _) = await runner.RunAsync(options, loadResult, ct);
        var outcome = exitCode == 0 ? ApplyOutcome.Applied : ApplyOutcome.Failed;
        foreach (var row in rows)
            results.Add(Row(target, row, outcome, $"scoped create exit={exitCode}"));
    }

    // ---- MP billing (carry current config, overwrite drifted leaves) ----

    private async Task ApplyMpBillingAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan, string mpId,
        MeteringPointInfoDto existing, BillingMeteringPointCurrentDto? current, string? getErr,
        List<ReconcileApplyRow> rows, bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        if (current == null)
        {
            AddAll(target, rows, results, ApplyOutcome.Failed, $"GET billing {getErr}");
            return;
        }

        // Intended leaves come from each row's carried IntendedJson; only uncarried rows fall back to the
        // captured plan's billing/payment config (null when the whole target was carried — no capture).
        var planMp = plan?.MeteringPoints.FirstOrDefault(m => string.Equals(m.Identification, mpId, StringComparison.Ordinal));

        // Rows tagged "<Config>@<versionStart>." target a specific historical version; untagged rows
        // target the current/latest version. Group per version so each is read + written once: the
        // current group reuses the already-fetched config, historical versions are fetched by their
        // Start. Writing each version carries its own Start, so the PUT updates that entry in place.
        foreach (var grp in rows.GroupBy(r => MpConfigVersionTag.Split(r.Field).Version))
        {
            var versionStart = grp.Key;
            BillingMeteringPointCurrentDto cfg;
            if (versionStart is null)
                cfg = current;
            else
            {
                var (v, vErr) = await _mpClient.GetBillingByIdAsync(
                    existing.Id, versionStart.Value.ToDateTime(new TimeOnly(12, 0)), ct);
                if (v == null)
                {
                    AddAll(target, grp, results, ApplyOutcome.Failed, $"GET billing@{versionStart:yyyy-MM-dd} {vErr}");
                    continue;
                }
                cfg = v;
            }
            await WriteMpConfigVersionAsync(target, planMp, existing, cfg, grp.ToList(), dryRun, results, ct);
        }
    }

    /// <summary>
    /// Writes one billing/payment-config version: carries <paramref name="cfg"/> so its Start/LifeTime/End
    /// survive (in-place update of that period, not a new historical entry), and overwrites only the
    /// drifted leaves from intended. For a historical version <paramref name="cfg"/> is that version's
    /// config, so the carried Start targets that entry.
    /// </summary>
    private async Task WriteMpConfigVersionAsync(
        ReconcileApplyTarget target, PlannedMeteringPoint? planMp,
        MeteringPointInfoDto existing, BillingMeteringPointCurrentDto cfg,
        List<ReconcileApplyRow> rows, bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        var billingNode = ToObject(cfg.BillingConfiguration);
        var paymentNode = ToObject(cfg.PaymentConfiguration);
        var intendedBilling = planMp?.Billing != null ? ToObject(planMp.Billing.BillingConfiguration) : null;
        var intendedPayment = planMp?.Billing != null ? ToObject(planMp.Billing.PaymentConfiguration) : null;

        bool touchedBilling = false, touchedPayment = false;
        var toWrite = new List<ReconcileApplyRow>();

        foreach (var row in rows)
        {
            var (kind, leaf) = ClassifyMpField(MpConfigVersionTag.Split(row.Field).Field);
            if (kind == MpConfig.None || leaf == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "unrecognised billing field"));
                continue;
            }
            var wantNode = kind == MpConfig.Billing ? intendedBilling : intendedPayment;

            // Whole-sub-config row: when Settl has NO billing/payment config (actual null) but create-true
            // intends one, the diff emits a single leaf="" row carrying the entire config. Backfill it from
            // create-true intended verbatim (its own Start — nothing current to carry, mirrors create).
            if (leaf.Length == 0)
            {
                var whole = ResolveWholeConfig(row, wantNode);
                if (whole == null)
                {
                    results.Add(Row(target, row, ApplyOutcome.Skipped, "no intended config to backfill"));
                    continue;
                }
                if (kind == MpConfig.Billing) { billingNode = whole; touchedBilling = true; }
                else { paymentNode = whole; touchedPayment = true; }
                toWrite.Add(row);
                continue;
            }

            // Start and End are period-management fields: Start identifies the version boundary in Settl,
            // End is set automatically when the next version begins. A PUT that overwrites Start with the
            // intended COS-date would split the billing timeline (new version) instead of updating the
            // existing entry — the old entry persists and reconcile keeps reporting the drift forever.
            if (leaf is "Start" or "End")
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "period boundary — Start/End cannot be rewritten via billing PUT (would split timeline)"));
                continue;
            }

            var curNode = kind == MpConfig.Billing ? billingNode : paymentNode;
            if (curNode == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "unrecognised billing field"));
                continue;
            }
            if (!ResolveIntendedLeaf(row, leaf, wantNode, out var wantedVal))
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "uncarried billing field (not in capture)"));
                continue;
            }
            var currentVal = curNode.ContainsKey(leaf) ? curNode[leaf] : null;
            if (JsonEqual(currentVal, wantedVal))
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }
            curNode[leaf] = wantedVal?.DeepClone();
            if (kind == MpConfig.Billing) touchedBilling = true; else touchedPayment = true;
            toWrite.Add(row);
        }

        if (toWrite.Count == 0) return;

        if (dryRun)
        {
            foreach (var row in toWrite)
                results.Add(Row(target, row, ApplyOutcome.WouldApply, row.Field));
            return;
        }

        var request = new UpdateBillingMeteringPointRequest
        {
            BillingConfiguration = touchedBilling
                ? JsonSerializer.Deserialize<BillingConfigurationDto>(billingNode!, SerializerOptions)
                : null,
            PaymentConfiguration = touchedPayment
                ? JsonSerializer.Deserialize<MeteringPointPaymentConfigurationDto>(paymentNode!, SerializerOptions)
                : null,
        };

        var (ok, putErr) = await _mpClient.UpdateBillingAsync(existing.Id, request, ct);
        foreach (var row in toWrite)
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? row.Field : $"PUT {putErr}"));
    }

    // ---- MP exemptions (additive) ----

    private async Task ApplyExemptionsAsync(
        ReconcileApplyTarget target, SettlMigrationPlan? plan, string mpId,
        MeteringPointInfoDto existing, BillingMeteringPointCurrentDto? current,
        List<ReconcileApplyRow> rows, bool dryRun, List<ReconcileApplyRowResult> results, CancellationToken ct)
    {
        // Intended exemption DTOs come from each row's carried IntendedJson (no capture); only rows from
        // a pre-IntendedJson cache fall back to the captured plan. planMp may be null when the whole
        // target was carried — that's fine, every row resolves its DTO from IntendedJson instead.
        var planMp = plan?.MeteringPoints.FirstOrDefault(m => string.Equals(m.Identification, mpId, StringComparison.Ordinal));

        var existingPeIds = current?.Exemptions?.Select(e => e.ProductElementId).ToHashSet() ?? [];

        var toAdd = new List<(ReconcileApplyRow Row, BillingExemptionDto Dto)>();
        foreach (var row in rows)
        {
            // The ProductElementId GUID lives in Field; EntityKey is the MP identification (natural key,
            // used above for the grouping + SearchByIdentificationAsync). See ReconcileModels doc.
            if (!Guid.TryParse(row.Field, out var peId))
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "exemption key is not a ProductElementId GUID"));
                continue;
            }
            if (existingPeIds.Contains(peId))
            {
                results.Add(Row(target, row, ApplyOutcome.NoLongerDrifts));
                continue;
            }
            var intended = ResolveIntendedExemption(row, peId, planMp?.Exemptions);
            if (intended == null)
            {
                results.Add(Row(target, row, ApplyOutcome.Skipped, "exemption not in carried intended or capture"));
                continue;
            }
            toAdd.Add((row, intended));
        }

        if (toAdd.Count == 0) return;

        if (dryRun)
        {
            foreach (var (row, dto) in toAdd)
                results.Add(Row(target, row, ApplyOutcome.WouldApply, $"+{dto.ExemptionPercentage}% exempt"));
            return;
        }

        var request = new AddMeteringPointExemptionsRequest { Exemptions = toAdd.Select(t => t.Dto).ToList() };
        var (ok, addErr) = await _mpClient.AddExemptionsAsync(existing.Id, request, ct);
        foreach (var (row, dto) in toAdd)
            results.Add(Row(target, row, ok ? ApplyOutcome.Applied : ApplyOutcome.Failed,
                ok ? $"+{dto.ExemptionPercentage}% exempt" : $"POST {addErr}"));
    }

    // ---- helpers ----

    // The intended exemption to add: the row's carried IntendedJson (scan-serialized BillingExemptionDto)
    // wins; only an uncarried row (pre-IntendedJson cache) falls back to the captured plan. Malformed
    // carried JSON falls through to the plan rather than throwing.
    private static BillingExemptionDto? ResolveIntendedExemption(
        ReconcileApplyRow row, Guid peId, IEnumerable<BillingExemptionDto>? planExemptions)
    {
        if (row.IntendedJson != null)
        {
            try
            {
                if (JsonSerializer.Deserialize<BillingExemptionDto>(row.IntendedJson, SerializerOptions) is { } dto)
                    return dto;
            }
            catch (JsonException) { /* fall through to the captured plan */ }
        }
        return planExemptions?.FirstOrDefault(e => e.ProductElementId == peId);
    }

    // The intended value for one billing leaf: the row's carried IntendedJson (round-trippable raw token)
    // wins; an uncarried row falls back to the captured plan node. Returns false when neither side has it
    // (the leaf isn't in the captured intended, or carried JSON is malformed and the plan lacks the key).
    private static bool ResolveIntendedLeaf(
        ReconcileApplyRow row, string leaf, JsonObject? intendedFromPlan, out JsonNode? value)
    {
        if (row.IntendedJson != null)
        {
            try { value = JsonNode.Parse(row.IntendedJson); return true; }
            catch (JsonException) { /* fall through to the captured plan */ }
        }
        if (intendedFromPlan != null && intendedFromPlan.ContainsKey(leaf))
        {
            value = intendedFromPlan[leaf];
            return true;
        }
        value = null;
        return false;
    }

    // The whole intended sub-config for a leaf="" backfill row: the row's carried IntendedJson (the full
    // create-true config object) wins; an uncarried row falls back to the captured plan's whole config.
    private static JsonObject? ResolveWholeConfig(ReconcileApplyRow row, JsonObject? fromPlan)
    {
        if (row.IntendedJson != null)
        {
            try { if (JsonNode.Parse(row.IntendedJson) is JsonObject o) return o; }
            catch (JsonException) { /* fall through to the captured plan */ }
        }
        return fromPlan;
    }

    private enum MpConfig { None, Billing, Payment }

    private static (MpConfig Kind, string? Leaf) ClassifyMpField(string field)
    {
        if (LeafAfter(field, "BillingConfiguration.") is { } b) return (MpConfig.Billing, b);
        if (LeafAfter(field, "PaymentConfiguration.") is { } p) return (MpConfig.Payment, p);
        return (MpConfig.None, null);
    }

    /// <summary>The single leaf after a known prefix (e.g. "PayerBilling.OrderIdentifier" → "OrderIdentifier").
    /// Returns null when the path does not start with the prefix. Nested billing paths are flat in phase 1.</summary>
    private static string? LeafAfter(string field, string prefix)
        => field.StartsWith(prefix, StringComparison.Ordinal) ? field[prefix.Length..] : null;

    private static JsonObject? ToObject(object? dto)
        => dto == null ? null : JsonSerializer.SerializeToNode(dto, dto.GetType(), SerializerOptions)!.AsObject();

    private static bool JsonEqual(JsonNode? a, JsonNode? b)
        => string.Equals(a?.ToJsonString(), b?.ToJsonString(), StringComparison.Ordinal);

    private static string Text(JsonNode? node)
    {
        var s = node?.ToJsonString();
        if (s is null) return "null";
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    private static void Add(Dictionary<string, List<ReconcileApplyRow>> map, string key, ReconcileApplyRow row)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(row);
    }

    private static void AddAll(
        ReconcileApplyTarget target, IEnumerable<ReconcileApplyRow> rows,
        List<ReconcileApplyRowResult> results, ApplyOutcome outcome, string? detail)
    {
        foreach (var row in rows) results.Add(Row(target, row, outcome, detail));
    }

    private static ReconcileApplyRowResult Row(
        ReconcileApplyTarget target, ReconcileApplyRow row, ApplyOutcome outcome, string? detail = null)
        => new(target.RepresentativeAccount, row.Entity, row.SubType, row.EntityKey, row.Field, outcome, detail);
}
