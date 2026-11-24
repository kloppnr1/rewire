using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.Migration.Services.MeteringPointStrategy;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class FixPaymentConfigRunOptions
{
    public string[] Accounts { get; set; } = [];
    public string[] MeteringPoints { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public string DeliveryCategory { get; set; } = MigrationConstants.DeliveryCategoryElectricity;
    public DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }
    public bool DryRun { get; set; }
    /// <summary>Spring Payer-fase over (kun MP-overrides). Default: false.</summary>
    public bool SkipPayerPhase { get; set; }
    /// <summary>Spring MP-fase over (kun Payer-niveau). Default: false.</summary>
    public bool SkipMpPhase { get; set; }
    /// <summary>Target environment for CustomerNumber-padding (ikke brugt direkte i runneren — extraction kører i Xellent-domænet og er env-agnostisk).</summary>
    public string? Env { get; set; }
}

/// <summary>
/// Backfilder PaymentConfiguration-felter på allerede-migrerede Payere og MPs efter den
/// nye 4-felt-regel introduceret i commit <c>ea55399b</c>:
///
/// <list type="bullet">
///   <item><b>Payer-niveau</b> (4 felter): <c>ElectronicNumber</c> (EAN), <c>OrderIdentifier</c>
///     (fra Einvoiceaccountcode), <c>AgreementIdentifier</c> (fra Custpurchaseorder),
///     <c>BuyerReferenceIdentifier</c> (fra Custcontactpersonid → CONTACTPERSON.NAME).
///     Default for alle MPs under Payeren.</item>
///   <item><b>MP-niveau overrides</b> (4 felter): <c>ElectronicNumber</c> (EAN),
///     <c>OrderIdentifier</c>, <c>AgreementIdentifier</c>, <c>BuyerReferenceIdentifier</c>.
///     Skrives kun når MP'ens egen kontrakt-block afviger fra Payerens. Domain-laget
///     populerer Payer-niveau-felterne null på MP når der ikke er afvigelse.</item>
/// </list>
///
/// <para>Payer-fasen: runneren GET'er Payer som rå JSON (<see cref="ISettlPayerClient.GetRawByIdAsync"/>)
/// for at diffe de fire felter mod Xellent, og skriver via <see cref="ISettlPayerClient.UpdatePayerBillingAsync"/>,
/// som indkapsler Settls flade update-form (patch kun de drivende leaves, alt andet roundtrippes verbatim
/// så customer↔payer-linket + ikke-modellerede felter overlever).</para>
///
/// <para>MP-PUT bruger den typed <c>UpdateBillingMeteringPointRequest</c>-sti (samme som
/// <c>SettlClient.EnsureBillingConfigAsync</c>) da MP-niveau-PaymentConfiguration er
/// partial-friendly i Settl Market v3 API.</para>
/// </summary>
public class FixPaymentConfigRunner
{
    private const string CountryCode = "DK";
    private readonly IXellentDomainLoader _loader;
    private readonly ISettlPayerClient _payerClient;
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly Action<string> _log;

    public FixPaymentConfigRunner(
        IXellentDomainLoader loader,
        ISettlPayerClient payerClient,
        ISettlMeteringPointClient meteringPointClient,
        Action<string>? log = null)
    {
        _loader = loader;
        _payerClient = payerClient;
        _meteringPointClient = meteringPointClient;
        _log = log ?? Console.WriteLine;
    }

    public async Task<int> RunAsync(FixPaymentConfigRunOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Accounts.Length == 0)
        {
            _log("ERROR: --account or --profile required");
            return 1;
        }

        if (!MeteringPointStrategyFactory.DelpointCategories.Contains(options.DeliveryCategory))
        {
            _log($"ERROR: fix-payment-config requires electricity DeliveryCategory (El-ekstern, El, FI, Tele, Supply, Service). Got: '{options.DeliveryCategory}'");
            return 1;
        }

        if (options.SkipPayerPhase && options.SkipMpPhase)
        {
            _log("ERROR: both --skip-payer-phase and --skip-mp-phase set — nothing to do");
            return 1;
        }

        _meteringPointClient.SetDeliveryCategory(options.DeliveryCategory);

        var warnings = new WarningCollector();
        _log($"Loading domain model for {options.Accounts.Length} account(s)...");
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: Commodity.Electricity,
            LegacyTg4Merge: options.LegacyTg4Merge);
        var loadResult = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);

        var mpFilter = options.MeteringPoints.Length > 0
            ? new HashSet<string>(options.MeteringPoints, StringComparer.Ordinal)
            : null;

        var payerOutcomes = new PhaseOutcomes("Payer");
        var mpOutcomes = new PhaseOutcomes("MP");

        if (!options.SkipPayerPhase)
            await RunPayerPhaseAsync(loadResult, options, payerOutcomes, cancellationToken);

        if (!options.SkipMpPhase)
            await RunMpPhaseAsync(loadResult, options, mpFilter, mpOutcomes, cancellationToken);

        _log("--- Summary ---");
        payerOutcomes.LogTo(_log, options.DryRun);
        mpOutcomes.LogTo(_log, options.DryRun);

        return (payerOutcomes.Failed + mpOutcomes.Failed) > 0 ? 1 : 0;
    }

    // === Phase A: Payer-niveau (4 felter via raw-JSON round-trip) ===

    private async Task RunPayerPhaseAsync(
        XellentLoadResult loadResult, FixPaymentConfigRunOptions options,
        PhaseOutcomes outcomes, CancellationToken ct)
    {
        var payerCandidates = loadResult.Customers
            .SelectMany(c => c.Payers.Select(p => (Account: c.AccountNumber, Payer: p)))
            .Where(x => !string.IsNullOrEmpty(x.Payer.BuyerInvoicing?.ElectronicNumber)
                     || !string.IsNullOrEmpty(x.Payer.BuyerInvoicing?.OrderIdentifier)
                     || !string.IsNullOrEmpty(x.Payer.BuyerInvoicing?.AgreementIdentifier)
                     || !string.IsNullOrEmpty(x.Payer.BuyerInvoicing?.BuyerReferenceIdentifier))
            .ToList();

        _log($"Phase A (Payer): {payerCandidates.Count} payer(s) with non-empty EAN/OrderId/AgreementId/BuyerRef from Xellent");

        await Parallel.ForEachAsync(
            payerCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (candidate, c) =>
            {
                var (account, payer) = candidate;
                var paddedPayerId = CustomerNumberPolicy.PadPayerIdentifierForEnv(payer.Identifier, account, options.Env, out var wasPayerPadded);
                if (wasPayerPadded)
                    _log($"  Payer [{account}/{payer.Identifier}] padded to {paddedPayerId} for env {options.Env}");
                var (info, _) = await _payerClient.SearchByPayerIdentificationAsync(paddedPayerId, CountryCode, c);
                if (info == null)
                {
                    outcomes.Increment("NotMigrated");
                    _log($"  SKIP Payer [{account}/{payer.Identifier}]: not found in Settl");
                    return;
                }

                var (json, getErr) = await _payerClient.GetRawByIdAsync(info.Id, c);
                if (json == null)
                {
                    outcomes.Increment("Failed");
                    _log($"  FAILED Payer [{account}/{payer.Identifier}]: GET {getErr}");
                    return;
                }

                var root = JsonNode.Parse(json);
                var billing = root?["PayerBilling"]?.AsObject();
                if (billing == null)
                {
                    outcomes.Increment("Failed");
                    _log($"  FAILED Payer [{account}/{payer.Identifier}]: response has no PayerBilling");
                    return;
                }

                var existingEan = billing["ElectronicNumber"]?.GetValue<string?>();
                var existingOrderId = billing["OrderIdentifier"]?.GetValue<string?>();
                var existingAgrId = billing["AgreementIdentifier"]?.GetValue<string?>();
                var existingBuyerRef = billing["BuyerReferenceIdentifier"]?.GetValue<string?>();

                var payerRefs = payer.BuyerInvoicing;
                var diffs = new List<string>();
                if (DiffersNonBlank(existingEan, payerRefs?.ElectronicNumber, out var eanAction))
                    diffs.Add($"EAN: '{existingEan}' → '{payerRefs?.ElectronicNumber}' ({eanAction})");
                if (DiffersNonBlank(existingOrderId, payerRefs?.OrderIdentifier, out var orderAction))
                    diffs.Add($"OrderId: '{existingOrderId}' → '{payerRefs?.OrderIdentifier}' ({orderAction})");
                if (DiffersNonBlank(existingAgrId, payerRefs?.AgreementIdentifier, out var agrAction))
                    diffs.Add($"AgreementId: '{existingAgrId}' → '{payerRefs?.AgreementIdentifier}' ({agrAction})");
                if (DiffersNonBlank(existingBuyerRef, payerRefs?.BuyerReferenceIdentifier, out var buyerAction))
                    diffs.Add($"BuyerRef: '{existingBuyerRef}' → '{payerRefs?.BuyerReferenceIdentifier}' ({buyerAction})");

                var hasMismatch = eanAction == PaymentConfigDiffAction.Mismatch
                    || orderAction == PaymentConfigDiffAction.Mismatch
                    || agrAction == PaymentConfigDiffAction.Mismatch
                    || buyerAction == PaymentConfigDiffAction.Mismatch;
                var hasBackfill = eanAction == PaymentConfigDiffAction.Backfill
                    || orderAction == PaymentConfigDiffAction.Backfill
                    || agrAction == PaymentConfigDiffAction.Backfill
                    || buyerAction == PaymentConfigDiffAction.Backfill;

                if (hasMismatch)
                {
                    outcomes.Increment("Mismatch");
                    _log($"  MISMATCH Payer [{account}/{payer.Identifier}]: {string.Join("; ", diffs)} — skipping (manual review)");
                    return;
                }

                if (!hasBackfill)
                {
                    outcomes.Increment("AlreadyCorrect");
                    return;
                }

                if (options.DryRun)
                {
                    outcomes.Increment("WouldUpdate");
                    _log($"  WouldUpdate Payer [{account}/{payer.Identifier}]: {string.Join("; ", diffs)}");
                    return;
                }

                // Patch only the drifted billing leaves; SettlClient.UpdatePayerBillingAsync owns the flat
                // update shape. (A wrapped { TransactionId, Payer } PUT zeroes CustomerId → deletes the
                // customer↔payer link and ignores PayerBilling — the bug this runner previously had.)
                var patches = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                if (eanAction == PaymentConfigDiffAction.Backfill)
                    patches["ElectronicNumber"] = JsonValue.Create(payerRefs?.ElectronicNumber);
                if (orderAction == PaymentConfigDiffAction.Backfill)
                    patches["OrderIdentifier"] = JsonValue.Create(payerRefs?.OrderIdentifier);
                if (agrAction == PaymentConfigDiffAction.Backfill)
                    patches["AgreementIdentifier"] = JsonValue.Create(payerRefs?.AgreementIdentifier);
                if (buyerAction == PaymentConfigDiffAction.Backfill)
                    patches["BuyerReferenceIdentifier"] = JsonValue.Create(payerRefs?.BuyerReferenceIdentifier);

                var (success, putErr) = await _payerClient.UpdatePayerBillingAsync(info.Id, patches, c);
                if (success)
                {
                    outcomes.Increment("Updated");
                    _log($"  UPDATED Payer [{account}/{payer.Identifier}]: {string.Join("; ", diffs)}");
                }
                else
                {
                    outcomes.Increment("Failed");
                    _log($"  FAILED Payer [{account}/{payer.Identifier}]: PUT {putErr}");
                }
            });
    }

    // === Phase B: MP-niveau overrides (ElectronicNumber + 3 felter via typed update) ===

    private async Task RunMpPhaseAsync(
        XellentLoadResult loadResult, FixPaymentConfigRunOptions options,
        HashSet<string>? mpFilter, PhaseOutcomes outcomes, CancellationToken ct)
    {
        // MP-overrides: kun MPs hvor Domain har populeret mindst ét af de 4 felter
        // (Domain skriver kun overrides når MP-block afviger fra Payer-block).
        var mpCandidates = loadResult.MeteringPoints
            .Where(mp => !string.IsNullOrEmpty(mp.BuyerInvoicing?.ElectronicNumber)
                      || !string.IsNullOrEmpty(mp.BuyerInvoicing?.OrderIdentifier)
                      || !string.IsNullOrEmpty(mp.BuyerInvoicing?.AgreementIdentifier)
                      || !string.IsNullOrEmpty(mp.BuyerInvoicing?.BuyerReferenceIdentifier))
            .Where(mp => mpFilter == null || mpFilter.Contains(mp.Identification))
            .ToList();

        _log($"Phase B (MP): {mpCandidates.Count} MP(s) with override-fields populated by Domain");

        await Parallel.ForEachAsync(
            mpCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
            async (mp, c) =>
            {
                var mpId = mp.Identification;
                var (existing, _) = await _meteringPointClient.SearchByIdentificationAsync(mpId, c);
                if (existing == null)
                {
                    outcomes.Increment("NotMigrated");
                    _log($"  SKIP MP {mpId}: not found in Settl");
                    return;
                }

                var (billing, _) = await _meteringPointClient.GetBillingByIdAsync(existing.Id, null, c);
                var pc = billing?.PaymentConfiguration;
                var existingEan = pc?.ElectronicNumber;
                var existingOrderId = pc?.OrderIdentifier;
                var existingAgrId = pc?.AgreementIdentifier;
                var existingBuyerRef = pc?.BuyerReference;

                var mpRefs = mp.BuyerInvoicing;
                var diffs = new List<string>();
                if (DiffersNonBlank(existingEan, mpRefs?.ElectronicNumber, out var eanAction))
                    diffs.Add($"EAN: '{existingEan}' → '{mpRefs?.ElectronicNumber}' ({eanAction})");
                if (DiffersNonBlank(existingOrderId, mpRefs?.OrderIdentifier, out var orderAction))
                    diffs.Add($"OrderId: '{existingOrderId}' → '{mpRefs?.OrderIdentifier}' ({orderAction})");
                if (DiffersNonBlank(existingAgrId, mpRefs?.AgreementIdentifier, out var agrAction))
                    diffs.Add($"AgreementId: '{existingAgrId}' → '{mpRefs?.AgreementIdentifier}' ({agrAction})");
                if (DiffersNonBlank(existingBuyerRef, mpRefs?.BuyerReferenceIdentifier, out var buyerAction))
                    diffs.Add($"BuyerRef: '{existingBuyerRef}' → '{mpRefs?.BuyerReferenceIdentifier}' ({buyerAction})");

                var hasMismatch = eanAction == PaymentConfigDiffAction.Mismatch
                    || orderAction == PaymentConfigDiffAction.Mismatch
                    || agrAction == PaymentConfigDiffAction.Mismatch
                    || buyerAction == PaymentConfigDiffAction.Mismatch;
                var hasBackfill = eanAction == PaymentConfigDiffAction.Backfill
                    || orderAction == PaymentConfigDiffAction.Backfill
                    || agrAction == PaymentConfigDiffAction.Backfill
                    || buyerAction == PaymentConfigDiffAction.Backfill;

                if (hasMismatch)
                {
                    outcomes.Increment("Mismatch");
                    _log($"  MISMATCH MP {mpId}: {string.Join("; ", diffs)} — skipping (manual review)");
                    return;
                }

                if (!hasBackfill)
                {
                    outcomes.Increment("AlreadyCorrect");
                    return;
                }

                if (options.DryRun)
                {
                    outcomes.Increment("WouldUpdate");
                    _log($"  WouldUpdate MP {mpId}: {string.Join("; ", diffs)}");
                    return;
                }

                var request = new UpdateBillingMeteringPointRequest
                {
                    PaymentConfiguration = new MeteringPointPaymentConfigurationDto
                    {
                        Start = DateOnly.FromDateTime(options.ReferenceDate),
                        LifeTime = "UntilNextHistoricalEntry",
                        ElectronicNumber = eanAction == PaymentConfigDiffAction.Backfill ? mpRefs?.ElectronicNumber : existingEan,
                        OrderIdentifier = orderAction == PaymentConfigDiffAction.Backfill ? mpRefs?.OrderIdentifier : existingOrderId,
                        AgreementIdentifier = agrAction == PaymentConfigDiffAction.Backfill ? mpRefs?.AgreementIdentifier : existingAgrId,
                        BuyerReference = buyerAction == PaymentConfigDiffAction.Backfill ? mpRefs?.BuyerReferenceIdentifier : existingBuyerRef
                    }
                };

                var (success, putErr) = await _meteringPointClient.UpdateBillingAsync(existing.Id, request, c);
                if (success)
                {
                    outcomes.Increment("Updated");
                    _log($"  UPDATED MP {mpId}: {string.Join("; ", diffs)}");
                }
                else
                {
                    outcomes.Increment("Failed");
                    _log($"  FAILED MP {mpId}: PUT {putErr}");
                }
            });
    }

    /// <summary>
    /// Thin wrapper over the shared <see cref="PaymentConfigDiff.Classify"/> rule (single source of
    /// truth — also used by the read-only <c>report-drift</c> scan). Returns whether an action is needed.
    /// </summary>
    private static bool DiffersNonBlank(string? existing, string? wanted, out PaymentConfigDiffAction action)
    {
        action = PaymentConfigDiff.Classify(existing, wanted);
        return action != PaymentConfigDiffAction.None;
    }

    private sealed class PhaseOutcomes(string label)
    {
        private readonly ConcurrentDictionary<string, int> _counts = new();
        public string Label => label;
        public int Failed => _counts.GetValueOrDefault("Failed");

        public void Increment(string key)
            => _counts.AddOrUpdate(key, 1, (_, v) => v + 1);

        public void LogTo(Action<string> log, bool dryRun)
        {
            int Get(string k) => _counts.GetValueOrDefault(k);
            var updatedLabel = dryRun ? "WouldUpdate" : "Updated";
            log($"  [{label}] {updatedLabel}={Get(dryRun ? "WouldUpdate" : "Updated")}, " +
                $"AlreadyCorrect={Get("AlreadyCorrect")}, " +
                $"Mismatch={Get("Mismatch")}, " +
                $"NotMigrated={Get("NotMigrated")}, " +
                $"Failed={Get("Failed")}");
        }
    }
}
