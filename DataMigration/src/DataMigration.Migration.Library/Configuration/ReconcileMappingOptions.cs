using DataMigration.Core;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// The general, per-dataarea mapping that drives <b>env-wide reconcile</b> in place of a profile.
/// Reconcile (unlike <c>create</c>) does not start from a profile's account list — the accounts are
/// derived from Settl's parent InDelivery MPs. But the create-true payload still depends on the
/// config a profile otherwise carries (contract-name resolution, owner clusters, merge rule). That
/// config is consolidated here, once per dataarea (hol / HJH / Han), so reconcile can rebuild
/// create-true for any account without knowing its profile.
///
/// <para>Authored as a single authoritative file per dataarea (e.g. <c>profiles/reconcile/hol.json</c>),
/// seeded from the union of the dataarea's profiles. It is consumed by reconcile only; <c>create</c>
/// stays profile-driven. If this file and the profiles diverge, reconcile reports false drift — so
/// it is the one place to keep in sync per dataarea.</para>
/// </summary>
public class ReconcileMappingOptions
{
    public string DataAreaId { get; set; } = "hol";

    public string DeliveryCategory { get; set; } = MigrationConstants.DeliveryCategoryElectricity;

    /// <summary>GroupNum → owner-account; wraps representatives sharing a TG4 under a synthetic owner.</summary>
    public Dictionary<string, string>? GroupOwners { get; set; }

    /// <summary>Account → owner-account; explicit owner assignment.</summary>
    public Dictionary<string, string>? AccountOwners { get; set; }

    /// <summary>Legacy (CVR + TG4) merge rule for branches that must merge despite varying addresses.</summary>
    public bool LegacyTg4Merge { get; set; }

    /// <summary>COS before which to apply legacy CVR+TG4 merge. Accounts migrated before this date
    /// used TG4 as the default merge rule; fingerprint applies from this date onward.</summary>
    public DateOnly? LegacyTg4MergeCutoffCos { get; set; }

    /// <summary>Returns the effective merge rule for a given COS slice: legacy TG4 if
    /// <see cref="LegacyTg4Merge"/> is set unconditionally, or if the slice COS is before
    /// <see cref="LegacyTg4MergeCutoffCos"/>; fingerprint otherwise.</summary>
    public bool EffectiveLegacyTg4Merge(DateOnly cos) =>
        LegacyTg4Merge || (LegacyTg4MergeCutoffCos.HasValue && cos < LegacyTg4MergeCutoffCos.Value);

    /// <summary>TG4-værdier der hverken merger eller danner OwnerCluster.
    ///
    /// <para>Ikke længere en kopi der skal holdes i sync med profilerne: de grupper der gælder
    /// hele dataområdet står i appsettings og lægges automatisk til (se
    /// <see cref="IgnoredGroupsLoader"/>). Her skriver man kun grupper der er særlige for netop
    /// dette dataområde — de lægges oveni basen, de erstatter den ikke.</para></summary>
    public string[]? IgnoredGroups { get; set; }

    /// <summary>Contract-name + settlement-group resolution maps (same shape as a profile's Mappings).</summary>
    public MappingOptions? Mappings { get; set; }

    /// <summary>
    /// "Draw a line in the sand" suppressions: drift rows that fall within the configured COS
    /// period and match the specified type / subtype / kind / category filters are dropped from
    /// reconcile output. Use for drift we have accepted as-is and do not intend to fix.
    /// <see cref="HistoricalSuppression.CosFrom"/> and <see cref="HistoricalSuppression.CosTo"/>
    /// are both nullable (null = open-ended). Filter arrays are ANDed (empty = match all).
    /// </summary>
    public HistoricalSuppression[]? HistoricalSuppressions { get; set; }
}

/// <summary>
/// One "line in the sand" entry. A drift row is suppressed when its effective COS falls in
/// [<see cref="CosFrom"/>, <see cref="CosTo"/>] (both inclusive; null = open-ended) AND all
/// non-empty filter arrays match. Effective COS = row's own <c>Cos</c> for MeteringPoint rows;
/// <c>CosFrom</c> (representative's earliest COS) for Customer/Payer rows. Rows with no
/// resolvable COS are never suppressed. <see cref="RepresentativeAccounts"/> suppresses an
/// entire representative's subtree (Customer + all Payers + all MPs) in one entry.
/// </summary>
public sealed class HistoricalSuppression
{
    /// <summary>Inclusive lower bound of the COS period. Null = no lower bound.</summary>
    public DateOnly? CosFrom { get; set; }

    /// <summary>Inclusive upper bound of the COS period. Null = no upper bound.</summary>
    public DateOnly? CosTo { get; set; }

    /// <summary>Entity types to match: "Customer", "Payer", "MeteringPoint". Empty = all.</summary>
    public string[] Types { get; set; } = [];

    /// <summary>SubType values to match: "null" or "" (core entity fields with no subtype facet),
    /// "Billing", "Exemption", "CustomerLink", "SettlementGroup", "PriceList", "PayerLink".
    /// Use "null" in JSON to select rows with no subtype (prefer over empty string). Empty array = all.</summary>
    public string[] SubTypes { get; set; } = [];

    /// <summary>DriftKind names to match: "FieldDiff", "MissingInSettl", "MissingInXellent",
    /// "Anomaly". Empty = all.</summary>
    public string[] Kinds { get; set; } = [];

    /// <summary>DriftCategorizer category labels to match (same strings as the CSV Category column).
    /// E.g. <c>"Billing reference (EAN/Order/Agreement/BuyerRef)"</c>, <c>"Contact / channel"</c>.
    /// Empty = all.</summary>
    public string[] Categories { get; set; } = [];

    /// <summary>EntityKey values to match (CustomerNumber, PayerIdentification, or MP identification).
    /// Same value as the EntityKey column in the CSV. Empty = all.</summary>
    public string[] EntityKeys { get; set; } = [];

    /// <summary>Representative Xellent account numbers to match. Suppresses ALL drift rows
    /// (Customer, Payer, all MeteringPoints) that belong to the specified representative,
    /// regardless of each row's individual EntityKey. Empty = all.</summary>
    public string[] RepresentativeAccounts { get; set; } = [];
}
