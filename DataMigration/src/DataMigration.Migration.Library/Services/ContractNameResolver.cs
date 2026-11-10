using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.Migration.Services;

/// <summary>
/// Resolves which Settl contract each MP belongs to, based on the mapping dictionaries in
/// <see cref="CreateOptions"/> (GroupNumContractMap, AccountNumContractMap, ProductNumContractMap).
///
/// <para><b>Resolution priority per MP — most specific wins, first hit stops the search:</b></para>
/// <list type="number">
/// <item><b>Explicit account → contract</b> (GroupNumContractMap or AccountNumContractMap). Somebody
/// named this account by hand; nothing outranks that.</item>
/// <item><b>Electricity product + linked charger's product</b>
/// (<see cref="CreateOptions.ElectricityChargerContractMap"/>). Decided per metering point, not per
/// account: fires for every MP an EV charger is linked to unambiguously (see <c>EvCharger</c> in
/// <c>NewSettlement.XellentDomain</c> — with several candidate MPs, the postal-code tie-break in
/// <c>BuildEvChargerLookups</c> links each installation to the one MP whose delpoint postal code
/// matches its own, and leaves what it cannot settle on the account), each with its own
/// charger(s); and for every MP of an account that has no charger data at all (the
/// <c>__NoCharge__</c> sentinel). An MP without a charger on an account whose charger sits on
/// ANOTHER of its MPs skips this rule entirely — no sentinel — and takes the template/product
/// rules below like any ordinary MP; that is what lets a charger customer with several El-ekstern
/// MPs migrate (business decisions 2026-09-01, confirmed on accounts 105212 and 105909). Narrower
/// than the template rule below, so it wins first when it applies; an ambiguous charger, or no
/// configured entry for the pair, falls through to the next rule. A charger with two or more
/// Charge_Abo products live at once (a stale, unclosed old row in Xellent) is not guessed at and
/// not refused: every live product that has a configured entry becomes its own contract link on
/// the MP, with Xellent's own overlapping periods, and a warning names what was sent (business
/// decision 2026-09-01). An MP with two or more charger installations linked to it resolves to the
/// one multi-installation contract (<c>__MultipleInstallations__</c>) when every live Charge_Abo
/// product is owned — wherever the installations are registered (user decisions 2026-09-01 on
/// accounts 491704, 109909, 471352 and 498288); otherwise that MP alone stays paused, counted and
/// logged with the test that failed.</item>
/// <item><b>Contract template + product (+ customer identification)</b>
/// (<see cref="CreateOptions.TemplateProductContractMap"/>). The only rule that can say one product
/// maps two ways: <c>V Variabel</c> on template <c>V_Privat</c> belongs on <c>V ErhVari</c> for a
/// company and on the contract of its own name for a private customer. An identification the table
/// says nothing about falls through to the next rule rather than matching — that is what leaves the
/// private customers where they already are.</item>
/// <item><b>Product (+ payment term)</b> (<see cref="CreateOptions.ProductNumContractMap"/>).</item>
/// <item><b>Fallback: the MP's ProductNum itself</b> (emits an info line).</item>
/// </list>
///
/// <para>Template and product keys match case-insensitively, as payment-term keys already do. The
/// last incident here was case alone — every live contract part said <c>Lbm+1d</c> while the config
/// said <c>lbm+1d</c> — and whether an account keeps its contract must not depend on how somebody
/// typed a JSON key.</para>
///
/// <para>Falling back to the product name is still allowed — it is right far more often than it
/// is wrong. What it may NOT do is pass silently when the name turns out not to exist in Settl:
/// the resolved name is then reported through <see cref="Result.AccountsMissingContracts"/> so the
/// run can refuse those accounts before it creates anything for them.</para>
/// </summary>
public class ContractNameResolver
{
    private readonly ISettlContractClient _contractClient;
    private readonly IWarningCollector _warnings;
    private readonly Action<string> _logLine;

    public ContractNameResolver(ISettlContractClient contractClient, IWarningCollector warnings, Action<string> logLine)
    {
        _contractClient = contractClient;
        _warnings = warnings;
        _logLine = logLine;
    }

    /// <summary>
    /// One resolved Settl-contract link for a given (Xellent) MeteringPoint × ContractPart row.
    /// Multiple links may exist for the same MeteringPointId when an MP has parallel ContractParts
    /// (e.g. regular + climate-friendly product) — each ContractPart resolves to its own contract —
    /// or when its charger has two Charge_Abo products live at once, in which case the links carry
    /// Xellent's own overlapping periods on purpose. Everything downstream keys on
    /// (MeteringPointId, contract), never on the MP alone, so a second link is a second Settl call,
    /// not an overwrite; <see cref="Result.MpContractNames"/> is the one MP-keyed view and shows
    /// the first link only.
    /// </summary>
    public record MpContractLink(
        string MeteringPointId,
        string ContractName,
        DateTime ContractPartStartdate,
        DateTime ContractPartEnddate,
        bool IsJoined,
        string ProductNum);

    /// <summary>
    /// One account that cannot be migrated, and the contract name(s) it needs but Settl does not
    /// have. The account is the representative accountnum — the same key the migrate-loop and the
    /// per-customer outcome use.
    /// </summary>
    public record AccountMissingContracts(string Account, IReadOnlyList<string> ContractNames);

    public record Result(
        Dictionary<string, ContractInfoDto> ContractsByName,
        Dictionary<string, string> MpContractNames,
        List<MpContractLink> Links)
    {
        /// <summary>
        /// Which contract names each account's metering points need. Carried per account (not only
        /// per MP) so a caller can decide the fate of the ACCOUNT — the unit that gets created in
        /// Settl and the unit the run books as done — without walking the links again.
        /// </summary>
        public Dictionary<string, List<string>> ContractNamesByAccount { get; init; } = [];

        /// <summary>
        /// The metering points whose contract came from the template+product rule, and which rule(s)
        /// fired — a List, not a single value, because an MP can appear more than once in
        /// <c>accountMpMapping</c> when it has more than one contract part (a currently-live one plus
        /// a staged future one, El-ekstern parts having no start-date cap in extraction), and each
        /// part is resolved independently. Only MPs the rule actually moved appear — one that took
        /// the product mapping or the product name is not in here, because the report already shows
        /// where it ended up and there is no rule to name.
        /// </summary>
        public Dictionary<string, List<MpContractRuleInfo>> MpContractRules { get; init; } = [];

        /// <summary>
        /// The metering points whose contract came from the linked EV charger's product, and which
        /// pair(s) decided it — a List for the same reason as <see cref="MpContractRules"/>: a
        /// current-part resolution and a future-part resolution for the same MP are two different
        /// facts, not one to overwrite the other. Only MPs the rule actually moved appear.
        /// </summary>
        public Dictionary<string, List<MpChargerContractRuleInfo>> MpChargerContractRules { get; init; } = [];

        /// <summary>
        /// The needed names Settl did not hand back a contract for. Empty from
        /// <see cref="ResolveLocal"/>: nothing was asked, so nothing can be concluded.
        /// </summary>
        public HashSet<string> MissingContractNames { get; init; } = [];

        /// <summary>
        /// The accounts that must not be migrated, because at least one of their metering points
        /// needs a contract that is not in Settl. Creating them anyway leaves a customer, payer and
        /// metering point behind with no contract link — and the account books as finished, so it
        /// never comes round again. Ordered by accountnum so a run reports them the same way twice.
        /// </summary>
        public IReadOnlyList<AccountMissingContracts> AccountsMissingContracts()
        {
            if (MissingContractNames.Count == 0) return [];

            var blocked = new List<AccountMissingContracts>();
            foreach (var (account, names) in ContractNamesByAccount)
            {
                var missing = names.Where(MissingContractNames.Contains).ToList();
                if (missing.Count > 0)
                    blocked.Add(new AccountMissingContracts(account, missing));
            }
            return [.. blocked.OrderBy(b => b.Account, StringComparer.Ordinal)];
        }
    }

    public async Task<Result> ResolveAsync(
        CreateOptions options,
        List<AccountGrouping> accountGroupings,
        Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>> accountMpMapping,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, CustomerIdentity>? identityByAccount = null,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint = null,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByAccount = null)
    {
        var local = ResolveLocal(
            options, accountGroupings, accountMpMapping, identityByAccount,
            evChargersByMeteringPoint, evChargersByAccount);

        // Load every contract referenced by MP resolution.
        var neededContractNames = new HashSet<string>(local.Links.Select(l => l.ContractName));

        // Load contracts in BOTH dry-run and live-run so missing-contract errors are caught
        // during dry-run instead of only at the live link step. FindContractsAsync is read-only.
        // CommodityType-filter er nødvendigt: uden det returnerer Settl KUN Electricity.
        var commodityType = MigrationConstants.MapCommodityType(options.DeliveryCategory);
        var (contractsByName, missingContractNames) = neededContractNames.Count > 0
            ? await LoadContractsAsync(neededContractNames, commodityType, options.MeteringPointsOnly, cancellationToken)
            : (new Dictionary<string, ContractInfoDto>(), new HashSet<string>(StringComparer.Ordinal));

        return local with
        {
            ContractsByName = contractsByName,
            MissingContractNames = missingContractNames
        };
    }

    /// <summary>
    /// Resolves the expected MP→contract-name mapping (and links) purely from local config
    /// (GroupNum/AccountNum/ProductNum maps) — no Settl calls. Used by offline dry-run so the
    /// report can still show the EXPECTED contract name without loading Settl contract GUIDs.
    /// <see cref="Result.ContractsByName"/> is empty (Settl GUIDs are unknown offline).
    /// </summary>
    public Result ResolveLocal(
        CreateOptions options,
        List<AccountGrouping> accountGroupings,
        Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>> accountMpMapping,
        IReadOnlyDictionary<string, CustomerIdentity>? identityByAccount = null,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint = null,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByAccount = null)
    {
        var accountContractMap = BuildAccountContractMap(options, accountGroupings);
        var (mpContractNames, links, namesByAccount, mpContractRules, mpChargerContractRules) = ResolvePerMeteringPoint(
            options, accountMpMapping, accountContractMap, identityByAccount,
            evChargersByMeteringPoint, evChargersByAccount);
        return new Result(new Dictionary<string, ContractInfoDto>(), mpContractNames, links)
        {
            ContractNamesByAccount = namesByAccount,
            MpContractRules = mpContractRules,
            MpChargerContractRules = mpChargerContractRules
        };
    }

    private static Dictionary<string, string> BuildAccountContractMap(
        CreateOptions options, List<AccountGrouping> accountGroupings)
    {
        var map = new Dictionary<string, string>();

        if (options.GroupNumContractMap is { Count: > 0 })
        {
            foreach (var grouping in accountGroupings)
            {
                if (grouping.GroupNum != null &&
                    options.GroupNumContractMap.TryGetValue(grouping.GroupNum, out var contractName))
                {
                    map[grouping.RepresentativeAccount] = contractName;
                }
            }
        }

        if (options.AccountNumContractMap is { Count: > 0 })
        {
            foreach (var grouping in accountGroupings)
            {
                foreach (var acct in grouping.MemberAccounts)
                {
                    if (options.AccountNumContractMap.TryGetValue(acct, out var contractName))
                    {
                        map[grouping.RepresentativeAccount] = contractName;
                        break;
                    }
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Looks up the Settl contract behind every needed name, and reports which names came back
    /// empty. A name only counts as MISSING when Settl actually answered — either with its list of
    /// contracts, or with an error saying it could not produce one. Both leave the metering point
    /// without a contract to link to, so both must cost the accounts that need the name. A client
    /// that hands back neither a list nor an error never asked: that is the empty in-memory
    /// stand-in used by offline runs and tests, and it keeps the run-level error line and blocks
    /// nobody — otherwise every account in an offline run would be refused on a question that was
    /// never put to Settl.
    ///
    /// <para>A metering-points-only run says nothing here. It links no contracts, so a name Settl
    /// does not have costs it nothing — and the accounts that need the name are already named, one
    /// by one, in the warning the run writes for exactly this. An error line on top would be a
    /// second message about the same fact, and it would read as breakage in a run where nothing
    /// broke. The one thing it still says is when Settl could not answer at all: that is a
    /// different fact from "the contract does not exist", and it is said once for the whole
    /// run.</para>
    /// </summary>
    private async Task<(Dictionary<string, ContractInfoDto> ContractsByName, HashSet<string> MissingContractNames)> LoadContractsAsync(
        HashSet<string> neededContractNames, string commodityType, bool meteringPointsOnly, CancellationToken cancellationToken)
    {
        var (allContracts, findError) = await _contractClient.FindContractsAsync(commodityType, cancellationToken);
        var contractsByName = allContracts?
            .Where(c => c.ContractName != null && neededContractNames.Contains(c.ContractName))
            .ToDictionary(c => c.ContractName!) ?? [];

        var settlAnswered = allContracts != null || findError != null;
        // Settl answering "no contracts" leaves nothing to say beyond the name; only a real refusal
        // has a reason worth printing. Without this the line ended in a bare colon with nothing
        // after it, which reads as a message that lost its second half.
        var reason = string.IsNullOrWhiteSpace(findError) ? null : findError!.Trim();
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in neededContractNames)
        {
            if (contractsByName.TryGetValue(name, out var contract))
            {
                _logLine($"  Contract: {name} ({contract.Id})");
            }
            else
            {
                if (!meteringPointsOnly)
                    _warnings.AddError(reason == null
                        ? $"Contract '{name}' not found in Settl"
                        : $"Contract '{name}' not found: {reason}");
                if (settlAnswered) missing.Add(name);
            }
        }

        // Said once, for the whole run: Settl refusing to list contracts is not the same fact as a
        // contract that does not exist, and the per-account warning cannot tell the two apart.
        if (meteringPointsOnly && missing.Count > 0 && reason != null)
            _warnings.AddWarning(
                $"Settl could not list contracts ({reason}), so every contract name this run "
                + "needed counts as missing.");

        return (contractsByName, missing);
    }

    private (Dictionary<string, string> MpContractNames, List<MpContractLink> Links, Dictionary<string, List<string>> ContractNamesByAccount, Dictionary<string, List<MpContractRuleInfo>> MpContractRules, Dictionary<string, List<MpChargerContractRuleInfo>> MpChargerContractRules) ResolvePerMeteringPoint(
        CreateOptions options,
        Dictionary<string, List<(string MeteringPointId, DateTime ContractStartDate, DateTime ContractPartStartdate, DateTime ContractPartEnddate, bool IsJoined, string ContractNum, string ProductNum, string AmrCode, string PaymentTerm)>> accountMpMapping,
        Dictionary<string, string> accountContractMap,
        IReadOnlyDictionary<string, CustomerIdentity>? identityByAccount,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByAccount)
    {
        // Compiled once per run: the pattern order is decided here, not per metering point.
        var templateRules = TemplateProductContractRules.Build(options.TemplateProductContractMap);
        var chargerRules = ElectricityChargerContractRules.Build(options.ElectricityChargerContractMap);
        // EvCharger.Parts is not pre-filtered to "live" — the extraction query only excludes parts
        // that have already ended, not parts that haven't started yet, so a staged future product
        // change sits right alongside the currently-live one. Must filter by start date too, or a
        // charger with a future-dated transition already entered reads as two live products at once.
        //
        // Per-tuple, not one date for the whole run: El-ekstern products have no start-date cap in
        // extraction, so an MP with a future-dated part reaches this resolver as its own tuple (see
        // the loop below), representing "the state once that part takes effect" — not today. Its
        // charger side must be evaluated as of THAT tuple's own effective date, or a future el-ekstern
        // tuple would be paired against the charger's TODAY state even when the charger has its own
        // staged change landing before that date. ResolveChargerContract takes the effective date per
        // call, computed per tuple just below.
        var chargerReferenceDate = DateOnly.FromDateTime(options.ReferenceDate);
        var templatesByContract = options.ContractTemplatesByContractNum;
        var mpContractNames = new Dictionary<string, string>();
        var links = new List<MpContractLink>();
        var namesByAccount = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var mpChargerContractRules = new Dictionary<string, List<MpChargerContractRuleInfo>>(StringComparer.Ordinal);

        // Counted per rule so a dry-run can show WHICH metering points the template dimension moved
        // — the whole point of the rule is that it moves a few and leaves thousands of look-alikes
        // alone, and that is not something a total can tell you.
        var templateRuleHits = new Dictionary<string, int>(StringComparer.Ordinal);
        var chargerRuleHits = new Dictionary<string, int>(StringComparer.Ordinal);

        // Same idea as chargerRuleHits, but for metering points the charger dimension deliberately
        // left alone because they're outside the "confirmed product, one charger per MP" scope this
        // pause covers — not "no charger data", but "there is charger data, and it isn't simple
        // enough yet". A Charge_Abo or El-ekstern product changing over time is no longer one of
        // these reasons — each contract-part tuple resolves independently against its own effective
        // date. Nor is an account with several El-ekstern MPs, charger-linked or not: each MP
        // resolves on its own. Counted so a dry-run shows the paused population instead of it
        // reading as plain non-charger accounts.
        var chargerScopeSkips = new Dictionary<string, int>(StringComparer.Ordinal);

        // The same decision per metering point, for the report: the run log is not carried into the
        // HTML report, so a total in the log cannot tell a reader which of two metering points on
        // the same product was moved and which was left alone.
        var mpContractRules = new Dictionary<string, List<MpContractRuleInfo>>(StringComparer.Ordinal);

        // The overlap warnings already written this run (full message text). The same two-live
        // state is seen again for every tuple of the MP and every future date the synthesis walks,
        // and the operator needs to read it once per account, not once per date.
        var overlapWarningsIssued = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (acct, mps) in accountMpMapping)
        {
            var accountNames = namesByAccount.TryGetValue(acct, out var existing)
                ? existing
                : namesByAccount[acct] = [];
            var identity = identityByAccount is not null && identityByAccount.TryGetValue(acct, out var kind)
                ? kind
                : CustomerIdentity.Blank;

            // The one real (Xellent-sourced) contract-part tuple per MP eligible for a
            // charger-driven future extension: the latest-starting one. Any earlier tuple for the
            // same MP is already correctly bounded by the real tuple that follows it (Xellent's own
            // ContractPartEnddate), so only the last one can have an open span a future charger
            // change needs to close. Scoped per account since an MP belongs to exactly one account.
            // A tuple owns a list of links, not one: with two Charge_Abo products live at once it
            // produced one link per product. Extend is false for a multi-installation hit: that
            // contract covers the whole tuple whatever the chargers' own products do later, so the
            // tuple is still recorded (it must win as the MP's latest, or an earlier tuple would be
            // walked in its place) but never walked.
            var mpLastRealTuple = new Dictionary<string, (List<int> LinkIndexes, DateOnly EffectiveDate, DateTime OriginalEnd, string ElProductNum, bool IsJoined, bool Extend)>(StringComparer.Ordinal);

            // The charger rule's scope is decided per metering point, not per account (business
            // decisions 2026-09-01, confirmed on account 105212 — one charger MP plus ordinary
            // El-ekstern MPs must migrate, each on its own rule — and on account 105909 — two
            // charger MPs, each with its own installation, must migrate too). The MPs on this
            // account a charger is unambiguously linked to are found up front:
            //  - none        → every MP asks the charger rule, as before. It fires the __NoCharge__
            //                  sentinel when the account has no charger data at all, and falls
            //                  through when the charger could only be tied to the account
            //                  (evChargersByAccount).
            //  - one or more → each linked MP asks the charger rule with its own charger(s). Every
            //                  unlinked MP on the account skips it entirely — no sentinel, no skip
            //                  counted — and resolves through the template/product rules like any
            //                  ordinary MP. The charger-driven future extension below follows for
            //                  free: it only ever extends charger rule hits, per MP.
            // What makes "one or more" safe is the postal-code tie-break in XellentDomain's
            // BuildEvChargerLookups: with several candidate MPs, an installation is linked only to
            // the one MP whose delpoint postal code equals its own, and anything it cannot settle
            // stays on the account (evChargersByAccount) — so an entry in evChargersByMeteringPoint
            // is always a unique link. An MP with two or more installations linked to it is still
            // paused, inside ResolveChargerContracts. An account's El-ekstern product changing over
            // its contract-part history is NOT restricted here; each tuple resolves independently
            // against the charger's state as of that tuple's own effective date (see
            // chargerReferenceDate/mpEffectiveDate above). Computed once per account — every tuple
            // in `mps` belongs to the same account.
            var accountMpIds = mps.Select(m => m.MeteringPointId).Distinct(StringComparer.Ordinal).ToList();
            var chargerLinkedMpIds = evChargersByMeteringPoint is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : accountMpIds.Where(evChargersByMeteringPoint.ContainsKey).ToHashSet(StringComparer.Ordinal);

            foreach (var mp in mps)
            {
                // The date this specific tuple is actually in force: itself for a future-dated
                // part, today for the currently-live one (whose own start date is already <=
                // today, so the max is a no-op there).
                var mpEffectiveDate = DateOnly.FromDateTime(mp.ContractPartStartdate);
                if (mpEffectiveDate < chargerReferenceDate) mpEffectiveDate = chargerReferenceDate;

                // The discount part is a marker for an UA contract, never a contract of its own.
                // A missing or ambiguous companion must stop create before it writes anything.
                if (string.Equals(mp.ProductNum, "V AboRabat", StringComparison.OrdinalIgnoreCase)
                    && !accountContractMap.ContainsKey(acct))
                {
                    var companions = mps.Where(other =>
                            other.MeteringPointId == mp.MeteringPointId
                            && other.ContractNum == mp.ContractNum
                            && !string.Equals(other.ProductNum, "V AboRabat", StringComparison.OrdinalIgnoreCase)
                            && DateOnly.FromDateTime(other.ContractPartStartdate) <= mpEffectiveDate
                            && (other.ContractPartEnddate == MigrationConstants.EndDateNull
                                || DateOnly.FromDateTime(other.ContractPartEnddate) >= mpEffectiveDate))
                        .Select(other => other.ProductNum)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    string? uaName = null;
                    if (companions.Count == 1)
                    {
                        uaName = companions[0] switch
                        {
                            "V Kvt UA" or "V Kvartal" => "V Kvartal UA",
                            "V Variabel" => "V Variabel UA",
                            "V LadVariP" when evChargersByMeteringPoint is not null
                                && evChargersByMeteringPoint.TryGetValue(mp.MeteringPointId, out var chargers)
                                && chargers.Count == 1 && chargers[0].AddressMatchesMeteringPoint
                                => ResolveDiscountCharger(chargers[0], mpEffectiveDate, chargerRules),
                            _ => null
                        };
                    }
                    if (uaName is null)
                        throw new InvalidOperationException(
                            $"MP {mp.MeteringPointId} (account {acct}): V AboRabat has no unique UA contract at {mpEffectiveDate}. Resolve this account before create.");
                    AddLink(uaName, mp.ContractPartStartdate, mp.ContractPartEnddate);
                    continue;
                }

                // With charger-linked MPs on the account, the charger rule is theirs alone.
                var mpAsksChargerRule = chargerLinkedMpIds.Count == 0 || chargerLinkedMpIds.Contains(mp.MeteringPointId);
                if (!accountContractMap.ContainsKey(acct) && mpAsksChargerRule)
                {
                    var chargerResolutions = ResolveChargerContracts(
                        chargerRules, evChargersByMeteringPoint, evChargersByAccount, acct, mp.MeteringPointId,
                        mp.ProductNum, mpEffectiveDate, _warnings, overlapWarningsIssued, out var scopeSkipReason);
                    if (scopeSkipReason != null)
                    {
                        chargerScopeSkips.TryGetValue(scopeSkipReason, out var seenSkip);
                        chargerScopeSkips[scopeSkipReason] = seenSkip + 1;
                    }
                    if (chargerResolutions.Count > 0)
                    {
                        // One link per resolved contract. A single live product spans the tuple
                        // exactly as it always has. Two or more live at once (a stale old row in
                        // Xellent) each span their own product's live window clipped to the tuple,
                        // so the overlap Settl receives is exactly the overlap Xellent has — no
                        // wider — and ResolveChargerContracts has already warned what is being
                        // sent. Whether Settl accepts one MP on two contracts for the same period
                        // is not pre-empted here: if it refuses, the account fails on the existing
                        // per-account error path.
                        var overlapping = chargerResolutions.Count > 1;
                        var tupleLinkIndexes = new List<int>(chargerResolutions.Count);
                        foreach (var resolution in chargerResolutions)
                        {
                            RecordChargerRuleHit(resolution.RuleHit, mp.MeteringPointId, chargerRuleHits, mpChargerContractRules);
                            var (start, end) = overlapping
                                ? ClipToTuple(resolution, mp.ContractPartStartdate, mp.ContractPartEnddate)
                                : (mp.ContractPartStartdate, mp.ContractPartEnddate);
                            AddLink(resolution.ContractName, start, end);
                            tupleLinkIndexes.Add(links.Count - 1);
                        }

                        // Track the latest-starting tuple per MP so a charger-only future change
                        // (the charger's own product changing when the electricity side isn't) can
                        // still extend it after the loop — see SynthesizeChargerFutureLinks below.
                        // Only when THIS tuple's own period actually came from the charger rule:
                        // extending a period that isn't charger-driven (today's data is ambiguous,
                        // or the pair simply isn't configured) would compare the charger's future
                        // state against a raw fallback name instead of a real contract, which is
                        // not a meaningful comparison.
                        //
                        // A multi-installation hit is recorded but not extended: the MP keeps that
                        // one contract for the whole tuple (user decision 2026-09-01). Walking the
                        // chargers' future product changes would split it into per-product links,
                        // which is exactly what this contract is not.
                        var isMultipleInstallations = chargerResolutions.Any(r => string.Equals(
                            r.RuleHit.ChargeAboProductNum, ElectricityChargerContractRules.MultipleInstallationsKey, StringComparison.Ordinal));
                        if (!mpLastRealTuple.TryGetValue(mp.MeteringPointId, out var existingLast)
                            || mpEffectiveDate >= existingLast.EffectiveDate)
                        {
                            mpLastRealTuple[mp.MeteringPointId] = (
                                tupleLinkIndexes, mpEffectiveDate, mp.ContractPartEnddate, mp.ProductNum, mp.IsJoined,
                                Extend: !isMultipleInstallations);
                        }
                        continue;
                    }
                }

                string? contractTemplate = null;
                templatesByContract?.TryGetValue(mp.ContractNum, out contractTemplate);

                var contractName = ResolveForMp(
                    options, accountContractMap, acct, mp.ProductNum, mp.PaymentTerm,
                    templateRules, contractTemplate, identity, out var fallbackReason,
                    out var templateRuleHit);
                if (templateRuleHit != null)
                {
                    var ruleKey = $"{templateRuleHit.ContractTemplate} + {templateRuleHit.ProductNum} "
                                + $"({templateRuleHit.Identification}) → {templateRuleHit.ContractName}";
                    templateRuleHits.TryGetValue(ruleKey, out var seen);
                    templateRuleHits[ruleKey] = seen + 1;
                    if (!mpContractRules.TryGetValue(mp.MeteringPointId, out var templateHitsForMp))
                        mpContractRules[mp.MeteringPointId] = templateHitsForMp = [];
                    templateHitsForMp.Add(templateRuleHit);
                }
                if (contractName == null)
                {
                    contractName = mp.ProductNum;
                    var msg = $"MP {mp.MeteringPointId} (account {acct}): {fallbackReason} — using contract part product '{mp.ProductNum}'";
                    _warnings.AddInfo(msg);
                    _logLine($"  FALLBACK: {msg}");
                }
                AddLink(contractName, mp.ContractPartStartdate, mp.ContractPartEnddate);

                // One place for what a link costs besides itself: the MP-keyed name view (first
                // link wins) and the account's needed-names list the missing-contract guard reads.
                void AddLink(string name, DateTime start, DateTime end)
                {
                    mpContractNames.TryAdd(mp.MeteringPointId, name);
                    if (!accountNames.Contains(name, StringComparer.Ordinal))
                        accountNames.Add(name);
                    links.Add(new MpContractLink(mp.MeteringPointId, name, start, end, mp.IsJoined, mp.ProductNum));
                }
            }

            // Charger-only future changes: the electricity side may never gain a second tuple (its
            // own product isn't changing), so the only way a future charger-driven contract change
            // reaches Settl is by extending the MP's last real tuple here, after the account's real
            // tuples are all known.
            foreach (var (mpId, lastTuple) in mpLastRealTuple)
            {
                // A multi-installation contract is one link for the tuple — skip the walk, see the
                // Extend flag above.
                if (!lastTuple.Extend) continue;
                SynthesizeChargerFutureLinks(
                    acct, mpId, lastTuple.LinkIndexes, lastTuple.EffectiveDate, lastTuple.OriginalEnd,
                    lastTuple.ElProductNum, lastTuple.IsJoined, chargerRules, evChargersByMeteringPoint,
                    links, mpChargerContractRules, chargerRuleHits, _warnings, overlapWarningsIssued);
            }
        }

        if (templateRuleHits.Count > 0)
        {
            _logLine("--- Template+product contracts ---");
            foreach (var (rule, count) in templateRuleHits.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal))
                _logLine($"  {count}x  {rule}");
            _logLine("");
        }

        if (chargerRuleHits.Count > 0)
        {
            _logLine("--- Charger contracts ---");
            foreach (var (rule, count) in chargerRuleHits.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal))
                _logLine($"  {count}x  {rule}");
            _logLine("");
        }

        if (chargerScopeSkips.Count > 0)
        {
            _logLine("--- Charger contracts skipped (out of scope for now) ---");
            foreach (var (reason, count) in chargerScopeSkips.OrderByDescending(h => h.Value).ThenBy(h => h.Key, StringComparer.Ordinal))
                _logLine($"  {count}x  {reason}");
            _logLine("");
        }

        return (mpContractNames, links, namesByAccount, mpContractRules, mpChargerContractRules);
    }

    /// <summary>
    /// One contract the charger rule resolved for a metering point at a given date, and — for a
    /// real Charge_Abo product — that product's own live window, so the caller can clip the link
    /// to it when two products are live at once. The window is null for the
    /// <see cref="ElectricityChargerContractRules.NoChargeKey"/> sentinel, which has no part.
    /// </summary>
    private sealed record ChargerResolution(
        string ContractName,
        MpChargerContractRuleInfo RuleHit,
        DateOnly? PartStart,
        DateOnly? PartEnd);

    /// <summary>
    /// Resolves one MP's contract(s) from its own El-ekstern product plus its account's linked EV
    /// charger's live Charge_Abo product(s) — or the reserved
    /// <see cref="ElectricityChargerContractRules.NoChargeKey"/> when the account has no live
    /// Charge/Charge_Abo part at all. Normally one result. Empty (no rule hit) for: no charger data
    /// configured, no charger linked to this MP, a charger linked ambiguously to the account instead
    /// (lives in <paramref name="evChargersByAccount"/>, never counts as "no charge contract"), a
    /// charger with a live Charge part but no live Charge_Abo, more than one charger linked to the
    /// MP without every live product owned (the multi-installation key
    /// <see cref="ElectricityChargerContractRules.MultipleInstallationsKey"/> covers the case where
    /// they are), the resolved product not being one of the confirmed ones yet (see
    /// <see cref="ElectricityChargerContractRules.ConfirmedChargeAboProducts"/>), or no configured
    /// entry for the resolved pair. A Charge_Abo product that changed over the charger's history is
    /// not one of these cases — <paramref name="effectiveDate"/> already picks out the product(s)
    /// live at THIS tuple's own effective date, and each is resolved on its own merits.
    ///
    /// <para><b>Two or more Charge_Abo products live at once</b> (business decision 2026-09-01):
    /// every live product with a configured entry is returned as its own resolution (open-ended
    /// first, then newest-starting), each carrying its own live window; a product without an entry is named in the warning
    /// and left out, and does not cost the others. One warning per MP and state, naming the products
    /// and the contracts sent. Nothing is guessed and nothing is refused: the stale end date is a
    /// Xellent problem to be fixed by hand, and Settl showing the overlap is what makes it visible.</para>
    ///
    /// <para><paramref name="scopeSkipReason"/> is set (independently of the return value) whenever
    /// one of the "paused for now" reasons — as opposed to "no data at all" — is why nothing was
    /// resolved, so the caller can report it instead of the case reading as a plain non-charger MP.</para>
    ///
    /// <para>Which MPs ask at all is the caller's decision (the per-metering-point scope in
    /// <see cref="ResolvePerMeteringPoint"/>): an MP without a charger on an account whose charger
    /// sits on another of its MPs never reaches this method, so the "no entry in either lookup"
    /// branch below genuinely means the account has no charger data — not "this MP has none".</para>
    /// </summary>
    private static IReadOnlyList<ChargerResolution> ResolveChargerContracts(
        ElectricityChargerContractRules? chargerRules,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByAccount,
        string account,
        string meteringPointId,
        string elProductNum,
        DateOnly effectiveDate,
        IWarningCollector warnings,
        ISet<string> overlapWarningsIssued,
        out string? scopeSkipReason)
    {
        scopeSkipReason = null;
        if (chargerRules is null) return [];

        // Neither lookup supplied at all means charger extraction did not run for this build (e.g.
        // a non-electricity commodity, or a caller that doesn't pass charger data) — that is a
        // different fact from "extraction ran and this account has none", and must not be read as
        // the latter. Only fire the no-charge sentinel when at least one lookup actually exists.
        if (evChargersByMeteringPoint is null && evChargersByAccount is null) return [];

        // The Charge_Abo products live at effectiveDate, each with its own live window. The
        // sentinel is a single candidate with no window.
        List<(string Product, DateOnly? Start, DateOnly? End)> candidates;
        if (evChargersByMeteringPoint is not null
            && evChargersByMeteringPoint.TryGetValue(meteringPointId, out var linkedChargers))
        {
            // Two or more physical charger installations tied to the same MP (user decisions
            // 2026-09-01 on accounts 491704, 109909, 471352 and 498288): one household with several
            // charging boxes is a known arrangement, and Settl has exactly one contract for it —
            // owned, Variabel. It applies when every Charge_Abo product live at this date across the
            // chargers is an owned one, wherever the installations are registered: the address the
            // charger sits at is not a condition here (EvCharger.AddressMatchesMeteringPoint only
            // drives the tie-break in XellentDomain that linked the charger to this MP). The MP then
            // resolves through the __MultipleInstallations__ key to ONE contract for the tuple; the
            // shared loop below applies the confirmed-products gate and the configured pair to it
            // like any other candidate. Anything else stays paused, and the reason says which test
            // failed — the dashboard prints these reasons as they are.
            if (linkedChargers.Count > 1)
            {
                var liveProducts = linkedChargers
                    .SelectMany(c => c.Parts)
                    .Where(p => p.Kind == EvChargerPartKind.Subscription && IsLiveAt(p, effectiveDate))
                    .Select(p => p.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();
                if (liveProducts.Count == 0
                    || liveProducts.Any(p => !ElectricityChargerContractRules.OwnedChargeAboProducts.Contains(p)))
                {
                    var mix = liveProducts.Count == 0 ? "none live" : string.Join(", ", liveProducts);
                    scopeSkipReason = "several EV chargers on metering point, product mix has no "
                                    + $"multi-installation contract: {mix} (paused for now)";
                    return [];
                }

                candidates = [(ElectricityChargerContractRules.MultipleInstallationsKey, null, null)];
            }
            else
            {
                var charger = linkedChargers[0];

                // EvCharger.Parts is not pre-filtered to "live" — the extraction query only excludes
                // parts that have already ended, not ones that haven't started yet, so a staged
                // future product change (very common on Charge_Abo — see the OAboEje0 -> OAboEjeMEL
                // lifecycle) sits right alongside the currently-live part. effectiveDate is the
                // caller's per-tuple date (today for the currently-live el-ekstern part, that part's
                // own start date for a future-dated one) — filtering by it here is what tells "one
                // live product, one staged for later relative to THIS tuple" apart from "two live
                // at once".
                var liveParts = charger.Parts
                    .Where(p => IsLiveAt(p, effectiveDate))
                    .ToList();

                if (liveParts.Count == 0)
                {
                    // Nothing live right now — only future-staged and/or already-ended rows. Same
                    // fact as "no charge contract at all" today.
                    candidates = [(ElectricityChargerContractRules.NoChargeKey, null, null)];
                }
                else
                {
                    var liveSubscriptions = liveParts
                        .Where(p => p.Kind == EvChargerPartKind.Subscription)
                        .ToList();

                    // A live Charge part with no live Charge_Abo is not a combination the business
                    // table has an answer for — ask the next rule.
                    if (liveSubscriptions.Count == 0) return [];

                    // One candidate per distinct product. Two rows of the same product live at once
                    // are one product with one window (earliest start, latest end, open wins). Order
                    // matters only for the MP-keyed name view, which shows the first link: the
                    // open-ended product first, then the newest-starting — the stale row in the
                    // confirmed Xellent pattern is the one left with a future end date, so the open
                    // one is the one somebody meant.
                    candidates = liveSubscriptions
                        .GroupBy(p => p.ProductCode, StringComparer.OrdinalIgnoreCase)
                        .Select(g => (
                            Product: g.Key,
                            Start: (DateOnly?)g.Min(p => p.StartDate),
                            End: g.Any(p => p.EndDate is null) ? null : g.Max(p => p.EndDate)))
                        .OrderBy(c => c.End is null ? 0 : 1)
                        .ThenByDescending(c => c.Start)
                        .ThenBy(c => c.Product, StringComparer.Ordinal)
                        .ToList();
                }
            }
        }
        else if (evChargersByAccount is not null && evChargersByAccount.ContainsKey(account))
        {
            // A charger exists but couldn't be tied to exactly one metering point — a different,
            // unresolved situation from "no charge contract at all". Leave it to the next rule.
            return [];
        }
        else
        {
            // No entry in either lookup: the account genuinely has no live Charge/Charge_Abo part.
            candidates = [(ElectricityChargerContractRules.NoChargeKey, null, null)];
        }

        // Each candidate on its own merits. Scope gate first: only the domain-owner-confirmed
        // productnums resolve through the charger table for now (see
        // docs/charge-account-classification.md) — everything else config still lists is paused
        // until confirmed. Then the configured pair.
        var hits = new List<ChargerResolution>(candidates.Count);
        var misses = new List<(string Product, string Reason, bool Paused)>();
        foreach (var (product, start, end) in candidates)
        {
            if (!ElectricityChargerContractRules.ConfirmedChargeAboProducts.Contains(product))
            {
                var reason = string.Equals(product, ElectricityChargerContractRules.NoChargeKey, StringComparison.Ordinal)
                    ? "no live Charge/Charge_Abo part (paused for now)"
                    : $"Charge_Abo product '{product}' not yet confirmed (paused for now)";
                misses.Add((product, reason, Paused: true));
                continue;
            }

            var contractName = chargerRules.Resolve(elProductNum, product);
            if (contractName is not { Length: > 0 })
            {
                misses.Add((product, $"no configured contract for '{elProductNum}' + '{product}'", Paused: false));
                continue;
            }

            hits.Add(new ChargerResolution(
                contractName, new MpChargerContractRuleInfo(elProductNum, product, contractName), start, end));
        }

        if (candidates.Count == 1)
        {
            // The single-product behaviour, unchanged: a paused product is counted as a scope skip,
            // an unmapped pair falls through silently to the next rule. The multi-installation key
            // is the one exception to the silence: it exists for V LadVariP only (Settl has no UA
            // variant), so an MP on V LadVarUA that passed both tests above is still a pause the
            // operator must be able to see, not a plain fall-through.
            if (misses.Count == 1)
            {
                if (misses[0].Paused)
                    scopeSkipReason = misses[0].Reason;
                else if (string.Equals(misses[0].Product, ElectricityChargerContractRules.MultipleInstallationsKey, StringComparison.Ordinal))
                    scopeSkipReason = "several EV chargers on metering point, no multi-installation "
                                    + $"contract configured for '{elProductNum}' (paused for now)";
            }
            return hits;
        }

        // Two or more Charge_Abo products live at once is not normal churn — it means an old row was
        // left with a stale future end date instead of being closed the day the new one started
        // (confirmed pattern in Xellent). Business decision 2026-09-01: do not pick one and do not
        // refuse. Send every product that resolves, with Xellent's own overlapping periods, and say
        // so — the overlap in Settl is what gets the stale end date fixed by hand. A product without
        // a configured entry is named and left out; it does not cost the others.
        var products = string.Join(", ", candidates.Select(c => c.Product));
        var head = $"MP {meteringPointId} (account {account}): charger has {candidates.Count} Charge_Abo "
                 + $"products live at once ({products}) — likely an unclosed old contract part in Xellent.";
        var leftOut = string.Join("; ", misses.Select(m => $"{m.Product} ({m.Reason})"));
        var message = hits.Count == 0
            ? $"{head} None of them has a contract for '{elProductNum}' ({leftOut}). "
              + "Falling back to the product-based contract rule."
            : $"{head} Sending every resolved contract with Xellent's own overlapping periods: "
              + string.Join(", ", hits.Select(h => $"{h.RuleHit.ChargeAboProductNum} → {h.ContractName}"))
              + (misses.Count > 0 ? $". Left out: {leftOut}" : "")
              + ". Fix the stale end date in Xellent by hand.";
        if (overlapWarningsIssued.Add(message)) warnings.AddWarning(message);

        if (hits.Count == 0 && misses.Any(m => m.Paused))
            scopeSkipReason = misses.First(m => m.Paused).Reason;
        return hits;
    }

    /// <summary>
    /// Whether a charger part is in force on the date: started, and not ended before it. Open
    /// (null end) means still running.
    /// </summary>
    private static bool IsLiveAt(EvChargerPart part, DateOnly date) =>
        part.StartDate <= date && (part.EndDate is null || part.EndDate.Value >= date);

    private static string? ResolveDiscountCharger(EvCharger charger, DateOnly date,
        ElectricityChargerContractRules? rules)
    {
        var products = charger.Parts
            .Where(p => p.Kind == EvChargerPartKind.Subscription && IsLiveAt(p, date))
            .Select(p => p.ProductCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return products.Count == 1 && ElectricityChargerContractRules.ConfirmedChargeAboProducts.Contains(products[0])
            ? rules?.Resolve("V LadVarUA", products[0])
            : null;
    }

    /// <summary>
    /// Books one charger-rule hit where the dry-run log and the report read it from.
    /// </summary>
    private static void RecordChargerRuleHit(
        MpChargerContractRuleInfo ruleHit,
        string meteringPointId,
        Dictionary<string, int> chargerRuleHits,
        Dictionary<string, List<MpChargerContractRuleInfo>> mpChargerContractRules)
    {
        var ruleKey = $"{ruleHit.ElProductNum} + {ruleHit.ChargeAboProductNum} → {ruleHit.ContractName}";
        chargerRuleHits.TryGetValue(ruleKey, out var seen);
        chargerRuleHits[ruleKey] = seen + 1;
        if (!mpChargerContractRules.TryGetValue(meteringPointId, out var hitsForMp))
            mpChargerContractRules[meteringPointId] = hitsForMp = [];
        hitsForMp.Add(ruleHit);
    }

    /// <summary>
    /// The part of a Charge_Abo product's own live window that falls inside the electricity tuple —
    /// the period its link gets when two or more products are live at once, so Settl's overlap is
    /// exactly Xellent's. A single live product never comes here: it keeps the tuple's own period,
    /// as it always has.
    /// </summary>
    private static (DateTime Start, DateTime End) ClipToTuple(
        ChargerResolution resolution, DateTime tupleStart, DateTime tupleEnd)
    {
        var start = tupleStart;
        if (resolution.PartStart is { } partStart)
        {
            var partStartDate = partStart.ToDateTime(TimeOnly.MinValue);
            if (partStartDate > start) start = partStartDate;
        }
        return (start, ClipEnd(resolution.PartEnd, tupleEnd));
    }

    /// <summary>
    /// The earlier of the part's own end and the tuple's. Open (null part end / EndDateNull tuple
    /// end) never wins over a real date.
    /// </summary>
    private static DateTime ClipEnd(DateOnly? partEnd, DateTime tupleEnd)
    {
        if (partEnd is not { } end) return tupleEnd;
        var partEndDate = end.ToDateTime(TimeOnly.MinValue);
        if (tupleEnd == MigrationConstants.EndDateNull) return partEndDate;
        return partEndDate < tupleEnd ? partEndDate : tupleEnd;
    }

    /// <summary>
    /// Extends an MP's last real (electricity-sourced) contract period(s) with further periods
    /// driven purely by the linked charger's own future product changes — the case
    /// <see cref="ResolveChargerContracts"/> alone cannot see, because it is only ever called once
    /// per electricity contract-part tuple, and an MP whose electricity product never changes has
    /// only one tuple. Walks every future date the charger's own timeline stages (a new Subscription
    /// product starting, or one ending with nothing yet replacing it) inside the tuple's own window,
    /// resolving each independently through the same rule used everywhere else, so the same
    /// behaviour (an unmapped pair falls through; two products live at once are both sent) applies
    /// here too.
    ///
    /// <para>At each date, the charger-driven links still in force on that date are compared with
    /// what the date resolves to, contract by contract: a contract in force that the date no longer
    /// resolves to ends the day before; a contract the date resolves to that is not in force starts
    /// on it. With one product live either side this is the old "different contract → cap and
    /// append" rule exactly; with two live at once it is the same rule applied per product, so a
    /// stale row that starts or ends in the future opens or closes its own link without touching the
    /// other one. Mutates <paramref name="links"/> in place (a resolver-local list, not the shared
    /// Xellent tuple, so nothing else that reads <c>ContractPartEnddate</c> is affected). A date that
    /// resolves to nothing configured is skipped without capping anything — an open period is left
    /// open rather than truncated with no replacement.</para>
    /// </summary>
    private static void SynthesizeChargerFutureLinks(
        string account,
        string meteringPointId,
        List<int> lastLinkIndexes,
        DateOnly lastEffectiveDate,
        DateTime lastOriginalEnd,
        string elProductNum,
        bool isJoined,
        ElectricityChargerContractRules? chargerRules,
        IReadOnlyDictionary<string, IReadOnlyList<EvCharger>>? evChargersByMeteringPoint,
        List<MpContractLink> links,
        Dictionary<string, List<MpChargerContractRuleInfo>> mpChargerContractRules,
        Dictionary<string, int> chargerRuleHits,
        IWarningCollector warnings,
        ISet<string> overlapWarningsIssued)
    {
        if (chargerRules is null) return;
        if (evChargersByMeteringPoint is null
            || !evChargersByMeteringPoint.TryGetValue(meteringPointId, out var linkedChargers))
            return;

        // Every future date the charger's own Subscription timeline stages beyond the last real
        // tuple's own effective date: a new product starting, or the current one ending with
        // nothing (yet) staged to replace it — the latter resolves through the same NoChargeKey
        // sentinel path ResolveChargerContracts already uses for "no live charge part". Only dates
        // inside the tuple's own window: a charger change after the electricity part has ended has
        // no electricity period left to split.
        var candidateDates = linkedChargers
            .SelectMany(c => c.Parts)
            .Where(p => p.Kind == EvChargerPartKind.Subscription)
            .SelectMany(p => p.EndDate is { } end
                ? (IEnumerable<DateOnly>)[p.StartDate, end.AddDays(1)]
                : [p.StartDate])
            .Where(d => d > lastEffectiveDate)
            .Where(d => lastOriginalEnd == MigrationConstants.EndDateNull || d <= DateOnly.FromDateTime(lastOriginalEnd))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        // The charger-driven links of this MP a later date may still have to close: the last real
        // tuple's own, plus every one appended here. Which of them a date actually touches is
        // decided by whether the link is still in force on that date.
        var chargerLinkIndexes = new List<int>(lastLinkIndexes);

        foreach (var candidate in candidateDates)
        {
            var resolutions = ResolveChargerContracts(
                chargerRules, evChargersByMeteringPoint, evChargersByAccount: null, account, meteringPointId,
                elProductNum, candidate, warnings, overlapWarningsIssued, out _);

            // Nothing configured at this date: skip it, leave the current open period(s) as-is
            // rather than truncating them with nothing to cover the gap.
            if (resolutions.Count == 0) continue;

            var resolvedNames = resolutions.Select(r => r.ContractName).ToHashSet(StringComparer.Ordinal);
            var inForce = chargerLinkIndexes.Where(i => IsInForce(links[i], candidate)).ToList();
            var inForceNames = inForce.Select(i => links[i].ContractName).ToHashSet(StringComparer.Ordinal);

            // A contract in force that this date no longer resolves to ends the day before.
            foreach (var i in inForce)
            {
                if (resolvedNames.Contains(links[i].ContractName)) continue;
                links[i] = links[i] with
                {
                    ContractPartEnddate = candidate.AddDays(-1).ToDateTime(TimeOnly.MinValue)
                };
            }

            // A contract this date resolves to that is not already in force starts here. One that
            // is in force already is the same answer as before: nothing changed for it at this date.
            // With two products live at this date the new link is clipped to its own product's
            // window, as in the main loop; with one it runs to the tuple's end and a later date caps
            // it, as it always has.
            foreach (var resolution in resolutions)
            {
                if (inForceNames.Contains(resolution.ContractName)) continue;
                var end = resolutions.Count > 1 ? ClipEnd(resolution.PartEnd, lastOriginalEnd) : lastOriginalEnd;
                links.Add(new MpContractLink(
                    meteringPointId, resolution.ContractName, candidate.ToDateTime(TimeOnly.MinValue), end,
                    isJoined, elProductNum));
                chargerLinkIndexes.Add(links.Count - 1);
                RecordChargerRuleHit(resolution.RuleHit, meteringPointId, chargerRuleHits, mpChargerContractRules);
            }
        }
    }

    /// <summary>Whether a link's period covers the date; EndDateNull means open.</summary>
    private static bool IsInForce(MpContractLink link, DateOnly date)
    {
        var day = date.ToDateTime(TimeOnly.MinValue);
        return link.ContractPartStartdate <= day
            && (link.ContractPartEnddate == MigrationConstants.EndDateNull || link.ContractPartEnddate >= day);
    }

    /// <summary>
    /// Resolves one MP's contract name, and — when it cannot — says WHY in
    /// <paramref name="fallbackReason"/>. The two reasons are not the same problem and do not have
    /// the same fix: a product nobody has mapped is a decision waiting to be made, while a mapped
    /// product whose payment terms do not cover this contract part is a typo in a JSON key. The
    /// log said "no contract mapping configured" to both, which is what let the second one hide
    /// until it had cost a production run.
    /// </summary>
    private static string? ResolveForMp(
        CreateOptions options,
        Dictionary<string, string> accountContractMap,
        string account,
        string productNum,
        string paymentTerm,
        TemplateProductContractRules? templateRules,
        string? contractTemplate,
        CustomerIdentity identity,
        out string fallbackReason,
        out MpContractRuleInfo? templateRuleHit)
    {
        fallbackReason = "no contract mapping configured";
        templateRuleHit = null;

        if (accountContractMap.TryGetValue(account, out var explicitName))
            return explicitName;

        if (templateRules?.Resolve(contractTemplate, productNum, identity) is { Length: > 0 } templateContract)
        {
            templateRuleHit = new MpContractRuleInfo(
                contractTemplate!, productNum, identity.ToString().ToLowerInvariant(), templateContract);
            return templateContract;
        }

        if (options.ProductNumContractMap is { Count: > 0 }
            && options.ProductNumContractMap.TryGetValue(productNum, out var entry))
        {
            if (entry.Resolve(paymentTerm) is { Length: > 0 } productContract)
                return productContract;

            fallbackReason = DescribeUnmatchedPaymentTerm(entry, productNum, paymentTerm);
        }

        return null;
    }

    /// <summary>
    /// Names the payment term that found nothing and the keys the product actually has, so the
    /// difference between what Xellent says and what the profile says can be read straight off the
    /// log line. Reaching here means the entry has no usable <c>"*"</c> default either — a default
    /// would have resolved.
    /// </summary>
    private static string DescribeUnmatchedPaymentTerm(
        ProductNumContractEntry entry, string productNum, string paymentTerm)
    {
        var term = string.IsNullOrEmpty(paymentTerm) ? "(empty)" : paymentTerm;
        var keys = entry.ByPaymentTerm is { Count: > 0 } byTerm
            ? string.Join(", ", byTerm.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"'{k}'"))
            : "none";

        return $"product '{productNum}' is mapped, but payment term '{term}' matches none of its "
             + $"keys ({keys}) and it has no '*' default";
    }
}
