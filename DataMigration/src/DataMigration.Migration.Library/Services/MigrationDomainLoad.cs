using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Runners;

namespace DataMigration.Migration.Services;

/// <summary>
/// Single source of truth for turning <see cref="CreateOptions"/> into a loaded
/// <see cref="XellentLoadResult"/>. Used by the <c>create</c> command and the read-only
/// <c>reconcile</c> command so both extract from Xellent with identical
/// <see cref="MapperOptions"/> (reference date, commodity, ended-cutoff, legacy-merge,
/// contract-part restore window) — a
/// prerequisite for reconcile's payload being create-true.
/// </summary>
public static class MigrationDomainLoad
{
    public static Commodity MapDeliveryCategoryToCommodity(string deliveryCategory) => deliveryCategory switch
    {
        MigrationConstants.DeliveryCategoryElectricity => Commodity.Electricity,
        MigrationConstants.DeliveryCategoryWater => Commodity.Water,
        MigrationConstants.DeliveryCategoryHeating => Commodity.Heat,
        MigrationConstants.DeliveryCategoryCharge => Commodity.EvCharging,
        _ => Commodity.Electricity
    };

    public static MapperOptions BuildMapperOptions(CreateOptions opts) => new(
        ReferenceDate: DateOnly.FromDateTime(opts.ReferenceDate),
        Commodity: MapDeliveryCategoryToCommodity(opts.DeliveryCategory),
        IncludeEndedCutoff: opts.IncludeEndedCutoff.HasValue
            ? DateOnly.FromDateTime(opts.IncludeEndedCutoff.Value)
            : null,
        IgnoredGroupNumbers: opts.IgnoredGroups is { Length: > 0 }
            ? opts.IgnoredGroups.ToHashSet(StringComparer.OrdinalIgnoreCase) : null,
        LegacyTg4Merge: opts.LegacyTg4Merge,
        BillingExemptionTemplateSuffix: opts.BillingExemptionTemplateSuffix,
        DisableRepresentativeMerge: opts.DisableRepresentativeMerge,
        RestorePartsChangedFrom: opts.RestorePartsChangedFrom,
        RestorePartsChangedBefore: opts.RestorePartsChangedBefore);

    /// <param name="onPhase">
    /// Samme fase-tilbagekald som <see cref="CreateRunner.OnPhase"/>. Udtrækket ligger uden for
    /// kørslen — kalderen henter det og rækker det videre — men det er den længste del af en
    /// kørsel, så uden det her står viseren tom i det meste af tiden. Meldes ind som
    /// <see cref="MigrationPhases.LoadingXellentData"/> og ryddes igen når udtrækket er hjemme;
    /// der er ingen deltælling at hente inde i det, og der opfindes ingen.
    /// </param>
    public static async Task<(XellentLoadResult LoadResult, WarningCollector Warnings)> LoadAsync(
        string connectionString, CreateOptions opts, CancellationToken ct = default,
        Action<string?>? onPhase = null)
    {
        var warnings = new WarningCollector();
        var mapperOptions = BuildMapperOptions(opts);
        onPhase?.Invoke(MigrationPhases.LoadingXellentData);
        try
        {
            var loadResult = await XellentDomainLoader.LoadAsync(
                connectionString, mapperOptions, warnings,
                opts.LoadEntireDataArea ? null : opts.Accounts,
                opts.DataAreaId, ct);

            // The contract template is the one thing the contract-name rules need that the domain
            // model does not carry. Read here rather than in the runner: this is the single point
            // every host — CLI, dashboard, reconcile — passes through, and the only one holding a
            // Xellent connection. Skipped entirely when no run has template rules configured.
            if (opts.TemplateProductContractMap is { Count: > 0 })
            {
                opts.ContractTemplatesByContractNum = await ContractTemplateLoader.LoadAsync(
                    connectionString,
                    opts.DataAreaId,
                    opts.LoadEntireDataArea ? null : opts.Accounts,
                    ct);
            }

            return (loadResult, warnings);
        }
        finally
        {
            // Også når udtrækket fejler: en fase der bliver stående, siger at kørslen stadig
            // henter data længe efter den er stoppet.
            onPhase?.Invoke(null);
        }
    }
}
