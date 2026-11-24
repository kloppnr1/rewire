using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class CompareRunOptions
{
    public string ProfileName { get; set; } = string.Empty;
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public required string DeliveryCategory { get; set; }
    public Dictionary<string, string>? GridAreaMap { get; set; }
    public bool Verbose { get; set; }
    public DateTime ReferenceDate { get; set; }
    /// <summary>Target Settl environment ("production" padder til 6 cifre; alle andre er no-op). See <see cref="CustomerNumberPolicy"/>.</summary>
    public string? Env { get; set; }
}

record FieldDiff(string Field, string? Xellent, string? Settl);

enum CompareStatus { Match, Mismatch, NotFound, Error }

record CustomerCompareResult(string CustomerNumber, CompareStatus Status, List<FieldDiff> Diffs, string? ErrorMessage = null);

public class CompareRunner
{
    private readonly IXellentDomainLoader _loader;
    private readonly ISettlCustomerClient _customerClient;

    public CompareRunner(IXellentDomainLoader loader, ISettlCustomerClient customerClient)
    {
        _loader = loader;
        _customerClient = customerClient;
    }

    public async Task<int> RunAsync(CompareRunOptions options, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"=== Compare customers: {options.ProfileName} ===");
        Console.WriteLine();

        try
        {
            await _loader.VerifyConnectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to connect to Xellent: {ex.Message}");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Accounts: {options.Accounts.Length}");

        var warnings = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: MapDeliveryCategoryToCommodity(options.DeliveryCategory),
            IncludeEndedCutoff: new DateOnly(2000, 1, 1));
        var loadResult = await _loader.LoadAsync(options.Accounts, mapperOptions, options.DataAreaId, warnings, cancellationToken);
        var extractionService = new SettlMappingService(
            loadResult, warnings, options.ReferenceDate, deliveryCategory: options.DeliveryCategory);

        Console.Write("Extracting customers from Xellent... ");
        var customers = await extractionService.ExtractCustomersAsync(options.Accounts);
        Console.WriteLine($"{customers.Count}");
        Console.WriteLine();

        var results = new List<CustomerCompareResult>();

        foreach (var customer in customers)
        {
            if (customer.CustomerNumber == null)
                continue;

            var settlCustomerNumber = CustomerNumberPolicy.PadForEnv(customer.CustomerNumber, options.Env, out var wasPadded);
            if (wasPadded)
                Console.Error.WriteLine($"  WARNING: CustomerNumber {customer.CustomerNumber} padded to {settlCustomerNumber} for env");

            var (settl, error) = await _customerClient.SearchByCustomerNumberAsync(settlCustomerNumber, cancellationToken: cancellationToken);

            if (error != null)
            {
                results.Add(new CustomerCompareResult(customer.CustomerNumber, CompareStatus.Error, [], error));
                continue;
            }

            if (settl == null)
            {
                results.Add(new CustomerCompareResult(customer.CustomerNumber, CompareStatus.NotFound, []));
                continue;
            }

            var diffs = CompareCustomer(customer, settl);
            var status = diffs.Count > 0 ? CompareStatus.Mismatch : CompareStatus.Match;
            results.Add(new CustomerCompareResult(customer.CustomerNumber, status, diffs));
        }

        var found = results.Count(r => r.Status != CompareStatus.NotFound && r.Status != CompareStatus.Error);
        var notFound = results.Count(r => r.Status == CompareStatus.NotFound);
        var errors = results.Count(r => r.Status == CompareStatus.Error);
        var matches = results.Count(r => r.Status == CompareStatus.Match);
        var mismatches = results.Count(r => r.Status == CompareStatus.Mismatch);

        Console.WriteLine($"Fundet: {found}  Ikke fundet: {notFound}  Match: {matches}  Mismatch: {mismatches}" +
                          (errors > 0 ? $"  Fejl: {errors}" : ""));
        Console.WriteLine();

        if (notFound > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Ikke fundet:");
            foreach (var r in results.Where(r => r.Status == CompareStatus.NotFound))
                Console.WriteLine($"  {r.CustomerNumber}");
            Console.ResetColor();
            Console.WriteLine();
        }

        if (errors > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Fejl:");
            foreach (var r in results.Where(r => r.Status == CompareStatus.Error))
                Console.WriteLine($"  {r.CustomerNumber}: {r.ErrorMessage}");
            Console.ResetColor();
            Console.WriteLine();
        }

        if (mismatches > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Mismatches:");
            foreach (var r in results.Where(r => r.Status == CompareStatus.Mismatch))
            {
                foreach (var diff in r.Diffs)
                    Console.WriteLine($"  {r.CustomerNumber}: {diff.Field,-35} Xellent={diff.Xellent ?? "(tom)"}  Settl={diff.Settl ?? "(tom)"}");
            }
            Console.ResetColor();
        }

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

    private static List<FieldDiff> CompareCustomer(CreateCustomerDto xellent, CustomerInfoDto settl)
    {
        var diffs = new List<FieldDiff>();

        void Check(string field, string? a, string? b)
        {
            a = a?.Trim();
            b = b?.Trim();
            if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                diffs.Add(new FieldDiff(field, a, b));
        }

        Check("CustomerType", xellent.CustomerType, settl.CustomerType);
        Check("VatIdentificationNumber", xellent.VatIdentificationNumber, settl.VatIdentificationNumber);
        Check("NationalIdentificationNumber", xellent.NationalIdentificationNumber, settl.NationalIdentificationNumber);
        Check("FullName", xellent.Address?.PostalAddress?.Name1?.FullName, settl.FullName);
        Check("Email", xellent.Address?.Email, settl.Email);
        Check("PostalCode", xellent.Address?.PostalAddress?.PhysicalAddress?.PostalCode, settl.PostalCode);
        Check("City", xellent.Address?.PostalAddress?.PhysicalAddress?.City, settl.City);

        return diffs;
    }
}
