using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Engros;
using DataMigration.Domain.Xellent.Internal;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Core.MigrationConstants;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Loads <see cref="Customer"/> aggregates from Xellent. Resolves CVR+Fingerprint
/// representative merge, then orchestrates customer / contract / metering-point mapping
/// plus owner-cluster and joint-invoice-group detection. Domain output types are source-
/// agnostic (live in <c>DataMigration.Domain</c>); the loader's return shape
/// (<see cref="XellentLoadResult"/>) lives in this assembly because it's loader plumbing.
///
/// <para>Reports data-quality issues via <see cref="IWarningCollector"/>. Policy-checks live
/// in <c>Domain.Xellent.Validation</c> and are run by the consumer if desired.</para>
/// </summary>
public class XellentDomainMapper
{
    private const string DefaultDataAreaId = "hol";

    private readonly XellentDbContext _context;
    private readonly IWarningCollector _warnings;
    private readonly MapperOptions _options;
    private readonly string _dataAreaId;
    private readonly CustomerMapper _customerMapper;
    private readonly ContractMapper _contractMapper;
    private readonly MeteringPointMapper _mpMapper;

    public XellentDomainMapper(
        XellentDbContext context,
        IWarningCollector warnings,
        MapperOptions options,
        string dataAreaId = DefaultDataAreaId)
    {
        _context = context;
        _warnings = warnings;
        _options = options;
        _dataAreaId = dataAreaId;
        _customerMapper = new CustomerMapper(context, warnings, options, dataAreaId);
        _contractMapper = new ContractMapper(context, warnings, options, dataAreaId);
        _mpMapper = new MeteringPointMapper(context, warnings, options, dataAreaId);
    }

    /// <summary>
    /// Loads customers and their related entities. Input accounts are resolved to their
    /// CVR+Fingerprint representative before loading; merged (non-rep) accounts collapse
    /// into the representative customer. Returns one <see cref="Customer"/> per
    /// representative with its contracts inline, plus the metering points referenced
    /// by those contracts and any cross-customer relations (owner-clusters, joint-invoice-groups).
    /// </summary>
    /// <param name="accountNumbers">
    /// Accounts to load, or <c>null</c> for every account in the data area that the mapper's
    /// commodity and reference date already select. Null only reaches the representative
    /// resolver, which has no list to filter on yet; from there on the resolved account set is
    /// known and every downstream query filters on it in chunks.
    /// </param>
    public async Task<XellentLoadResult> LoadAsync(IEnumerable<string>? accountNumbers, CancellationToken cancellationToken = default)
    {
        var accounts = accountNumbers?.Distinct(StringComparer.Ordinal).ToArray();
        var snapshotDate = _options.ReferenceDate;

        if (accounts is { Length: 0 })
            return new XellentLoadResult([], [], [], [], snapshotDate, new Dictionary<string, string?>(), [], new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal));

        // Derived before anything reads a contract header: the batch end-dated the headers too, and
        // EXU_CONTRACTTABLE has no audit log to restore them from. The exemption lets exactly the
        // contracts the log names back in — see ContractHeaderExemption for why a date cannot.
        var headerExemption = await ContractHeaderExemption.DeriveAsync(
            _context, _warnings, _options, _dataAreaId, cancellationToken);

        var customerResult = await _customerMapper.MapAsync(accounts, headerExemption, cancellationToken);
        if (customerResult.Customers.Count == 0)
            return new XellentLoadResult([], [], [], [], snapshotDate, new Dictionary<string, string?>(), [], new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal));

        // Always materialised — see CustomerMapper: downstream queries need the account set as a
        // selective predicate and chunk it, rather than running unfiltered.
        var allInputAccounts = customerResult.AccountToRepresentative.Keys.ToArray();

        // Built once for the whole run, before any part row is read: it needs the run's contract
        // numbers, which are only known now. Returns the no-op instance unless a restore window is
        // configured, and every call site short-circuits on that.
        var partRestore = await ContractPartRestore.CreateAsync(
            _context, _warnings, _options, _dataAreaId, allInputAccounts, cancellationToken);

        // Chunked at the call site: a contract belongs to exactly one account, so concatenating
        // per-chunk results is the same list the unchunked call would have produced.
        var contracts = new List<DataMigration.Domain.Customers.Contract>();
        foreach (var chunk in allInputAccounts.Chunk(ChunkSize))
            contracts.AddRange(await _contractMapper.MapAsync(
                chunk, customerResult.AccountToRepresentative, partRestore, headerExemption, cancellationToken));

        var mpResult = await _mpMapper.MapAsync(allInputAccounts, partRestore, headerExemption, cancellationToken);

        // Inline each customer's contracts onto the customer record (aggregate root),
        // then compute the payer fan-out from the now-populated contracts. Payers must
        // be derived after Contracts so PayerMapper can read each contract's EAN.
        //
        // Grouped up front rather than scanned per customer: a whole-category load is
        // ~44.000 customers over ~100.000 contracts, and the per-customer scan made this
        // quadratic — measured at 66s of the extraction's runtime.
        var contractsByCustomer = contracts.ToLookup(c => c.CustomerNumber, StringComparer.Ordinal);

        var customers = customerResult.Customers.Select(customer =>
        {
            var customerContracts = contractsByCustomer[customer.AccountNumber].ToList();
            var payers = PayerMapper.Build(customer, customerContracts);
            return customer with { Contracts = customerContracts, Payers = payers };
        }).ToList();

        var representatives = customers.Select(c => c.AccountNumber).ToArray();
        var ownerClusters = await BuildOwnerClustersAsync(representatives, cancellationToken);
        var accountGroupings = await BuildAccountGroupingsAsync(customerResult.AccountToRepresentative, cancellationToken);
        // Both keyed by metering point, and a metering point hangs under one account — merging
        // per-chunk results is exact.
        var mpOrderIdentifiers = new Dictionary<string, string?>(StringComparer.Ordinal);
        var mpActiveContractTemplates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        // Chargers key on metering point and on account, both of which live under exactly one
        // account, so per-chunk results concatenate without overlap.
        var evChargersByMp = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);
        var evChargersByAccount = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);
        foreach (var chunk in allInputAccounts.Chunk(ChunkSize))
        {
            foreach (var kv in await BuildMpOrderIdentifiersAsync(chunk, partRestore, headerExemption, cancellationToken))
                mpOrderIdentifiers[kv.Key] = kv.Value;
            foreach (var kv in await BuildMpActiveContractTemplatesAsync(chunk, headerExemption, cancellationToken))
                mpActiveContractTemplates[kv.Key] = kv.Value;

            var (chargersByMp, chargersByAccount) = await BuildEvChargerLookupsAsync(chunk, partRestore, headerExemption, cancellationToken);
            foreach (var kv in chargersByMp)
                evChargersByMp[kv.Key] = evChargersByMp.TryGetValue(kv.Key, out var mpExisting)
                    ? [.. mpExisting, .. kv.Value] : kv.Value;
            foreach (var kv in chargersByAccount)
                evChargersByAccount[kv.Key] = evChargersByAccount.TryGetValue(kv.Key, out var accExisting)
                    ? [.. accExisting, .. kv.Value] : kv.Value;
        }

        // Per-MP, per-field billing override: a reference field (OrderIdentifier, AgreementIdentifier,
        // BuyerReferenceIdentifier) lands on the MP only when the owning Payer does NOT carry it
        // (because it diverges across the Payer's contracts) and the MP's own contract has a non-blank
        // value. Uniform fields stay on the Payer and the MP inherits them (null on MP). EAN is never
        // set on the MP. Settl PaymentConfiguration is therefore a partial override of the Payer.
        var meteringPoints = ApplyMpBillingOverrides(mpResult.MeteringPoints, customers);

        // Mark each MP that qualifies for a billing-fee exemption: one of its active contract
        // templates (Contracttemplatenum) ends with the configured suffix. The decision lives here
        // (single source of truth) so consumers read MeteringPoint.BillingExemptionTemplate instead
        // of re-running the suffix match against the raw template dictionary.
        meteringPoints = ApplyBillingExemptionMarker(
            meteringPoints, mpActiveContractTemplates, _options.BillingExemptionTemplateSuffix);

        return new XellentLoadResult(
            Customers: customers,
            MeteringPoints: meteringPoints,
            OwnerClusters: ownerClusters,
            JointInvoiceGroups: mpResult.JointInvoiceGroups,
            SnapshotDate: snapshotDate,
            MpOrderIdentifiers: mpOrderIdentifiers,
            AccountGroupings: accountGroupings,
            FingerprintByRepresentative: customerResult.FingerprintByRepresentative)
        {
            EvChargersByMeteringPoint = evChargersByMp,
            EvChargersByAccount = evChargersByAccount
        };
    }

    /// <summary>
    /// Per-MP override af <see cref="BuyerInvoicingReferences"/>. For hver MP findes dens contract og
    /// contract'ens owning Payer. De tre referencefelter (OrderIdentifier, AgreementIdentifier,
    /// BuyerReferenceIdentifier) behandles som et sæt: sættes på MP'en KUN når Payeren ikke bærer nogen
    /// af dem (fordi mindst ét felt divergerede på tværs af Payerens contracts) og MP'ens egen contract
    /// har en non-blank værdi for det givne felt. Bærer Payeren sættet (alle uniforme), arver MP'en
    /// alle tre (null på MP). EAN sættes aldrig på MP'en. Payere uden EAN springer over — referencefelter
    /// er OioUbl-identifikatorer der kun giver mening ved e-fakturering.
    /// </summary>
    internal static IReadOnlyList<DataMigration.Domain.MeteringPoints.MeteringPoint> ApplyMpBillingOverrides(
        IReadOnlyList<DataMigration.Domain.MeteringPoints.MeteringPoint> meteringPoints,
        IReadOnlyList<Customer> customers)
    {
        if (meteringPoints.Count == 0 || customers.Count == 0) return meteringPoints;

        // Build mpId → Contract index (an MP belongs to at most one active contract per
        // commodity; pick first encountered if more than one).
        var contractByMpId = new Dictionary<string, Contract>(StringComparer.Ordinal);
        // Build contractNum → Payer index.
        var payerByContractNum = new Dictionary<string, Payer>(StringComparer.Ordinal);

        foreach (var customer in customers)
        {
            foreach (var contract in customer.Contracts)
            {
                foreach (var assignment in contract.MeteringPoints)
                {
                    contractByMpId.TryAdd(assignment.MeteringPointId, contract);
                }
            }
            foreach (var payer in customer.Payers)
            {
                foreach (var contractNum in payer.ContractNumbers)
                {
                    payerByContractNum.TryAdd(contractNum, payer);
                }
            }
        }

        var result = new List<DataMigration.Domain.MeteringPoints.MeteringPoint>(meteringPoints.Count);
        foreach (var mp in meteringPoints)
        {
            if (!contractByMpId.TryGetValue(mp.Identification, out var contract)
                || !payerByContractNum.TryGetValue(contract.Number, out var payer))
            {
                result.Add(mp);
                continue;
            }

            // E-invoicing gate: the reference fields are OioUbl identifiers that only apply when the
            // Payer has an electronic number. A Payer without an EAN carries no references (see
            // PayerMapper.BuildPayerReferences) and must not push any down to its MPs either — the
            // fields are ignored entirely, mirroring the Payer-level rule.
            if (string.IsNullOrWhiteSpace(payer.BuyerInvoicing?.ElectronicNumber))
            {
                result.Add(mp);
                continue;
            }

            // Per-field: the MP carries each reference field that its Payer does NOT (because the
            // field diverges across the Payer's contracts), taken from the MP's own contract when
            // non-blank. Fields the Payer carries (uniform) are left null so the MP inherits them.
            // The EAN is never set on the MP — it is always inherited from the Payer (the fan-out key).
            var payerRefs = payer.BuyerInvoicing;
            // OrderIdentifier and BuyerReference of "." are Xellent placeholders that may legitimately
            // live on the Payer (uniform case) but must NEVER be pushed down as a MeteringPoint override.
            // Treat them as blank here so the MP inherits from its Payer instead. AgreementIdentifier and
            // EAN are unaffected.
            var orderId = MpFieldOrNull(payerRefs?.OrderIdentifier, WithoutDotPlaceholder(contract.BuyerInvoicing?.OrderIdentifier));
            var agreementId = MpFieldOrNull(payerRefs?.AgreementIdentifier, contract.BuyerInvoicing?.AgreementIdentifier);
            var buyerRef = MpFieldOrNull(payerRefs?.BuyerReferenceIdentifier, WithoutDotPlaceholder(contract.BuyerInvoicing?.BuyerReferenceIdentifier));

            result.Add(orderId is null && agreementId is null && buyerRef is null
                ? mp
                : mp with { BuyerInvoicing = new BuyerInvoicingReferences(null, orderId, agreementId, buyerRef) });
        }
        return result;
    }

    /// <summary>
    /// Sets <see cref="DataMigration.Domain.MeteringPoints.MeteringPoint.BillingExemptionTemplate"/>
    /// to the first of the MP's active contract templates that ends with <paramref name="suffix"/>
    /// (case-insensitive), or leaves it null when none match or no suffix is configured. Mirrors the
    /// suffix match that previously lived in the migration consumer.
    /// </summary>
    private static IReadOnlyList<DataMigration.Domain.MeteringPoints.MeteringPoint> ApplyBillingExemptionMarker(
        IReadOnlyList<DataMigration.Domain.MeteringPoints.MeteringPoint> meteringPoints,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mpActiveContractTemplates,
        string? suffix)
    {
        if (string.IsNullOrEmpty(suffix) || meteringPoints.Count == 0) return meteringPoints;

        var result = new List<DataMigration.Domain.MeteringPoints.MeteringPoint>(meteringPoints.Count);
        foreach (var mp in meteringPoints)
        {
            var matched = mpActiveContractTemplates.TryGetValue(mp.Identification, out var templates)
                ? templates.FirstOrDefault(t => t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                : null;
            result.Add(matched is null ? mp : mp with { BillingExemptionTemplate = matched });
        }
        return result;
    }

    /// <summary>The contract's value when the Payer does not carry the field (diverges) and the
    /// contract value is non-blank; otherwise null so the MP inherits the field from its Payer.</summary>
    private static string? MpFieldOrNull(string? payerValue, string? contractValue)
    {
        if (!string.IsNullOrWhiteSpace(payerValue)) return null;          // Payer carries it → inherit
        return string.IsNullOrWhiteSpace(contractValue) ? null : contractValue;
    }

    /// <summary>A lone "." is a Xellent placeholder for "no real reference". It is allowed on the Payer
    /// but must never become a MeteringPoint override, so it is treated as blank when sourcing the
    /// MP value (OrderIdentifier / BuyerReference only).</summary>
    private static string? WithoutDotPlaceholder(string? value)
        => value?.Trim() == "." ? null : value;

    /// <summary>
    /// Loads the settlement basis (afregningsgrundlag) for a single electricity metering point
    /// over a date range. Returns null when the MP has no flex-billing-history (= unknown MP
    /// or no historic data in the period).
    /// </summary>
    public Task<SettlementBasis?> LoadSettlementBasisAsync(
        string meteringPointId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken = default)
    {
        var mapper = new SettlementBasisMapper(_context, _dataAreaId);
        return mapper.MapAsync(meteringPointId, periodStart, periodEnd, cancellationToken);
    }

    /// <summary>
    /// Builds per-MP contact-person names from the active contracts' <c>Custcontactpersonid</c>.
    /// Used by use-case-2 OioUbl flow: when a customer's contracts have varying contact-person
    /// names, the per-MP name is written to <c>PaymentConfiguration.OrderIdentifier</c>. Only
    /// populated for electricity (delpoint-based); returns empty for other commodities.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string?>> BuildMpOrderIdentifiersAsync(
        string[]? allInputAccounts,
        ContractPartRestore restore,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        if (_options.Commodity != Commodity.Electricity)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var deliveryCategory = DeliveryCategoryElectricity;
        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        var includeEndedCutoff = _options.IncludeEndedCutoff?.ToDateTime(TimeOnly.MinValue);
        // Part clause only — the contract header clause below keeps the un-widened cutoff.
        var partEndCutoff = _options.IncludeEndedCutoffForParts?.ToDateTime(TimeOnly.MinValue);
        var endDateNull = EndDateNull;

        List<(string Meteringpoint, string? Cpid)> rows;

        if (restore.IsEmpty)
        {
            // Distinct in SQL: the contract→part→agreement→delpoint fan-out returned 1,3M rows to
            // build a ~48.000-key dictionary, and the duplicates were dropped client-side anyway.
            var page = await (
                from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(allInputAccounts))
                join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                    equals new { part.Contractnum, part.Dataareaid }
                join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                    equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                where contract.Dataareaid == _dataAreaId
                    && (allInputAccounts == null || allInputAccounts.Contains(contract.Custaccount))
                    && contract.Deliverycategory == deliveryCategory
                    && delpoint.Deliverycategory == deliveryCategory
                    && (part.Enddate == endDateNull || part.Enddate >= today
                        || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                select new { Meteringpoint = delpoint.Meteringpoint, Cpid = contract.Custcontactpersonid }
            ).Distinct().ToListAsync(cancellationToken);

            rows = page.Select(r => (r.Meteringpoint, (string?)r.Cpid)).ToList();
        }
        else
        {
            // Restoring changes what "an active part" means, so this query has to carry the part key
            // and re-decide in memory — which costs the narrow Distinct above, since the key columns
            // make nearly every fan-out row unique. That is why it is a second shape rather than the
            // only one: with the restore off, nobody pays for it.
            var page = await (
                from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(allInputAccounts))
                join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                    equals new { part.Contractnum, part.Dataareaid }
                join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                    equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                where contract.Dataareaid == _dataAreaId
                    && (allInputAccounts == null || allInputAccounts.Contains(contract.Custaccount))
                    && contract.Deliverycategory == deliveryCategory
                    && delpoint.Deliverycategory == deliveryCategory
                    && (part.Enddate == endDateNull || part.Enddate >= today
                        || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                select new
                {
                    Meteringpoint = delpoint.Meteringpoint,
                    Cpid = contract.Custcontactpersonid,
                    part.Contractnum,
                    part.Instagreenum,
                    part.Companyid,
                    part.Productnum,
                    part.Amrcode,
                    part.Startdate,
                    part.Enddate
                }
            ).Distinct().ToListAsync(cancellationToken);

            rows = page
                .Where(r => ContractPartInclusion.EndDateIncluded(
                    restore.RestoredEndDate(new ContractPartLogKey(
                        r.Contractnum, r.Instagreenum, deliveryCategory, r.Companyid,
                        r.Productnum, r.Amrcode, r.Startdate)) ?? r.Enddate,
                    today,
                    includeEndedCutoff))
                .Select(r => (r.Meteringpoint, (string?)r.Cpid))
                .ToList();

            // Deleted parts are unreachable from EXU_CONTRACTPARTTABLE, so the metering points behind
            // them carry no order identifier unless they are added here. That is the OioUBL invoice
            // reference — a missing one is visible on the customer's invoice, not just a null column.
            // The restore already walked contract → agreement → delivery point to resolve them, one
            // entry per delivery point, so only the contact person is left to read off the contract.
            var deletedParts = restore.DeletedPartsFor(deliveryCategory)
                .Where(p => ContractPartInclusion.EndDateIncluded(p.EndDate, today, includeEndedCutoff))
                .ToList();

            if (deletedParts.Count > 0)
            {
                var contractNums = deletedParts
                    .Select(p => p.Key.ContractNum)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                var cpidByContract = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var chunk in contractNums.Chunk(ChunkSize))
                {
                    var contracts = await ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForContracts(chunk))
                        .Where(c => c.Dataareaid == _dataAreaId
                            && chunk.Contains(c.Contractnum)
                            && c.Deliverycategory == deliveryCategory
                            && (allInputAccounts == null || allInputAccounts.Contains(c.Custaccount)))
                        .Select(c => new { c.Contractnum, c.Custcontactpersonid })
                        .ToListAsync(cancellationToken);
                    foreach (var c in contracts)
                        cpidByContract[c.Contractnum] = c.Custcontactpersonid;
                }

                // Appended last on purpose: the dictionary below is first-wins, so a metering point
                // that a live part already spoke for keeps that contract's name.
                foreach (var part in deletedParts)
                    if (cpidByContract.TryGetValue(part.Key.ContractNum, out var cpid))
                        rows.Add((part.MeteringPoint, cpid));
            }
        }

        var contactPersonIds = rows
            .Select(r => r.Cpid)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        var nameByCpid = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in contactPersonIds.Chunk(ChunkSize))
        {
            var page = await _context.ContactPersons
                .Where(cp => cp.Dataareaid == _dataAreaId && chunk.Contains(cp.Contactpersonid))
                .Select(cp => new { cp.Contactpersonid, cp.Name })
                .ToListAsync(cancellationToken);
            foreach (var cp in page)
                nameByCpid[cp.Contactpersonid] = cp.Name;
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var cpid = row.Cpid ?? "";
            string? name = null;
            if (!string.IsNullOrEmpty(cpid) && nameByCpid.TryGetValue(cpid, out var raw))
            {
                var trimmed = raw?.Trim();
                if (!string.IsNullOrEmpty(trimmed)) name = trimmed;
            }
            result.TryAdd(row.Meteringpoint, name);
        }
        return result;
    }

    /// <summary>
    /// Electricity products that carry a charger. Their contract part points — via
    /// <c>Instagreenum</c> → agreement → delpoint — at the E17 consumption metering point the charger
    /// draws from. <c>UA</c> is "uden abonnement" and describes the electricity price, not whether a
    /// subscription exists, so both products are treated the same.
    /// </summary>
    private static readonly string[] EvChargerElectricityProducts = ["V LadVariP", "V LadVarUA"];

    /// <summary>Xellent meter-point type codes that mean consumption (E17).</summary>
    private static readonly string[] EvChargerConsumptionMeterCodes = ["E17/Forb", "FORB"];

    /// <summary>Delivery categories a charger installation's own contract parts use.</summary>
    private static readonly string[] EvChargerDeliveryCategories =
        [DeliveryCategoryCharge, DeliveryCategoryChargeSubscription];

    /// <summary>
    /// Ties charger installations to the electricity metering point they charge from. Two queries:
    /// the electricity side (contract parts with a charger product → agreement → delpoint → E17
    /// metering point) and the charger side (contract parts in <c>Charge</c>/<c>Charge_Abo</c> →
    /// installation). Nothing in Xellent joins the two, so the account is the meeting point.
    ///
    /// <para>Only for electricity loads; other commodities get empty lookups. Only charger parts in
    /// force at the reference date are read — an ended part is already closed and needs no
    /// decision.</para>
    ///
    /// <para>The electricity side reads the parts as the contract-part restore sees them: with a
    /// restore on, the part predicate is widened to <see cref="MapperOptions.IncludeEndedCutoffForParts"/>,
    /// the restored end dates go back on in memory, the charger-product parts the batch deleted are
    /// added, and the ordinary date rule runs again (<see cref="RestoreEvChargerLinkRows"/>). Without
    /// that, a charger whose only electricity link the batch ended or deleted is left on the account
    /// while the contract mapper still migrates the part. The header predicate is not widened — it
    /// keeps the reference date plus the named exemptions, like every other header query. The charger
    /// side is left alone: the batch touched El-ekstern only.</para>
    ///
    /// <para>The delivery point's address — DAR id, street name through <c>EXU_STREET</c>, house
    /// number and letter — rides along on every link row, in both query shapes and on the rows
    /// synthesised for deleted parts. It feeds the address tie-break and
    /// <see cref="EvCharger.AddressMatchesMeteringPoint"/>.</para>
    /// </summary>
    internal async Task<(IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> ByMeteringPoint,
                         IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> ByAccount)>
        BuildEvChargerLookupsAsync(
            string[] accounts,
            ContractPartRestore restore,
            ContractHeaderExemption? headerExemption,
            CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);
        if (_options.Commodity != Commodity.Electricity || accounts.Length == 0)
            return (empty, empty);

        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        // Part clause only — the contract header keeps the reference date (see MapperOptions).
        var partEndCutoff = _options.IncludeEndedCutoffForParts?.ToDateTime(TimeOnly.MinValue);
        var endDateNull = EndDateNull;
        var chargerProducts = EvChargerElectricityProducts;
        var consumptionCodes = EvChargerConsumptionMeterCodes;
        var chargerCategories = EvChargerDeliveryCategories;

        IReadOnlyCollection<EvChargerElectricityLinkRow> linkRows;

        if (restore.IsEmpty)
        {
            // Distinct in SQL for the same reason as BuildMpOrderIdentifiersAsync — the
            // contract→part→agreement→delpoint fan-out returns orders of magnitude more rows than the
            // handful of (account, metering point) pairs it reduces to.
            var page = await (
                from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff: null, headerExemption?.ForAccounts(accounts))
                join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                    equals new { part.Contractnum, part.Dataareaid }
                join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                    equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                join street in _context.ExuStreets on new { delpoint.Streetnum, delpoint.Countynum, delpoint.Dataareaid }
                    equals new { street.Streetnum, street.Countynum, street.Dataareaid } into streetJoin
                from street in streetJoin.DefaultIfEmpty()
                join meterType in _context.EgsMeterpointTypecodeperiods on new { Delpointnum = delpoint.Num, delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid, delpoint.Deliverycategory }
                    equals new { meterType.Delpointnum, meterType.Attachmentnum, meterType.Dataareaid, meterType.Companyid, meterType.Deliverycategory }
                where contract.Dataareaid == _dataAreaId
                    && accounts.Contains(contract.Custaccount)
                    && contract.Deliverycategory == DeliveryCategoryElectricity
                    && delpoint.Deliverycategory == DeliveryCategoryElectricity
                    && chargerProducts.Contains(part.Productnum)
                    && meterType.Enddate == endDateNull
                    && consumptionCodes.Contains(meterType.Meterpointtypecode)
                    && (part.Enddate == endDateNull || part.Enddate >= today)
                select new
                {
                    AccountNumber = contract.Custaccount,
                    MeteringPointId = delpoint.Meteringpoint,
                    ProductCode = part.Productnum,
                    PostalCode = delpoint.Zipcodeid,
                    DarId = delpoint.Addressid,
                    StreetName = street != null ? street.Streetname : null,
                    delpoint.Addressstreet,
                    delpoint.Housenumstart,
                    delpoint.Houseletterstart
                }
            ).Distinct().ToListAsync(cancellationToken);

            linkRows = page
                .Select(r => new EvChargerElectricityLinkRow(
                    r.AccountNumber, r.MeteringPointId, r.ProductCode, r.PostalCode,
                    DelpointAddress(r.DarId, r.StreetName, r.Addressstreet, r.Housenumstart, r.Houseletterstart)))
                .ToList();
        }
        else
        {
            // Same second shape as BuildMpOrderIdentifiersAsync: the part key rides along so the
            // restored end date can be found, and the widened cutoff lets the batch-ended rows through
            // to memory, where the ordinary rule is re-run. Charger-product parts are few, so the lost
            // Distinct costs nothing here.
            var page = await (
                from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff: null, headerExemption?.ForAccounts(accounts))
                join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                    equals new { part.Contractnum, part.Dataareaid }
                join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                    equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                join street in _context.ExuStreets on new { delpoint.Streetnum, delpoint.Countynum, delpoint.Dataareaid }
                    equals new { street.Streetnum, street.Countynum, street.Dataareaid } into streetJoin
                from street in streetJoin.DefaultIfEmpty()
                join meterType in _context.EgsMeterpointTypecodeperiods on new { Delpointnum = delpoint.Num, delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid, delpoint.Deliverycategory }
                    equals new { meterType.Delpointnum, meterType.Attachmentnum, meterType.Dataareaid, meterType.Companyid, meterType.Deliverycategory }
                where contract.Dataareaid == _dataAreaId
                    && accounts.Contains(contract.Custaccount)
                    && contract.Deliverycategory == DeliveryCategoryElectricity
                    && delpoint.Deliverycategory == DeliveryCategoryElectricity
                    && chargerProducts.Contains(part.Productnum)
                    && meterType.Enddate == endDateNull
                    && consumptionCodes.Contains(meterType.Meterpointtypecode)
                    && (part.Enddate == endDateNull || part.Enddate >= today
                        || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                select new
                {
                    AccountNumber = contract.Custaccount,
                    MeteringPointId = delpoint.Meteringpoint,
                    PostalCode = delpoint.Zipcodeid,
                    DarId = delpoint.Addressid,
                    StreetName = street != null ? street.Streetname : null,
                    delpoint.Addressstreet,
                    delpoint.Housenumstart,
                    delpoint.Houseletterstart,
                    part.Contractnum,
                    part.Instagreenum,
                    part.Companyid,
                    part.Productnum,
                    part.Amrcode,
                    part.Startdate,
                    part.Enddate
                }
            ).Distinct().ToListAsync(cancellationToken);

            var liveRows = page
                .Select(r => new EvChargerLinkPartRow(
                    r.AccountNumber, r.MeteringPointId, r.PostalCode,
                    new ContractPartLogKey(
                        r.Contractnum, r.Instagreenum, DeliveryCategoryElectricity, r.Companyid,
                        r.Productnum, r.Amrcode, r.Startdate),
                    r.Enddate,
                    DelpointAddress(r.DarId, r.StreetName, r.Addressstreet, r.Housenumstart, r.Houseletterstart)))
                .ToList();

            var deletedRows = await ResolveDeletedEvChargerLinkRowsAsync(
                accounts, restore, headerExemption, today, cancellationToken);

            linkRows = RestoreEvChargerLinkRows(liveRows, deletedRows, restore, _options.ReferenceDate);
        }

        var partRows = await (
            from contract in _context.ExuContractTables
            join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { part.Contractnum, part.Dataareaid }
            join installation in _context.ExuInstallations on new { Instnum = part.Instagreenum, part.Dataareaid }
                equals new { installation.Instnum, installation.Dataareaid }
            where contract.Dataareaid == _dataAreaId
                && accounts.Contains(contract.Custaccount)
                && chargerCategories.Contains(part.Deliverycategory)
                && (part.Enddate == endDateNull || part.Enddate >= today)
            select new
            {
                AccountNumber = contract.Custaccount,
                ContractNumber = contract.Contractnum,
                InstallationNumber = installation.Instnum,
                Address = installation.Instaddressformatted,
                PostalCode = installation.Zipcodeid,
                DarId = installation.Addressid,
                DeliveryCategory = part.Deliverycategory,
                ProductCode = part.Productnum,
                StartDate = part.Startdate,
                EndDate = part.Enddate
            }
        ).Distinct().ToListAsync(cancellationToken);

        return BuildEvChargerLookups(
            linkRows,
            partRows.Select(r => new EvChargerPartRow(
                r.AccountNumber, r.ContractNumber, r.InstallationNumber, r.Address, r.PostalCode,
                r.DeliveryCategory, r.ProductCode, r.StartDate, r.EndDate, r.DarId)).ToList(),
            _options.ReferenceDate,
            _warnings);
    }

    /// <summary>
    /// The link rows for charger-product parts the batch deleted. A deleted part is not in
    /// <c>EXU_CONTRACTPARTTABLE</c>, so the live query never sees it. The restore has already resolved
    /// its metering point; this reads the rest — the account off the contract header, under the same
    /// header rule and exemption as the live query, and the postal code and E17 check off the
    /// delivery point. A part whose header is out of the window, or whose metering point is not an
    /// E17 consumption point, yields no row, exactly as it would not have in the live query.
    /// </summary>
    private async Task<IReadOnlyList<EvChargerLinkPartRow>> ResolveDeletedEvChargerLinkRowsAsync(
        string[] accounts,
        ContractPartRestore restore,
        ContractHeaderExemption? headerExemption,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var deletedParts = restore.DeletedPartsFor(DeliveryCategoryElectricity)
            .Where(p => EvChargerElectricityProducts.Contains(p.Key.ProductNum, StringComparer.Ordinal))
            .ToList();
        if (deletedParts.Count == 0) return [];

        var contractNums = deletedParts
            .Select(p => p.Key.ContractNum)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var accountByContract = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in contractNums.Chunk(ChunkSize))
        {
            var page = await ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff: null, headerExemption?.ForContracts(chunk))
                .Where(c => c.Dataareaid == _dataAreaId
                    && chunk.Contains(c.Contractnum)
                    && c.Deliverycategory == DeliveryCategoryElectricity
                    && accounts.Contains(c.Custaccount))
                .Select(c => new { c.Contractnum, c.Custaccount })
                .ToListAsync(cancellationToken);
            foreach (var c in page)
                accountByContract[c.Contractnum] = c.Custaccount;
        }

        var inScope = deletedParts.Where(p => accountByContract.ContainsKey(p.Key.ContractNum)).ToList();
        if (inScope.Count == 0) return [];

        var endDateNull = EndDateNull;
        var consumptionCodes = EvChargerConsumptionMeterCodes;
        var agreementNums = inScope
            .Select(p => p.Key.InstAgreeNum)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var delpoints = new List<(string Companyid, string Agreementnum, string Meteringpoint, string? Zipcodeid, EvChargerDelpointAddress Address)>();
        foreach (var chunk in agreementNums.Chunk(ChunkSize))
        {
            var page = await (
                from agreement in _context.ExuAgreementTables
                join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                join street in _context.ExuStreets on new { delpoint.Streetnum, delpoint.Countynum, delpoint.Dataareaid }
                    equals new { street.Streetnum, street.Countynum, street.Dataareaid } into streetJoin
                from street in streetJoin.DefaultIfEmpty()
                join meterType in _context.EgsMeterpointTypecodeperiods on new { Delpointnum = delpoint.Num, delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid, delpoint.Deliverycategory }
                    equals new { meterType.Delpointnum, meterType.Attachmentnum, meterType.Dataareaid, meterType.Companyid, meterType.Deliverycategory }
                where agreement.Dataareaid == _dataAreaId
                    && chunk.Contains(agreement.Agreementnum)
                    && delpoint.Deliverycategory == DeliveryCategoryElectricity
                    && meterType.Enddate == endDateNull
                    && consumptionCodes.Contains(meterType.Meterpointtypecode)
                select new
                {
                    agreement.Companyid,
                    agreement.Agreementnum,
                    delpoint.Meteringpoint,
                    delpoint.Zipcodeid,
                    DarId = delpoint.Addressid,
                    StreetName = street != null ? street.Streetname : null,
                    delpoint.Addressstreet,
                    delpoint.Housenumstart,
                    delpoint.Houseletterstart
                }
            ).Distinct().ToListAsync(cancellationToken);
            delpoints.AddRange(page.Select(d => (
                d.Companyid, d.Agreementnum, d.Meteringpoint, (string?)d.Zipcodeid,
                DelpointAddress(d.DarId, d.StreetName, d.Addressstreet, d.Housenumstart, d.Houseletterstart))));
        }

        // Company id folded to one casing on both sides — see ContractPartRestore.CompanyKey.
        var delpointsByAgreement = delpoints.ToLookup(d => (ContractPartRestore.CompanyKey(d.Companyid), d.Agreementnum));

        var rows = new List<EvChargerLinkPartRow>();
        foreach (var part in inScope)
        {
            var account = accountByContract[part.Key.ContractNum];
            foreach (var d in delpointsByAgreement[(ContractPartRestore.CompanyKey(part.Key.CompanyId), part.Key.InstAgreeNum)]
                         .Where(d => string.Equals(d.Meteringpoint, part.MeteringPoint, StringComparison.Ordinal)))
                rows.Add(new EvChargerLinkPartRow(account, d.Meteringpoint, d.Zipcodeid, part.Key, part.EndDate, d.Address));
        }
        return rows;
    }

    /// <summary>
    /// Applies the contract-part restore to the electricity link rows, in memory. Live rows get the
    /// end date the batch overwrote put back; the charger-product parts the batch deleted come in as
    /// <paramref name="deletedRows"/>, already resolved to account, metering point and postal code.
    /// The ordinary rule — open-ended or still running at <paramref name="referenceDate"/> — then runs
    /// over both, so a restored end date that is genuinely in the past drops its row again. No
    /// start-date cap, as in the live query: a future-dated part links its charger today.
    /// Pure, so it can be tested without a database.
    /// </summary>
    internal static IReadOnlyList<EvChargerElectricityLinkRow> RestoreEvChargerLinkRows(
        IReadOnlyCollection<EvChargerLinkPartRow> liveRows,
        IReadOnlyCollection<EvChargerLinkPartRow> deletedRows,
        ContractPartRestore restore,
        DateOnly referenceDate)
    {
        var today = referenceDate.ToDateTime(TimeOnly.MinValue);
        return liveRows
            .Select(r => restore.RestoredEndDate(r.Key) is { } restored ? r with { EndDate = restored } : r)
            .Concat(deletedRows)
            .Where(r => ContractPartInclusion.EndDateIncluded(r.EndDate, today, includeEndedCutoff: null))
            .Select(r => r.ToLinkRow())
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Groups charger contract parts into installations and decides, per account, whether they can be
    /// tied to a metering point. One candidate metering point ties them all, no postal code asked.
    /// Several candidates are told apart one installation at a time, postal code first: the
    /// installation's <c>ZIPCODEID</c> against the candidates' delpoint <c>ZIPCODEID</c>. Exactly one
    /// candidate with the same postal code ties that installation. When several share it, the address
    /// is asked next (<see cref="EvChargerAddressMatch"/>: DAR id, else street and house number), and
    /// exactly one address match ties it. Anything else — no postal-code match, or an address that
    /// does not single one out — leaves the installation on the account with a warning naming the
    /// candidates and both outcomes as counts, never the address itself. Two installations on one
    /// account can so end up on two different metering points. No candidates at all leaves the
    /// chargers on the account without a warning — there is nothing to decide between.
    ///
    /// <para>Every tied charger also gets <see cref="EvCharger.AddressMatchesMeteringPoint"/>: the same
    /// address comparison against the metering point it was tied to, whatever settled the tie. A
    /// single candidate still links as before; the flag says whether the installation is at that
    /// point's address. A charger left on the account never carries the flag.</para>
    ///
    /// <para>Parts that had already ended at <paramref name="referenceDate"/> are dropped: they are
    /// closed and call for no decision. The query filters them too — the rule is repeated here so it
    /// holds for any caller and can be tested without a database.</para>
    /// </summary>
    internal static (IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> ByMeteringPoint,
                     IReadOnlyDictionary<string, IReadOnlyList<EvCharger>> ByAccount)
        BuildEvChargerLookups(
            IReadOnlyCollection<EvChargerElectricityLinkRow> linkRows,
            IReadOnlyCollection<EvChargerPartRow> partRows,
            DateOnly referenceDate,
            IWarningCollector warnings)
    {
        var byMeteringPoint = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);
        var byAccount = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal);

        partRows = partRows
            .Where(r => ChargerPartEndDate(r.EndDate) is not { } end || end >= referenceDate)
            .ToList();
        if (partRows.Count == 0) return (byMeteringPoint, byAccount);

        // An MP reached by both charger products is still one candidate; the postal code and the
        // address are the same on every row, so the first non-empty one will do.
        var candidatesByAccount = linkRows
            .GroupBy(r => r.AccountNumber, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.MeteringPointId, StringComparer.Ordinal)
                      .Select(mp => new EvChargerCandidate(
                          mp.Key,
                          NormalizePostalCode(mp.Select(r => r.PostalCode)),
                          mp.Select(r => r.Address).FirstOrDefault(a => a is not null)))
                      .OrderBy(c => c.MeteringPointId, StringComparer.Ordinal)
                      .ToList(),
                StringComparer.Ordinal);

        var chargersByAccount = partRows
            .GroupBy(r => (r.AccountNumber, r.ContractNumber, r.InstallationNumber))
            .Select(g => new EvChargerInstallation(
                new EvCharger(
                    InstallationNumber: g.Key.InstallationNumber,
                    CustomerAccount: g.Key.AccountNumber,
                    ContractNumber: g.Key.ContractNumber,
                    Address: g.Select(r => r.Address).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim(),
                    Parts: g.Select(r => new EvChargerPart(
                            DeliveryCategory: r.DeliveryCategory,
                            Kind: ChargerPartKind(r.DeliveryCategory),
                            ProductCode: r.ProductCode,
                            StartDate: DateOnly.FromDateTime(r.StartDate),
                            EndDate: ChargerPartEndDate(r.EndDate)))
                        .OrderBy(p => p.StartDate)
                        .ThenBy(p => p.DeliveryCategory, StringComparer.Ordinal)
                        .ThenBy(p => p.ProductCode, StringComparer.Ordinal)
                        .ToList()),
                NormalizePostalCode(g.Select(r => r.PostalCode)),
                g.Select(r => r.DarId).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))?.Trim()))
            .GroupBy(c => c.Charger.CustomerAccount, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.Charger.InstallationNumber, StringComparer.Ordinal)
                      .ThenBy(c => c.Charger.ContractNumber, StringComparer.Ordinal)
                      .ToList(),
                StringComparer.Ordinal);

        void Link(string mpId, IReadOnlyList<EvCharger> chargers) =>
            byMeteringPoint[mpId] = byMeteringPoint.TryGetValue(mpId, out var existing)
                ? [.. existing, .. chargers]
                : chargers;

        void LeaveOnAccount(string account, IReadOnlyList<EvCharger> chargers) =>
            byAccount[account] = byAccount.TryGetValue(account, out var already)
                ? [.. already, .. chargers]
                : chargers;

        foreach (var (account, chargers) in chargersByAccount.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            candidatesByAccount.TryGetValue(account, out var candidates);

            if (candidates is { Count: 1 })
            {
                Link(candidates[0].MeteringPointId, chargers.Select(c => c.TiedTo(candidates[0])).ToList());
                continue;
            }

            if (candidates is not { Count: > 1 })
            {
                LeaveOnAccount(account, chargers.Select(c => c.Charger).ToList());
                continue;
            }

            // Several candidates: let each installation's postal code pick among them, and its address
            // when the postal code leaves more than one.
            var unlinked = new List<EvCharger>();
            var reasons = new List<string>();
            foreach (var installation in chargers)
            {
                var (charger, postalCode, _) = installation;
                var byPostalCode = postalCode is null
                    ? []
                    : candidates.Where(c => string.Equals(c.PostalCode, postalCode, StringComparison.Ordinal)).ToList();

                if (byPostalCode.Count == 1)
                {
                    Link(byPostalCode[0].MeteringPointId, [installation.TiedTo(byPostalCode[0])]);
                    continue;
                }

                var byAddress = byPostalCode.Count > 1
                    ? byPostalCode.Where(installation.IsAt).ToList()
                    : [];

                if (byAddress.Count == 1)
                {
                    Link(byAddress[0].MeteringPointId, [installation.TiedTo(byAddress[0])]);
                    continue;
                }

                unlinked.Add(charger);
                reasons.Add(postalCode is null
                    ? $"installation {charger.InstallationNumber} has no postal code"
                    : byPostalCode.Count > 1
                        ? $"installation {charger.InstallationNumber} (postal code {postalCode}) matches {byPostalCode.Count}, address matches {byAddress.Count}/{byPostalCode.Count}"
                        : $"installation {charger.InstallationNumber} (postal code {postalCode}) matches {byPostalCode.Count}");
            }

            if (unlinked.Count == 0) continue;

            LeaveOnAccount(account, unlinked);
            warnings.AddWarning(
                $"Account {account}: {unlinked.Count} charger installation(s) left unlinked — the account's " +
                $"charger electricity products point at {candidates.Count} consumption metering points " +
                $"({string.Join(", ", candidates.Select(c => $"{c.MeteringPointId} [{c.PostalCode ?? "no postal code"}]"))}), " +
                $"picking one would be a guess, and neither the postal-code tie-break nor the address settled it " +
                $"({string.Join("; ", reasons)}) " +
                "[Xellent: EXU_CONTRACTPARTTABLE PRODUCTNUM V LadVariP/V LadVarUA; EXU_INSTALLATION.ZIPCODEID vs EXU_DELPOINT.ZIPCODEID; " +
                "EXU_INSTALLATION.ADDRESSID/INSTADDRESSFORMATTED vs EXU_DELPOINT.ADDRESSID/EXU_STREET.STREETNAME+HOUSENUMSTART]");
        }

        return (byMeteringPoint, byAccount);
    }

    /// <summary>A consumption metering point an account's charger electricity products point at.</summary>
    private sealed record EvChargerCandidate(string MeteringPointId, string? PostalCode, EvChargerDelpointAddress? Address);

    /// <summary>
    /// A charger installation with what the tie-break compares beside the public record: its postal
    /// code and DAR id. The formatted address is public already, as <see cref="EvCharger.Address"/>.
    /// </summary>
    private sealed record EvChargerInstallation(EvCharger Charger, string? PostalCode, string? DarId)
    {
        public bool IsAt(EvChargerCandidate candidate) =>
            EvChargerAddressMatch.Matches(DarId, Charger.Address, candidate.Address);

        /// <summary>The public charger tied to <paramref name="candidate"/>, with the address flag computed.</summary>
        public EvCharger TiedTo(EvChargerCandidate candidate) =>
            Charger with { AddressMatchesMeteringPoint = IsAt(candidate) };
    }

    /// <summary>First non-blank postal code, trimmed; null when there is none. Xellent stores blanks as empty strings.</summary>
    private static string? NormalizePostalCode(IEnumerable<string?> postalCodes) =>
        postalCodes.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim();

    /// <summary>
    /// The delpoint address as one value: the street name off <c>EXU_STREET</c> when the street code
    /// resolves, else the delpoint's own <c>ADDRESSSTREET</c> (blank on every delivery point seen so far).
    /// </summary>
    private static EvChargerDelpointAddress DelpointAddress(
        string? darId, string? streetName, string? addressStreet, int? houseNumber, string? houseLetter) =>
        new(darId, !string.IsNullOrWhiteSpace(streetName) ? streetName : addressStreet, houseNumber, houseLetter);

    private static EvChargerPartKind ChargerPartKind(string deliveryCategory) =>
        string.Equals(deliveryCategory, DeliveryCategoryChargeSubscription, StringComparison.OrdinalIgnoreCase)
            ? EvChargerPartKind.Subscription
            : string.Equals(deliveryCategory, DeliveryCategoryCharge, StringComparison.OrdinalIgnoreCase)
                ? EvChargerPartKind.Consumption
                : throw new InvalidOperationException($"Not a charger delivery category: '{deliveryCategory}'");

    /// <summary>Xellent's two "no end date" sentinels become null; anything else is a real end date.</summary>
    private static DateOnly? ChargerPartEndDate(DateTime endDate) =>
        endDate == EndDateNull || endDate == EndDateFuture ? null : DateOnly.FromDateTime(endDate);

    /// <summary>
    /// Builds per-MP active contract templates. Used by the create-flow to detect billing
    /// exemptions (e.g. <c>*_Erh_UG</c> templates). Only contracts with a non-empty
    /// <c>Contracttemplatenum</c> and an active end-date are included.
    /// </summary>
    /// <param name="headerExemption">
    /// Lets contract headers the batch end-dated back in, by name. Needed here as much as anywhere:
    /// a contract that stays filtered out here loses its template, and with it the billing-exemption
    /// marker the create-flow reads off the metering point.
    /// </param>
    internal async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> BuildMpActiveContractTemplatesAsync(
        string[]? allInputAccounts,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        var endDateNull = EndDateNull;

        if (_options.Commodity != Commodity.Electricity)
        {
            var deliveryCategory = _options.Commodity switch
            {
                Commodity.Water => DeliveryCategoryWater,
                Commodity.Heat => DeliveryCategoryHeating,
                Commodity.EvCharging => DeliveryCategoryCharge,
                _ => throw new InvalidOperationException($"Unsupported commodity: {_options.Commodity}")
            };

            var instRows = await (
                from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff: null, headerExemption?.ForAccounts(allInputAccounts))
                join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                    equals new { part.Contractnum, part.Dataareaid }
                join installation in _context.ExuInstallations on new { Instnum = part.Instagreenum, part.Dataareaid }
                    equals new { installation.Instnum, installation.Dataareaid }
                where contract.Dataareaid == _dataAreaId
                    && (allInputAccounts == null || allInputAccounts.Contains(contract.Custaccount))
                    && part.Deliverycategory == deliveryCategory
                    && !string.IsNullOrEmpty(contract.Contracttemplatenum)
                select new { Instnum = installation.Instnum, Template = contract.Contracttemplatenum }
            ).Distinct().ToListAsync(cancellationToken);

            return instRows
                .GroupBy(r => $"V{r.Instnum}01", StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)g.Select(x => x.Template).Distinct().ToList(),
                    StringComparer.Ordinal);
        }

        // Distinct in SQL for the same reason as BuildMpOrderIdentifiersAsync — the GroupBy below
        // already reduces to distinct (meteringpoint, template) pairs.
        var elRows = await (
            from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff: null, headerExemption?.ForAccounts(allInputAccounts))
            join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { part.Contractnum, part.Dataareaid }
            join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
            where contract.Dataareaid == _dataAreaId
                && (allInputAccounts == null || allInputAccounts.Contains(contract.Custaccount))
                && contract.Deliverycategory == DeliveryCategoryElectricity
                && delpoint.Deliverycategory == DeliveryCategoryElectricity
                && !string.IsNullOrEmpty(contract.Contracttemplatenum)
            select new { Meteringpoint = delpoint.Meteringpoint, Template = contract.Contracttemplatenum }
        ).Distinct().ToListAsync(cancellationToken);

        return elRows
            .GroupBy(r => r.Meteringpoint, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(x => x.Template).Distinct().ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds <see cref="AccountGrouping"/> list from the resolved representative map.
    /// One entry per representative; members are all accounts that merged into it.
    /// </summary>
    private async Task<IReadOnlyList<AccountGrouping>> BuildAccountGroupingsAsync(
        IReadOnlyDictionary<string, string> accountToRepresentative,
        CancellationToken cancellationToken)
    {
        var accounts = accountToRepresentative.Keys.ToArray();
        if (accounts.Length == 0) return [];

        var rows = new List<(string Accountnum, string Name, string GroupNum, string Cvr)>();
        foreach (var chunk in accounts.Chunk(ChunkSize))
        {
            var page = await _context.CustTables
                .Where(c => c.Dataareaid == _dataAreaId && chunk.Contains(c.Accountnum))
                .Select(c => new { c.Accountnum, Name = c.Name ?? string.Empty, GroupNum = c.ExuTechnicgroup4, Cvr = c.ExuCvrDisponent1 })
                .ToListAsync(cancellationToken);
            rows.AddRange(page.Select(r => (r.Accountnum, r.Name, r.GroupNum, r.Cvr)));
        }

        var groupNumbers = rows
            .Select(r => r.GroupNum)
            .Where(g => !string.IsNullOrEmpty(g))
            .Distinct()
            .ToList();

        IReadOnlyDictionary<string, string?> groupNames;
        if (groupNumbers.Count > 0)
        {
            var nameRows = await _context.ExuGroupTables
                .Where(g => g.Dataareaid == _dataAreaId && groupNumbers.Contains(g.Groupnum))
                .Select(g => new { g.Groupnum, g.Groupname })
                .ToListAsync(cancellationToken);
            groupNames = nameRows
                .GroupBy(g => g.Groupnum, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Groupname, StringComparer.Ordinal);
        }
        else
        {
            groupNames = new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        var rowByAccount = rows.ToDictionary(r => r.Accountnum, StringComparer.Ordinal);

        return accountToRepresentative
            .GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .Select(g =>
            {
                var members = g.Select(kv => kv.Key)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                var hasRepRow = rowByAccount.TryGetValue(g.Key, out var repRow);
                var groupNum = hasRepRow ? repRow.GroupNum : null;
                if (string.IsNullOrEmpty(groupNum)) groupNum = null;
                var groupName = groupNum != null && groupNames.TryGetValue(groupNum, out var gn) ? gn : null;
                var accountNames = members
                    .Where(rowByAccount.ContainsKey)
                    .ToDictionary(a => a, a => rowByAccount[a].Name, StringComparer.Ordinal);
                return new AccountGrouping(
                    RepresentativeAccount: g.Key,
                    MemberAccounts: members,
                    GroupNum: groupNum,
                    GroupName: groupName,
                    Cvr: hasRepRow ? repRow.Cvr : null,
                    AccountNames: accountNames);
            })
            .OrderBy(g => g.RepresentativeAccount, StringComparer.Ordinal)
            .ToList<AccountGrouping>();
    }

    /// <summary>
    /// Builds <see cref="OwnerCluster"/>s from TG4 groupings: one cluster per TG4 where ≥2
    /// representatives share it. TG4-grouping is independent of merge mode and always runs.
    /// TG4 values in <see cref="MapperOptions.IgnoredGroupNumbers"/> never form a cluster.
    /// </summary>
    private async Task<IReadOnlyList<OwnerCluster>> BuildOwnerClustersAsync(
        string[] representatives,
        CancellationToken cancellationToken)
    {
        if (representatives.Length < 2) return [];

        // TG4/kundegruppe sammenlignes case-insensitivt (matcher AccountRepresentativeResolver).
        var ignored = _options.IgnoredGroupNumbers is { Count: > 0 }
            ? new HashSet<string>(_options.IgnoredGroupNumbers, StringComparer.OrdinalIgnoreCase)
            : null;

        var rows = new List<(string Accountnum, string Tg4, string GroupName)>();
        foreach (var chunk in representatives.Chunk(ChunkSize))
        {
            var page = await (
                from cust in _context.CustTables
                join grp in _context.ExuGroupTables
                    on new { Groupnum = cust.ExuTechnicgroup4!, cust.Dataareaid }
                    equals new { grp.Groupnum, grp.Dataareaid } into grpJoin
                from grp in grpJoin.DefaultIfEmpty()
                where cust.Dataareaid == _dataAreaId
                    && chunk.Contains(cust.Accountnum)
                    && cust.ExuTechnicgroup4 != null && cust.ExuTechnicgroup4 != ""
                select new
                {
                    cust.Accountnum,
                    Tg4 = cust.ExuTechnicgroup4!,
                    GroupName = grp != null ? grp.Groupname : null
                }
            ).ToListAsync(cancellationToken);
            rows.AddRange(page.Select(r => (r.Accountnum, r.Tg4, r.GroupName)));
        }

        return rows
            .Where(r => ignored == null || !ignored.Contains(r.Tg4))
            .GroupBy(r => r.Tg4, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(r => r.Accountnum).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(g =>
            {
                var members = g.Select(r => r.Accountnum)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();
                var groupName = g.Select(r => r.GroupName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
                return new OwnerCluster(
                    GroupCode: g.Key,
                    OwnerName: groupName ?? g.Key,
                    MemberAccountNumbers: members,
                    RepresentativeAccountNumber: members[0]);
            })
            .OrderBy(c => c.GroupCode, StringComparer.Ordinal)
            .ToList();
    }
}
