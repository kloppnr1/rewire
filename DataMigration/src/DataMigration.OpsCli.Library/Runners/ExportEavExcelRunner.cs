using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Runners;

public class ExportEavExcelRunOptions
{
    public string[] Accounts { get; set; } = [];
    public string[] MeteringPoints { get; set; } = [];
    public string OutputPath { get; set; } = string.Empty;
    public DateOnly EffectiveDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateTime ReferenceDate { get; set; } = DateTime.Today;
    public string DataAreaId { get; set; } = "hol";
    public bool LegacyTg4Merge { get; set; }

    /// <summary>
    /// When set, customers still active in Xellent on this date are skipped.
    /// See <see cref="StillActiveCustomerFilter"/>.
    /// </summary>
    public DateTime? ChangeOfSupplierDate { get; set; }
}

/// <summary>
/// Read-only generator: produces a Settl bulk-import Excel (Type=MeteringPointMeter, Version=8)
/// for backfilling EstimatedAnnualVolume on already-migrated el-ekstern MPs. Reuses
/// <see cref="SettlMappingService.ExtractMeteringPointsAsync"/> so child MP exclusion,
/// representation principle and EAV lookup match the create-flow exactly.
/// </summary>
public class ExportEavExcelRunner
{
    private const string DeliveryCategory = "El-ekstern";
    private readonly IXellentDomainLoader _loader;

    public ExportEavExcelRunner(IXellentDomainLoader loader)
    {
        _loader = loader;
    }

    public async Task<int> RunAsync(ExportEavExcelRunOptions options, CancellationToken cancellationToken = default)
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

        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: Commodity.Electricity,
            LegacyTg4Merge: options.LegacyTg4Merge);
        var loadResult = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);
        var extraction = new SettlMappingService(loadResult, warnings, options.ReferenceDate, DeliveryCategory);

        var meteringPoints = await extraction.ExtractMeteringPointsAsync(options.Accounts);

        Console.Error.WriteLine($"Extracted {meteringPoints.Count} el-ekstern MP(s) (child MPs excluded)");

        var skipMpIds = await ResolveStillActiveMpIdsAsync(
            options, Commodity.Electricity, warnings, cancellationToken);

        if (skipMpIds.Count > 0)
            Console.Error.WriteLine($"Skipping {skipMpIds.Count} MP(s) belonging to customers still active in Xellent on {options.ChangeOfSupplierDate:yyyy-MM-dd}");

        var mpFilter = options.MeteringPoints.Length > 0
            ? new HashSet<string>(options.MeteringPoints, StringComparer.Ordinal)
            : null;

        var rows = meteringPoints
            .Where(mp => !skipMpIds.Contains(mp.Identification?.MeteringPointIdentification ?? ""))
            .Where(mp => mp.MeterCharacteristics?.EstimatedAnnualVolume is { } eav && eav.Value > 0)
            .Where(mp => mpFilter == null || mpFilter.Contains(mp.Identification?.MeteringPointIdentification ?? ""))
            .Select(mp => new EavRow(
                mp.Identification!.MeteringPointIdentification!,
                options.EffectiveDate,
                (decimal)mp.MeterCharacteristics!.EstimatedAnnualVolume!.Value))
            .OrderBy(r => r.MeteringPointIdentification, StringComparer.Ordinal)
            .ToList();

        EavExcelWriter.Write(options.OutputPath, rows);

        Console.Error.WriteLine($"Wrote {rows.Count} row(s) to {options.OutputPath}");
        return 0;
    }

    private async Task<HashSet<string>> ResolveStillActiveMpIdsAsync(
        ExportEavExcelRunOptions options,
        Commodity commodity,
        WarningCollector warnings,
        CancellationToken ct)
    {
        if (options.ChangeOfSupplierDate is not { } cos)
            return new HashSet<string>(StringComparer.Ordinal);
        return await StillActiveCustomerFilter.ResolveMpIdsAsync(
            _loader, options.Accounts, cos, commodity, options.DataAreaId,
            options.LegacyTg4Merge, warnings, ct);
    }
}
