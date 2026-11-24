using System.Text.Json;
using System.Text.Json.Serialization;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Engros;
using DataMigration.Domain.Xellent;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Runners;

public class ShowSettlementBasisRunOptions
{
    public string MeteringPointId { get; set; } = string.Empty;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public string DataAreaId { get; set; } = "hol";
}

/// <summary>
/// Smoke-test consumer of <see cref="XellentDomainMapper.LoadSettlementBasisAsync"/> —
/// loads a <see cref="SettlementBasis"/> for one electricity metering point in a period
/// and writes it as JSON. Validates the Engros mapper end-to-end against AXDB50.
/// </summary>
public class ShowSettlementBasisRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = Services.JsonOutput.HumanReadable
    };

    private readonly IXellentDomainLoader _loader;

    public ShowSettlementBasisRunner(IXellentDomainLoader loader)
    {
        _loader = loader;
    }

    public async Task<int> RunAsync(
        ShowSettlementBasisRunOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.MeteringPointId))
        {
            Console.Error.WriteLine("ERROR: --metering-point er påkrævet");
            return 1;
        }
        if (options.PeriodStart >= options.PeriodEnd)
        {
            Console.Error.WriteLine("ERROR: --from skal være før --to");
            return 1;
        }

        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.PeriodEnd),
            Commodity: Commodity.Electricity);

        Console.Error.WriteLine($"=== show-settlement-basis: MP={options.MeteringPointId} {options.PeriodStart:yyyy-MM-dd}..{options.PeriodEnd:yyyy-MM-dd} ===");
        var basis = await _loader.LoadSettlementBasisAsync(options.MeteringPointId, options.PeriodStart, options.PeriodEnd, mapperOptions, options.DataAreaId, warnings, cancellationToken);

        if (basis is null)
        {
            Console.Error.WriteLine("  No flex-billing-history for this MP+period. Returning empty output.");
            await output.WriteAsync("null");
            await output.WriteLineAsync();
            return 0;
        }

        Console.Error.WriteLine($"  Hours: {basis.Hours.Count}, Components: {basis.Components.Count}");
        var byKind = basis.Components.GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.Count());
        foreach (var kind in Enum.GetValues<PriceComponentKind>())
            if (byKind.TryGetValue(kind, out var count) && count > 0)
                Console.Error.WriteLine($"    {kind}: {count}");

        var json = JsonSerializer.Serialize(basis, SerializerOptions);
        await output.WriteAsync(json);
        await output.WriteLineAsync();

        return 0;
    }
}
