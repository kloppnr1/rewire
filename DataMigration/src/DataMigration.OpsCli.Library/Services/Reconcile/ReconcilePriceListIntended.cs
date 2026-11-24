using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// Builds the create-true intended customer↔price-list associations for one COS slice's accounts and
/// merges them into <paramref name="intendedByRep"/> (keyed by the padded representative
/// CustomerNumber). Shared by the env-wide reconcile scan (<c>ReconcileRunner</c>) and the
/// reconcile-apply path (<c>ReconcileApplier</c>) so the "intended" set is computed one way only.
///
/// <para>Mirrors the export-customer-pricelists gather→build pipeline, including the still-active
/// filter: representatives still active in Xellent on the COS date are skipped, because the export
/// that produced Settl's actual associations skipped them too. Resolves price-list names against the
/// Settl catalog (<paramref name="priceListNameToId"/>) rather than a CSV.</para>
/// </summary>
public static class ReconcilePriceListIntended
{
    public static async Task AccumulateAsync(
        string connectionString,
        IReadOnlyList<ProductElementMappingRule> productElementMappings,
        string[] accounts, XellentLoadResult loadResult, DateOnly cos, DateOnly refDate, string? env,
        string dataAreaId, bool legacyTg4Merge,
        IReadOnlyDictionary<string, string> priceListNameToId,
        Dictionary<string, List<CustomerPriceListAssociationRow>> intendedByRep,
        CancellationToken ct)
    {
        var warnings = new WarningCollector();
        var refDateTime = refDate.ToDateTime(TimeOnly.MinValue);
        var mapping = new SettlMappingService(loadResult, warnings, refDateTime, MigrationConstants.DeliveryCategoryElectricity);
        var accountMpMapping = await mapping.ExtractAccountMeteringPointMappingAsync(accounts);

        var contractNums = accountMpMapping.Values
            .SelectMany(list => list)
            .Select(mp => mp.ContractNum)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (contractNums.Length == 0) return;

        var loader = new XellentDomainLoader(connectionString);

        // Representatives still active in Xellent on the COS date — the same filter export applies. Their
        // associations were never imported to Settl, so the builder must skip them (else false MissingInSettl).
        var stillActiveOnCos = await StillActiveCustomerFilter.ResolveAsync(
            loader, accounts, cos.ToDateTime(TimeOnly.MinValue),
            Commodity.Electricity, dataAreaId, legacyTg4Merge, warnings, ct);

        var contractEndDates = await loader.LookupContractEndDatesAsync(contractNums, dataAreaId, ct);
        var deliveryRateCodes = await loader.LookupDeliveryGroupRateCodesAsync(contractNums, dataAreaId, ct);
        var contractPartLogEndDates = await loader.LookupContractPartLogEndDatesAsync(contractNums, dataAreaId, ct);

        var mpsByRep = accountMpMapping.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(mp => new PriceListMpInput(
                mp.MeteringPointId, mp.ContractNum, mp.ProductNum, mp.AmrCode,
                mp.ContractPartStartdate, mp.ContractPartEnddate)).ToList(),
            StringComparer.Ordinal);

        var result = CustomerPriceListAssociationBuilder.Build(
            mpsByRep,
            contractEndDates,
            deliveryRateCodes,
            contractPartLogEndDates,
            name => priceListNameToId.TryGetValue(name, out var id) ? id : null,
            new ProductElementCodeResolver(productElementMappings),
            stillActiveOnCos,
            cos.ToDateTime(TimeOnly.MinValue),
            refDateTime,
            env);

        foreach (var row in result.Rows)
        {
            if (!intendedByRep.TryGetValue(row.CustomerNumber, out var list))
                intendedByRep[row.CustomerNumber] = list = [];
            list.Add(row);
        }
    }
}
