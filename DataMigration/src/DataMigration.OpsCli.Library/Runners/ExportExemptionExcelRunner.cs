using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Runners;

public class ExportExemptionExcelRunOptions
{
    public string[] Accounts { get; set; } = [];
    public string[] MeteringPoints { get; set; } = [];
    public string OutputPath { get; set; } = string.Empty;
    public DateOnly EffectiveDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateTime ReferenceDate { get; set; } = DateTime.Today;
    public string DataAreaId { get; set; } = "hol";
    public bool LegacyTg4Merge { get; set; }
    public string DeliveryCategory { get; set; } = "El-ekstern";
    public string TemplateSuffix { get; set; } = "_Erh_UG";
    public string ProductElementCode { get; set; } = "Fakturagebyr";

    /// <summary>
    /// When set, customers still active in Xellent on this date are skipped.
    /// See <see cref="StillActiveCustomerFilter"/>.
    /// </summary>
    public DateTime? ChangeOfSupplierDate { get; set; }
}

/// <summary>
/// Read-only generator: produces a Settl bulk-import Excel (Type=MeteringPoint, Version=16) for
/// backfilling 100% billing exemptions on already-migrated MPs whose active contract template
/// matches <see cref="ExportExemptionExcelRunOptions.TemplateSuffix"/> (e.g. <c>_Erh_UG</c>).
/// Reuses <see cref="SettlMappingService"/> so child MP exclusion, representation principle
/// and active-template lookup match the create-flow exactly.
/// </summary>
public class ExportExemptionExcelRunner
{
    private readonly IXellentDomainLoader _loader;

    public ExportExemptionExcelRunner(IXellentDomainLoader loader)
    {
        _loader = loader;
    }

    public async Task<int> RunAsync(ExportExemptionExcelRunOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            Console.Error.WriteLine("ERROR: --output is required");
            return 1;
        }
        if (options.Accounts.Length == 0)
        {
            Console.Error.WriteLine("ERROR: specify --account or --profile");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(options.TemplateSuffix))
        {
            Console.Error.WriteLine("ERROR: --template-suffix is required");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(options.ProductElementCode))
        {
            Console.Error.WriteLine("ERROR: --product-element-code is required");
            return 1;
        }

        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: MapDeliveryCategoryToCommodity(options.DeliveryCategory),
            LegacyTg4Merge: options.LegacyTg4Merge,
            // Drive the domain exemption-marker with this command's suffix so the qualify-decision
            // is made once in the mapper (MeteringPoint.BillingExemptionTemplate), not re-matched here.
            BillingExemptionTemplateSuffix: options.TemplateSuffix);
        var loadResult = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);
        var extraction = new SettlMappingService(loadResult, warnings, options.ReferenceDate, options.DeliveryCategory);

        var meteringPoints = await extraction.ExtractMeteringPointsAsync(options.Accounts);
        // MPs the mapper marked as fee-exempt (its active contract template ends with the suffix).
        var exemptMpIds = loadResult.MeteringPoints
            .Where(mp => mp.BillingExemptionTemplate != null)
            .Select(mp => mp.Identification)
            .ToHashSet(StringComparer.Ordinal);

        Console.Error.WriteLine($"Extracted {meteringPoints.Count} {options.DeliveryCategory} MP(s) (child MPs excluded)");

        var commodity = MapDeliveryCategoryToCommodity(options.DeliveryCategory);
        var skipMpIds = options.ChangeOfSupplierDate is { } cos
            ? await StillActiveCustomerFilter.ResolveMpIdsAsync(
                _loader, options.Accounts, cos, commodity, options.DataAreaId,
                options.LegacyTg4Merge, warnings, cancellationToken)
            : new HashSet<string>(StringComparer.Ordinal);
        if (skipMpIds.Count > 0)
            Console.Error.WriteLine($"Skipping {skipMpIds.Count} MP(s) belonging to customers still active in Xellent on {options.ChangeOfSupplierDate:yyyy-MM-dd}");

        var mpFilter = options.MeteringPoints.Length > 0
            ? new HashSet<string>(options.MeteringPoints, StringComparer.Ordinal)
            : null;

        var rows = meteringPoints
            .Select(mp => mp.Identification?.MeteringPointIdentification)
            .Where(id => !string.IsNullOrEmpty(id))
            .Where(id => !skipMpIds.Contains(id!))
            .Where(id => mpFilter == null || mpFilter.Contains(id!))
            .Where(id => exemptMpIds.Contains(id!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new ExemptionRow(id!, options.EffectiveDate, options.ProductElementCode))
            .ToList();

        ExemptionExcelWriter.Write(options.OutputPath, rows);

        Console.Error.WriteLine($"Wrote {rows.Count} row(s) to {options.OutputPath} (suffix='{options.TemplateSuffix}', code='{options.ProductElementCode}')");
        return 0;
    }

    private static Commodity MapDeliveryCategoryToCommodity(string deliveryCategory) => deliveryCategory switch
    {
        MigrationConstants.DeliveryCategoryElectricity => Commodity.Electricity,
        MigrationConstants.DeliveryCategoryWater => Commodity.Water,
        MigrationConstants.DeliveryCategoryHeating => Commodity.Heat,
        MigrationConstants.DeliveryCategoryCharge => Commodity.EvCharging,
        _ => Commodity.Electricity
    };
}
