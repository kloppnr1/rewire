using System.Text.Json;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.SettlClient.Internal;

namespace DataMigration.SettlClient;

/// <summary>
/// Implements <see cref="ISettlClient"/> by translating Domain types into Settl API
/// mutations. Mirrors the six-step pipeline of <c>CustomerBundleMigrator</c>:
/// customer → payer → metering-points → customer-MP links → payer-MP links → contract-MP links.
///
/// <para>Grid UUID resolution (MarketBalanceArea / MeteringGridArea) is performed lazily on
/// the first non-dry-run call that encounters electricity MPs.</para>
/// </summary>
public sealed class SettlClient : ISettlClient
{
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlPayerClient _payerClient;
    private readonly ISettlMeteringPointClient _mpClient;
    private readonly ISettlContractClient _contractClient;
    private readonly ISettlSettlementGroupClient _settlementGroupClient;
    private readonly string _deliveryCategory;
    private readonly Action<string> _logLine;
    private readonly int _searchRetryDelayMs;

    private readonly Dictionary<string, Guid> _settlementGroupIdByName = new(StringComparer.OrdinalIgnoreCase);
    // Customer-based membership cache. Verdo: SettlementGroups are keyed by CustomerId, not
    // by MeteringPointId — Settl rejects MP-based membership in practice.
    private readonly Dictionary<Guid, HashSet<Guid>> _settlementGroupCustomerMembers = [];

    // Shared contract-MP cache across MigrateAsync calls (same contract may serve multiple customers).
    private readonly Dictionary<Guid, HashSet<Guid>> _contractMpCache = [];

    // Grid UUID maps, loaded lazily before first electricity MP create.
    private Dictionary<string, Guid>? _marketBalanceAreaMap;
    private Dictionary<string, Guid>? _gridAreaMap;

    // Electricity MPs resolved by PrecreateMeteringPointsAsync, keyed by identification.
    // MigrateAsync reads its GUIDs from here instead of creating the MP itself.
    private readonly Dictionary<string, PrecreatedMeteringPoint> _precreatedMps = new(StringComparer.Ordinal);

    // Created up front, but Settl had not surfaced them when the read-back gave up. The customer
    // loop searches these again — it must never re-create them, or Settl gets a duplicate.
    private readonly HashSet<string> _precreateUnresolved = new(StringComparer.Ordinal);

    public SettlClient(
        ISettlCustomerClient customerClient,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient mpClient,
        ISettlContractClient contractClient,
        ISettlSettlementGroupClient settlementGroupClient,
        string deliveryCategory,
        Action<string> logLine,
        // Pause between search attempts when waiting for Settl to surface a just-created MP.
        // A test seam: production leaves it at one second, tests set it to 0 so the wait is real
        // behaviour but costs nothing.
        int searchRetryDelayMs = 1000)
    {
        _searchRetryDelayMs = searchRetryDelayMs;
        _customerClient = customerClient;
        _payerClient = payerClient;
        _mpClient = mpClient;
        _contractClient = contractClient;
        _settlementGroupClient = settlementGroupClient;
        _deliveryCategory = deliveryCategory;
        _logLine = logLine;

        _mpClient.SetDeliveryCategory(deliveryCategory);
    }

    public async Task<SettlMigrationResult> MigrateAsync(
        Customer customer,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        SettlMigrationContext context,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var payerIds = new Dictionary<string, Guid>();
        var mpGuids = new Dictionary<string, Guid>();
        int customerLinks = 0, payerLinks = 0, contractLinks = 0;
        int customerMpsFailed = 0, payerMpsFailed = 0, contractMpsFailed = 0;
        int payersCreated = 0, payersExisting = 0;
        int mpsCreated = 0, mpsExisting = 0, mpsFailed = 0;
        var existingPayerIdentifications = new List<string>();
        var existingMpIdentifications = new List<string>();

        // Collect all unique MPs for this customer across all contracts.
        var customerMpIds = customer.Contracts
            .SelectMany(c => c.MeteringPoints.Select(a => a.MeteringPointId))
            .Distinct()
            .ToList();

        // Derive earliest start per MP (used as MeteringPointDto section start dates).
        var earliestStartByMpId = customer.Contracts
            .SelectMany(c => c.MeteringPoints)
            .GroupBy(a => a.MeteringPointId)
            .ToDictionary(g => g.Key, g => g.Min(a => a.Start));

        var customerDomainMps = customerMpIds
            .Where(meteringPoints.ContainsKey)
            .Select(id => meteringPoints[id])
            .ToList();

        // Commodity type — derive from first MP (all MPs in a bundle share commodity).
        var commodityType = customerDomainMps.FirstOrDefault()?.Commodity is { } c
            ? MapCommodityType(c)
            : "Electricity";

        // === Step 1: Customer ===
        // Caller pre-applies any env-specific CustomerNumber normalisation (fx pad-9) før
        // MigrateAsync; SettlClient er agnostisk omkring padding-strategi og bruger
        // customerDto.CustomerNumber som den er.
        var customerDto = BuildCustomerDto(customer, context);

        var (existingCustomer, _) = await _customerClient.SearchByCustomerNumberAsync(
            customerDto.CustomerNumber!, cancellationToken: cancellationToken);

        // Legacy-customer fallback: pad-9 rule was introduced after some customers were created
        // in production-Settl with their raw un-padded CustomerNumber. When primary lookup
        // misses, retry with the original un-padded alias (if caller provided one).
        string? legacyCustomerNum = null;
        if (existingCustomer == null
            && context.LegacyCustomerNumbers != null
            && context.LegacyCustomerNumbers.TryGetValue(customerDto.CustomerNumber!, out var legacyCustNum)
            && !string.Equals(legacyCustNum, customerDto.CustomerNumber, StringComparison.Ordinal))
        {
            var (legacyResult, _) = await _customerClient.SearchByCustomerNumberAsync(legacyCustNum, cancellationToken: cancellationToken);
            if (legacyResult != null)
            {
                existingCustomer = legacyResult;
                legacyCustomerNum = legacyCustNum;
            }
        }

        Guid customerGuid;
        bool customerCreated;
        if (existingCustomer != null)
        {
            customerGuid = existingCustomer.Id;
            customerDto.Id = existingCustomer.Id;
            customerCreated = false;
            if (legacyCustomerNum != null)
                _logLine($"  Customer {customer.AccountNumber} already exists (legacy num {legacyCustomerNum}; ID: {existingCustomer.Id})");
            else
                _logLine($"  Customer {customer.AccountNumber} already exists (ID: {existingCustomer.Id})");
        }
        else if (context.DryRun)
        {
            customerGuid = customerDto.Id;
            customerCreated = true;
            _logLine($"  [dry-run] Would create customer {customer.AccountNumber}");
        }
        else
        {
            var request = new CreateCustomersRequest { Customers = [customerDto] };
            var (success, error) = await _customerClient.CreateAsync(request, cancellationToken);
            if (!success)
                return Fail(errors, $"Customer {customer.AccountNumber} create failed: {error}",
                    payerIds, mpGuids, customerLinks, payerLinks, contractLinks, warnings);

            customerGuid = customerDto.Id;
            customerCreated = true;
            _logLine($"  Created customer {customer.AccountNumber}");
        }

        // === Step 2: Payers — 1:1 fan-out from Domain.Customer.Payers ===
        // PayerMapper (Domain) owns the fan-out logic (which contracts → which payer).
        // Here we just translate each Domain Payer to a Settl DTO.
        var payerDtos = customer.Payers
            .Select(p => PayerTranslator.Build(customer, p, customerGuid))
            .ToList();

        // Settl's InvoiceChannel enum has no MobilePay value (PaymentChannel does). The
        // translator downgrades it to PDF (payer has an email) or PBS_BS (no email) — surface
        // the channel actually rendered so the loss is visible. payerDtos is index-aligned
        // with customer.Payers.
        for (var i = 0; i < customer.Payers.Count; i++)
        {
            var p = customer.Payers[i];
            if (p.InvoiceChannel != InvoiceChannel.MobilePay) continue;
            warnings.Add(
                $"Customer {customer.AccountNumber} payer {p.Identifier}: MobilePay is not a valid Settl InvoiceChannel — " +
                $"rendered as {payerDtos[i].PayerBilling!.InvoiceChannel} (PaymentChannel still MobilePay).");
        }
        var defaultPayer = customer.Payers.FirstOrDefault(p => p.IsPrimary) ?? customer.Payers[0];
        var defaultPayerIdentification = defaultPayer.Identifier;
        // ContractNumber → PayerIdentifier; pre-built so Step 5 is O(1) per MP.
        var payerIdentifierByContract = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in customer.Payers)
        {
            foreach (var contractNumber in p.ContractNumbers)
                payerIdentifierByContract[contractNumber] = p.Identifier;
        }

        foreach (var payerDto in payerDtos)
        {
            var payerIdentification = payerDto.PayerInformation?.PayerIdentifier ?? customerDto.CustomerNumber!;
            var (existingPayer, _) = await _payerClient.SearchByPayerIdentificationAsync(
                payerIdentification, cancellationToken: cancellationToken);

            // Legacy-payer fallback: pad-9 rule was introduced after some payers were created
            // in production-Settl with their raw un-padded identifier. When primary lookup
            // misses, retry with the original un-padded alias (if caller provided one).
            string? legacyHitId = null;
            if (existingPayer == null
                && context.LegacyPayerIdentifiers != null
                && context.LegacyPayerIdentifiers.TryGetValue(payerIdentification, out var legacyId)
                && !string.Equals(legacyId, payerIdentification, StringComparison.Ordinal))
            {
                var (legacyResult, _) = await _payerClient.SearchByPayerIdentificationAsync(
                    legacyId, cancellationToken: cancellationToken);
                if (legacyResult != null)
                {
                    existingPayer = legacyResult;
                    legacyHitId = legacyId;
                    _logLine($"  Payer {payerIdentification} already exists (legacy id {legacyId} — not re-padded; ID: {legacyResult.Id})");
                }
            }

            if (existingPayer != null)
            {
                payerIds[payerIdentification] = existingPayer.Id;
                payersExisting++;
                // Append BOTH the padded form (which CreateRunner/dashboard sees on the payer DTO) and
                // the legacy form (when fallback hit) — så consumer kan match'e direkte mod den form
                // de selv kender.
                existingPayerIdentifications.Add(payerIdentification);
                if (legacyHitId != null) existingPayerIdentifications.Add(legacyHitId);
                if (legacyHitId == null)
                    _logLine($"  Payer {payerIdentification} already exists (ID: {existingPayer.Id})");
            }
            else if (context.DryRun)
            {
                payerIds[payerIdentification] = payerDto.Id;
                payersCreated++;
                _logLine($"  [dry-run] Would create payer {payerIdentification}");
            }
            else
            {
                var request = new CreatePayersRequest { Payers = [payerDto] };
                var (success, error) = await _payerClient.CreateAsync(request, cancellationToken);
                if (!success)
                    return Fail(errors, $"Payer {payerIdentification} create failed: {error}",
                        payerIds, mpGuids, customerLinks, payerLinks, contractLinks, warnings);

                payerIds[payerIdentification] = payerDto.Id;
                payersCreated++;
                _logLine($"  Created payer {payerIdentification}");
            }
        }

        // === Step 3: Metering Points ===
        var isElectricity = customerDomainMps.Any(mp => mp.Grid != null);

        if (isElectricity && !context.DryRun && _marketBalanceAreaMap == null)
            await EnsureGridMapsAsync(cancellationToken);

        // Per-MP billing overrides: Domain populates mp.ElectronicNumber/OrderIdentifier/
        // AgreementIdentifier/BuyerReferenceIdentifier when MP's contract differs from its Payer's billing-block.
        // Otherwise the fields are null and MP inherits the Payer's values implicitly.
        var paymentConfigStart = DateOnly.FromDateTime(context.ChangeOfSupplierDate);

        // Track MPs created in this run — billing config and exemptions are only applied to
        // freshly created MPs. Existing MPs are never mutated by create (idempotent reads only).
        var newMpIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mp in customerDomainMps)
        {
            var mpId = mp.Identification;
            var start = earliestStartByMpId.TryGetValue(mpId, out var s) ? s : DateOnly.FromDateTime(DateTime.Today);
            var mpDto = MeteringPointTranslator.Build(mp, customer);

            if (mp.Grid != null)
                ApplyGridUuids(mpDto, _marketBalanceAreaMap, _gridAreaMap);

            Guid? mpGuid = null;

            // Give an MP the batch phase created but could not read back one more chance — by now
            // more time has passed, so Settl has usually caught up.
            if (mp.Grid != null && _precreateUnresolved.Contains(mpId))
            {
                var late = await RetrySearchAsync(mpId, cancellationToken);
                if (late.HasValue)
                {
                    _precreatedMps[mpId] = new PrecreatedMeteringPoint(late.Value, alreadyExisted: false);
                    _precreateUnresolved.Remove(mpId);
                }
            }

            if (mp.Grid != null && TryTakePrecreated(mpId, out var preGuid, out var preIsExisting))
            {
                // Created (or found) by the up-front batch phase — no search, no create here.
                // Counting is unchanged: the first customer to claim a freshly created MP counts it
                // as created, any later customer sharing the same MP counts it as existing, exactly
                // as the per-customer search-or-create did.
                mpGuid = preGuid;
                if (preIsExisting)
                {
                    mpsExisting++;
                    existingMpIdentifications.Add(mpId);
                    _logLine($"  MP {mpId} already exists (ID: {preGuid})");
                }
                else
                {
                    newMpIds.Add(mpId);
                    mpsCreated++;
                    _logLine($"  Created MP {mpId} (batch; ID: {preGuid})");
                }
            }
            else if (mp.Grid != null && _precreateUnresolved.Contains(mpId))
            {
                // The batch phase did create it, Settl just had not surfaced it yet. Search again —
                // never create, that would duplicate it.
                warnings.Add($"MP {mpId} created but not found in search after retries");
                _logLine($"  WARNING: MP {mpId} created in batch phase but still not searchable");
            }
            else if (mp.Grid != null)
            {
                // Electricity: search market v3
                var (existing, _) = await _mpClient.SearchByIdentificationAsync(mpId, cancellationToken);
                if (existing != null)
                {
                    mpGuid = existing.Id;
                    mpsExisting++;
                    existingMpIdentifications.Add(mpId);
                    _logLine($"  MP {mpId} already exists (ID: {existing.Id})");
                }
                else if (context.DryRun)
                {
                    mpGuid = mpDto.Id;
                    newMpIds.Add(mpId);
                    mpsCreated++;
                    _logLine($"  [dry-run] Would create MP {mpId}");
                }
                else
                {
                    // v4 create: Settl removed the v3 POST route entirely (404). The DAR
                    // flags are per-run, so they are stamped on the request here — the translator
                    // stays a pure domain→DTO mapper.
                    var request = new CreateMeteringPointsV4Request
                    {
                        MeteringPointCreateObjectDtos =
                        [
                            MeteringPointCreateObjectV4Dto.FromMeteringPoint(
                                mpDto, context.EnrichAddress, context.SkipAddressValidation)
                        ]
                    };
                    var (success, error) = await _mpClient.CreateV4Async(request, cancellationToken);
                    if (!success)
                    {
                        errors.Add($"MP {mpId} create failed: {error}");
                        _logLine($"  FAILED: MP {mpId} create failed: {error}");
                        mpsFailed++;
                        continue;
                    }

                    _logLine($"  Created MP {mpId}");
                    mpGuid = await RetrySearchAsync(mpId, cancellationToken);
                    if (!mpGuid.HasValue)
                        warnings.Add($"MP {mpId} created but not found in search after retries");
                    else
                    {
                        newMpIds.Add(mpId);
                        mpsCreated++;
                    }
                }
            }
            else
            {
                // Water/heat: must be pre-populated externally — search only.
                var (existing, _) = await _mpClient.SearchWaterByIdentificationAsync(mpId, cancellationToken);
                if (existing != null)
                {
                    mpGuid = existing.Id;
                    mpsExisting++;
                    existingMpIdentifications.Add(mpId);
                    _logLine($"  MP {mpId} (water) already exists (ID: {existing.Id})");
                }
                else if (context.Offline)
                {
                    // Offline dry-run: no real Settl to confirm pre-population. Report would-create
                    // instead of erroring (the "must be pre-populated" check only holds online).
                    mpGuid = mpDto.Id;
                    newMpIds.Add(mpId);
                    mpsCreated++;
                    _logLine($"  [offline] Would create MP {mpId} (water/heat)");
                }
                else
                {
                    var msg = $"MP {mpId}: not found in Settl — water/heat MPs must be pre-populated before migration";
                    errors.Add(msg);
                    _logLine($"  ERROR: {msg}");
                    mpsFailed++;
                }
            }

            if (!mpGuid.HasValue) continue;
            mpGuids[mpId] = mpGuid.Value;

            // Billing config for every electricity MP, new or existing. EnsureBillingConfigAsync
            // is idempotent (reads current, POSTs only when Currency/JointInvoice/JointInvoiceNumber
            // or a payment override actually differs), so re-running create backfills the billing
            // config on MPs that pre-existed in Settl — the migration's backfill mechanism now that
            // the fix-* runners are gone. This is also what keeps the live POST symmetric with the
            // create-true capture (CapturePlanAsync builds BillingConfiguration unconditionally);
            // gating the live path on newMpIds left reconcile flagging every pre-existing joined MP
            // as JointInvoice true→false drift create could never close.
            //
            // The read is only worth paying for on an MP that pre-existed in Settl — that is the
            // backfill case above. For an MP this run just created (newMpIds), the read can only
            // answer "not configured", so it is skipped and the config written straight away.
            if (mp.Grid != null && !context.DryRun)
            {
                await EnsureBillingConfigAsync(mpGuid.Value, mpId, start, mp.JointInvoiceGroupId,
                    mp.BuyerInvoicing?.ElectronicNumber, mp.BuyerInvoicing?.OrderIdentifier, mp.BuyerInvoicing?.AgreementIdentifier,
                    mp.BuyerInvoicing?.BuyerReferenceIdentifier,
                    paymentConfigStart, newMpIds.Contains(mpId), errors, cancellationToken);
            }
        }

        // === Step 3b: Billing exemptions — every MP, new or existing ===
        // The qualify-decision lives in the domain: mp.BillingExemptionTemplate is non-null when the
        // MP is on a fee-exempt contract template (resolved by XellentDomainMapper). SettlClient only
        // applies the Settl-side exemption (ProductElementId @ %); it no longer matches suffixes.
        // ApplyBillingExemptionsAsync is idempotent (GETs current, skips if the ProductElementId
        // exemption already exists, and only touches MPs the domain flagged), so running it over ALL
        // MPs backfills the exemption on MPs that pre-existed in Settl at create-time — same symmetry
        // fix as the billing-config gate above: create-true capture flags the exemption for every
        // qualifying MP, so gating the live path on newMpIds left reconcile flagging the missing
        // exemption on every pre-existing MP as drift create could never close.
        int exemptionsApplied = 0, exemptionsSkipped = 0, exemptionsFailed = 0;
        if (context.ExemptionContext != null && mpGuids.Count > 0)
        {
            var exemptTemplateByMpId = customerDomainMps
                .ToDictionary(m => m.Identification, m => m.BillingExemptionTemplate, StringComparer.Ordinal);
            (exemptionsApplied, exemptionsSkipped, exemptionsFailed) = await ApplyBillingExemptionsAsync(
                mpGuids, context.ExemptionContext, exemptTemplateByMpId,
                context.DryRun, errors, cancellationToken);
        }

        // === Step 4: Customer → MP links ===
        var (existingCustMps, _) = await _customerClient.GetMeteringPointsAsync(customerGuid, cancellationToken);
        var existingCustMpIds = existingCustMps?.Select(m => m.MeteringPointId).ToHashSet() ?? [];

        var customerMpDtos = new List<CustomerMeteringPointDto>();
        foreach (var (mpId, mpGuid) in mpGuids)
        {
            if (existingCustMpIds.Contains(mpGuid)) continue;
            var start = earliestStartByMpId.TryGetValue(mpId, out var s) ? s : DateOnly.FromDateTime(DateTime.Today);
            customerMpDtos.Add(BuildCustomerLinkDto(mpId, mpGuid, commodityType, start));
        }

        if (customerMpDtos.Count > 0)
        {
            if (context.DryRun)
            {
                _logLine($"  [dry-run] Customer {customer.AccountNumber}: would link {customerMpDtos.Count} MPs");
                customerLinks += customerMpDtos.Count;
            }
            else
            {
                var req = new AddCustomerMeteringPointsRequest { CustomerMeteringPoints = customerMpDtos };
                var (success, error) = await _customerClient.AddMeteringPointsAsync(customerGuid, req, cancellationToken);
                if (!success)
                {
                    errors.Add($"Customer {customer.AccountNumber} MP linking failed: {error}");
                    _logLine($"  FAILED: Customer MP linking: {error}");
                    customerMpsFailed += customerMpDtos.Count;
                }
                else
                {
                    _logLine($"  Customer {customer.AccountNumber}: linked {customerMpDtos.Count} MPs");
                    customerLinks += customerMpDtos.Count;
                }
            }
        }

        // === Step 5: Payer → MP links ===
        // PayerMapper (Domain) already resolved contract→payer routing (including
        // divergent-EAN handling). Walk MP → contract → payer via the pre-built map.
        // If an MP is on contracts that resolve to multiple payers (cannot happen under
        // PayerMapper's invariants) or the contract is missing from the map (Domain
        // corruption), route to default and warn.
        var mpsByPayerIdentifier = ResolvePayerRouting(
            customer, mpGuids.Keys, payerIdentifierByContract, defaultPayerIdentification, warnings);

        foreach (var (targetPayerId, mpIdsForPayer) in mpsByPayerIdentifier)
        {
            if (!payerIds.TryGetValue(targetPayerId, out var payerGuid)) continue;

            var (existingPayerMps, _) = await _payerClient.GetMeteringPointsAsync(payerGuid, cancellationToken);
            var existingPayerMpIds = existingPayerMps?.Select(m => m.MeteringPointId).ToHashSet() ?? [];

            var payerMpDtos = new List<PayerMeteringPointDto>();
            foreach (var mpId in mpIdsForPayer)
            {
                if (!mpGuids.TryGetValue(mpId, out var mpGuid)) continue;
                if (existingPayerMpIds.Contains(mpGuid)) continue;
                var start = earliestStartByMpId.TryGetValue(mpId, out var s) ? s : DateOnly.FromDateTime(DateTime.Today);
                payerMpDtos.Add(BuildPayerLinkDto(mpId, mpGuid, commodityType, start));
            }

            if (payerMpDtos.Count > 0)
            {
                if (context.DryRun)
                {
                    _logLine($"  [dry-run] Payer {targetPayerId}: would link {payerMpDtos.Count} MPs");
                    payerLinks += payerMpDtos.Count;
                }
                else
                {
                    var req = new AddPayerMeteringPointsRequest { MeteringPoints = payerMpDtos };
                    var (success, error) = await _payerClient.AddMeteringPointsAsync(payerGuid, req, cancellationToken);
                    if (!success)
                    {
                        errors.Add($"Payer {targetPayerId} MP linking failed: {error}");
                        _logLine($"  FAILED: Payer MP linking: {error}");
                        payerMpsFailed += payerMpDtos.Count;
                    }
                    else
                    {
                        _logLine($"  Payer {targetPayerId}: linked {payerMpDtos.Count} MPs");
                        payerLinks += payerMpDtos.Count;
                    }
                }
            }
        }

        // === Step 6: Contract → MP links (via pre-resolved MpContractLinks from context) ===
        if (context.MpContractLinks.Count > 0)
        {
            var customerMpGuids = mpGuids.Keys.ToHashSet(StringComparer.Ordinal);

            // Group context links by contract for this customer's MPs (dedup by (mpId, contractId)).
            var dedupedLinks = context.MpContractLinks
                .Where(l => customerMpGuids.Contains(l.MeteringPointId))
                .GroupBy(l => (l.MeteringPointId, l.ContractId))
                .Select(g =>
                {
                    var maxEnd = g.Any(l => l.ContractPartEnddate == MigrationConstants.EndDateNull)
                        ? MigrationConstants.EndDateNull
                        : g.Max(l => l.ContractPartEnddate);
                    return new SettlMpContractLink(
                        g.Key.MeteringPointId,
                        g.Key.ContractId,
                        g.Max(l => l.ContractPartStartdate),
                        maxEnd,
                        g.Any(l => l.IsJoined),
                        g.Any(l => l.OpenEnded));
                })
                .GroupBy(l => l.ContractId)
                .ToList();

            foreach (var contractGroup in dedupedLinks)
            {
                var contractId = contractGroup.Key;

                if (!_contractMpCache.TryGetValue(contractId, out var existingContractMpIds))
                {
                    var (existingContractMps, _) = await _contractClient.GetContractMeteringPointsAsync(contractId, cancellationToken);
                    existingContractMpIds = existingContractMps?.Select(m => m.MeteringPointId).ToHashSet() ?? [];
                    _contractMpCache[contractId] = existingContractMpIds;
                }

                var contractMpDtos = new List<ContractMeteringPointDto>();
                foreach (var link in contractGroup)
                {
                    if (!mpGuids.TryGetValue(link.MeteringPointId, out var mpGuid)) continue;
                    if (existingContractMpIds.Contains(mpGuid)) continue;

                    contractMpDtos.Add(BuildContractLinkDto(link, mpGuid));
                }

                if (contractMpDtos.Count == 0) continue;

                if (context.DryRun)
                {
                    foreach (var dto in contractMpDtos)
                        existingContractMpIds.Add(dto.MeteringPointId);
                    _logLine($"  [dry-run] Contract {contractId}: would link {contractMpDtos.Count} MPs");
                    contractLinks += contractMpDtos.Count;
                }
                else
                {
                    var req = new AddContractMeteringPointsRequest { ContractMeteringPoints = contractMpDtos };
                    var (success, error) = await _contractClient.AddContractMeteringPointsAsync(contractId, req, cancellationToken);
                    if (!success)
                    {
                        errors.Add($"Contract {contractId} MP linking failed: {error}");
                        _logLine($"  FAILED: Contract MP linking: {error}");
                        contractMpsFailed += contractMpDtos.Count;
                    }
                    else
                    {
                        foreach (var dto in contractMpDtos)
                            existingContractMpIds.Add(dto.MeteringPointId);
                        _logLine($"  Contract {contractId}: linked {contractMpDtos.Count} MPs");
                        contractLinks += contractMpDtos.Count;
                    }
                }
            }
        }

        // === Step 7: Settlement Groups (optional) ===
        var (sgAdded, sgSkipped, sgFailed) = (0, 0, 0);
        if (context.SettlementGroupMap != null)
        {
            (sgAdded, sgSkipped, sgFailed) = await AssignSettlementGroupsAsync(
                customer, customerGuid, context.SettlementGroupMap,
                context.DryRun, errors, cancellationToken);
        }

        _logLine($"  OK ({mpGuids.Count} MPs)");
        return new SettlMigrationResult(
            customerGuid, customerCreated, payerIds, mpGuids,
            customerLinks, payerLinks, contractLinks, warnings, errors,
            PayersCreated: payersCreated, PayersExisting: payersExisting,
            MeteringPointsCreated: mpsCreated, MeteringPointsExisting: mpsExisting,
            ExistingPayerIdentifications: existingPayerIdentifications,
            ExistingMeteringPointIdentifications: existingMpIdentifications,
            SettlementGroupsAdded: sgAdded,
            SettlementGroupsSkippedExisting: sgSkipped,
            SettlementGroupsFailed: sgFailed,
            MeteringPointsFailed: mpsFailed,
            CustomerMpsFailed: customerMpsFailed,
            PayerMpsFailed: payerMpsFailed,
            ContractMpsFailed: contractMpsFailed,
            BillingExemptionsApplied: exemptionsApplied,
            BillingExemptionsSkipped: exemptionsSkipped,
            BillingExemptionsFailed: exemptionsFailed);
    }

    /// <inheritdoc />
    public async Task<SettlMeteringPointPrecreateResult> PrecreateMeteringPointsAsync(
        IReadOnlyList<Customer> customers,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        SettlMigrationContext context,
        CancellationToken cancellationToken,
        Action<int, int>? onBatchProgress = null)
    {
        // Dry-run keeps the per-customer path untouched: nothing is written, the read-caches
        // answer without hitting Settl, so there is no waiting to save and no report to change.
        if (context.DryRun) return SettlMeteringPointPrecreateResult.Empty;

        // First owner wins — the same rule the create-report's MP list already uses for an MP
        // that hangs under more than one account.
        var owners = new Dictionary<string, (MeteringPoint Mp, Customer Customer)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var customer in customers)
        {
            foreach (var contract in customer.Contracts)
            {
                foreach (var assignment in contract.MeteringPoints)
                {
                    var mpId = assignment.MeteringPointId;
                    if (owners.ContainsKey(mpId)) continue;
                    if (!meteringPoints.TryGetValue(mpId, out var mp)) continue;
                    // Water/heat are pre-populated externally — searched, never created.
                    if (mp.Grid == null) continue;
                    owners[mpId] = (mp, customer);
                    order.Add(mpId);
                }
            }
        }

        if (order.Count == 0) return SettlMeteringPointPrecreateResult.Empty;

        var errors = new List<string>();
        var warnings = new List<string>();
        var batchSize = Math.Max(1, context.MeteringPointCreateBatchSize);
        var searchConcurrency = Math.Max(1, context.SearchConcurrency);

        if (_marketBalanceAreaMap == null)
            await EnsureGridMapsAsync(cancellationToken);

        _logLine($"Metering points: pre-creating {order.Count} electricity MPs in batches of {batchSize}");

        // "Settled" means the same thing the closing summary counts: created + already existing +
        // failed. Already-existing MPs are settled by the lookup below, before the first batch, so
        // they get their own call the moment the lookup is folded in — same definition, just earlier.
        onBatchProgress?.Invoke(0, order.Count);

        // Split into what already exists and what has to be created. The lookups run side by side,
        // but the results are folded in afterwards on this one thread, in the order of `order` —
        // _precreatedMps is a plain Dictionary and pending must stay in the order the batches expect.
        var existingHits = await SearchManyAsync(order, searchConcurrency, cancellationToken);

        // What became of each metering point, written down where the answer arrives instead of only
        // being added to a counter. Nothing here steers the phase — it is the same run, told
        // metering point by metering point.
        var settled = new Dictionary<string, SettlMeteringPointOutcome>(StringComparer.Ordinal);

        var pending = new List<(string Id, MeteringPointCreateObjectV4Dto Dto)>();
        var alreadyExisting = 0;
        for (var i = 0; i < order.Count; i++)
        {
            var mpId = order[i];
            var (mp, customer) = owners[mpId];
            var existing = existingHits[i];
            if (existing != null)
            {
                _precreatedMps[mpId] = new PrecreatedMeteringPoint(existing.Id, alreadyExisted: true);
                settled[mpId] = new SettlMeteringPointOutcome(mpId, SettlMeteringPointStatus.AlreadyExisted);
                alreadyExisting++;
                continue;
            }

            // Translation is pure CPU — it stays here rather than in the parallel lookups.
            var mpDto = MeteringPointTranslator.Build(mp, customer);
            ApplyGridUuids(mpDto, _marketBalanceAreaMap, _gridAreaMap);
            pending.Add((mpId, MeteringPointCreateObjectV4Dto.FromMeteringPoint(
                mpDto, context.EnrichAddress, context.SkipAddressValidation)));
        }

        onBatchProgress?.Invoke(alreadyExisting, order.Count);

        int created = 0, failed = 0, batches = 0;
        for (var i = 0; i < pending.Count; i += batchSize)
        {
            var batch = pending.GetRange(i, Math.Min(batchSize, pending.Count - i));
            batches++;

            var postedIds = new List<string>();
            // Metering points Settl refused because they are already there. They belong with the
            // ones the lookup found, not with the failures — the run wanted them in Settl, and in
            // Settl is where they are.
            var conflictIds = new List<string>();
            var batchFailures = new List<(string Id, string Error)>();
            await PostMeteringPointBatchAsync(
                batch, postedIds, conflictIds, batchFailures, errors, cancellationToken);
            alreadyExisting += conflictIds.Count;
            failed += batch.Count - postedIds.Count - conflictIds.Count;

            // A metering point Settl accepted is created whatever the read-back below makes of it:
            // an id search has not caught up with yet is still a metering point Settl has.
            foreach (var id in postedIds)
                settled[id] = new SettlMeteringPointOutcome(id, SettlMeteringPointStatus.Created);
            foreach (var id in conflictIds)
                settled[id] = new SettlMeteringPointOutcome(id, SettlMeteringPointStatus.AlreadyExisted);
            foreach (var (id, error) in batchFailures)
                settled[id] = new SettlMeteringPointOutcome(id, SettlMeteringPointStatus.Failed, error);

            if (!context.ResolveCreatedMeteringPointIds)
            {
                // Nobody downstream needs the GUIDs, so a 201 is the whole answer: Settl says it
                // created them. Counting the accepted ids is not a weaker count than the read-back's
                // — the read-back only ever confirmed that search had caught up.
                created += postedIds.Count;
            }
            else
            {
                // One shared pause for the whole batch, then read the batch back in one pass.
                created += await ReadBackCreatedMpsAsync(postedIds, searchConcurrency, warnings, cancellationToken);
            }

            onBatchProgress?.Invoke(alreadyExisting + created + failed, order.Count);
        }

        var unresolved = _precreateUnresolved.Count;
        var tail = context.ResolveCreatedMeteringPointIds
            ? $"{unresolved} awaiting Settl"
            : "ids not resolved (no read-back — this run does not link anything)";
        _logLine($"Metering points: {created} created, {alreadyExisting} already existed, "
                 + $"{failed} failed, {tail} ({batches} batches)");

        return new SettlMeteringPointPrecreateResult(
            order.Count, alreadyExisting, created, failed, unresolved, batches, errors, warnings,
            BuildMeteringPointOutcomes(order, settled));
    }

    /// <summary>
    /// Lines the settled outcomes up with the metering points the phase set out to handle, in that
    /// same order. A metering point with no answer is reported as
    /// <see cref="SettlMeteringPointStatus.NotAttempted"/> rather than left out: a caller reading a
    /// missing entry as "fine" is exactly the mistake this list exists to prevent. A run that gets
    /// to the end settles every one of them — the gap can only open if the run stops early, and a
    /// run that throws never hands back a result at all.
    /// </summary>
    internal static IReadOnlyList<SettlMeteringPointOutcome> BuildMeteringPointOutcomes(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, SettlMeteringPointOutcome> settled)
    {
        var outcomes = new List<SettlMeteringPointOutcome>(order.Count);
        foreach (var mpId in order)
            outcomes.Add(settled.TryGetValue(mpId, out var outcome)
                ? outcome
                : new SettlMeteringPointOutcome(mpId, SettlMeteringPointStatus.NotAttempted));

        return outcomes;
    }

    /// <summary>
    /// POSTs one v4 create with the whole batch. On rejection the batch is halved and each half
    /// retried, down to a single MP — so the error text names the exact metering point that Settl
    /// refused, and a batch that was simply too big still gets through.
    ///
    /// <para>"Already exist" is the one rejection that is not a failure: the metering point is in
    /// Settl, which is where the run wanted it, so it is counted with the ones the up-front lookup
    /// found and reported through <paramref name="existingIds"/>. Settl names the aggregate it
    /// refused in the error body, so those metering points are lifted straight out of the batch and
    /// the rest is re-posted in one call — a couple of metering points the lookup could not see must
    /// not drag a batch of hundreds through the halving. If the body names an aggregate we did not
    /// send, or cannot be read at all, the halving still runs and the conflict is classified once
    /// the metering point stands alone; it costs the extra calls, but it never mislabels.</para>
    ///
    /// <para>Whatever the route, each metering point in the batch comes out in exactly one of
    /// <paramref name="postedIds"/>, <paramref name="existingIds"/> or <paramref name="failures"/> —
    /// the last one pairing the metering point with the text <paramref name="errors"/> reports.</para>
    /// </summary>
    private async Task PostMeteringPointBatchAsync(
        List<(string Id, MeteringPointCreateObjectV4Dto Dto)> batch,
        List<string> postedIds,
        List<string> existingIds,
        List<(string Id, string Error)> failures,
        List<string> errors,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        var request = new CreateMeteringPointsV4Request
        {
            MeteringPointCreateObjectDtos = batch.Select(b => b.Dto).ToList()
        };
        var (success, error) = await _mpClient.CreateV4Async(request, ct);
        if (success)
        {
            foreach (var (id, _) in batch)
            {
                postedIds.Add(id);
                _logLine($"  Created MP {id}");
            }
            return;
        }

        var conflict = ParseAlreadyExistsConflict(error);
        if (conflict.Ids.Count > 0)
        {
            var remainder = new List<(string Id, MeteringPointCreateObjectV4Dto Dto)>();
            foreach (var entry in batch)
            {
                if (!conflict.Ids.Contains(entry.Dto.Id))
                {
                    remainder.Add(entry);
                    continue;
                }

                // Settl refused the very id this run sent, so that id is also the one the aggregate
                // already in Settl carries — good enough to hand the linking phase, exactly as the
                // lookup's hit would have been.
                _precreatedMps[entry.Id] = new PrecreatedMeteringPoint(entry.Dto.Id, alreadyExisted: true);
                existingIds.Add(entry.Id);
                _logLine($"  MP {entry.Id} already exists (ID: {entry.Dto.Id})");
            }

            // Only re-post when the batch actually got smaller — otherwise this is the same call
            // over again, and the halving below is the way forward.
            if (remainder.Count < batch.Count)
            {
                await PostMeteringPointBatchAsync(remainder, postedIds, existingIds, failures, errors, ct);
                return;
            }
        }

        if (batch.Count == 1)
        {
            // One metering point in the call leaves no doubt about who the answer is about, so an
            // already-exists rejection is classified here even when the body named an aggregate id
            // this run never sent.
            if (conflict.OnlyAlreadyExists)
            {
                existingIds.Add(batch[0].Id);
                _logLine($"  MP {batch[0].Id} already exists (Settl refused the create as a duplicate)");
                return;
            }

            var msg = $"MP {batch[0].Id} create failed: {error}";
            errors.Add(msg);
            failures.Add((batch[0].Id, error ?? ""));
            _logLine($"  FAILED: {msg}");
            return;
        }

        _logLine($"  Batch of {batch.Count} MPs rejected ({error}) — splitting in two");
        var half = batch.Count / 2;
        await PostMeteringPointBatchAsync(
            batch.GetRange(0, half), postedIds, existingIds, failures, errors, ct);
        await PostMeteringPointBatchAsync(
            batch.GetRange(half, batch.Count - half), postedIds, existingIds, failures, errors, ct);
    }

    /// <summary>
    /// Picks the "already exist" rejections out of Settl's error body. The body is a list of errors,
    /// each carrying the aggregate id it concerns and a message along the lines of
    /// <c>Aggregate '…' (type MeteringPointAggregate) already exist.</c>. Anything else in the same
    /// list — a postal code Settl does not know, say — is a real failure, and
    /// <c>OnlyAlreadyExists</c> then stays false so the caller keeps treating the call as one.
    /// The body arrives as the client's <c>HTTP 409: {…}</c> string, so the JSON starts at the first
    /// brace; a body that will not parse simply yields nothing and the caller falls back to halving.
    /// </summary>
    private static (HashSet<Guid> Ids, bool OnlyAlreadyExists) ParseAlreadyExistsConflict(string? error)
    {
        var ids = new HashSet<Guid>();
        if (string.IsNullOrEmpty(error)) return (ids, false);

        var brace = error.IndexOf('{');
        if (brace < 0) return (ids, false);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(error[brace..]);
        }
        catch (JsonException)
        {
            return (ids, false);
        }

        using (doc)
        {
            if (!TryGetProperty(doc.RootElement, "Errors", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
                return (ids, false);

            var alreadyExists = false;
            var somethingElse = false;

            foreach (var entry in entries.EnumerateArray())
            {
                var text = TryGetProperty(entry, "Message", out var message)
                           && TryGetProperty(message, "Text", out var t)
                           && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;

                if (text == null || !text.Contains("already exist", StringComparison.OrdinalIgnoreCase))
                {
                    somethingElse = true;
                    continue;
                }

                alreadyExists = true;
                if (TryGetProperty(entry, "Id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && Guid.TryParse(idElement.GetString(), out var id))
                    ids.Add(id);
            }

            return (ids, alreadyExists && !somethingElse);
        }
    }

    /// <summary>
    /// Case-insensitive property lookup — Settl answers in PascalCase today, and nothing here is
    /// worth breaking over a casing change.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a just-created batch back to get its Settl GUIDs. Settl is not immediately consistent,
    /// so passes two and onwards wait first — but the whole batch shares each wait, which is the
    /// entire point of batching. The first pass looks straight away: Settl has usually caught up by
    /// the time the create call returns, and when it has, the run pays nothing at all. The waits then
    /// escalate (a quarter, a half, one, two times the retry delay), so the rare laggard still gets
    /// the same overall grace as before. Whatever is still missing after five passes is parked in
    /// <see cref="_precreateUnresolved"/>; the customer loop looks for it again and never re-creates it.
    /// </summary>
    private async Task<int> ReadBackCreatedMpsAsync(List<string> ids, int searchConcurrency, List<string> warnings, CancellationToken ct)
    {
        // Wait before pass 2..5 — pass 1 looks first.
        var delayFactors = new[] { 0.0, 0.25, 0.5, 1.0, 2.0 };
        var remaining = ids;
        var found = 0;

        for (var attempt = 0; attempt < 5 && remaining.Count > 0; attempt++)
        {
            var delayMs = (int)(_searchRetryDelayMs * delayFactors[attempt]);
            if (delayMs > 0) await Task.Delay(delayMs, ct);

            // Lookups side by side, bookkeeping afterwards on this one thread — _precreatedMps and
            // the found count are never touched concurrently, and stillMissing keeps its input order.
            var hits = await SearchManyAsync(remaining, searchConcurrency, ct);

            var stillMissing = new List<string>();
            for (var i = 0; i < remaining.Count; i++)
            {
                var id = remaining[i];
                var hit = hits[i];
                if (hit != null)
                {
                    _precreatedMps[id] = new PrecreatedMeteringPoint(hit.Id, alreadyExisted: false);
                    found++;
                }
                else
                {
                    stillMissing.Add(id);
                }
            }

            if (stillMissing.Count > 0 && attempt < 4)
                _logLine($"  Batch read-back: {stillMissing.Count} MPs not searchable yet (attempt {attempt + 1}/5)");
            remaining = stillMissing;
        }

        foreach (var id in remaining)
        {
            warnings.Add($"MP {id} created but not found in search after retries");
            _precreateUnresolved.Add(id);
        }

        return found;
    }

    /// <summary>
    /// Looks up a list of metering-point identifications with at most <paramref name="concurrency"/>
    /// requests in flight, and returns the answers in the <b>same order as the input</b> — the caller
    /// folds them into its own state sequentially, so nothing shared is written from more than one
    /// thread. A concurrency of 1 is the plain sequential loop, byte for byte.
    /// </summary>
    private async Task<MeteringPointInfoDto?[]> SearchManyAsync(
        IReadOnlyList<string> ids, int concurrency, CancellationToken ct)
    {
        var results = new MeteringPointInfoDto?[ids.Count];
        if (ids.Count == 0) return results;

        if (concurrency <= 1 || ids.Count == 1)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var (hit, _) = await _mpClient.SearchByIdentificationAsync(ids[i], ct);
                results[i] = hit;
            }
            return results;
        }

        using var gate = new SemaphoreSlim(concurrency, concurrency);

        async Task LookupAsync(int index)
        {
            await gate.WaitAsync(ct);
            try
            {
                var (hit, _) = await _mpClient.SearchByIdentificationAsync(ids[index], ct);
                results[index] = hit;
            }
            finally
            {
                gate.Release();
            }
        }

        var tasks = new Task[ids.Count];
        for (var i = 0; i < ids.Count; i++) tasks[i] = LookupAsync(i);
        await Task.WhenAll(tasks);

        return results;
    }

    /// <summary>
    /// Hands out a metering point resolved by the batch phase. The first caller for a freshly
    /// created MP gets it as created; every caller after that gets it as existing — which is what
    /// the per-customer search-or-create produced for an MP shared by several accounts.
    /// </summary>
    private bool TryTakePrecreated(string mpId, out Guid guid, out bool countAsExisting)
    {
        guid = Guid.Empty;
        countAsExisting = false;
        if (!_precreatedMps.TryGetValue(mpId, out var entry)) return false;

        guid = entry.Id;
        countAsExisting = entry.AlreadyExisted || entry.Consumed;
        entry.Consumed = true;
        return true;
    }

    private sealed class PrecreatedMeteringPoint(Guid id, bool alreadyExisted)
    {
        public Guid Id { get; } = id;
        public bool AlreadyExisted { get; } = alreadyExisted;
        public bool Consumed { get; set; }
    }

    /// <summary>
    /// Builds the <b>full</b> intended Settl create-payload for one customer bundle —
    /// customer + payer(s) + MP master + MP billing + exemptions + customer/payer/contract→MP
    /// links — <b>without any writes and independently of existing Settl state</b> (no
    /// search-or-create short-circuits). Grid UUIDs are resolved read-only via
    /// <see cref="EnsureGridMapsAsync"/>. Reuses the exact same builder helpers as
    /// <see cref="MigrateAsync"/>, so the captured payload equals what create would POST for a
    /// fresh bundle. Used by the DataMigration <c>reconcile</c> command to diff intended-vs-actual.
    /// </summary>
    public async Task<SettlMigrationPlan> CapturePlanAsync(
        Customer customer,
        IReadOnlyDictionary<string, MeteringPoint> meteringPoints,
        SettlMigrationContext context,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        var customerMpIds = customer.Contracts
            .SelectMany(c => c.MeteringPoints.Select(a => a.MeteringPointId))
            .Distinct()
            .ToList();

        var earliestStartByMpId = customer.Contracts
            .SelectMany(c => c.MeteringPoints)
            .GroupBy(a => a.MeteringPointId)
            .ToDictionary(g => g.Key, g => g.Min(a => a.Start));

        var customerDomainMps = customerMpIds
            .Where(meteringPoints.ContainsKey)
            .Select(id => meteringPoints[id])
            .ToList();

        var commodityType = customerDomainMps.FirstOrDefault()?.Commodity is { } c
            ? MapCommodityType(c)
            : "Electricity";

        DateOnly StartFor(string mpId) =>
            earliestStartByMpId.TryGetValue(mpId, out var s) ? s : DateOnly.FromDateTime(DateTime.Today);

        // === Customer ===
        var customerDto = BuildCustomerDto(customer, context);
        var customerGuid = customerDto.Id;

        // === Payers (1:1 fan-out, owned by Domain) ===
        var payerDtos = BuildPayerDtos(customer, customerGuid);
        var defaultPayer = customer.Payers.FirstOrDefault(p => p.IsPrimary) ?? customer.Payers[0];
        var defaultPayerIdentification = defaultPayer.Identifier;
        var payerIdentifierByContract = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in customer.Payers)
            foreach (var contractNumber in p.ContractNumbers)
                payerIdentifierByContract[contractNumber] = p.Identifier;

        // === Grid maps (read-only) — only when electricity MPs are present ===
        if (customerDomainMps.Any(mp => mp.Grid != null) && _marketBalanceAreaMap == null)
            await EnsureGridMapsAsync(cancellationToken);

        var paymentConfigStart = DateOnly.FromDateTime(context.ChangeOfSupplierDate);

        // === Metering points + billing + exemptions ===
        var mpGuids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var plannedMps = new List<PlannedMeteringPoint>();
        foreach (var mp in customerDomainMps)
        {
            var mpId = mp.Identification;
            var start = StartFor(mpId);
            var mpDto = MeteringPointTranslator.Build(mp, customer);
            if (mp.Grid != null)
                ApplyGridUuids(mpDto, _marketBalanceAreaMap, _gridAreaMap);
            mpGuids[mpId] = mpDto.Id;

            // Billing config mirrors EnsureBillingConfigAsync's payload for a freshly created
            // electricity MP: BillingConfiguration always set, PaymentConfiguration when override.
            UpdateBillingMeteringPointRequest? billing = null;
            if (mp.Grid != null)
            {
                var inv = mp.BuyerInvoicing;
                billing = new UpdateBillingMeteringPointRequest
                {
                    BillingConfiguration = BuildBillingConfiguration(start, mp.JointInvoiceGroupId)
                };
                if (HasPaymentOverride(inv?.ElectronicNumber, inv?.OrderIdentifier, inv?.AgreementIdentifier, inv?.BuyerReferenceIdentifier))
                    billing.PaymentConfiguration = BuildPaymentConfiguration(
                        paymentConfigStart, inv?.ElectronicNumber, inv?.OrderIdentifier, inv?.AgreementIdentifier, inv?.BuyerReferenceIdentifier);
            }

            var exemptions = new List<BillingExemptionDto>();
            if (context.ExemptionContext != null && mp.BillingExemptionTemplate != null)
            {
                exemptions.Add(new BillingExemptionDto
                {
                    ProductElementId = context.ExemptionContext.ProductElementId,
                    ExemptionPercentage = context.ExemptionContext.Percentage
                });
            }

            plannedMps.Add(new PlannedMeteringPoint(mpId, mp.Grid != null, mpDto, billing, exemptions));
        }

        // === Customer → MP links (full, no existing-filter) ===
        var customerMpLinks = mpGuids
            .Select(kv => BuildCustomerLinkDto(kv.Key, kv.Value, commodityType, StartFor(kv.Key)))
            .ToList();

        // === Payer → MP links (full, routed) ===
        var mpsByPayer = ResolvePayerRouting(customer, mpGuids.Keys, payerIdentifierByContract, defaultPayerIdentification, warnings);
        var payerMpLinks = new List<PlannedPayerMpLink>();
        foreach (var (payerId, mpIdsForPayer) in mpsByPayer)
            foreach (var mpId in mpIdsForPayer)
                payerMpLinks.Add(new PlannedPayerMpLink(payerId, BuildPayerLinkDto(mpId, mpGuids[mpId], commodityType, StartFor(mpId))));

        // === Contract → MP links (full, deduped — same shape as MigrateAsync Step 6) ===
        var contractMpLinks = new List<PlannedContractMpLink>();
        var customerMpSet = mpGuids.Keys.ToHashSet(StringComparer.Ordinal);
        var dedupedLinks = context.MpContractLinks
            .Where(l => customerMpSet.Contains(l.MeteringPointId))
            .GroupBy(l => (l.MeteringPointId, l.ContractId))
            .Select(g =>
            {
                var maxEnd = g.Any(l => l.ContractPartEnddate == MigrationConstants.EndDateNull)
                    ? MigrationConstants.EndDateNull
                    : g.Max(l => l.ContractPartEnddate);
                return new SettlMpContractLink(
                    g.Key.MeteringPointId, g.Key.ContractId,
                    g.Max(l => l.ContractPartStartdate), maxEnd, g.Any(l => l.IsJoined),
                    g.Any(l => l.OpenEnded));
            });
        foreach (var link in dedupedLinks)
            if (mpGuids.TryGetValue(link.MeteringPointId, out var mpGuid))
                contractMpLinks.Add(new PlannedContractMpLink(link.ContractId, BuildContractLinkDto(link, mpGuid)));

        return new SettlMigrationPlan(
            customerDto.CustomerNumber!,
            customerDto,
            payerDtos,
            plannedMps,
            customerMpLinks,
            payerMpLinks,
            contractMpLinks,
            warnings);
    }

    // === Shared payload builders (used by both MigrateAsync and CapturePlanAsync) ===

    private static CreateCustomerDto BuildCustomerDto(Customer customer, SettlMigrationContext context)
    {
        var dto = CustomerTranslator.Build(customer);
        // Apply owner mapping from context (set by CustomerOwnerResolver / OwnerClusterResolver).
        // OwnerByCustomerNumber must be keyed on the same CustomerNumber-form CustomerTranslator
        // produces (caller's responsibility).
        if (context.OwnerByCustomerNumber?.TryGetValue(dto.CustomerNumber!, out var ownerId) == true)
            dto.Owner = new CustomerOwnerDto
            {
                // Owner-relationen skal starte samme dato som kunden selv (tidligste
                // kontrakt-start), ikke COS. Ellers bliver koblingen fremtidsdateret og
                // dukker ikke op i Settl før COS-datoen. Hentes fra domænet — samme kilde
                // som Address.Start — så de to aldrig kan divergere.
                Start = CustomerTranslator.EarliestStart(customer),
                OwnerId = ownerId
            };
        return dto;
    }

    private static List<CreatePayerDto> BuildPayerDtos(Customer customer, Guid customerGuid) =>
        customer.Payers.Select(p => PayerTranslator.Build(customer, p, customerGuid)).ToList();

    private static void ApplyGridUuids(
        MeteringPointDto mpDto,
        IReadOnlyDictionary<string, Guid>? marketBalanceAreaMap,
        IReadOnlyDictionary<string, Guid>? gridAreaMap)
    {
        if (marketBalanceAreaMap == null || gridAreaMap == null) return;
        if (mpDto.Grid?.MarketBalanceAreaName != null
            && marketBalanceAreaMap.TryGetValue(mpDto.Grid.MarketBalanceAreaName, out var mbId))
            mpDto.Grid.MarketBalanceAreaId = mbId;
        if (mpDto.Grid?.GridAreaCode != null
            && gridAreaMap.TryGetValue(mpDto.Grid.GridAreaCode, out var gaId))
            mpDto.Grid.MeteringGridAreaId = gaId;
    }

    private static bool HasPaymentOverride(string? electronicNumber, string? orderIdentifier, string? agreementIdentifier, string? buyerReference) =>
        !string.IsNullOrEmpty(electronicNumber)
        || !string.IsNullOrEmpty(orderIdentifier)
        || !string.IsNullOrEmpty(agreementIdentifier)
        || !string.IsNullOrEmpty(buyerReference);

    private static BillingConfigurationDto BuildBillingConfiguration(DateOnly start, string? jointInvoiceGroupId) => new()
    {
        Start = start,
        Currency = "DKK",
        JointInvoice = !string.IsNullOrEmpty(jointInvoiceGroupId),
        JointInvoiceNumber = jointInvoiceGroupId,
        LifeTime = "UntilNextHistoricalEntry"
    };

    private static MeteringPointPaymentConfigurationDto BuildPaymentConfiguration(
        DateOnly paymentConfigStart, string? electronicNumber, string? orderIdentifier,
        string? agreementIdentifier, string? buyerReference) => new()
    {
        Start = paymentConfigStart,
        LifeTime = "UntilNextHistoricalEntry",
        ElectronicNumber = electronicNumber,
        OrderIdentifier = orderIdentifier,
        AgreementIdentifier = agreementIdentifier,
        BuyerReference = buyerReference
    };

    private static CustomerMeteringPointDto BuildCustomerLinkDto(string mpId, Guid mpGuid, string commodityType, DateOnly start) => new()
    {
        ExternalIdentifier = mpId,
        CommodityType = commodityType,
        MeteringPointId = mpGuid,
        Start = start,
        End = MigrationConstants.MeteringPointEndDate
    };

    private static PayerMeteringPointDto BuildPayerLinkDto(string mpId, Guid mpGuid, string commodityType, DateOnly start) => new()
    {
        ExternalIdentifier = mpId,
        CommodityType = commodityType,
        MeteringPointId = mpGuid,
        Start = start,
        End = MigrationConstants.MeteringPointEndDate
    };

    private static ContractMeteringPointDto BuildContractLinkDto(SettlMpContractLink link, Guid mpGuid)
    {
        DateOnly? endDate = (link.OpenEnded || link.ContractPartEnddate == MigrationConstants.EndDateNull)
            ? MigrationConstants.MeteringPointEndDate
            : DateOnly.FromDateTime(link.ContractPartEnddate);
        return new ContractMeteringPointDto
        {
            MeteringPointId = mpGuid,
            MeteringPointIdentification = link.MeteringPointId,
            Start = DateOnly.FromDateTime(link.ContractPartStartdate),
            End = endDate,
            Category = link.IsJoined ? "JointInvoicing" : null
        };
    }

    /// <summary>
    /// Routes each MP id to a target PayerIdentifier via contract→payer mapping. MPs whose
    /// contract is missing from the map, or that route to multiple payers, fall back to the
    /// default payer (and emit a warning). Shared by MigrateAsync Step 5 and CapturePlanAsync.
    /// </summary>
    private static Dictionary<string, List<string>> ResolvePayerRouting(
        Customer customer,
        IEnumerable<string> mpIds,
        Dictionary<string, string> payerIdentifierByContract,
        string defaultPayerIdentification,
        List<string> warnings)
    {
        var payerIdsByMpId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var contract in customer.Contracts)
        {
            if (!payerIdentifierByContract.TryGetValue(contract.Number, out var payerIdentifier))
            {
                warnings.Add($"Contract {contract.Number}: not present in Domain.Customer.Payers — routing its MPs to default payer");
                payerIdentifier = defaultPayerIdentification;
            }
            foreach (var assignment in contract.MeteringPoints)
            {
                if (!payerIdsByMpId.TryGetValue(assignment.MeteringPointId, out var set))
                    payerIdsByMpId[assignment.MeteringPointId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(payerIdentifier);
            }
        }

        var mpsByPayerIdentifier = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var mpId in mpIds)
        {
            string targetPayerId = defaultPayerIdentification;
            if (payerIdsByMpId.TryGetValue(mpId, out var set))
            {
                if (set.Count == 1)
                    targetPayerId = set.First();
                else
                    warnings.Add($"MP {mpId}: on contracts routing to multiple payers — using default payer");
            }
            if (!mpsByPayerIdentifier.TryGetValue(targetPayerId, out var list))
                mpsByPayerIdentifier[targetPayerId] = list = [];
            list.Add(mpId);
        }
        return mpsByPayerIdentifier;
    }

    private async Task EnsureGridMapsAsync(CancellationToken ct)
    {
        var mbMap = new Dictionary<string, Guid>();
        var gaMap = new Dictionary<string, Guid>();

        var (mbAreas, _) = await _mpClient.FindMarketBalanceAreasAsync(ct);
        if (mbAreas != null)
            foreach (var a in mbAreas.Where(a => a.Name != null))
                mbMap[a.Name!] = a.Id;

        var (gaAreas, _) = await _mpClient.FindMeteringGridAreasAsync(ct);
        if (gaAreas != null)
            foreach (var a in gaAreas.Where(a => a.MeteringGridAreaIdentification != null))
                gaMap[a.MeteringGridAreaIdentification!] = a.Id;

        _marketBalanceAreaMap = mbMap;
        _gridAreaMap = gaMap;
    }

    private async Task<Guid?> RetrySearchAsync(string mpId, CancellationToken ct)
    {
        for (int i = 0; i < 5; i++)
        {
            if (i > 0)
            {
                _logLine($"  MP {mpId}: search attempt {i + 1}/5...");
                if (_searchRetryDelayMs > 0) await Task.Delay(_searchRetryDelayMs, ct);
            }
            var (found, _) = await _mpClient.SearchByIdentificationAsync(mpId, ct);
            if (found != null) return found.Id;
        }
        return null;
    }

    private async Task EnsureBillingConfigAsync(
        Guid mpGuid, string mpId, DateOnly start, string? jointInvoiceGroupId,
        string? electronicNumber, string? orderIdentifier, string? agreementIdentifier,
        string? buyerReference,
        DateOnly paymentConfigStart,
        bool justCreated,
        List<string> errors, CancellationToken ct)
    {
        // A metering point this run created seconds ago cannot have a billing configuration yet, so
        // reading it back can only ever answer "not configured" — one round trip per new MP for an
        // answer we already know. Skip the read and write as if nothing were configured.
        var billingOk = false;
        if (!justCreated)
        {
            var (current, _) = await _mpClient.GetBillingByIdAsync(mpGuid, null, ct);
            var cfg = current?.BillingConfiguration;
            var currencyOk = cfg?.Currency == "DKK";
            var jointOk = cfg != null
                && cfg.JointInvoice == !string.IsNullOrEmpty(jointInvoiceGroupId)
                && (cfg.JointInvoiceNumber ?? "") == (jointInvoiceGroupId ?? "");
            billingOk = currencyOk && jointOk;
        }
        var hasPaymentOverride = HasPaymentOverride(electronicNumber, orderIdentifier, agreementIdentifier, buyerReference);

        if (billingOk && !hasPaymentOverride) return;

        var request = new UpdateBillingMeteringPointRequest();
        if (!billingOk)
            request.BillingConfiguration = BuildBillingConfiguration(start, jointInvoiceGroupId);
        if (hasPaymentOverride)
            request.PaymentConfiguration = BuildPaymentConfiguration(
                paymentConfigStart, electronicNumber, orderIdentifier, agreementIdentifier, buyerReference);

        var (success, error) = await _mpClient.UpdateBillingAsync(mpGuid, request, ct);
        if (!success)
        {
            var msg = $"MP {mpId}: failed to set BillingConfiguration: {error}";
            errors.Add(msg);
            _logLine($"  FAILED: {msg}");
        }
        else
        {
            if (!billingOk)
                _logLine($"  MP {mpId}: BillingConfiguration set (Currency=DKK, JointInvoice={!string.IsNullOrEmpty(jointInvoiceGroupId)})");
            if (hasPaymentOverride)
                _logLine($"  MP {mpId}: PaymentConfiguration override set (EAN='{electronicNumber}', OrderId='{orderIdentifier}', AgreementId='{agreementIdentifier}', BuyerRef='{buyerReference}')");
        }
    }

    private async Task<(int Applied, int Skipped, int Failed)> ApplyBillingExemptionsAsync(
        Dictionary<string, Guid> mpGuids,
        SettlBillingExemptionContext ctx,
        IReadOnlyDictionary<string, string?> exemptTemplateByMpId,
        bool dryRun, List<string> errors, CancellationToken ct)
    {
        int applied = 0, skipped = 0, failed = 0;
        foreach (var (mpId, mpGuid) in mpGuids)
        {
            // The domain already decided: non-null = this MP is on a fee-exempt contract template.
            if (!exemptTemplateByMpId.TryGetValue(mpId, out var matched) || matched is null) continue;

            if (dryRun)
            {
                _logLine($"  [dry-run] MP {mpId}: would apply '{ctx.ProductElementCode}' exemption (template={matched})");
                applied++;
                continue;
            }

            var (current, getErr) = await _mpClient.GetBillingByIdAsync(mpGuid, null, ct);
            if (getErr != null)
            {
                var msg = $"MP {mpId}: GET billing failed before exemption: {getErr}";
                errors.Add(msg);
                _logLine($"  FAILED: {msg}");
                failed++;
                continue;
            }

            if (current?.Exemptions?.Any(e => e.ProductElementId == ctx.ProductElementId) == true)
            {
                _logLine($"  MP {mpId}: '{ctx.ProductElementCode}' exemption already applied");
                skipped++;
                continue;
            }

            var request = new AddMeteringPointExemptionsRequest
            {
                Exemptions =
                [
                    new BillingExemptionDto
                    {
                        ProductElementId = ctx.ProductElementId,
                        ExemptionPercentage = ctx.Percentage
                    }
                ]
            };
            var (success, error) = await _mpClient.AddExemptionsAsync(mpGuid, request, ct);
            if (!success)
            {
                var msg = $"MP {mpId}: failed to apply '{ctx.ProductElementCode}' exemption: {error}";
                errors.Add(msg);
                _logLine($"  FAILED: {msg}");
                failed++;
            }
            else
            {
                _logLine($"  MP {mpId}: applied '{ctx.ProductElementCode}' exemption (template={matched})");
                applied++;
            }
        }
        return (applied, skipped, failed);
    }

    /// <summary>
    /// Adds the customer to all Settlement Groups whose BillingPlanCode appears on the customer's
    /// contracts. Verdo SettlementGroups are keyed by CustomerId, not by MeteringPointId — Settl
    /// rejects MP-based membership in practice.
    /// </summary>
    /// <summary>
    /// Assigns the customer to each settlement group its contracts map to. Returns per-customer
    /// counts: <c>Added</c> (in dry-run: "would add"), <c>SkippedExisting</c> (already a member),
    /// <c>Failed</c> (group not found in Settl or the add call failed). Errors are also appended
    /// to <paramref name="errors"/> so the caller surfaces them in the report.
    /// </summary>
    private async Task<(int Added, int SkippedExisting, int Failed)> AssignSettlementGroupsAsync(
        Customer customer, Guid customerGuid,
        IReadOnlyDictionary<string, string> settlementGroupMap,
        bool dryRun, List<string> errors, CancellationToken ct)
    {
        const string countryCode = "DK";
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skippedExisting = 0;
        var failed = 0;

        foreach (var contract in customer.Contracts)
        {
            var planCode = contract.Billing?.PlanCode;
            if (string.IsNullOrEmpty(planCode)) continue;
            if (!settlementGroupMap.TryGetValue(planCode, out var groupName)) continue;
            groupNames.Add(groupName);
        }

        foreach (var groupName in groupNames)
        {
            if (!_settlementGroupIdByName.TryGetValue(groupName, out var groupId))
            {
                var (group, _) = await _settlementGroupClient.FindByNameAsync(countryCode, groupName, ct);
                if (group == null)
                {
                    errors.Add($"Settlement Group '{groupName}' not found in Settl");
                    _logLine($"  FAILED: Settlement Group '{groupName}' not found");
                    failed++;
                    continue;
                }
                groupId = group.Id;
                _settlementGroupIdByName[groupName] = groupId;
            }

            if (!_settlementGroupCustomerMembers.TryGetValue(groupId, out var existingMembers))
            {
                var (members, _) = await _settlementGroupClient.ListCustomerIdsAsync(groupId, ct);
                existingMembers = members ?? [];
                _settlementGroupCustomerMembers[groupId] = existingMembers;
            }

            if (existingMembers.Contains(customerGuid))
            {
                _logLine($"  Settlement Group '{groupName}': customer {customer.AccountNumber} already member");
                skippedExisting++;
                continue;
            }

            if (dryRun)
            {
                _logLine($"  [dry-run] Settlement Group '{groupName}': would add customer {customer.AccountNumber}");
                added++;
                continue;
            }

            var request = new AddSettlementGroupCustomersRequest
            {
                Customers = [new SettlementGroupCustomerEntry { CustomerId = customerGuid, IncludeCustomerSubsidiaries = false }]
            };
            var (success, error) = await _settlementGroupClient.AddCustomersAsync(groupId, request, ct);
            if (success)
            {
                existingMembers.Add(customerGuid);
                _logLine($"  Settlement Group '{groupName}': added customer {customer.AccountNumber}");
                added++;
            }
            else
            {
                errors.Add($"Settlement Group '{groupName}' add failed: {error}");
                _logLine($"  FAILED: Settlement Group '{groupName}': {error}");
                failed++;
            }
        }

        return (added, skippedExisting, failed);
    }

    private static string MapCommodityType(Commodity commodity) => commodity switch
    {
        Commodity.Water => "Water",
        Commodity.Heat => "Heat",
        _ => "Electricity"
    };

    private SettlMigrationResult Fail(
        List<string> errors, string message,
        Dictionary<string, Guid> payerIds, Dictionary<string, Guid> mpGuids,
        int customerLinks, int payerLinks, int contractLinks, List<string> warnings)
    {
        // Log inline (mirror af MP-create-fejlens "FAILED: …"-linje) så customer/payer-create-fejl
        // er synlige i den streamede hot-run-log med det samme — ikke kun i errors-listen, hvor
        // slut-summaryens top-20-cap kan skjule dem.
        _logLine($"  FAILED: {message}");
        errors.Add(message);
        return new SettlMigrationResult(Guid.Empty, false, payerIds, mpGuids, customerLinks, payerLinks, contractLinks, warnings, errors);
    }
}
