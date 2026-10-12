using System.Text.Json;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;

namespace DataMigration.OpsCli.Runners;

public class ExportSettlementConfigRunOptions
{
    public string OutputPath { get; set; } = string.Empty;
    public string DataAreaId { get; set; } = "hol";
    public string Environment { get; set; } = "";
    /// <summary>
    /// BillingPlanNum → Settlement Schedule name, loaded from the solution's
    /// <c>settlement-config.json</c> (<c>schedules</c>) via <see cref="SettlementConfigLoader"/>.
    /// Plan codes not present in the map keep their raw code as schedule name.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ScheduleMap { get; set; }
    /// <summary>
    /// Contract template (<c>Contracttemplatenum</c>) → Settl Customer Segment per customer
    /// identification, loaded from <c>settlement-config.json</c> (<c>segments</c>) via
    /// <see cref="SettlementConfigLoader"/>. The identification (CVR / CPR / blank) is read from
    /// Xellent for each representative account, because Settl does not accept a private segment on a
    /// CVR number. Several values for one identification mean the segment still cannot be decided;
    /// those customers are reported as conflicts. Templates not in the map — and templates that say
    /// nothing about the customer's identification — keep their raw name as segment.
    /// </summary>
    public IReadOnlyDictionary<string, SegmentRule>? SegmentMap { get; set; }
    /// <summary>Stop after this many Settl metering points (0 = all). For quick sample runs.</summary>
    public int Limit { get; set; }
    /// <summary>
    /// The whole loaded config, carrying the Settl-driven rules (<c>groupSchedules</c>,
    /// <c>settlSegments</c>, <c>templateProducts</c>) alongside the two maps above. Null leaves the
    /// export exactly as it was: no rule answers, no comparison.
    /// </summary>
    public SettlementConfig? Config { get; set; }
    /// <summary>
    /// Also emit the full rule-by-rule comparison (one row per customer, with agreement counted).
    /// On by default for the CLI, which exists to make that comparison. Hosts that only want what
    /// the rules say — the dashboard shows "what should this customer have" — turn it off and read
    /// the two answers off <see cref="SettlementConfigCustomerFacts"/> instead.
    /// </summary>
    public bool IncludeComparison { get; set; } = true;
    /// <summary>
    /// Emit one row per customer with what Settl says about her — settlement groups, exemptions, and
    /// the two rule answers. On by default. A host that reads none of it turns it off: on the full
    /// population the rows are around 7.000 and some 2 MB of the file, and every value in them is
    /// either derivable from the groups above or already present per customer in
    /// <see cref="IncludeComparison"/>'s rows.
    /// </summary>
    public bool IncludeCustomerFacts { get; set; } = true;
    /// <summary>
    /// Read each metering point's billing exemptions from Settl. Off by default: it is one HTTP call
    /// per metering point (~11k over the full population), where every other source in this export
    /// costs fewer calls. When requested, a resolved CVR proposal becomes Erhverv_UG if any MP has
    /// a current exemption, except SME. Historical applicability is not verified and is reported.
    /// </summary>
    public bool WithExemptions { get; set; }
    /// <summary>
    /// Select customers with an actual supplier change on this Danish date using historical delivery
    /// and dated payer ownership. Include only their metering points delivering and owned on that
    /// date. Missing historical evidence is unresolved, with no current-owner fallback. Null keeps
    /// the current in-delivery population. See <see cref="SettlementConfigDocument.HistoricalSelection"/>.
    /// With MeteringPointIds, this required date applies to every listed point and selects its
    /// Xellent contract at the preceding day, without Settl population or historical ownership reads.
    /// </summary>
    public DateOnly? ChangeOfSupplierDate { get; set; }
    /// <summary>Strict point selection, requiring ChangeOfSupplierDate and Limit=0. The dated raw
    /// Xellent account supplies the customer; no other point contributes and no representative merge is used.</summary>
    public IReadOnlyList<string>? MeteringPointIds { get; set; }
    /// <summary>
    /// Turn the CVR+fingerprint merge off, so every Xellent account stands as its own customer.
    /// Off by default — a run that does not set it resolves representatives exactly as before.
    ///
    /// <para>Set it for a population migrated all-active, where one account became one Settl
    /// customer. Xellent's merge then names representatives Settl does not have, and the only place
    /// that shows is the fallback: a metering point Settl cannot name a customer for would get the
    /// merge-group's lowest account instead of its own. The customer number itself still comes from
    /// Settl either way — this changes nothing about where it is read.</para>
    ///
    /// <para>It is also the cheap path. With no merge the resolver drops both of its joins and
    /// becomes a single chunked CustTable read, which is the difference between minutes and seconds
    /// on the ~45.000-account production population.</para>
    /// </summary>
    public bool DisableRepresentativeMerge { get; set; }
    public string CountryCode { get; set; } = "DK";
}

/// <summary>
/// Read-only post-migration export: lists Settl customer numbers grouped by Settlement Schedule.
///
/// <para>Population comes from Settl, the schedule value from Xellent
/// (<c>ExuContractTable.Billingplannum</c>) — Settl exposes no schedule field. Both the population
/// rule and the Xellent join follow <c>reconcile</c>: market-v3 pagination keeping in-delivery parent
/// MPs, then <see cref="MeteringPointAccountResolver"/> pinning each MP to the contract that ended at
/// <c>COS-1</c>. That pinning is what makes the result correct — an MP identification can move
/// between accounts over time, so a date-agnostic join would report whichever contract happens to
/// look current rather than the one that was migrated.</para>
///
/// <para><b>The customer number is read from Settl, not computed.</b> Each metering point is asked
/// who holds it (metering point → payer → customer), and that customer number is what the export
/// writes. The Xellent representative is only the fallback for metering points Settl cannot answer
/// for. Recomputing the merge from Xellent looks right and is not: a customer's number was decided
/// by the merge rule <i>and</i> the account list in force the day its batch was migrated, so a
/// rule that has since changed — or a population wider than that batch — names a customer number
/// Settl does not have. Sparekassen Kronjylland was one Settl customer and thirteen rows in the
/// export before this (see docs/representation-principle.md for the merge rule itself).</para>
/// <para>MP-list mode is the exception: it uses the supplied common COS date and the raw dated
/// Xellent account, formatted for the environment without merging. Settl is read only for
/// explicitly requested current exemptions and their ownership verification.</para>
/// </summary>
public class ExportSettlementConfigRunner
{
    /// <summary>How many metering point ids fit in one payersForMeteringPoints call. The endpoint
    /// takes the whole batch in the body, so this is only about keeping a single request sane —
    /// 400 covers the full ~12k population in ~30 calls.</summary>
    private const int PayerLookupBatchSize = 400;

    /// <summary>What Settl holds about one customer: the number it is known by, its name, and the
    /// identification the segment rule turns on. Read from Settl rather than Xellent, so it stays
    /// the customer's own answer even when the two systems disagree about who the customer is.</summary>
    private sealed record SettlCustomer(string Number, string? Name, CustomerIdentityKind Identity);

    /// <summary>Settl returns delivery-period starts as UTC instants of Danish midnight
    /// (2026-05-01 becomes "2026-04-30T22:00:00Z"); the COS must be the Danish calendar date, so the
    /// instant is converted before the date is taken — otherwise COS lands one day early and no
    /// contract matches COS-1. Same conversion as ReconcileRunner.</summary>
    private static readonly TimeZoneInfo DkTz = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Romance Standard Time" : "Europe/Copenhagen");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = JsonOutput.HumanReadable
    };

    private readonly ISettlMeteringPointClient? _meteringPointClient;
    private readonly IXellentDomainLoader _loader;
    private readonly string _connectionString;
    private readonly Action<string> _log;
    private readonly ISettlCustomerClient? _customerClient;
    private readonly ISettlContractClient? _contractClient;
    private readonly ISettlSettlementGroupClient? _settlementGroupClient;
    private readonly ISettlPayerClient? _payerClient;
    private readonly Action<SettlementConfigProgress>? _progress;
    private readonly object _progressSync = new();

    /// <param name="meteringPointClient">Required for legacy Settl selection and optional exemption
    /// reads. May be null for an MP-list run without exemptions.</param>
    /// <param name="log">Sink for progress/summary lines, one complete line per call. Defaults to
    /// stderr for CLI use. Hosts that run this in-process (the dashboard) pass their own sink —
    /// redirecting <see cref="Console.Error"/> there would swallow output from every other runner
    /// sharing the process.</param>
    /// <param name="customerClient">Needed to map Settl customer GUIDs to customer numbers, which is
    /// what settlement-group membership arrives as. Omit and the membership stays unread.</param>
    /// <param name="contractClient">Needed to learn which Settl contract each metering point sits on
    /// (charger ownership and the SME test). Omit only when neither answer is needed.</param>
    /// <param name="settlementGroupClient">Needed to read the real membership of each Settlement
    /// Group from Settl. Supply it together with <paramref name="customerClient"/> to get group
    /// membership in the per-customer facts, with or without the candidate-rule comparison.</param>
    /// <param name="payerClient">Needed to name the customer behind a metering point that has no
    /// Xellent contract — the payer listing is the only route from metering point to customer. Supply
    /// it together with <paramref name="customerClient"/> to get segment answers on the unresolved
    /// rows; omit and they keep their reason and nothing else.</param>
    /// <param name="progress">Optional structured stage/count updates, separate from human logs.
    /// Counts reflect completed work, including failed reads; unknown totals stay null.</param>
    /// <remarks>The three Settl clients are optional so existing callers — the dashboard constructs
    /// this runner in-process — keep compiling and behave exactly as before. A missing client is
    /// reported in the output rather than silently producing an empty column.</remarks>
    public ExportSettlementConfigRunner(
        ISettlMeteringPointClient? meteringPointClient,
        IXellentDomainLoader loader,
        string connectionString,
        Action<string>? log = null,
        ISettlCustomerClient? customerClient = null,
        ISettlContractClient? contractClient = null,
        ISettlSettlementGroupClient? settlementGroupClient = null,
        ISettlPayerClient? payerClient = null,
        Action<SettlementConfigProgress>? progress = null)
    {
        _meteringPointClient = meteringPointClient;
        _loader = loader;
        _connectionString = connectionString;
        _log = log ?? Console.Error.WriteLine;
        _customerClient = customerClient;
        _contractClient = contractClient;
        _settlementGroupClient = settlementGroupClient;
        _payerClient = payerClient;
        _progress = progress;
    }

    private void ReportProgress(SettlementConfigProgress progress)
    {
        lock (_progressSync) _progress?.Invoke(progress);
    }

    private void ReportProgress(string stage, int? done = null, int? total = null, string? unit = null) =>
        ReportProgress(new SettlementConfigProgress(stage, done, total, unit));

    public async Task<int> RunAsync(ExportSettlementConfigRunOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            _log("ERROR: --output is required");
            return 1;
        }

        _log("=== export-settlement-config (read-only) ===");
        _log("");

        var selection = SettlementConfigCoverage.Validate(options.MeteringPointIds,options.ChangeOfSupplierDate,options.Limit);
        if (options.Config is not null)
            options.Config = options.Config with { CustomerExceptions = SettlementCustomerExceptionRules.Validate(options.Config) };
        var selectionProblems = new List<SettlementConfigUnresolved>();
        if (selection is null && _meteringPointClient is null)
            throw new ArgumentException("A Settl metering-point client is required without an MP list.");
        Dictionary<string,DateOnly> cosByMpId;
        Dictionary<string,Guid> settlIdByMpId;
        int scanned;
        if (selection is not null)
        {
            cosByMpId = options.MeteringPointIds!.Distinct(StringComparer.Ordinal)
                .ToDictionary(id => id,_ => options.ChangeOfSupplierDate!.Value,StringComparer.Ordinal);
            settlIdByMpId = new(StringComparer.Ordinal);
            scanned = cosByMpId.Count;
            ReportProgress("MeteringPointSelection",scanned,scanned,"meteringPoints");
        }
        else (cosByMpId,settlIdByMpId,scanned) = await LoadInDeliveryMeteringPointsAsync(options, selectionProblems, cancellationToken);
        if (selection is null && options.ChangeOfSupplierDate is null && cosByMpId.Count == 0)
        {
            _log("  No in-delivery metering points found — nothing to export");
            return 1;
        }
        HistoricalSettlementSource.Result? historical = null;
        var historicalIssues = new List<SettlementConfigHistoricalIssue>();
        var incompleteCustomers = new HashSet<string>(StringComparer.Ordinal);
        var portfolioCauses = new Dictionary<string,List<SettlementConfigHistoricalIssue>>(StringComparer.Ordinal);
        void RecordPortfolioCause(string customer,SettlementConfigHistoricalIssue cause)
        {
            if (!portfolioCauses.TryGetValue(customer,out var causes)) portfolioCauses[customer] = causes = [];
            causes.Add(cause);
        }
        var customerReadProblems = new Dictionary<string,string>(StringComparer.Ordinal);
        var settlCustomers = selection is not null ? null
            : await LoadSettlCustomerDirectoryAsync(cancellationToken, options.ChangeOfSupplierDate is not null);
        IReadOnlyDictionary<string, SettlCustomer>? settlCustomerByMpId;
        if (selection is null && options.ChangeOfSupplierDate is { } asOfDate)
        {
            historical = await HistoricalSettlementSource.ReadAsync(asOfDate, settlIdByMpId,
                (id, ct) => _meteringPointClient!.GetDeliveryStatusesAsync(id, ct),
                (ids, date, ct) => _payerClient is null
                    ? Task.FromResult<(List<DataMigration.SettlApi.Models.PayerForMeteringPointsDto>?, string?)>((null, "Payer client unavailable"))
                    : _payerClient.FindPayersForMeteringPointsAsync(ids, date, ct), _log, cancellationToken, ReportProgress);
        }
        if (historical is not null)
        {
            cosByMpId = historical.DeliveryStartByMeteringPoint;
            historicalIssues.AddRange(historical.Issues);
            var datedCustomers = new Dictionary<string, SettlCustomer>(StringComparer.Ordinal);
            foreach (var (mp, customerId) in historical.CustomerByMeteringPoint)
            {
                if (settlCustomers is not null && settlCustomers.TryGetValue(customerId, out var customer)) datedCustomers[mp] = customer;
                else historicalIssues.Add(new(mp, settlCustomers is null
                    ? SettlementConfigUnresolvedReason.HistoricalCustomerReadFailed : SettlementConfigUnresolvedReason.HistoricalCustomerMissing));
            }
            settlCustomerByMpId = datedCustomers;
            foreach (var id in historical.IncompleteCustomers)
                if (settlCustomers is not null && settlCustomers.TryGetValue(id, out var customer)) incompleteCustomers.Add(customer.Number);
            foreach (var (id,causes) in historical.IncompleteCustomerCauses)
                if (settlCustomers is not null && settlCustomers.TryGetValue(id,out var customer))
                    foreach (var cause in causes) RecordPortfolioCause(customer.Number,cause);
        }
        else settlCustomerByMpId = selection is not null ? null
            : await LoadCustomerByMeteringPointAsync(settlIdByMpId, settlCustomers, cancellationToken,customerReadProblems);
        if (cosByMpId.Count == 0)
        {
            if (historicalIssues.Count == 0 && selection is null)
            {
                _log("  No in-delivery metering points found — nothing to export");
                return 1;
            }
            _log("  Historical population could not be established; exporting unresolved history issues.");
        }

        // Select whole historical customers before reading their dated Xellent sources.
        SettlementConfigChangeOfSupplierFilter? cosFilter = null;
        if (selection is null && options.ChangeOfSupplierDate is { } cosDate)
        {
            var cohort = ChangeOfSupplierCohort.Select(
                cosDate,
                cosByMpId,
                settlCustomerByMpId!.ToDictionary(kv => kv.Key, kv => kv.Value.Number, StringComparer.Ordinal),
                CustomerNames(settlCustomerByMpId, new Dictionary<string, string>()),
                historical!.ChangeOfSupplierMeteringPoints);
            cosFilter = cohort.Filter;

            var keep = cohort.KeptMeteringPointIdentifications.ToHashSet(StringComparer.Ordinal);
            cosByMpId = cosByMpId.Where(kv => keep.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            settlIdByMpId = settlIdByMpId.Where(kv => keep.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

            _log($"  Change-of-supplier filter {cosDate:yyyy-MM-dd}: {cosFilter.Customers} customer(s) kept,"
                + $" {cosFilter.MeteringPointsOnDate} MP(s) on the date"
                + $" + {cosFilter.MeteringPointsFromSharedCustomer} MP(s) from a shared customer"
                + $" = {keep.Count} MP(s)");
            if (cosFilter.MeteringPointsOnDateWithoutCustomer.Count > 0)
                _log($"    {cosFilter.MeteringPointsOnDateWithoutCustomer.Count} MP(s) on the date have no confirmed historical customer in Settl — kept on their own date");
            if (keep.Count == 0 && historicalIssues.Count == 0)
            {
                _log("  No metering point changed supplier on that date — nothing to export");
                return 1;
            }
        }

        _log("  Resolving accounts and billing plans in Xellent (COS-1)...");
        var sourceDates = historical is null ? cosByMpId : cosByMpId
            .Where(kv => settlCustomerByMpId!.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        ReportProgress("ContractParts");
        var contractParts = await ResolveContractPartsAsync(sourceDates,options.DataAreaId,cancellationToken);
        var resolution = contractParts.Resolution;
        var accountByMpId = resolution.Pairs
            .GroupBy(p => p.MpId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Account, StringComparer.Ordinal);
        _log($"  Resolved {accountByMpId.Count} of {cosByMpId.Count} MP(s) to a Xellent account");

        // Fallback identity for metering points Settl could not name a customer for. Still the
        // representative and not the raw contract account: if we have to guess from Xellent, guess
        // the way the migration did (see docs/representation-principle.md).
        var accounts = accountByMpId.Values.Distinct(StringComparer.Ordinal).ToArray();
        // The wording follows the option, because "resolved N → M representatives" reads as a
        // finding, and with the merge off it would be N → N every time — a number that says nothing.
        var noMerge = selection is not null || options.DisableRepresentativeMerge;
        _log(noMerge
            ? $"  Reading account names for {accounts.Length} account(s) — representative merge disabled, each account is its own customer..."
            : $"  Resolving representatives for {accounts.Length} account(s)...");
        ReportProgress(selection is not null ? "AccountNames" : "Representatives");
        var representation = accounts.Length == 0 ? RepresentativePortioning.Merge([])
            : await _loader.ResolveRepresentativesAsync(accounts, options.DataAreaId, cancellationToken, noMerge);
        _log(noMerge
            ? $"  Read {representation.NameByAccount.Count} of {accounts.Length} account(s) in Xellent — no merge, no representative lookup"
            : $"  Resolved {accounts.Length} account(s) → {representation.RepresentativeByAccount.Values.Distinct(StringComparer.Ordinal).Count()} representative(s)");

        // Current contract links are not attributed to a historical customer. Current exemptions
        // are explicitly requested as proposal inputs; their historical validity is reported as unknown.
        IReadOnlyDictionary<string, IReadOnlyList<string>>? contractNamesByMpId = null;
        var missingExemptionPoints = new List<SettlementConfigExemptionRead>();
        if (selection is not null && options.WithExemptions)
            settlIdByMpId = await LoadSelectedExemptionPointsAsync(cosByMpId.Keys,missingExemptionPoints,cancellationToken);
        var exemptionReads = await LoadExemptionsAsync(options, settlIdByMpId, cancellationToken);
        if (exemptionReads is not null && missingExemptionPoints.Count > 0)
            exemptionReads = exemptionReads.Concat(missingExemptionPoints).OrderBy(r => r.MeteringPointIdentification,StringComparer.Ordinal).ToArray();
        var currentExemptionCustomers = selection is not null && exemptionReads is not null
            ? await LoadSelectedExemptionCustomersAsync(settlIdByMpId,cancellationToken)
            : historical is not null && exemptionReads is not null
            ? await SettlementCurrentCustomerSource.ReadAsync(settlIdByMpId,
                settlCustomers?.ToDictionary(kv => kv.Key, kv => kv.Value.Number),
                (ids, at, ct) => _payerClient is null
                    ? Task.FromResult<(List<DataMigration.SettlApi.Models.PayerForMeteringPointsDto>?, string?)>((null, "Payer client unavailable"))
                    : _payerClient.FindPayersForMeteringPointsAsync(ids, at, ct), DateTime.UtcNow, cancellationToken,
                p => ReportProgress(p with { Stage = "ExemptionCustomers" }))
            : null;
        var exemptionByMpId = exemptionReads?.Where(r => r.ReadStatus == "Read").ToDictionary(r => r.MeteringPointIdentification, r => r.CurrentExemptions.Count > 0, StringComparer.Ordinal);

        var entries = new List<SettlementConfigEntry>();
        var unresolved = historicalIssues.Select(i => new SettlementConfigUnresolved(i.MeteringPointIdentification, i.Reason)).ToList();
        unresolved.AddRange(selectionProblems);
        // Identification per customer, taken from Settl for every customer Settl named. Only the
        // fallback customers are looked up in Xellent afterwards.
        var identityFromSettl = new Dictionary<string, CustomerIdentityKind>(StringComparer.Ordinal);
        var fallbackCustomers = new HashSet<string>(StringComparer.Ordinal);
        var meteringPointsFromSettl = 0;
        var movedCustomers = new HashSet<string>(StringComparer.Ordinal);

        ReportProgress("CalculatingProposals");

        foreach (var mpId in cosByMpId.Keys)
        {
            if (historical is not null && !settlCustomerByMpId!.ContainsKey(mpId)) continue;
            if (!accountByMpId.TryGetValue(mpId, out var account))
            {
                // No account at COS-1: either the MP has no Xellent origin at all (created directly
                // in Settl) or its migrated contract is gone/changed. The two are worth telling apart.
                var reason = contractParts.ConflictingMeteringPoints.Contains(mpId)
                        ? SettlementConfigUnresolvedReason.AmbiguousHistoricalContract
                        : resolution.XellentMpIds.Contains(mpId)
                        ? SettlementConfigUnresolvedReason.NoContractAtChangeOfSupplier
                        : SettlementConfigUnresolvedReason.NotFoundInXellent;
                if (historical is not null)
                {
                    var customer = settlCustomerByMpId![mpId].Number;
                    incompleteCustomers.Add(customer);
                    RecordPortfolioCause(customer,new(mpId,reason));
                }
                unresolved.Add(new SettlementConfigUnresolved(mpId,reason));
                continue;
            }
            if (!resolution.BillingPlanByMpId.TryGetValue(mpId, out var planCode) || string.IsNullOrWhiteSpace(planCode))
            {
                if (historical is not null)
                {
                    var customer = settlCustomerByMpId![mpId].Number;
                    incompleteCustomers.Add(customer);
                    RecordPortfolioCause(customer,new(mpId,SettlementConfigUnresolvedReason.NoBillingPlan));
                }
                unresolved.Add(new SettlementConfigUnresolved(mpId, SettlementConfigUnresolvedReason.NoBillingPlan));
                planCode = "";
            }
            var settlCustomer = settlCustomerByMpId is not null
                && settlCustomerByMpId.TryGetValue(mpId, out var found)
                ? found
                : null;
            representation.RepresentativeByAccount.TryGetValue(account, out var representative);

            // Settl first, Xellent only where Settl stayed silent. A metering point with neither is
            // the same dead end as before: no customer to hang the row on.
            var customerNumber = selection is not null ? CustomerNumberPolicy.PadForEnv(account,options.Environment,out _)
                : settlCustomer?.Number ?? (historical is null ? representative : null);
            if (customerNumber is null)
            {
                unresolved.Add(new SettlementConfigUnresolved(mpId, SettlementConfigUnresolvedReason.NoRepresentative));
                continue;
            }

            // The dated contract account can be the customer's original migration identity even
            // when today's merge picks another representative. Accept either proven account form.
            var customerAccountCandidates = CustomerNumberPolicy.AccountCandidates(customerNumber, options.Environment);
            if (historical is not null
                && !customerAccountCandidates.Contains(account, StringComparer.Ordinal)
                && (representative is null || !customerAccountCandidates.Contains(representative, StringComparer.Ordinal)))
            {
                incompleteCustomers.Add(customerNumber);
                RecordPortfolioCause(customerNumber,new(mpId,SettlementConfigUnresolvedReason.XellentCustomerMismatch));
                historicalIssues.Add(new(mpId, SettlementConfigUnresolvedReason.XellentCustomerMismatch));
                unresolved.Add(new(mpId, SettlementConfigUnresolvedReason.XellentCustomerMismatch));
            }

            string? name;
            if (settlCustomer is not null)
            {
                meteringPointsFromSettl++;
                identityFromSettl[customerNumber] = settlCustomer.Identity;
                name = settlCustomer.Name;
                // Worth counting on its own: this is exactly the drift that made one Settl customer
                // show up as several rows before the number was read from Settl. Compared against the
                // un-padded forms too — 41589 and 941589 are the same customer, not drift.
                if (representative is not null
                    && !CustomerNumberPolicy.AccountCandidates(customerNumber, options.Environment)
                        .Contains(representative, StringComparer.Ordinal))
                    movedCustomers.Add(customerNumber);
                if (string.IsNullOrWhiteSpace(name) && representative is not null)
                    representation.NameByAccount.TryGetValue(representative, out name);
            }
            else
            {
                fallbackCustomers.Add(customerNumber);
                representation.NameByAccount.TryGetValue(selection is not null ? account : customerNumber, out name);
            }

            // Segment source: same contract row as the schedule (COS-1 pinned). A contract without a
            // template is not an unresolved schedule row — it only means no segment for that MP.
            resolution.ContractTemplateByMpId.TryGetValue(mpId, out var template);
            entries.Add(new SettlementConfigEntry(
                mpId, planCode, customerNumber, name, template,
                // Products stay paired with the template of the same pinned contract, per MP — see
                // TemplateProductSegmentResolver on why they must not be pooled per customer.
                Lookup(resolution.ProductNumsByMpId, mpId),
                Lookup(contractNamesByMpId, mpId),
                // Absent from the map = not read, which is null rather than false: "no exemption"
                // is an answer the run did not produce.
                selection is null && historical is null && exemptionByMpId is not null && exemptionByMpId.TryGetValue(mpId, out var exempt)
                    ? exempt
                    : null,
                XellentAccount: account,
                ReferenceDate: cosByMpId[mpId].AddDays(-1),
                XellentContractNumber: resolution.OwnerContractByMpId.GetValueOrDefault(mpId)?.ContractNumber,
                MeterPointTypeCode: resolution.MeterPointTypesByMpId?.GetValueOrDefault(mpId)?.Code,
                MeterPointTypeReadStatus: resolution.MeterPointTypesByMpId?.GetValueOrDefault(mpId)?.ReadStatus ?? "NotRead"));
        }

        // Which customer numbers are Xellent accounts at all — asked for every customer, not just the
        // ones Settl could not name. This is what decides whether a metering point's COS-1 contract
        // describes the customer on the row or the holder before her: a customer created directly in
        // Settl took the metering point over after cutover, and the contract is the previous holder's.
        // The lookup doubles as the identification read, since a customer with no CustTable row is
        // exactly the one it cannot answer for.
        var customers = entries.Select(e => e.CustomerNumber).Distinct(StringComparer.Ordinal).ToArray();

        // A customer number read in Settl is not always the Xellent Accountnum. Production requires
        // six digits, so shorter accounts were left-padded with '9' when they were created: Randers
        // Sportsdykkerklub is account 41589 in Xellent and customer 941589 in Settl. Asking CustTable
        // for the number Settl carries finds nothing for her, and taking that as "created in Settl"
        // would strip the plan and template off 249 perfectly ordinary customers.
        var accountCandidates = selection is not null
            ? entries.GroupBy(e => e.CustomerNumber,StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (IReadOnlyList<string>)g.Select(e => e.XellentAccount!).Distinct(StringComparer.Ordinal).ToArray(),StringComparer.Ordinal)
            : customers.ToDictionary(
            c => c,
            c => CustomerNumberPolicy.AccountCandidates(c, options.Environment),
            StringComparer.Ordinal);

        var lookedUp = accountCandidates.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToArray();
        _log($"  Reading CVR/CPR in Xellent for {customers.Length} customer number(s)"
            + (lookedUp.Length > customers.Length ? $" ({lookedUp.Length} account candidates after un-padding)" : "") + "...");
        ReportProgress("CustomerIdentities");
        var identityByAccount = await ResolveCustomerIdentitiesAsync(lookedUp, options.DataAreaId, cancellationToken);

        // The first candidate that is a real account: the number as Settl holds it before any
        // de-padded form, so a genuine six-digit account beginning with 9 keeps its own identity.
        var identityFromXellent = accountCandidates
            .Select(kv => (kv.Key, Account: kv.Value.FirstOrDefault(identityByAccount.ContainsKey)))
            .Where(x => x.Account is not null)
            .ToDictionary(x => x.Key, x => identityByAccount[x.Account!], StringComparer.Ordinal);

        // Proposal identity comes from Xellent. Settl identity never changes a proposal.
        var identityByCustomer = new Dictionary<string, CustomerIdentityKind>(identityFromXellent, StringComparer.Ordinal);

        // Clear every Xellent-derived field for customers that are not Xellent accounts. Done on the
        // entries, before any grouping, so schedule, segment, products and the comparison all follow
        // from one decision instead of each remembering to check.
        var notInXellent = customers.Where(c => !identityFromXellent.ContainsKey(c)).ToHashSet(StringComparer.Ordinal);
        if (notInXellent.Count > 0)
        {
            entries = [.. entries.Select(e => notInXellent.Contains(e.CustomerNumber)
                ? e with { CustomerInXellent = false, BillingPlanNum = "", ContractTemplate = null, ProductNums = [], MeterPointTypeCode = null, MeterPointTypeReadStatus = "NotRead" }
                : e)];
            _log(selection is not null
                ? $"  No current Xellent customer record found for {notInXellent.Count} customer(s); plan, template and products withheld"
                : $"  {notInXellent.Count} customer(s) exist only in Settl — plan, template and products withheld"
                    + " (their metering points' Xellent contract belonged to the previous holder)");
        }

        // Current settlement-group membership is read only for comparison with the Xellent proposal.
        var (groupsByCustomer, groupNames) = selection is not null
            ? ((IReadOnlyDictionary<string,IReadOnlyList<string>>?)null,(IReadOnlyCollection<string>?)null)
            : await LoadSettlementGroupMembershipAsync(options, settlCustomers, cancellationToken);

        foreach (var entry in entries.Where(e => incompleteCustomers.Contains(e.CustomerNumber)))
        {
            historicalIssues.Add(new(entry.MeteringPointIdentification, SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete));
            unresolved.Add(new(entry.MeteringPointIdentification, SettlementConfigUnresolvedReason.HistoricalPortfolioIncomplete));
        }
        // Keep the legacy unresolved total a metering-point count. The historical issue list retains
        // every reason, while this row keeps the first specific reason for each metering point.
        if (historical is not null) unresolved = unresolved.DistinctBy(u => u.MeteringPointIdentification).ToList();
        ReportProgress("CalculatingProposals");
        var document = SettlementScheduleGrouper.Build(
            entries.Where(e => !incompleteCustomers.Contains(e.CustomerNumber)).ToList(),
            unresolved,
            options.ScheduleMap,
            cosByMpId.Keys.Concat(historicalIssues.Select(i => i.MeteringPointIdentification)).Distinct(StringComparer.Ordinal).Count(),
            options.Environment,
            options.DataAreaId,
            DateTime.UtcNow,
            conflictRules: options.Config?.ScheduleConflicts);

        _log($"  Identification: {identityFromSettl.Count} customer(s) from Settl, {identityFromXellent.Count} from Xellent");
        _log($"  Customer number from Settl for {meteringPointsFromSettl} of {entries.Count} metering point(s)"
            + (fallbackCustomers.Count > 0 ? $"; {fallbackCustomers.Count} customer(s) fell back to the Xellent representative" : ""));
        if (movedCustomers.Count > 0)
            _log($"    {movedCustomers.Count} customer(s) where the Xellent representative would have named someone else");

        _log("  Reading private customers' Xellent charging subscriptions at their COS-1 dates...");
        SettlementChargingHistoryReader.Result? chargingHistory = null;
        ReportProgress("ChargingHistory");
        var chargingSource = await SettlementChargingSource.ResolveAsync(entries, identityByCustomer,
            async (requests,ct) =>
            {
                chargingHistory = await ReadChargingHistoryAsync(requests,options.DataAreaId,ct);
                return chargingHistory.Products;
            }, cancellationToken, requireCompleteRead: true);
        entries = chargingSource.Entries.ToList();
        var chargeAnswers = chargingSource.Answers;
        var chargeFailures = chargingSource.FailedCustomers;
        if (chargeFailures.Count > 0) _log($"  WARNING: charging source read failed for {chargeFailures.Count} customers; their segments remain unresolved");
        ReportProgress("CalculatingProposals");
        var segments = CustomerSegmentGrouper.Build(entries.Where(e => !incompleteCustomers.Contains(e.CustomerNumber)).ToList(), options.SegmentMap, identityByCustomer,
            options.Config?.MultipleSegments, segmentByCustomerFromXellentCharge: chargeAnswers,
            chargeReadFailedCustomers: chargeFailures);

        var e18Application = SettlementE18Proposal.Apply(segments,
            entries.Where(e => !incompleteCustomers.Contains(e.CustomerNumber)).ToList(), identityByCustomer);
        segments = e18Application.Segments;
        document = document with { E18Review = e18Application.Review };
        var exemptionApplication = SettlementExemptionProposal.Apply(segments, entries, identityByCustomer,
            exemptionReads, options.ChangeOfSupplierDate, DateTime.UtcNow, currentExemptionCustomers, options.Environment,
            requestedDatesByMp: selection is not null ? cosByMpId : null);
        segments = exemptionApplication.Segments;
        document = document with { ExemptionReview = exemptionApplication.Review };

        var identityCounts = customers
            .GroupBy(c => identityByCustomer.TryGetValue(c, out var kind) ? kind : CustomerIdentityKind.Blank)
            .ToDictionary(g => g.Key, g => g.Count());

        // The metering points with no Xellent contract get their segment answer here, after the
        // segments exist: a customer already placed by its other metering points keeps that segment,
        // and unresolved source gaps remain visible without a Settl-derived fallback.
        var answeredUnresolved = UnresolvedSegmentResolver.Apply(
            unresolved,
            UnresolvedCustomers(unresolved, settlCustomerByMpId),
            segments.Segments
                .SelectMany(g => g.Customers.Select(c => (c.CustomerNumber, g.Segment)))
                .ToDictionary(x => x.CustomerNumber, x => x.Segment, StringComparer.Ordinal),
            rules: null);

        document = document with
        {
            Unresolved = answeredUnresolved,
            Totals = document.Totals with
            {
                Segments = segments.Segments.Count,
                SegmentsAssigned = segments.Segments.Sum(g => g.CustomerCount),
                SegmentConflicts = segments.Conflicts.Count,
                // Metering points, not rows: the list is one row per customer, and the number next to
                // "MPs without template" has to keep meaning metering points.
                SegmentUnresolved = segments.Unresolved.Sum(u => u.MeteringPointIdentifications.Count),
                SegmentsFromSettlRule = segments.SettlRuleCustomers.Count,
                CustomersWithCvr = identityCounts.GetValueOrDefault(CustomerIdentityKind.Cvr),
                CustomersWithCpr = identityCounts.GetValueOrDefault(CustomerIdentityKind.Cpr),
                CustomersWithoutIdentifier = identityCounts.GetValueOrDefault(CustomerIdentityKind.Blank),
                SegmentsFromMultiple = segments.MultipleSegmentCustomers.Count,
                UnresolvedWithSegment = answeredUnresolved.Count(u => u.Segment is not null),
                MeteringPointsWithSettlCustomer = meteringPointsFromSettl,
                CustomersFromXellentFallback = fallbackCustomers.Count,
                CustomersMovedBySettlLookup = movedCustomers.Count,
                UniqueCustomers = historical is null ? document.Totals.UniqueCustomers
                    : entries.Select(e => e.CustomerNumber).Concat(answeredUnresolved.Select(u => u.CustomerNumber).OfType<string>()).Distinct(StringComparer.Ordinal).Count(),
                CustomersWithoutSchedule = document.Totals.CustomersWithoutSchedule
                    + entries.Where(e => incompleteCustomers.Contains(e.CustomerNumber)).Select(e => e.CustomerNumber).Distinct(StringComparer.Ordinal).Count()
            },
            UnmappedContractTemplates = segments.UnmappedContractTemplates,
            Segments = segments.Segments,
            SegmentConflicts = segments.Conflicts,
            SegmentUnresolved = segments.Unresolved
        };

        // One row per customer with what Settl says. Not built at all when the caller does not want
        // them: they are a copy of what the comparison already carries per customer, and on the full
        // population they are around 7.000 rows a host may have no use for.
        var facts = options.IncludeCustomerFacts
            ? BuildCustomerFacts(entries, identityByCustomer, groupsByCustomer)
            : [];

        // Null rather than an empty list when they were not asked for: an empty array would read as
        // "there are no customers", which is a claim about the population and not about the request.
        document = document with
        {
            CustomerFacts = options.IncludeCustomerFacts ? facts : null,
            // Null when the run was not narrowed, and then left out of the JSON entirely — the file is
            // the one a full run always produced.
            ChangeOfSupplierFilter = cosFilter,
            HistoricalSelection = selection is null && options.ChangeOfSupplierDate is { } selectedDate
                ? new(selectedDate, "SettlDeliveryStatuses", "SettlPayersAtDate", historicalIssues.Distinct().OrderBy(i => i.MeteringPointIdentification, StringComparer.Ordinal).ToArray())
                : null
        };

        var currentSegments = selection is not null ? new Dictionary<string,SettlementConfigCurrentSegment>() : await LoadCurrentSegmentsAsync(settlCustomers,
            entries.Select(e => e.CustomerNumber).Concat(answeredUnresolved.Select(u => u.CustomerNumber).OfType<string>()).ToHashSet(StringComparer.Ordinal), cancellationToken);
        ReportProgress("PreparingJson");
        document = document with { Review = SettlementConfigReview.Build(document, entries, groupsByCustomer,
            options.Config?.GroupSchedules, currentSegments, contractNamesByMpId is not null,assignmentsSkipped:selection is not null) };
        var coveredIds = options.MeteringPointIds is null
            ? cosByMpId.Keys.Concat(document.Unresolved.Select(u => u.MeteringPointIdentification)).Distinct(StringComparer.Ordinal).ToList()
            : options.MeteringPointIds.Distinct(StringComparer.Ordinal).ToList();
        var coveredIdSet = coveredIds.ToHashSet(StringComparer.Ordinal);
        var scopedPortfolioCauses = portfolioCauses.ToDictionary(kv => kv.Key,
            kv => (IReadOnlyList<SettlementConfigHistoricalIssue>)kv.Value.Where(i => coveredIdSet.Contains(i.MeteringPointIdentification))
                .DistinctBy(i => i.MeteringPointIdentification).OrderBy(i => i.MeteringPointIdentification,StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        document = document with
        {
            MeteringPointSelection = selection,
            MeteringPointCoverage = SettlementConfigCoverage.Build(coveredIds,cosByMpId,document,entries,
                exemptionReads?.Where(r => r.ReadStatus != "Read").Select(r => r.MeteringPointIdentification).ToHashSet(StringComparer.Ordinal),customerReadProblems,
                options.MeteringPointIds is not null && options.WithExemptions
                    ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(document.ExemptionReview?.ReadAt ?? DateTime.UtcNow,DkTz)) : null,
                scopedPortfolioCauses),
            ContractPartEvidence = contractParts.Evidence.Where(e => coveredIdSet.Contains(e.MeteringPointIdentification)).ToList(),
            ChargingContractPartEvidence = chargingHistory?.Evidence
        };
        if (selection is not null)
            document = SettlementConfigCustomerResults.Apply(document,entries,cosByMpId,options,identityByCustomer,
                incompleteCustomers,chargeFailures,exemptionReads,currentExemptionCustomers);

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        ReportProgress("JsonWriting", 0, 1, "files");
        await File.WriteAllTextAsync(options.OutputPath, JsonSerializer.Serialize(document, JsonOptions), cancellationToken);
        ReportProgress("JsonWriting", 1, 1, "files");

        WriteSummary(document, scanned, options.OutputPath, _log);
        return 0;
    }

    /// <summary>
    /// Metering point identification → the customer number the export will put on its row. Same
    /// decision as the row loop makes: Settl's answer where Settl named a holder, the Xellent
    /// representative otherwise, and nothing at all when neither can. Metering points with no customer
    /// are simply absent from the result.
    ///
    /// <para>Only used to select the change-of-supplier cohort, which needs the customer before the
    /// rows exist. The rows themselves keep deciding this for themselves — this is not the source of
    /// the customer number on the output.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> CustomerNumberByMeteringPoint(
        IEnumerable<string> meteringPointIds,
        IReadOnlyDictionary<string, string> accountByMpId,
        IReadOnlyDictionary<string, SettlCustomer>? settlCustomerByMpId,
        IReadOnlyDictionary<string, string> representativeByAccount)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mpId in meteringPointIds)
        {
            string? customer = null;
            if (settlCustomerByMpId is not null && settlCustomerByMpId.TryGetValue(mpId, out var settlCustomer))
                customer = settlCustomer.Number;
            else if (accountByMpId.TryGetValue(mpId, out var account)
                && representativeByAccount.TryGetValue(account, out var representative))
                customer = representative;

            if (!string.IsNullOrWhiteSpace(customer)) result[mpId] = customer;
        }
        return result;
    }

    /// <summary>Customer number → name, from both sources, for the cohort listing only.</summary>
    private static IReadOnlyDictionary<string, string> CustomerNames(
        IReadOnlyDictionary<string, SettlCustomer>? settlCustomerByMpId,
        IReadOnlyDictionary<string, string> nameByAccount)
    {
        var names = new Dictionary<string, string>(nameByAccount, StringComparer.Ordinal);
        foreach (var customer in settlCustomerByMpId?.Values ?? [])
            if (!string.IsNullOrWhiteSpace(customer.Name)) names[customer.Number] = customer.Name!;
        return names;
    }

    /// <summary>
    /// Env-wide Settl enumeration over market-v3, mirroring reconcile's selection: parent MPs only
    /// (child MPs are Settl relations, not customers of their own) with <c>InDelivery=true</c>, each
    /// pinned to the start of its current/next delivery period as change-of-supplier date. The billing
    /// v1 list cannot be used here: it exposes neither parent nor delivery status, and carries no COS.
    /// </summary>
    private async Task<(Dictionary<string, DateOnly> CosByMpId, Dictionary<string, Guid> SettlIdByMpId, int Scanned)> LoadInDeliveryMeteringPointsAsync(
        ExportSettlementConfigRunOptions options, List<SettlementConfigUnresolved> selectionProblems, CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var cosByMpId = new Dictionary<string, DateOnly>(StringComparer.Ordinal);
        // The market-v3 GUID doubles as the billing-v1 id, so keeping it here is what makes reading
        // exemptions possible later without a second lookup per metering point.
        var settlIdByMpId = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var skip = 0;
        var total = 0;
        var seen = 0;
        var skippedChildren = 0;
        var skippedNotInDelivery = 0;
        var skippedNoDeliveryPeriod = 0;
        var selectedIds = options.MeteringPointIds?.ToHashSet(StringComparer.Ordinal);
        var foundIds = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new Dictionary<string,(Guid Id,Guid? Parent,bool? InDelivery,DateTimeOffset? Start)>(StringComparer.Ordinal);
        var conflictingIds = new HashSet<string>(StringComparer.Ordinal);
        var inventoryIds = new HashSet<Guid>();

        ReportProgress("MeteringPointInventory", 0, selectedIds?.Count, "meteringPoints");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (items, count, err) = await _meteringPointClient!.FindMeteringPointsAsync(pageSize, skip, doCount: skip == 0, cancellationToken);
            if (err != null)
            {
                _log($"  FAILED at skip {skip}: {err}");
                throw new InvalidOperationException($"Settl FindMeteringPoints failed: {err}");
            }
            if (skip == 0 && count > 0) total = count;
            if (items.Count == 0) break;
            if (selectedIds is not null && !items.Aggregate(false,(anyNew,mp) => inventoryIds.Add(mp.Id) || anyNew))
                throw new InvalidOperationException("Settl repeated a metering-point page without new records; the MP list cannot be completed safely.");

            foreach (var mp in items)
            {
                var id = mp.MeteringPointIdentification;
                if (selectedIds is not null && (string.IsNullOrEmpty(id) || !selectedIds.Contains(id))) continue;
                seen++;
                if (string.IsNullOrEmpty(id)) continue;
                foundIds.Add(id);
                if (selectedIds is not null)
                {
                    var snapshot = (mp.Id,mp.ParentMeteringPoint?.Id,mp.InDelivery,mp.CurrentOrNextDeliveryPeriod?.Start);
                    if (snapshots.TryGetValue(id,out var previous))
                    {
                        if (previous != snapshot && conflictingIds.Add(id))
                        {
                            cosByMpId.Remove(id);
                            settlIdByMpId.Remove(id);
                            selectionProblems.RemoveAll(p => p.MeteringPointIdentification == id);
                            selectionProblems.Add(new(id,SettlementConfigUnresolvedReason.AmbiguousSettlRecord));
                        }
                        continue;
                    }
                    snapshots[id] = snapshot;
                    settlIdByMpId[id] = mp.Id;
                }
                if (mp.ParentMeteringPoint?.Id is { } parentId && parentId != Guid.Empty)
                {
                    skippedChildren++;
                    if (selectedIds is not null)
                    {
                        settlIdByMpId.Remove(id);
                        selectionProblems.Add(new(id,SettlementConfigUnresolvedReason.ChildMeteringPoint));
                    }
                    continue;
                }
                // A strict list is the population. Historical first delivery supplies its dates;
                // today's flag/current period may describe a later occupant or an ended delivery.
                if (selectedIds is not null) continue;
                if (options.ChangeOfSupplierDate is { } selectedDate)
                {
                    // Enumeration only. Replace these placeholders with actual historical periods
                    // before any source reads or cohort selection; today's InDelivery is irrelevant.
                    cosByMpId[id] = selectedDate;
                    settlIdByMpId[id] = mp.Id;
                    if (options.Limit > 0 && cosByMpId.Count >= options.Limit) break;
                    continue;
                }
                if (mp.InDelivery != true)
                {
                    skippedNotInDelivery++;
                    if (selectedIds is not null) selectionProblems.Add(new(id,SettlementConfigUnresolvedReason.NotInDelivery));
                    continue;
                }
                if (mp.CurrentOrNextDeliveryPeriod is not { } period || period.Start == default)
                {
                    skippedNoDeliveryPeriod++;
                    if (selectedIds is not null) selectionProblems.Add(new(id,SettlementConfigUnresolvedReason.NoDeliveryPeriod));
                    continue;
                }

                cosByMpId[id] = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(period.Start.UtcDateTime, DkTz));
                settlIdByMpId[id] = mp.Id;
                if (options.Limit > 0 && cosByMpId.Count >= options.Limit) break;
            }

            // One line per page rather than an in-place \r update: the sink is line-based, and a
            // host collecting these into a log needs whole lines to show progress at all.
            _log(selectedIds is not null
                ? $"  Scanning selected Settl metering points... {seen} (parent points for first-delivery history: {settlIdByMpId.Count})"
                : $"  Scanning selected Settl metering points... {seen} ({(options.ChangeOfSupplierDate is null ? "in delivery" : "candidates for history")}: {cosByMpId.Count})");
            ReportProgress("MeteringPointInventory", selectedIds is null ? seen : foundIds.Count,
                selectedIds?.Count ?? (total > 0 ? total : null), "meteringPoints");

            if (options.Limit > 0 && cosByMpId.Count >= options.Limit) break;
            skip += items.Count;
            if (selectedIds is null && total > 0 && skip >= total) break;
        }

        _log(selectedIds is not null ? $"  Found {settlIdByMpId.Count} selected parent point(s) for historical delivery lookup"
            : $"  Scanned {seen} metering point(s) → {cosByMpId.Count} {(options.ChangeOfSupplierDate is null ? "in delivery" : "candidates for history")}");
        if (skippedChildren > 0) _log($"    skipped {skippedChildren} child-MP(s) (Settl relations)");
        if (skippedNotInDelivery > 0) _log($"    skipped {skippedNotInDelivery} MP(s) not in delivery");
        if (skippedNoDeliveryPeriod > 0) _log($"    skipped {skippedNoDeliveryPeriod} MP(s) with no resolvable COS");
        if (options.Limit > 0) _log($"    --limit {options.Limit} applied — this is a sample, not the full population");

        if (selectedIds is not null)
            selectionProblems.AddRange(selectedIds.Except(foundIds,StringComparer.Ordinal)
                .Select(id => new SettlementConfigUnresolved(id,SettlementConfigUnresolvedReason.NotFoundInSettl)));
        // Missing listed IDs are accounted for only after inventory enumeration finishes.
        ReportProgress("MeteringPointInventory", selectedIds?.Count ?? seen,
            selectedIds?.Count ?? (total > 0 ? total : options.Limit == 0 ? seen : null), "meteringPoints");
        return (cosByMpId, settlIdByMpId, seen);
    }

    /// <summary>
    /// One row per customer with what Settl says and nothing derived from it. Null stays null:
    /// a customer whose group membership was never read must not look like one who is in no group,
    /// and the same holds for exemptions.
    /// </summary>
    private static IReadOnlyList<SettlementConfigCustomerFacts> BuildCustomerFacts(
        IReadOnlyList<SettlementConfigEntry> entries,
        IReadOnlyDictionary<string, CustomerIdentityKind> identityByCustomer,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? groupsByCustomer) =>
        [.. entries
            .GroupBy(e => e.CustomerNumber, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new SettlementConfigCustomerFacts(
                g.Key,
                g.Select(e => e.CustomerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                identityByCustomer.TryGetValue(g.Key, out var identity) ? identity : CustomerIdentityKind.Blank,
                g.Count(),
                groupsByCustomer is null
                    ? null
                    : groupsByCustomer.TryGetValue(g.Key, out var groups) ? groups : [],
                // Counted only when the exemptions were actually read; every metering point then
                // carries true or false, never null.
                g.Any(e => e.HasExemption is not null) ? g.Count(e => e.HasExemption == true) : null,
                [.. g.Select(e => e.ContractTemplate).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. g.SelectMany(e => e.ProductNums ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))];

    /// <summary>
    /// The Settl customer behind each metering point that produced no schedule row, projected out of
    /// the map the whole run already uses. These metering points have no Xellent contract and
    /// therefore no customer from the Xellent side, but Settl knows who holds them like it knows for
    /// every other metering point — no separate lookup needed.
    /// </summary>
    private static IReadOnlyDictionary<string, UnresolvedSegmentResolver.UnresolvedCustomer>? UnresolvedCustomers(
        IReadOnlyList<SettlementConfigUnresolved> unresolved,
        IReadOnlyDictionary<string, SettlCustomer>? customerByMpId)
    {
        if (unresolved.Count == 0 || customerByMpId is null) return null;

        var result = new Dictionary<string, UnresolvedSegmentResolver.UnresolvedCustomer>(StringComparer.Ordinal);
        foreach (var row in unresolved)
        {
            if (customerByMpId.TryGetValue(row.MeteringPointIdentification, out var customer))
                result[row.MeteringPointIdentification] =
                    new UnresolvedSegmentResolver.UnresolvedCustomer(customer.Number, customer.Name, customer.Identity);
        }
        return result;
    }

    /// <summary>
    /// Every Settl customer by its id, so a customer id coming off a payer can be named without a
    /// call of its own. One paginated pass — around eight calls for the whole tenant, against one
    /// call per customer the other way round.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, SettlCustomer>?> LoadSettlCustomerDirectoryAsync(
        CancellationToken cancellationToken, bool historical = false)
    {
        ReportProgress("CustomerDirectory", 0, null, "customers");
        if (_customerClient is null)
        {
            _log(historical ? "  Historical customers remain unresolved — no customer client supplied"
                : "  Customer number will come from Xellent — no customer client supplied");
            return null;
        }

        var byId = new Dictionary<Guid, SettlCustomer>();
        var skip = 0;
        int? knownTotal = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (items, total, error) = await _customerClient.FindCustomersAsync(1000, skip, cancellationToken);
            if (error != null)
            {
                // A partial directory would silently push customers onto the Xellent fallback, which
                // is the very thing this lookup exists to avoid. Better no directory than half of one.
                _log($"  WARNING: could not page Settl customers at skip {skip} ({error}) — "
                    + (historical ? "historical customers remain unresolved" : "customer numbers fall back to Xellent"));
                return null;
            }
            if (items.Count == 0) break;
            if (total > 0) knownTotal = total;

            foreach (var c in items.Where(c => !string.IsNullOrWhiteSpace(c.CustomerNumber)))
            {
                byId[c.Id] = new SettlCustomer(
                    c.CustomerNumber!,
                    c.FullName,
                    // A CVR makes it a company even when a CPR is also on file — same precedence as
                    // CustomerIdentityResolver applies on the Xellent side.
                    !string.IsNullOrWhiteSpace(c.VatIdentificationNumber) ? CustomerIdentityKind.Cvr
                    : !string.IsNullOrWhiteSpace(c.NationalIdentificationNumber) ? CustomerIdentityKind.Cpr
                    : CustomerIdentityKind.Blank);
            }

            skip += items.Count;
            ReportProgress("CustomerDirectory", skip, knownTotal, "customers");
            if (total > 0 && skip >= total) break;
        }

        ReportProgress("CustomerDirectory", skip, knownTotal ?? skip, "customers");
        _log($"  Read {byId.Count} Settl customer(s)");
        return byId;
    }

    /// <summary>
    /// Metering point identification → the Settl customer holding it, via the payers. That is the
    /// only direction Settl offers: neither metering-point API carries a customer link.
    ///
    /// <para>Batched, so the whole population costs around thirty calls rather than one per metering
    /// point. A batch that fails leaves its metering points out of the map — they fall back to the
    /// Xellent representative and are counted as such, rather than quietly losing their customer.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, SettlCustomer>?> LoadCustomerByMeteringPointAsync(
        IReadOnlyDictionary<string, Guid> settlIdByMpId,
        IReadOnlyDictionary<Guid, SettlCustomer>? customersById,
        CancellationToken cancellationToken, Dictionary<string,string> customerReadProblems)
    {
        ReportProgress("CurrentPayers", 0, settlIdByMpId.Count, "meteringPoints");
        if (customersById is null)
        {
            foreach (var id in settlIdByMpId.Keys) customerReadProblems[id] = "Could not read the Settl customer directory";
            return null;
        }
        if (_payerClient is null)
        {
            _log("  Customer number will come from Xellent — no payer client supplied");
            foreach (var id in settlIdByMpId.Keys) customerReadProblems[id] = "Could not read the Settl customer linked to this metering point";
            return null;
        }

        var mpIdByGuid = new Dictionary<Guid, string>();
        foreach (var (mpId, guid) in settlIdByMpId) mpIdByGuid[guid] = mpId;
        if (mpIdByGuid.Count == 0) return null;

        _log($"  Asking Settl who holds {mpIdByGuid.Count} metering point(s)...");
        var result = new Dictionary<string, SettlCustomer>(StringComparer.Ordinal);
        // Which metering points already have their answer from a primary customer payer. A
        // non-primary payer must not overwrite that, and it is the primary that decides the customer.
        var fromPrimary = new HashSet<string>(StringComparer.Ordinal);
        var guids = mpIdByGuid.Keys.ToList();
        var failedBatches = 0;
        var unnamed = 0;

        for (var offset = 0; offset < guids.Count; offset += PayerLookupBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = guids.GetRange(offset, Math.Min(PayerLookupBatchSize, guids.Count - offset));

            var (payers, error) = await _payerClient.FindPayersForMeteringPointsAsync(batch, null, cancellationToken);
            ReportProgress("CurrentPayers", Math.Min(offset + batch.Count, guids.Count), guids.Count, "meteringPoints");
            if (error != null || payers is null)
            {
                failedBatches++;
                foreach (var guid in batch) customerReadProblems[mpIdByGuid[guid]] = "Could not read the Settl customer linked to this metering point";
                continue;
            }

            foreach (var entry in payers)
            {
                if (entry.Payer?.CustomerId is not { } customerId || customerId == Guid.Empty) continue;
                var isPrimary = entry.Payer.IsPrimaryCustomerPayer;
                if (!customersById.TryGetValue(customerId, out var customer)) { unnamed++; continue; }

                foreach (var mp in entry.MeteringPoints ?? [])
                {
                    if (!mpIdByGuid.TryGetValue(mp.MeteringPointId, out var mpId)) continue;
                    if (result.ContainsKey(mpId) && (fromPrimary.Contains(mpId) || !isPrimary)) continue;
                    result[mpId] = customer;
                    if (isPrimary) fromPrimary.Add(mpId);
                }
            }

            _log($"    customer lookup: {Math.Min(offset + batch.Count, guids.Count)}/{guids.Count}");
        }

        _log($"  Settl named the customer for {result.Count} of {mpIdByGuid.Count} metering point(s)");
        if (failedBatches > 0)
            _log($"    WARNING: {failedBatches} payer batch(es) failed — those metering points fall back to Xellent");
        if (unnamed > 0)
            _log($"    WARNING: {unnamed} payer(s) point at a customer that is not in the customer listing");

        return result;
    }

    protected virtual Task<SettlementChargingHistoryReader.Result> ReadChargingHistoryAsync(
        IReadOnlyCollection<(string Account,DateOnly Date)> requests,string dataAreaId,CancellationToken ct) =>
        SettlementChargingHistoryReader.ReadAsync(_connectionString,requests,dataAreaId,ct);

    protected virtual Task<SettlementContractPartResolver.Result> ResolveContractPartsAsync(
        IReadOnlyDictionary<string,DateOnly> dates,string dataAreaId,CancellationToken ct) =>
        SettlementContractPartResolver.ResolveAsync(_connectionString,dates,dataAreaId,ct);

    protected virtual Task<IReadOnlyDictionary<string,CustomerIdentityKind>> ResolveCustomerIdentitiesAsync(
        IReadOnlyCollection<string> accounts,string dataAreaId,CancellationToken ct) =>
        CustomerIdentityResolver.ResolveAsync(_connectionString,accounts,dataAreaId,ct);

    private static IReadOnlyList<string>? Lookup(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? map, string key) =>
        map is not null && map.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Metering point identification → the Settl contracts it is attached to, built by fanning out
    /// over contracts rather than asking per metering point. There are dozens of contracts and
    /// thousands of metering points, so the inverted direction is three orders of magnitude cheaper
    /// and gives the same map.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> LoadContractNamesAsync(
        CancellationToken cancellationToken)
    {
        if (_contractClient is null) return null;

        // Explicit CommodityType: the endpoint happens to return only electricity when the filter is
        // omitted, but that is undocumented behaviour to rely on rather than state.
        var (contracts, error) = await _contractClient.FindContractsAsync("Electricity", cancellationToken);
        if (error != null || contracts is null)
        {
            _log($"  WARNING: could not list Settl contracts ({error}) — SME and charger segment detection unavailable");
            return null;
        }

        var byMpId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var failed = 0;
        foreach (var contract in contracts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (mps, mpError) = await _contractClient.GetContractMeteringPointsAsync(contract.Id, cancellationToken);
            if (mpError != null || mps is null) { failed++; continue; }

            foreach (var mp in mps)
            {
                if (string.IsNullOrEmpty(mp.MeteringPointIdentification)) continue;
                if (!byMpId.TryGetValue(mp.MeteringPointIdentification, out var names))
                    byMpId[mp.MeteringPointIdentification] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(contract.ContractName)) names.Add(contract.ContractName);
            }
        }

        _log($"  Read metering-point links for {contracts.Count - failed} of {contracts.Count} Settl contract(s)");
        // A partial map would make customers look like they are on no SME contract when the call for
        // that very contract failed — say so rather than let the silence read as evidence.
        if (failed > 0) _log($"    WARNING: {failed} contract(s) failed — SME and charger segment detection are incomplete");

        if (failed > 0) return null;
        return byMpId.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Metering point identification → whether it carries a billing exemption in Settl. Opt-in: this
    /// is one call per metering point, unlike every other source here. The market-v3 GUID is the same
    /// id the billing API uses, so no extra lookup is needed.
    /// </summary>
    private async Task<Dictionary<string,Guid>> LoadSelectedExemptionPointsAsync(IEnumerable<string> ids,
        List<SettlementConfigExemptionRead> failures,CancellationToken ct)
    {
        var selected = ids.ToArray();
        var result = new Dictionary<string,Guid>(StringComparer.Ordinal);
        var sync = new object();
        ReportProgress("ExemptionMeteringPoints",0,selected.Length,"meteringPoints");
        var done = 0;
        await Parallel.ForEachAsync(selected,new ParallelOptions { MaxDegreeOfParallelism = 16,CancellationToken = ct },async (id,token) =>
        {
            Guid? found = null;
            try
            {
                var (point,error) = _meteringPointClient is null ? (null,"Metering-point client unavailable")
                    : await _meteringPointClient.SearchByIdentificationAsync(id,token);
                if (error is null && point is not null && point.Id != Guid.Empty
                    && point.MeteringPointIdentification == id) found = point.Id;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
            lock (sync)
            {
                if (found is { } guid) result[id] = guid;
                else failures.Add(new(id,null,"ReadFailed",[],"NotVerified",null));
                ReportProgress("ExemptionMeteringPoints",++done,selected.Length,"meteringPoints");
            }
        });
        return result;
    }

    private async Task<IReadOnlyDictionary<string,SettlementCurrentCustomerEvidence>> LoadSelectedExemptionCustomersAsync(
        IReadOnlyDictionary<string,Guid> points,CancellationToken ct)
    {
        var numbers = new Dictionary<Guid,string>();
        var failedCustomers = new HashSet<Guid>();
        var failedPoints = new HashSet<Guid>();
        var now = DateTime.UtcNow;
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now,DkTz));
        var evidence = await SettlementCurrentCustomerSource.ReadAsync(points,numbers,async (ids,at,token) =>
        {
            if (_payerClient is null || _customerClient is null) return (null,"Exemption ownership clients unavailable");
            var (payers,error) = await _payerClient.FindPayersForMeteringPointsAsync(ids,at,token);
            if (payers is null || error is not null) return (payers,error);
            var active = payers.Where(p => (p.MeteringPoints ?? []).Any(m => ids.Contains(m.MeteringPointId)
                && m.Start <= day && day < m.End)).ToArray();
            var unknown = active.Select(p => p.Payer?.CustomerId).OfType<Guid>().Where(id => id != Guid.Empty)
                .Distinct().Where(id => !numbers.ContainsKey(id) && !failedCustomers.Contains(id)).ToArray();
            var sync = new object();
            await Parallel.ForEachAsync(unknown,new ParallelOptions { MaxDegreeOfParallelism = 16,CancellationToken = token },async (id,readToken) =>
            {
                string? number = null;
                try
                {
                    var (customer,readError) = await _customerClient.GetByIdAsync(id,readToken);
                    if (readError is null && customer?.Id == id && !string.IsNullOrWhiteSpace(customer.CustomerNumber))
                        number = customer.CustomerNumber;
                }
                catch (OperationCanceledException) when (readToken.IsCancellationRequested) { throw; }
                catch (Exception) { }
                lock (sync)
                {
                    if (number is not null) numbers[id] = number;
                    else failedCustomers.Add(id);
                }
            });
            foreach (var payer in active.Where(p => p.Payer?.CustomerId is { } id && failedCustomers.Contains(id)))
                foreach (var point in (payer.MeteringPoints ?? []).Where(m => ids.Contains(m.MeteringPointId)
                    && m.Start <= day && day < m.End)) failedPoints.Add(point.MeteringPointId);
            return (payers,error);
        },now,ct,p => ReportProgress(p with { Stage = "ExemptionCustomers" }));
        return evidence.ToDictionary(kv => kv.Key,kv => failedPoints.Contains(points[kv.Key])
            ? new SettlementCurrentCustomerEvidence(null,"ReadFailed") : kv.Value,StringComparer.Ordinal);
    }

    private async Task<IReadOnlyList<SettlementConfigExemptionRead>?> LoadExemptionsAsync(
        ExportSettlementConfigRunOptions options,
        IReadOnlyDictionary<string, Guid> settlIdByMpId,
        CancellationToken cancellationToken)
    {
        if (!options.WithExemptions) return null;

        ReportProgress("Exemptions", 0, settlIdByMpId.Count, "meteringPoints");
        _log($"  Reading billing exemptions for {settlIdByMpId.Count} metering point(s) — one call each...");
        var result = new Dictionary<string, SettlementConfigExemptionRead>(StringComparer.Ordinal);
        var failed = 0;
        var done = 0;
        var sync = new object();

        // Parallel.ForEachAsync rather than a task per metering point behind a semaphore: it caps
        // concurrency without materialising ~11.7k tasks up front, and owns its own lifetime.
        await Parallel.ForEachAsync(
            settlIdByMpId,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken },
            async (kv, ct) =>
            {
                SettlementConfigExemptionRead read;
                try
                {
                    var (billing, error) = _meteringPointClient is null ? (null,"Metering-point client unavailable")
                        : await _meteringPointClient.GetBillingByIdAsync(kv.Value, null, ct);
                    read = new(kv.Key, null, error is null && billing is not null ? "Read" : "ReadFailed",
                        billing?.Exemptions?.Select(e => new SettlementConfigCurrentExemption(e.Id, e.ExemptionType, e.ProductElementId, e.ExemptionPercentage)).ToArray() ?? [],
                        options.ChangeOfSupplierDate is null ? "Current" : "NotVerified", null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { read = new(kv.Key, null, "ReadFailed", [], options.ChangeOfSupplierDate is null ? "Current" : "NotVerified", null); }
                lock (sync)
                {
                    // A failed read leaves the MP out of the map entirely, so it stays "not read"
                    // rather than becoming a false "no exemption".
                    if (read.ReadStatus == "ReadFailed") failed++;
                    result[kv.Key] = read;
                    if (++done % 1000 == 0) _log($"    exemptions: {done}/{settlIdByMpId.Count}");
                    ReportProgress("Exemptions", done, settlIdByMpId.Count, "meteringPoints");
                }
            });

        _log($"  Read exemptions for {result.Count} metering point(s); {result.Values.Count(v => v.CurrentExemptions.Count > 0)} carry one");
        if (failed > 0) _log($"    WARNING: {failed} metering point(s) could not be read — reported as unknown");
        return result.Values.OrderBy(r => r.MeteringPointIdentification, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Customer number → the Settlement Groups the customer is really in, read live from Settl.
    /// Deliberately not the profile's <c>Mappings.SettlementGroup</c> nor the computed expectation:
    /// the comparison is about what Settl holds, not what the migration intended.
    ///
    /// <para><b>Only the customer list.</b> A Settl group also holds metering points, in a separate
    /// list, but membership is read at customer level and nothing else: a settlement schedule belongs
    /// to the customer, and letting a single metering point's group membership speak for her would
    /// hand the schedule to whichever of her metering points happened to be enrolled somewhere.</para>
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>>? ByCustomer, IReadOnlyCollection<string>? GroupNames)>
        LoadSettlementGroupMembershipAsync(
            ExportSettlementConfigRunOptions options,
            IReadOnlyDictionary<Guid, SettlCustomer>? customersById,
            CancellationToken cancellationToken)
    {
        ReportProgress("SettlementGroups", 0, null, "groups");
        if (_settlementGroupClient is null || customersById is null)
        {
            _log("  Settlement-group membership skipped — no settlement-group client or no customer listing");
            return (null, null);
        }

        var (groups, groupsError) = await _settlementGroupClient.ListAllAsync(options.CountryCode, cancellationToken);
        if (groupsError != null || groups is null)
        {
            _log($"  WARNING: could not list Settl settlement groups ({groupsError})");
            return (null, null);
        }

        var active = groups.Where(g => g.IsActive && !string.IsNullOrWhiteSpace(g.GroupName)).ToList();
        ReportProgress("SettlementGroups", 0, active.Count, "groups");
        _log($"  Reading membership of {active.Count} active settlement group(s)...");

        // Membership arrives as customer GUIDs; the customer listing the run already read turns them
        // into customer numbers. The per-customer search would be one call each — thousands instead.
        var byCustomer = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var failed = 0;
        var done = 0;

        foreach (var group in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (customerIds, error) = await _settlementGroupClient.ListCustomerIdsAsync(group.Id, cancellationToken);
            ReportProgress("SettlementGroups", ++done, active.Count, "groups");
            if (error != null || customerIds is null) { failed++; continue; }

            foreach (var id in customerIds)
            {
                // A member whose GUID is not in the customer listing cannot be named; skipping it is
                // safe here because the customer would not be in the export either.
                if (!customersById.TryGetValue(id, out var member)) continue;
                if (!byCustomer.TryGetValue(member.Number, out var names))
                    byCustomer[member.Number] = names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                names.Add(group.GroupName!);
            }
        }
        if (failed > 0) _log($"    WARNING: membership of {failed} group(s) could not be read");
        _log($"  {byCustomer.Count} customer(s) belong to at least one settlement group");

        if (failed > 0) return (null, active.Select(g => g.GroupName!).ToList());
        return (
            byCustomer.ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal),
            active.Select(g => g.GroupName!).ToList());
    }

    private async Task<IReadOnlyDictionary<string, SettlementConfigCurrentSegment>> LoadCurrentSegmentsAsync(
        IReadOnlyDictionary<Guid, SettlCustomer>? directory, IReadOnlySet<string> customers, CancellationToken ct)
    {
        ReportProgress("SegmentCatalog");
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, SettlementConfigCurrentSegment>(StringComparer.Ordinal);
        if (_customerClient is null || directory is null) return result;
        IReadOnlyDictionary<Guid,string?>? names = null;
        try
        {
            var (catalog, error) = await _customerClient.FindCustomerSegmentsAsync(ct);
            if (error is null) names = catalog?.ToDictionary(s => s.Id, s => s.Name);
            else _log("  WARNING: current Settl segment catalog could not be read; segment IDs remain in review");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _log("  WARNING: current Settl segment catalog read failed; proposals are unaffected"); }
        _log($"  Reading current Settl segment assignments for {customers.Count} customer(s)...");
        var selected = directory.Where(kv => customers.Contains(kv.Value.Number)).ToArray();
        var done = 0;
        var progressSync = new object();
        ReportProgress("CurrentSegments", 0, selected.Length, "customers");
        await Parallel.ForEachAsync(selected,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (kv, token) =>
            {
                try
                {
                    var (json, readError) = await _customerClient.GetRawByIdV3Async(kv.Key, token);
                    result[kv.Value.Number] = readError is not null || json is null
                        ? new(null, "ReadFailed") : SettlementConfigReview.ReadCurrentSegment(json, names);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { result[kv.Value.Number] = new(null, "ReadFailed"); }
                lock (progressSync) ReportProgress("CurrentSegments", ++done, selected.Length, "customers");
            });
        return result;
    }

    private static void WriteSummary(SettlementConfigDocument document, int scanned, string outputPath, Action<string> log)
    {
        var t = document.Totals;
        log("");
        log("=== Summary ===");
        log($"  MPs scanned:       {scanned}");
        // Said before every other number, because with a filter on, every one of them describes the
        // cohort and not the environment.
        if (document.ChangeOfSupplierFilter is { } f)
        {
            log($"  Change-of-supplier filter: {f.Date:yyyy-MM-dd} — this export covers that cohort only");
            log($"    customers kept:  {f.Customers}");
            log($"    MPs on the date: {f.MeteringPointsOnDate}");
            log($"    MPs along for a shared customer: {f.MeteringPointsFromSharedCustomer}");
            if (f.MeteringPointsOnDateWithoutCustomer.Count > 0)
                log($"    MPs on the date with no customer: {f.MeteringPointsOnDateWithoutCustomer.Count}");
        }
        log($"  MPs in delivery:   {t.MeteringPointsInSettl}");
        log($"  MPs resolved:      {t.MeteringPointsResolved}");
        log($"  Customers:         {t.UniqueCustomers}");
        // Said next to the customer count, because it is what makes that count trustworthy: a
        // customer number read from Settl is a fact, one derived from Xellent is an inference.
        log($"  ...kundenummer fra Settl: {t.MeteringPointsWithSettlCustomer} MP(er)"
            + (t.CustomersFromXellentFallback > 0 ? $", {t.CustomersFromXellentFallback} kunde(r) fra Xellent-repræsentant" : ""));
        if (t.CustomersMovedBySettlLookup > 0)
            log($"  ...heraf hvor Xellent ville have sagt et andet kundenummer: {t.CustomersMovedBySettlLookup}");
        // Said next to the customer count too: for these customers the whole Xellent half of the
        // export is deliberately blank, and a reader who does not know that will read it as a gap.
        if (t.CustomersNotInXellent > 0)
            log($"  ...uden Xellent-konto: {t.CustomersNotInXellent} kunde(r) — skema/segment/produkter kommer ikke fra Xellent for dem");
        log($"  Schedules:         {t.Schedules}");
        if (t.SchedulesFromSettlementGroup > 0)
            log($"  ...heraf afgjort af settlement group: {t.SchedulesFromSettlementGroup} kunde(r) med MP'er på flere schedules");
        if (t.ScheduleConflicts > 0)
            log($"  Schedule-konflikter: {t.ScheduleConflicts} kunde(r) hvor gruppen heller ikke svarer");
        // Not a conflict and not a group member: nothing named a schedule for them at all. The only
        // number in the summary that says "these customers are in no schedule group above".
        if (t.CustomersWithoutSchedule > 0)
        {
            log($"  Uden skema:        {t.CustomersWithoutSchedule} kunde(r) uden Xellent-konto og uden settlement group");
            // Navngivet, ikke kun talt: det er en beslutning der venter på et menneske, og et tal
            // alene tvinger hende til at regne sig frem til hvem det handler om.
            foreach (var c in (document.CustomersWithoutSchedule ?? []).Take(10))
                log($"    {c.CustomerNumber} {c.Name} ({c.MeteringPointCount} MP) — {c.Reason}");
            if (t.CustomersWithoutSchedule > 10)
                log($"    ... og {t.CustomersWithoutSchedule - 10} flere (se JSON)");
        }
        log($"  Unresolved:        {t.Unresolved}");
        // An unresolved MP that already has a segment answer is not a decision waiting on anyone, so
        // the two are counted apart rather than left as one number that overstates the worklist.
        if (t.UnresolvedWithSegment > 0)
            log($"  ...heraf med segment: {t.UnresolvedWithSegment}");
        log("");

        foreach (var group in document.Schedules)
            log($"  {group.Schedule,-24} {group.CustomerCount,6} customer(s)  {group.MeteringPointCount,6} MP(s){(group.Mapped ? "" : "  [unmapped]")}");

        // The customers no schedule group contains, and why. Printed as the pairs they are torn
        // between, because that is what a decision has to be made about — one line per customer
        // would be 94 lines saying the same four things.
        if (document.ScheduleConflicts is { Count: > 0 } scheduleConflicts)
        {
            log("");
            log($"  Kunder med målepunkter på flere schedules ({scheduleConflicts.Count}) — ikke talt med ovenfor:");
            foreach (var byPair in scheduleConflicts
                .GroupBy(c => string.Join(" + ", c.Candidates.Select(x => x.Schedule).Order(StringComparer.Ordinal)))
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal))
            {
                log($"    {byPair.Count(),6}  {byPair.Key}");
            }
        }

        if (document.UnmappedBillingPlanNums.Count > 0)
        {
            log("");
            log($"  Billing plans without an entry in {SettlementConfigLoader.FileName} ({document.UnmappedBillingPlanNums.Count}):");
            foreach (var plan in document.UnmappedBillingPlanNums)
                log($"    {plan}");
        }

        log("");
        log("=== Customer segments ===");
        log($"  Customers in a segment: {t.SegmentsAssigned}");
        log($"  Segments:               {t.Segments}");
        log($"  Conflicts:              {t.SegmentConflicts}");
        // Placed by the file's standing answer, not by their own metering points agreeing — so it is
        // shown next to the conflicts it replaced rather than disappearing into the segment counts.
        if (t.SegmentsFromMultiple > 0)
            log($"  ...heraf uenige MPs:    {t.SegmentsFromMultiple} (multipleSegments)");
        // Their segment rests on what Settl holds today rather than on a contract, so it is said out
        // loud instead of hiding inside the segment counts.
        if (t.SegmentsFromSettlRule > 0)
            log($"  ...heraf uden Xellent-konto: {t.SegmentsFromSettlRule} (Settl-drevet regel)");
        log($"  MPs without template:   {t.SegmentUnresolved}");
        // The identification split decides which column of the segment map was used, so it belongs
        // next to the segment counts rather than in the JSON alone.
        log($"  Identification:         CVR {t.CustomersWithCvr}, CPR {t.CustomersWithCpr}, blank {t.CustomersWithoutIdentifier}");
        log("");

        foreach (var group in document.Segments ?? [])
            log($"  {group.Segment,-24} {group.CustomerCount,6} customer(s)  {group.MeteringPointCount,6} MP(s){(group.Mapped ? "" : "  [unmapped]")}");

        // Conflicts are the manual worklist, so the reason breakdown belongs in the summary — the
        // per-customer detail (incl. MP ids) is in the JSON.
        if (document.SegmentConflicts is { Count: > 0 } conflicts)
        {
            log("");
            foreach (var byReason in conflicts.GroupBy(c => c.Reason).OrderBy(g => g.Key))
                log($"  conflict: {byReason.Key,-20} {byReason.Count(),6} customer(s)");
        }

        if (document.UnmappedContractTemplates is { Count: > 0 } unmappedTemplates)
        {
            log("");
            log($"  Contract templates without an entry in {SettlementConfigLoader.FileName} ({unmappedTemplates.Count}):");
            foreach (var template in unmappedTemplates)
                log($"    {template}");
        }

        WriteComparisonSummary(document, log);

        log("");
        log($"Output written to {outputPath}");
    }

    /// <summary>
    /// The candidate-rule comparison, which is the point of the run: how often the competing sources
    /// agree. Printed as counts rather than a verdict — the per-customer detail is in the JSON, and
    /// which rule wins is not this export's call.
    /// </summary>
    private static void WriteComparisonSummary(SettlementConfigDocument document, Action<string> log)
    {
        if (document.Comparison is not { } c) return;

        // Said first, and loudly: every number below is conditional on these two having been read.
        // A zero that means "unknown" and a zero that means "none" look identical otherwise.
        if (!c.SettlementGroupMembershipRead || !c.SettlContractsRead)
        {
            log("");
            log("  *** INCOMPLETE — the comparison below is missing an input ***");
            if (!c.SettlementGroupMembershipRead)
                log("      settlement-group membership was NOT read: no customer has a group-derived schedule");
            if (!c.SettlContractsRead)
                log("      Settl contracts were NOT read: the SME test never ran, so no Settl-driven segment");
        }

        log("");
        log("=== Schedule sources (Settl group vs. Xellent billing plan) ===");
        log($"  agree:      {c.ScheduleAgree,6}");
        log($"  disagree:   {c.ScheduleDisagree,6}");
        // Spelled out because "undecided" dominating the counts is a finding about the rules, not a
        // defect in the run: it means only one source spoke for those customers.
        log($"  undecided:  {c.ScheduleUndecided,6}  (fewer than two sources produced a value)");
        log($"  in a settlement group in Settl: {(c.SettlementGroupMembershipRead ? c.CustomersInSettlementGroup.ToString() : "not read")}");

        log("");
        log("=== Segment sources (template vs. Settl-driven vs. template+product) ===");
        log($"  agree:      {c.SegmentAgree,6}");
        log($"  disagree:   {c.SegmentDisagree,6}");
        log($"  undecided:  {c.SegmentUndecided,6}");
        log($"  with billing exemption in Settl: {c.CustomersWithExemption}");

        if (c.ScheduleNamesNotInSettl is null)
        {
            log("");
            log("  groupSchedules entries vs. Settl: not checked (group listing unavailable)");
        }
        else if (c.ScheduleNamesNotInSettl.Count > 0)
        {
            log("");
            log($"  groupSchedules entries matching no active Settl group ({c.ScheduleNamesNotInSettl.Count}):");
            foreach (var name in c.ScheduleNamesNotInSettl) log($"    {name}");
        }

        if (c.SettlementGroupsWithoutSchedule.Count > 0)
        {
            log("");
            log($"  Settl groups with members but no groupSchedules entry ({c.SettlementGroupsWithoutSchedule.Count}):");
            foreach (var name in c.SettlementGroupsWithoutSchedule) log($"    {name}");
        }
    }
}
