using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.Migration.Services.Extraction;
using DataMigration.OpsCli.Reporting;
using DataMigration.OpsCli.Services;

namespace DataMigration.OpsCli.Runners;

public class ReportRunOptions
{
    public string ProfileName { get; set; } = string.Empty;
    public string[] Accounts { get; set; } = [];
    public string DataAreaId { get; set; } = "hol";
    public required string DeliveryCategory { get; set; }
    public Dictionary<string, string>? GroupOwners { get; set; }
    public Dictionary<string, string>? AccountOwners { get; set; }
    public Dictionary<string, string>? GroupNumContractMap { get; set; }
    public Dictionary<string, string>? AccountNumContractMap { get; set; }
    public Dictionary<string, DataMigration.Migration.Configuration.ProductNumContractEntry>? ProductNumContractMap { get; set; }
    public Dictionary<string, string>? GridAreaMap { get; set; }
    public string OutputPath { get; set; } = "output";
    public required DateTime ReferenceDate { get; set; }
    public bool LegacyTg4Merge { get; set; }

    /// <summary>
    /// Indlæs hele dataområdets leveringskategori i stedet for <see cref="Accounts"/>.
    /// Sat af <c>--all-active</c>; samme mekanisme som <c>create --all-active</c>.
    /// </summary>
    public bool LoadEntireDataArea { get; set; }
}

public class ReportRunner
{
    private readonly IXellentDomainLoader _loader;

    public ReportRunner(IXellentDomainLoader loader)
    {
        _loader = loader;
    }

    public async Task<int> RunAsync(ReportRunOptions options, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"=== Settl Migration Report: {options.ProfileName} ===");
        Console.WriteLine();

        var report = new OpsMigrationReport();

        Console.WriteLine("Testing database connection...");
        try
        {
            await _loader.VerifyConnectionAsync(cancellationToken);
            Console.WriteLine("  Connected to Xellent database");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Failed to connect: {ex.Message}");
            return 1;
        }

        if (options.LoadEntireDataArea)
        {
            Console.WriteLine($"Accounts: hele {options.DataAreaId}/{options.DeliveryCategory}");
        }
        else
        {
            Console.WriteLine($"Accounts: {options.Accounts.Length}");
            if (options.Accounts.Length <= 10)
                Console.WriteLine($"  [{string.Join(", ", options.Accounts)}]");
        }
        Console.WriteLine();

        var warningCollector = new WarningCollector();
        var mapperOptions = new MapperOptions(
            ReferenceDate: DateOnly.FromDateTime(options.ReferenceDate),
            Commodity: MapDeliveryCategoryToCommodity(options.DeliveryCategory),
            LegacyTg4Merge: options.LegacyTg4Merge);
        var loadResult = await _loader.LoadAsync(
            options.LoadEntireDataArea ? null : options.Accounts,
            mapperOptions, options.DataAreaId, warningCollector, cancellationToken);
        var extractionService = new SettlMappingService(
            loadResult, warningCollector, options.ReferenceDate, deliveryCategory: options.DeliveryCategory);

        // Extract account groupings and MP mapping
        Console.Write("  Extracting account groupings... ");
        report.AccountGroupings = await extractionService.ExtractAccountGroupingsAsync(options.Accounts);
        var mpMapping = await extractionService.ExtractAccountMeteringPointMappingAsync(options.Accounts);
        report.AccountMeteringPoints = mpMapping.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Select(mp => mp.MeteringPointId).Distinct().ToList());
        report.JoinedMeteringPoints = mpMapping.Values
            .SelectMany(v => v.Where(mp => mp.IsJoined).Select(mp => mp.MeteringPointId))
            .ToHashSet();
        report.ContractMeteringPoints = mpMapping.Values
            .SelectMany(v => v)
            .GroupBy(mp => mp.ContractNum)
            .ToDictionary(g => g.Key, g => g.Select(mp => mp.MeteringPointId).Distinct().ToList());
        Console.WriteLine($"{report.AccountGroupings.Count} groupings");

        // Customers
        Console.Write("  Extracting customers... ");
        var customers = await extractionService.ExtractCustomersAsync(options.Accounts);
        report.CustomersExtracted = customers.Count;
        report.Customers = customers;

        // De rå identifikations-felter for kunder uden CVR og CPR. Kun for dem: for alle andre
        // er de normaliserede felter udfyldt, og så er de rå værdier bare det samme igen.
        foreach (var c in loadResult.Customers)
        {
            if (!string.IsNullOrWhiteSpace(c.Cvr) || !string.IsNullOrWhiteSpace(c.Cpr)) continue;

            var raw = c.RawIdentification;
            var cause = MissingIdentityClassifier.Classify(
                raw.Cvr, raw.Cpr, raw.AlternativeNumber, raw.AlternativeNumberCode);
            report.UnknownIdentities[c.AccountNumber] = new UnknownIdentitySource(
                raw.Cvr, raw.Cpr, raw.AlternativeNumber, raw.AlternativeNumberCode,
                MissingIdentityClassifier.Describe(cause));
        }
        Console.WriteLine($"{customers.Count} customers");

        // Payers — use extracted customer IDs as placeholders (dry-run style)
        Console.Write("  Extracting payers... ");
        var customerIdByAccount = customers
            .Where(c => c.CustomerNumber != null)
            .ToDictionary(c => c.CustomerNumber!, c => c.Id);
        var (payers, _) = await extractionService.ExtractPayersAsync(options.Accounts, customerIdByAccount);
        report.PayersExtracted = payers.Count;
        report.Payers = payers;
        Console.WriteLine($"{payers.Count} payers");

        // Metering points
        Console.Write("  Extracting metering points... ");
        var meteringPoints = await extractionService.ExtractMeteringPointsAsync(options.Accounts);
        report.MeteringPointsExtracted = meteringPoints.Count;
        report.MeteringPoints = meteringPoints;
        Console.WriteLine($"{meteringPoints.Count} metering points");

        // Per-MP e-faktura-reference overrides (BuyerInvoicing != null = divergerende felt skubbet
        // til MP-niveau; jf. XellentDomainMapper.ApplyMpBillingOverrides). Same source as CreateRunner.
        foreach (var mp in loadResult.MeteringPoints)
        {
            if (mp.BuyerInvoicing is not { } refs) continue;
            report.MpBillingOverrides.TryAdd(mp.Identification, new MpBillingOverrideInfo(
                refs.OrderIdentifier, refs.AgreementIdentifier, refs.BuyerReferenceIdentifier));
        }

        // Build MpContractNames (same logic as CreateRunner)
        var accountContractMap = new Dictionary<string, string>();
        if (options.GroupNumContractMap is { Count: > 0 })
        {
            foreach (var grouping in report.AccountGroupings)
            {
                if (grouping.GroupNum != null &&
                    options.GroupNumContractMap.TryGetValue(grouping.GroupNum, out var contractName))
                {
                    accountContractMap[grouping.RepresentativeAccount] = contractName;
                }
            }
        }
        if (options.AccountNumContractMap is { Count: > 0 })
        {
            foreach (var grouping in report.AccountGroupings)
            {
                foreach (var acct in grouping.MemberAccounts)
                {
                    if (options.AccountNumContractMap.TryGetValue(acct, out var contractName))
                    {
                        accountContractMap[grouping.RepresentativeAccount] = contractName;
                        break;
                    }
                }
            }
        }

        report.MpContractNames = new Dictionary<string, string>();
        foreach (var (acct, mps) in mpMapping)
        {
            foreach (var mp in mps)
            {
                string? contractName;
                if (accountContractMap.TryGetValue(acct, out var explicitName))
                {
                    contractName = explicitName;
                }
                else if (options.ProductNumContractMap is { Count: > 0 }
                    && options.ProductNumContractMap.TryGetValue(mp.ProductNum, out var entry)
                    && entry.Resolve(mp.PaymentTerm) is { Length: > 0 } productContract)
                {
                    contractName = productContract;
                }
                else
                {
                    contractName = null;
                }
                if (contractName == null)
                {
                    warningCollector.AddError($"MP {mp.MeteringPointId} (account {acct}, product {mp.ProductNum}): no contract mapping configured");
                    continue;
                }
                report.MpContractNames.TryAdd(mp.MeteringPointId, contractName);
            }
        }

        Console.WriteLine();

        var warnings = warningCollector.GetWarnings();
        var errors = warningCollector.GetErrors();

        if (errors.Count > 0)
        {
            Console.WriteLine($"Errors ({errors.Count}):");
            foreach (var e in errors.Take(20))
                Console.WriteLine($"  - {e}");
        }
        if (warnings.Count > 0)
        {
            Console.WriteLine($"Warnings ({warnings.Count}):");
            foreach (var w in warnings.Take(20))
                Console.WriteLine($"  - {w}");
        }

        // Generate and write HTML report
        var html = OpsMigrationReportGenerator.GenerateHtmlReport(
            report,
            options.ProfileName,
            dryRun: true,
            warningCollector: warningCollector,
            // All-active kender ikke sit scope som liste — tæl de konti extraktionen fandt.
            accountsCount: options.LoadEntireDataArea
                ? report.AccountGroupings.Sum(g => g.MemberAccounts.Count)
                : options.Accounts.Length,
            groupOwners: options.GroupOwners);

        Directory.CreateDirectory(options.OutputPath);
        var outputFile = Path.Combine(options.OutputPath, "report.html");
        await File.WriteAllTextAsync(outputFile, html, cancellationToken);
        Console.WriteLine($"Report written to: {Path.GetFullPath(outputFile)}");

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
