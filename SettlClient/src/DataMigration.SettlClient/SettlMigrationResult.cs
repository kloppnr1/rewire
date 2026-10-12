using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient;

/// <summary>
/// The <b>full intended</b> Settl create-payload for one customer bundle, captured by
/// <see cref="ISettlClient.CapturePlanAsync"/> without writes and independently of existing
/// Settl state. Mirrors what <see cref="ISettlClient.MigrateAsync"/> would POST for a fresh
/// bundle. Consumed by the DataMigration <c>reconcile</c> command, which diffs each member
/// 1:1 against the actual Settl state (matching on natural keys, excluding GUIDs/links/audit).
/// </summary>
public sealed record SettlMigrationPlan(
    string CustomerNumber,
    CreateCustomerDto Customer,
    IReadOnlyList<CreatePayerDto> Payers,
    IReadOnlyList<PlannedMeteringPoint> MeteringPoints,
    IReadOnlyList<CustomerMeteringPointDto> CustomerMpLinks,
    IReadOnlyList<PlannedPayerMpLink> PayerMpLinks,
    IReadOnlyList<PlannedContractMpLink> ContractMpLinks,
    IReadOnlyList<string> Warnings);

/// <summary>One MP's intended master-data + billing + exemptions payload.</summary>
public sealed record PlannedMeteringPoint(
    string Identification,
    bool IsElectricity,
    MeteringPointDto Master,
    UpdateBillingMeteringPointRequest? Billing,
    IReadOnlyList<BillingExemptionDto> Exemptions);

/// <summary>One intended payer→MP link, paired with the target PayerIdentifier (natural key).</summary>
public sealed record PlannedPayerMpLink(string PayerIdentifier, PayerMeteringPointDto Link);

/// <summary>One intended contract→MP link, paired with the Settl contract GUID it links to.</summary>
public sealed record PlannedContractMpLink(Guid ContractId, ContractMeteringPointDto Link);

/// <summary>
/// Output of <see cref="ISettlClient.MigrateAsync"/>: the Guids assigned by (or reused
/// from) Settl for the customer, its payer(s), its metering-points, plus link-counts
/// and any warnings/errors collected during the pipeline.
///
/// <para><c>PayersCreated</c>/<c>PayersExisting</c> and <c>MeteringPointsCreated</c>/
/// <c>MeteringPointsExisting</c> mirror <see cref="CustomerCreated"/> for the
/// per-payer/per-MP search-or-create branches.</para>
///
/// <para><c>ExistingPayerIdentifications</c>/<c>ExistingMeteringPointIdentifications</c>
/// expose the specific identifiers that were found in Settl (vs. just the counts), so
/// dry-run callers can mark them in their UI. For payers the identifier is the one that
/// actually hit Settl — may be the legacy un-padded form when the legacy fallback (see
/// <see cref="SettlMigrationContext.LegacyPayerIdentifiers"/>) rescued the search.</para>
/// </summary>
public sealed record SettlMigrationResult(
    Guid CustomerId,
    bool CustomerCreated,
    IReadOnlyDictionary<string, Guid> PayerIds,
    IReadOnlyDictionary<string, Guid> MeteringPointIds,
    int CustomerLinks,
    int PayerLinks,
    int ContractLinks,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    int PayersCreated = 0,
    int PayersExisting = 0,
    int MeteringPointsCreated = 0,
    int MeteringPointsExisting = 0,
    IReadOnlyList<string>? ExistingPayerIdentifications = null,
    IReadOnlyList<string>? ExistingMeteringPointIdentifications = null,
    int SettlementGroupsAdded = 0,
    int SettlementGroupsSkippedExisting = 0,
    int SettlementGroupsFailed = 0,
    int MeteringPointsFailed = 0,
    int CustomerMpsFailed = 0,
    int PayerMpsFailed = 0,
    int ContractMpsFailed = 0,
    int BillingExemptionsApplied = 0,
    int BillingExemptionsSkipped = 0,
    int BillingExemptionsFailed = 0);
