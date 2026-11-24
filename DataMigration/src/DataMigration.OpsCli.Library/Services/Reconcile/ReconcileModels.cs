namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Controls which commodity's Settl MPs are enumerated in the env-wide reconcile scan
/// (<c>ScanAsync</c>). Does not affect the per-representative full-payload view
/// (<c>ScanRepresentativeAsync</c>), which always covers all commodities based on the
/// captured create-true plan.
/// </summary>
public enum CommodityFilter
{
    /// <summary>Only electricity MPs (market-v3 API). Default — backward-compatible.</summary>
    Electricity,
    /// <summary>Only water/heat/district MPs (commodity API). Requires FindWaterMeteringPointsAsync
    /// in ISettlMeteringPointClient — logs a warning until that API is available.</summary>
    Water,
    /// <summary>Both electricity and water/heat MPs. Water enumeration requires Phase-2 SettlClient
    /// change (FindWaterMeteringPointsAsync) — electricity part always runs.</summary>
    All,
}

/// <summary>Kind of reconcile finding.</summary>
public enum DriftKind
{
    /// <summary>A property differs between intended (create-true) payload and actual Settl state.</summary>
    FieldDiff,
    /// <summary>Intended entity/link/exemption exists in the create payload but is absent in Settl.</summary>
    MissingInSettl,
    /// <summary>Settl has an InDelivery MP that the Xellent extract did not produce.</summary>
    MissingInXellent,
    /// <summary>Structural anomaly (e.g. Contractenddate != COS-1, &gt;1 contract per MP).</summary>
    Anomaly,
    /// <summary>Either (a) a field whose intended (create-true) value already equals actual Settl
    /// state — emitted per field by the per-representative full-payload view
    /// (<c>ScanRepresentativeAsync</c>) — or (b) a representative-level sentinel emitted by the
    /// env-wide scan (<c>ScanAsync</c>) for each representative that was fully scanned and had zero
    /// drift rows. Sentinel rows have empty Entity/EntityKey/Field/Intended/Actual; they exist so the
    /// dashboard can search for "clean" representatives. Read-only by construction — the applier never
    /// writes a Match row.</summary>
    Match,
}

/// <summary>
/// One reconcile finding, grouped per representative account. Entity + EntityKey identify the
/// compared entity (natural keys, never GUIDs); Field is the property path or anomaly code.
/// <para><see cref="Entity"/> is always one of the three domain entities (Customer, Payer,
/// MeteringPoint). <see cref="SubType"/> names the facet/API-surface within that entity (e.g.
/// Billing for a metering point's billing-v1 config, Exemption, or CustomerLink); it is blank for
/// the entity's own core fields. This keeps Entity a clean domain type instead of overloading it
/// with API surfaces like "Billing".</para>
/// </summary>
public sealed record ReconcileDriftRow(
    string RepresentativeAccount,
    string Entity,        // Customer | Payer | MeteringPoint
    string EntityKey,     // CustomerNumber / PayerIdentifier / MP identification
    string Field,
    string? Intended,
    string? Actual,
    DriftKind Kind,
    string SubType = "", // "" (core) | Billing | Exemption | CustomerLink | SettlementGroup
    DateOnly? Cos = null, // change-of-supplier date; set on MeteringPoint rows only (null for Customer/Payer)
    string? MemberAccounts = null, // comma-joined Xellent accountnums in this representative's merge-group (incl. the representative); set on every row under a representative, null on rep-less anomaly rows
    string? RepresentativeName = null, // name of the representative customer; representative-level, set on every row under a representative
    string? MemberNames = null, // names parallel to MemberAccounts (same order); the merged-away individual customers
    string? EntityName = null, // name of this row's own entity: Customer→customer name, Payer→payer name, blank for MeteringPoint
    DateOnly? CosFrom = null, // earliest COS across the representative's MPs; representative-level, set on every row under a representative (incl. Customer/Payer rows that have no single COS). Null on rep-less anomaly rows.
    DateOnly? CosTo = null, // latest COS across the representative's MPs; pairs with CosFrom to form the representative's COS period
    string? IntendedJson = null) // structured create-true intended payload for this row's write unit, serialized at scan time so reconcile-apply can write it WITHOUT re-deriving the plan via a scoped capture. Exemption rows carry the full BillingExemptionDto; billing rows carry the intended leaf's raw JSON token. Null on rows whose write still needs the capture (PriceList/SettlementGroup) or from a pre-IntendedJson cache → applier falls back to capture.
{
    /// <summary>Version Start parsed out of a version-tagged <see cref="Field"/>
    /// (e.g. "PaymentConfiguration@2024-01-01.BuyerReference" → 2024-01-01); null for untagged fields
    /// (current/latest version or any non-config field). Presentational split for consumers — <see
    /// cref="Field"/> itself keeps the inline tag because the applier parses it back to PUT that
    /// version's own Start in place. Single source of the format: <see cref="MpConfigVersionTag"/>.</summary>
    public DateOnly? FieldVersion => MpConfigVersionTag.Split(Field).Version;

    /// <summary>The bare field path with any version tag stripped
    /// (e.g. "PaymentConfiguration@2024-01-01.BuyerReference" → "PaymentConfiguration.BuyerReference");
    /// untagged fields are returned unchanged. Display-only — apply still keys on the tagged <see
    /// cref="Field"/>.</summary>
    public string FieldPath => MpConfigVersionTag.Split(Field).Field;
}
