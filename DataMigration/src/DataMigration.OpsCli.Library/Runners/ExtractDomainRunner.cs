using System.Text.Json;
using System.Text.Json.Serialization;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Domain.Xellent.Validation;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Runners;

public class ExtractDomainRunOptions
{
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public Commodity Commodity { get; set; } = Commodity.Electricity;
    public DateOnly? ReferenceDate { get; set; }
    public DateOnly? IncludeEndedSince { get; set; }
    public DateTime? RestorePartsChangedFrom { get; set; }
    public DateTime? RestorePartsChangedBefore { get; set; }
    public bool Validate { get; set; }
}

/// <summary>
/// First consumer of <see cref="XellentDomainMapper"/>. Loads <see cref="XellentLoadResult"/>
/// for the given accounts and writes JSON to the supplied <see cref="TextWriter"/> — provides
/// a smoke-test of the mapper end-to-end against AXDB50 and an entry point for first goldens.
/// </summary>
public class ExtractDomainRunner
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = Services.JsonOutput.HumanReadable
    };

    private readonly IXellentDomainLoader _loader;

    public ExtractDomainRunner(IXellentDomainLoader loader)
    {
        _loader = loader;
    }

    public async Task<int> RunAsync(
        ExtractDomainRunOptions options,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (options.Accounts.Length == 0)
        {
            Console.Error.WriteLine("ERROR: --accounts er påkrævet");
            return 1;
        }

        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: options.ReferenceDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Commodity: options.Commodity,
            IncludeEndedCutoff: options.IncludeEndedSince,
            RestorePartsChangedFrom: options.RestorePartsChangedFrom,
            RestorePartsChangedBefore: options.RestorePartsChangedBefore);

        Console.Error.WriteLine($"=== extract-domain: {options.Accounts.Length} accounts, commodity={options.Commodity} ===");
        var result = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);
        Console.Error.WriteLine($"  Customers: {result.Customers.Count}, MeteringPoints: {result.MeteringPoints.Count}, OwnerClusters: {result.OwnerClusters.Count}, JointInvoiceGroups: {result.JointInvoiceGroups.Count}");

        if (options.Validate)
            RunValidation(result, warnings);

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        await output.WriteAsync(json);
        await output.WriteLineAsync();

        var infos = warnings.GetInfos();
        var warningsList = warnings.GetWarnings();
        var errors = warnings.GetErrors();

        Console.Error.WriteLine($"  Warnings: {warningsList.Count}, Errors: {errors.Count}, Infos: {infos.Count}");
        foreach (var err in errors.Take(10))
            Console.Error.WriteLine($"  ERROR: {err}");
        foreach (var warn in warningsList.Take(10))
            Console.Error.WriteLine($"  WARN:  {warn}");

        return errors.Count > 0 ? 1 : 0;
    }

    private static void RunValidation(XellentLoadResult result, IWarningCollector warnings)
    {
        var validators = new ICustomerValidator[]
        {
            new PersonRequiresCprValidator(),
            new RequiredNameValidator(),
            new AddressCompletenessValidator()
        };
        var runner = new CustomerValidationRunner(validators);
        foreach (var customer in result.Customers)
            runner.RunAndCollect(customer, warnings);

        var batchValidators = new ICustomerBatchValidator[]
        {
            new EmailOnlyFingerprintSplitValidator()
        };
        runner.RunBatchAndCollect(batchValidators, result.Customers, warnings);
    }
}
