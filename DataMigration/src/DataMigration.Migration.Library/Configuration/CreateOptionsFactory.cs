using DataMigration.Migration.Runners;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// Billing-exemption settings sourced from appsettings (not the profile). Passed to
/// <see cref="CreateOptionsFactory.Build"/> so both create and reconcile feed the exemption
/// step identically.
/// </summary>
public sealed record BillingExemptionConfig(
    Guid? ProductElementId,
    string? ProductElementCode,
    string? TemplateSuffix,
    double Percentage);

/// <summary>
/// Single source of truth for translating a <see cref="ProfileOptions"/> into
/// <see cref="CreateOptions"/>. Used by the <c>create</c> command (Migration CLI) and the
/// read-only <c>reconcile</c> command (OpsCli) alike — so reconcile cannot drift from create
/// at the options layer either (the payload layer is already shared via
/// <see cref="CreateRunner"/> + capture-mode).
/// </summary>
public static class CreateOptionsFactory
{
    public static CreateOptions Build(
        ProfileOptions profile,
        string? env,
        DateTime referenceDate,
        BillingExemptionConfig exemption,
        IEnumerable<string> baseIgnoredGroups,
        bool dryRun = false,
        bool continueOnError = false,
        int? accountLimit = null,
        bool offline = false) => new()
    {
        Accounts = profile.Accounts.Distinct().ToArray(),
        DataAreaId = profile.Extraction.DataAreaId,
        DeliveryCategory = profile.Extraction.DeliveryCategory,
        DryRun = dryRun,
        Offline = offline,
        GroupOwners = profile.Extraction.GroupOwners,
        AccountOwners = profile.Extraction.AccountOwners,
        GroupNumContractMap = profile.Mappings?.GroupNum,
        SettlementGroupMap = profile.Mappings?.SettlementGroup,
        AccountNumContractMap = profile.Mappings?.AccountNum,
        ProductNumContractMap = profile.Mappings?.ProductNum,
        TemplateProductContractMap = profile.Mappings?.TemplateProducts,
        ElectricityChargerContractMap = profile.Mappings?.ElectricityChargerContractMap,
        ReferenceDate = referenceDate,
        LegacyTg4Merge = profile.Extraction.LegacyTg4Merge,
        IgnoredGroups = IgnoredGroupsLoader.Union(baseIgnoredGroups, profile.Extraction.IgnoredGroups),
        Env = env,
        BillingExemptionProductElementId = exemption.ProductElementId,
        BillingExemptionProductElementCode = exemption.ProductElementCode,
        BillingExemptionTemplateSuffix = exemption.TemplateSuffix,
        BillingExemptionPercentage = exemption.Percentage,
        ContinueOnError = continueOnError,
        AccountLimit = accountLimit,
    };

    /// <summary>
    /// Build <see cref="CreateOptions"/> for env-wide reconcile: the same options shape as
    /// <see cref="Build"/>, but the account list comes from Settl-derived accounts (not a profile)
    /// and the config comes from the per-dataarea <see cref="ReconcileMappingOptions"/>. Keeps
    /// reconcile's payload create-true at the options layer as long as the mapping mirrors the
    /// profiles' union.
    /// </summary>
    public static CreateOptions BuildFromMapping(
        ReconcileMappingOptions mapping,
        string[] accounts,
        string? env,
        DateTime referenceDate,
        BillingExemptionConfig exemption,
        IEnumerable<string> baseIgnoredGroups,
        DateOnly cos,
        bool dryRun = true,
        string? deliveryCategoryOverride = null) => new()
    {
        Accounts = accounts.Distinct().ToArray(),
        DataAreaId = mapping.DataAreaId,
        DeliveryCategory = deliveryCategoryOverride ?? mapping.DeliveryCategory,
        DryRun = dryRun,
        GroupOwners = mapping.GroupOwners,
        AccountOwners = mapping.AccountOwners,
        GroupNumContractMap = mapping.Mappings?.GroupNum,
        SettlementGroupMap = mapping.Mappings?.SettlementGroup,
        AccountNumContractMap = mapping.Mappings?.AccountNum,
        ProductNumContractMap = mapping.Mappings?.ProductNum,
        TemplateProductContractMap = mapping.Mappings?.TemplateProducts,
        ElectricityChargerContractMap = mapping.Mappings?.ElectricityChargerContractMap,
        ReferenceDate = referenceDate,
        LegacyTg4Merge = mapping.EffectiveLegacyTg4Merge(cos),
        IgnoredGroups = IgnoredGroupsLoader.Union(baseIgnoredGroups, mapping.IgnoredGroups),
        Env = env,
        BillingExemptionProductElementId = exemption.ProductElementId,
        BillingExemptionProductElementCode = exemption.ProductElementCode,
        BillingExemptionTemplateSuffix = exemption.TemplateSuffix,
        BillingExemptionPercentage = exemption.Percentage,
    };

    /// <summary>
    /// Sætter et options-objekt over på all-active-scope: extract'ets egne prædikater (dataområde,
    /// leveringskategori, referencedato) udtrykker scopet, så kontolisten ryger ud, og
    /// sammenlægningen slås fra fordi all-active er én Settl-kunde per Xellent-konto.
    /// Lever her frem for i CLI'ens Program.cs så scope og sammenlægning ikke kan skilles ad —
    /// ellers gav samme scope forskelligt resultat alt efter om det blev startet fra
    /// kommandolinjen eller gennem dashboardets batch-vej. Ejer-klyngerne (OwnerCluster/TG4)
    /// er upåvirkede — de grupperer bare konti frem for repræsentanter.
    /// </summary>
    public static CreateOptions ApplyAllActiveScope(CreateOptions options)
    {
        options.LoadEntireDataArea = true;
        options.Accounts = [];
        options.DisableRepresentativeMerge = true;
        return options;
    }
}
