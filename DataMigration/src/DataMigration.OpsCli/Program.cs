using Microsoft.EntityFrameworkCore;
using System.CommandLine;
using System.Text.Json;
using DataMigration.Domain.Customers;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Services;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Reporting;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Configuration
// Both spellings System.CommandLine accepts: "--env production" and "--env=production". Missing the
// second form is not a cosmetic bug — this variable also selects the Xellent database (see below),
// so an unrecognised "--env=production" would silently read test data and compare it to production.
var envArgIndex = Array.IndexOf(args, "--env");
var offlineStateRun = args.Contains("generate-state-from-settl", StringComparer.Ordinal)
    && args.Contains("--offline", StringComparer.Ordinal)
    || args.Contains("prepare-customer-payer-repair", StringComparer.Ordinal)
    || args.Contains("build-migration-model", StringComparer.Ordinal)
    || args.Contains("report-contract-impact", StringComparer.Ordinal)
    || args.Contains("prepare-aborabat-repair", StringComparer.Ordinal)
    || args.Contains("apply-aborabat-repair", StringComparer.Ordinal)
    || args.Contains("export-active-customers", StringComparer.Ordinal)
    || args.Contains("pull-state-sources", StringComparer.Ordinal)
        && (args.Contains("--only=settl", StringComparer.Ordinal)
            || args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault() == "settl");
var settlEnv = envArgIndex >= 0 && envArgIndex + 1 < args.Length
    ? args[envArgIndex + 1]
    : args.FirstOrDefault(a => a.StartsWith("--env=", StringComparison.Ordinal))?["--env=".Length..] is { Length: > 0 } inlineEnv
        ? inlineEnv
        : Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "featuretest";
var globalVerbose = Array.IndexOf(args, "--verbose") >= 0;
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);
var config = configBuilder.Build();

var envSection = config.GetSection($"SettlApi:Environments:{settlEnv}");
if (envSection.Exists())
{
    configBuilder.AddInMemoryCollection(
        envSection.GetChildren().SelectMany(FlattenSection("SettlApi"))
    );
    config = configBuilder.Build();
}

// Env-specific Xellent override: SettlApi:Environments:{env}:XellentDatabase wins over
// ConnectionStrings:XellentDatabase. Without it --env switched only the Settl side, so a
// production run read Xellent from whatever the connection string said — on a developer machine
// a user-secret pinning AXDB50_TEST — and compared test data against production Settl.
var envXellent = config[$"SettlApi:Environments:{settlEnv}:XellentDatabase"];
var repairPreparation = args.Contains("prepare-customer-payer-repair", StringComparer.Ordinal)
    || args.Contains("build-migration-model", StringComparer.Ordinal);
var connectionString = repairPreparation ? "" : !string.IsNullOrEmpty(envXellent)
    ? envXellent
    : config.GetConnectionString("XellentDatabase")
      ?? (offlineStateRun ? "" : throw new InvalidOperationException(
          "Connection string 'XellentDatabase' not found in configuration"));

// Name the Xellent database on the startup line, not just the environment. The env override above
// beats a user-secret silently, so "[env: featuretest]" alone lets a run that was meant for
// production read the older test copy and look merely surprising rather than wrong.
Console.WriteLine(repairPreparation ? $"[env: {settlEnv}, offline preparation from saved files]"
    : $"[env: {settlEnv}, xellent: {DescribeXellentDatabase(connectionString)}]");

// Invoice history window: configured via Invoicing:HistoryYears, defaulting to
// Invoice.ExportHistoryYears when the key is missing or unparseable — never silently 0.
var historyYears = int.TryParse(config["Invoicing:HistoryYears"], out var configuredHistoryYears)
    ? configuredHistoryYears
    : Invoice.ExportHistoryYears;

static string DescribeXellentDatabase(string connectionString)
{
    try
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        var database = string.IsNullOrEmpty(builder.InitialCatalog) ? "?" : builder.InitialCatalog;
        return string.IsNullOrEmpty(builder.DataSource) ? database : $"{builder.DataSource}/{database}";
    }
    catch (Exception)
    {
        // Never let a diagnostic line stop the run.
        return "?";
    }
}

// Kundegrupper der aldrig må merge. Læses ét sted og gives til loaderen, så alle read-only
// kommandoer ser samme merge-grupper som create — ikke kun de kommandoer nogen huskede at wire.
var baseIgnoredGroups = IgnoredGroupsLoader.Load(config);

var xellentLoader = new XellentDomainLoader(connectionString, baseIgnoredGroups);

var apiOptions = config.GetSection(SettlApiOptions.SectionName).Get<SettlApiOptions>() ?? new SettlApiOptions();

static Func<IConfigurationSection, IEnumerable<KeyValuePair<string, string?>>> FlattenSection(string prefix)
{
    return section =>
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return [new KeyValuePair<string, string?>(prefix + ":" + section.Key, section.Value)];
        return children.SelectMany(FlattenSection(prefix + ":" + section.Key));
    };
}

static string DataAreaIdForEnv(string env) => env.ToLowerInvariant() switch
{
    "production-vhel" => "Han",
    "production-hev" => "HJH",
    _ => "hol"
};

var infrastructureSection = config.GetSection("Infrastructure");
var profilesDirectory = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    infrastructureSection["ProfilesDirectory"] ?? "profiles"));
var profileLoader = new ProfileLoader(profilesDirectory);

// DI
var services = new ServiceCollection();
services.Configure<SettlApiOptions>(config.GetSection(SettlApiOptions.SectionName));
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(globalVerbose ? LogLevel.Warning : LogLevel.Error);
});
services.AddHttpClient<ISettlAuthService, SettlAuthService>();
services.AddHttpClient<ISettlCustomerClient, SettlCustomerClient>();
services.AddHttpClient<ISettlPayerClient, SettlPayerClient>();
services.AddHttpClient<ISettlMeteringPointClient, SettlMeteringPointClient>();
services.AddHttpClient<IConsumerVersionCleanup, ConsumerVersionCleanup>();
services.AddHttpClient<ISettlCustomerLinkLookup, SettlCustomerLinkLookup>();
services.AddHttpClient<ISettlContractClient, SettlContractClient>();
services.AddHttpClient<ISettlSettlementGroupClient, SettlSettlementGroupClient>();
services.AddHttpClient<ISettlPriceListClient, SettlPriceListClient>();
services.AddHttpClient<ISettlRawReader, SettlRawReader>();

var serviceProvider = services.BuildServiceProvider();

var rootCommand = new RootCommand("Migration Operations CLI — fix, report, compare, generate");

// === report-meteringpoints command ===
var reportMpEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var reportMpDataAreaOption = new Option<string>("--data-area") { Description = "Override Xellent DataAreaId (default: derived from --env)" };
var reportMpReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date for Settl MP version lookup" };
var reportMpUseDeliveryStartOption = new Option<bool>("--use-delivery-start") { Description = "Hent Settl-version gyldig på leveringsperiode-startdatoen (kræver ekstra API-kald per MP)" };
var reportMpVerboseOption = new Option<bool>("--verbose") { Description = "Vis skippede MPs og per-record detaljer" };
var reportMpMeteringPointsOption = new Option<string[]>("--metering-points")
{
    Description = "Kun disse metering point identifications (space- eller komma-separeret)",
    AllowMultipleArgumentsPerToken = true
};
var reportMpIncludeChildrenOption = new Option<bool>("--include-children") { Description = "Inkluder child-målepunkter (springer normalt over)" };
var reportMpIncludeNotInDeliveryOption = new Option<bool>("--include-not-in-delivery") { Description = "Inkluder målepunkter der ikke længere er i levering (springer normalt over)" };
var reportMpMatchTypeOnlyOption = new Option<bool>("--match-type-only") { Description = "NeedsUpdate kun hvis type, subtype og MeterReadingOccurrence ikke matcher (ignorer EAV og MultiplicationFactor)" };
var reportMpShowAllOption = new Option<bool>("--show-all") { Description = "Inkluder også OK-rækker (udelades som standard)" };

var reportMeteringPointsCommand = new Command("report-meteringpoints", "Read-only scan: check currency, EAN, joint, grid, type, subtype, occurrence, EAV and meters for all MPs in Settl")
{
    reportMpEnvOption,
    reportMpDataAreaOption,
    reportMpReferenceDateOption,
    reportMpUseDeliveryStartOption,
    reportMpVerboseOption,
    reportMpMeteringPointsOption,
    reportMpIncludeChildrenOption,
    reportMpIncludeNotInDeliveryOption,
    reportMpMatchTypeOnlyOption,
    reportMpShowAllOption
};

reportMeteringPointsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var dataAreaOverride = parseResult.GetValue(reportMpDataAreaOption);
    var referenceDateOverride = parseResult.GetValue(reportMpReferenceDateOption);
    var useDeliveryStart = parseResult.GetValue(reportMpUseDeliveryStartOption);
    var verbose = parseResult.GetValue(reportMpVerboseOption);
    var meteringPointsFilter = parseResult.GetValue(reportMpMeteringPointsOption) ?? [];
    var includeChildren = parseResult.GetValue(reportMpIncludeChildrenOption);
    var includeNotInDelivery = parseResult.GetValue(reportMpIncludeNotInDeliveryOption);
    var matchTypeOnly = parseResult.GetValue(reportMpMatchTypeOnlyOption);
    var showAll = parseResult.GetValue(reportMpShowAllOption);

    meteringPointsFilter = meteringPointsFilter
        .SelectMany(m => m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();

    try
    {
        var dataAreaId = dataAreaOverride ?? DataAreaIdForEnv(settlEnv);
        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();

        var runner = new ReportMeteringPointsRunner(meteringPointClient, xellentLoader);
        var outputPath = $"report-meteringpoints-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(new ReportMeteringPointsRunOptions
        {
            DataAreaId = dataAreaId,
            ReferenceDate = referenceDateOverride,
            UseDeliveryStart = useDeliveryStart,
            Verbose = verbose,
            MeteringPoints = meteringPointsFilter,
            IncludeChildren = includeChildren,
            IncludeNotInDelivery = includeNotInDelivery,
            MatchTypeOnly = matchTypeOnly,
            ShowAll = showAll,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportMeteringPointsCommand);

// === report-payers command ===
var reportPayersEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var reportPayersVerboseOption = new Option<bool>("--verbose") { Description = "Vis per-record detaljer" };
var reportPayersShowAllOption = new Option<bool>("--show-all") { Description = "Inkluder også OK-rækker (udelades som standard)" };

var reportPayersCommand = new Command("report-payers", "Read-only scan: verify that all payers in Settl have a valid customer reference")
{
    reportPayersEnvOption,
    reportPayersVerboseOption,
    reportPayersShowAllOption
};

reportPayersCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var verbose = parseResult.GetValue(reportPayersVerboseOption);
    var showAll = parseResult.GetValue(reportPayersShowAllOption);

    try
    {
        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var payerClient = serviceProvider.GetRequiredService<ISettlPayerClient>();

        var runner = new ReportPayersRunner(customerClient, payerClient);
        var outputPath = $"report-payers-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(new ReportPayersRunOptions
        {
            Verbose = verbose,
            ShowAll = showAll,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportPayersCommand);

// === export-active-customers command ===
var exportActiveEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var exportActiveOutputOption = new Option<string?>("--output") { Description = "Output JSON path (default: active-customers-<env>-<timestamp>.json)" };
var exportActiveCustomersOption = new Option<string[]>("--customer")
{
    Description = "Only these Settl customer numbers (skips the env-wide scan; for testing)",
    AllowMultipleArgumentsPerToken = true
};
var exportActiveLimitOption = new Option<int>("--limit") { Description = "Stop after this many customers (0 = all)" };
var exportActiveParallelOption = new Option<int>("--parallel") { Description = "Customers read concurrently", DefaultValueFactory = _ => 8 };
var exportActivePrettyOption = new Option<bool>("--pretty") { Description = "Indent the JSON (much bigger file)" };
var exportActivePseudonymiseOption = new Option<bool>("--pseudonymise") { Description = "Replace CPR, CVR, names, addresses, email, phone and other identifiers with consistent fake values (sample data)" };

var exportActiveCustomersCommand = new Command("export-active-customers",
    "Read-only: every Settl customer linked today to an electricity metering point in delivery, as nested JSON")
{
    exportActiveEnvOption,
    exportActiveOutputOption,
    exportActiveCustomersOption,
    exportActiveLimitOption,
    exportActiveParallelOption,
    exportActivePrettyOption,
    exportActivePseudonymiseOption
};

exportActiveCustomersCommand.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var runner = new ExportActiveCustomersRunner(
            serviceProvider.GetRequiredService<ISettlRawReader>(),
            serviceProvider.GetRequiredService<ISettlMeteringPointClient>(),
            serviceProvider.GetRequiredService<ISettlCustomerLinkLookup>(),
            serviceProvider.GetRequiredService<ISettlCustomerClient>());
        return await runner.RunAsync(new ExportActiveCustomersRunOptions
        {
            OutputPath = parseResult.GetValue(exportActiveOutputOption)
                ?? $"active-customers-{settlEnv}-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            CustomerNumbers = (parseResult.GetValue(exportActiveCustomersOption) ?? [])
                .SelectMany(c => c.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray(),
            Limit = parseResult.GetValue(exportActiveLimitOption),
            Parallelism = parseResult.GetValue(exportActiveParallelOption),
            Pretty = parseResult.GetValue(exportActivePrettyOption),
            Pseudonymise = parseResult.GetValue(exportActivePseudonymiseOption),
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(exportActiveCustomersCommand);


// === report-payer-diff command ===
var payerDiffProfileArgument = new Argument<string>("profile") { Description = "Profile name (loads accounts + extraction settings)" };
var payerDiffEnvOption = new Option<string>("--env") { Description = "Target environment (only used for Xellent dataarea derivation)" };
var payerDiffDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: from profile or --env)" };
var payerDiffReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date override" };
var payerDiffLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Force legacy CVR+TechnicGroup4 merge (default: from profile)" };
var payerDiffShowAllOption = new Option<bool>("--show-all") { Description = "Inkluder også kunder uden fan-out (kun default-payer)" };
var payerDiffNoExplodeOption = new Option<bool>("--no-explode-payers") { Description = "Saml alle payers i én række pr. account (default: én række pr. payer)" };

var reportPayerDiffCommand = new Command(
    "report-payer-diff",
    "Read-only Xellent diff: payer fan-out før (1/kunde) vs efter (1 default + N pr. unik kontrakt-EAN), jf. commit b9496a81.")
{
    payerDiffProfileArgument,
    payerDiffEnvOption,
    payerDiffDataAreaOption,
    payerDiffReferenceDateOption,
    payerDiffLegacyTg4Option,
    payerDiffShowAllOption,
    payerDiffNoExplodeOption,
};

reportPayerDiffCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(payerDiffProfileArgument)!;
    var dataAreaOverride = parseResult.GetValue(payerDiffDataAreaOption);
    var referenceDateOverride = parseResult.GetValue(payerDiffReferenceDateOption);
    var legacyTg4Override = parseResult.GetValue(payerDiffLegacyTg4Option);
    var showAll = parseResult.GetValue(payerDiffShowAllOption);
    var noExplodePayers = parseResult.GetValue(payerDiffNoExplodeOption);

    try
    {
        var profile = profileLoader.Load(profileName);
        var accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();
        var dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId ?? DataAreaIdForEnv(settlEnv);
        var legacyTg4 = legacyTg4Override || profile.Extraction.LegacyTg4Merge;
        var referenceDate = referenceDateOverride
            ?? profile.Extraction.ReferenceDate
            ?? profile.Extraction.ChangeOfSupplierDate
            ?? DateTime.Today;

        var runner = new ReportPayerDiffRunner(connectionString, baseIgnoredGroups);
        var outputPath = $"report-payer-diff-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var exitCode = await runner.RunAsync(new ReportPayerDiffRunOptions
        {
            ProfileName = profile.Name,
            Accounts = accounts,
            DataAreaId = dataAreaId,
            ReferenceDate = referenceDate,
            LegacyTg4Merge = legacyTg4,
            ShowAll = showAll,
            ExplodePayers = !noExplodePayers,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportPayerDiffCommand);


// === report-drift command ===
var reportDriftEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var reportDriftDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: from --profile or --env)" };
var reportDriftReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Xellent snapshot date for the fan-out expectation (default: from --profile or today)" };
var reportDriftProfileOption = new Option<string?>("--profile") { Description = "Narrow the fan-out check to a profile's accounts (default: CVR-scoped from all Settl reps)" };
var reportDriftAccountsOption = new Option<string[]>("--accounts")
{
    Description = "Narrow the fan-out check to these accounts (space- or comma-separated); alternative to --profile",
    AllowMultipleArgumentsPerToken = true
};
var reportDriftLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Use legacy CVR+TechnicGroup4 merge for the fan-out expectation (default: from profile, else fingerprint)" };
var reportDriftNoFanOutOption = new Option<bool>("--no-fan-out") { Description = "Skip the payer fan-out check (MP + intrinsic payer scan only)" };
var reportDriftVerboseOption = new Option<bool>("--verbose") { Description = "Vis per-record detaljer" };
var reportDriftIncludeNotInDeliveryOption = new Option<bool>("--include-not-in-delivery") { Description = "Inkluder målepunkter der ikke længere er i levering i MP-scanningen (springer normalt over)" };
var reportDriftShowAllOption = new Option<bool>("--show-all") { Description = "Inkluder også OK-rækker (udelades som standard)" };

var reportDriftCommand = new Command(
    "report-drift",
    "Read-only unified 'forkert-i-Settl' scan: aggregates report-meteringpoints + report-payers and adds a payer fan-out drift check.")
{
    reportDriftEnvOption,
    reportDriftDataAreaOption,
    reportDriftReferenceDateOption,
    reportDriftProfileOption,
    reportDriftAccountsOption,
    reportDriftLegacyTg4Option,
    reportDriftNoFanOutOption,
    reportDriftVerboseOption,
    reportDriftIncludeNotInDeliveryOption,
    reportDriftShowAllOption,
};

reportDriftCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var dataAreaOverride = parseResult.GetValue(reportDriftDataAreaOption);
    var referenceDateOverride = parseResult.GetValue(reportDriftReferenceDateOption);
    var profileName = parseResult.GetValue(reportDriftProfileOption);
    var explicitAccounts = parseResult.GetValue(reportDriftAccountsOption) ?? [];
    var legacyTg4Override = parseResult.GetValue(reportDriftLegacyTg4Option);
    var noFanOut = parseResult.GetValue(reportDriftNoFanOutOption);
    var verbose = parseResult.GetValue(reportDriftVerboseOption);
    var includeNotInDelivery = parseResult.GetValue(reportDriftIncludeNotInDeliveryOption);
    var showAll = parseResult.GetValue(reportDriftShowAllOption);

    explicitAccounts = explicitAccounts
        .SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();

    if (!string.IsNullOrWhiteSpace(profileName) && explicitAccounts.Length > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERROR: specify either --profile OR --accounts, not both");
        Console.ResetColor();
        return 1;
    }

    try
    {
        var profile = string.IsNullOrWhiteSpace(profileName) ? null : profileLoader.Load(profileName);
        var accounts = explicitAccounts.Length > 0
            ? explicitAccounts.Distinct(StringComparer.Ordinal).ToArray()
            : profile?.Accounts.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var dataAreaId = dataAreaOverride ?? profile?.Extraction.DataAreaId ?? DataAreaIdForEnv(settlEnv);
        var legacyTg4 = legacyTg4Override || (profile?.Extraction.LegacyTg4Merge ?? false);
        var referenceDate = referenceDateOverride
            ?? profile?.Extraction.ReferenceDate
            ?? profile?.Extraction.ChangeOfSupplierDate
            ?? DateTime.Today;

        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();
        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var payerClient = serviceProvider.GetRequiredService<ISettlPayerClient>();

        var runner = new ReportDriftRunner(meteringPointClient, customerClient, payerClient, xellentLoader, connectionString, baseIgnoredGroups);
        var outputPath = $"report-drift-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var exitCode = await runner.RunAsync(new ReportDriftRunOptions
        {
            DataAreaId = dataAreaId,
            ReferenceDate = referenceDate,
            LegacyTg4Merge = legacyTg4,
            Accounts = accounts,
            NoFanOut = noFanOut,
            IncludeNotInDelivery = includeNotInDelivery,
            Verbose = verbose,
            ShowAll = showAll,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportDriftCommand);


// === report-meter-info command ===
var reportMeterInfoProfileArgument = new Argument<string?>("profile")
{
    Description = "Profile name (omit when using --accounts)",
    Arity = ArgumentArity.ZeroOrOne
};
var reportMeterInfoAccountsOption = new Option<string[]>("--accounts")
{
    Description = "Explicit account numbers (space- or comma-separated); alternative to profile",
    AllowMultipleArgumentsPerToken = true
};
var reportMeterInfoScanOption = new Option<bool>("--scan") { Description = "Scan ALL billing metering points in Settl (ignores profile/accounts)" };
var reportMeterInfoEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production)" };
var reportMeterInfoReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date override" };
var reportMeterInfoMeteringPointsOption = new Option<string[]>("--metering-points")
{
    Description = "Kun disse metering point identifications (space- eller komma-separeret)",
    AllowMultipleArgumentsPerToken = true
};
var reportMeterInfoIncludeChildrenOption = new Option<bool>("--include-children") { Description = "Inkluder child-målepunkter (springer normalt over)" };
var reportMeterInfoIncludeNotInDeliveryOption = new Option<bool>("--include-not-in-delivery") { Description = "Inkluder målepunkter der ikke længere er i levering (kun --scan; springer normalt over)" };
var reportMeterInfoMatchTypeOnlyOption = new Option<bool>("--match-type-only") { Description = "AlreadyCorrect kun hvis type, subtype og MeterReadingOccurrence matcher (ignorer EAV og MultiplicationFactor)" };
var reportMeterInfoShowAllOption = new Option<bool>("--show-all") { Description = "Inkluder også AlreadyCorrect-rækker (udelades som standard)" };

var reportMeterInfoCommand = new Command("report-meter-info", "Read-only TSV report of EstimatedAnnualVolume, type, meters and MeterReadingOccurrence — El-ekstern only (water MPs are installation-based in Xellent and not covered by this runner). Settl vs Xellent.")
{
    reportMeterInfoProfileArgument,
    reportMeterInfoAccountsOption,
    reportMeterInfoScanOption,
    reportMeterInfoEnvOption,
    reportMeterInfoReferenceDateOption,
    reportMeterInfoMeteringPointsOption,
    reportMeterInfoIncludeChildrenOption,
    reportMeterInfoIncludeNotInDeliveryOption,
    reportMeterInfoMatchTypeOnlyOption,
    reportMeterInfoShowAllOption
};

reportMeterInfoCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(reportMeterInfoProfileArgument);
    var explicitAccounts = parseResult.GetValue(reportMeterInfoAccountsOption) ?? [];
    var scan = parseResult.GetValue(reportMeterInfoScanOption);
    var referenceDateOverride = parseResult.GetValue(reportMeterInfoReferenceDateOption);
    var meteringPointsFilter = parseResult.GetValue(reportMeterInfoMeteringPointsOption) ?? [];
    var includeChildren = parseResult.GetValue(reportMeterInfoIncludeChildrenOption);
    var includeNotInDelivery = parseResult.GetValue(reportMeterInfoIncludeNotInDeliveryOption);
    var reportMeterInfoMatchTypeOnly = parseResult.GetValue(reportMeterInfoMatchTypeOnlyOption);
    var reportMeterInfoShowAll = parseResult.GetValue(reportMeterInfoShowAllOption);

    meteringPointsFilter = meteringPointsFilter
        .SelectMany(m => m.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();

    explicitAccounts = explicitAccounts
        .SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();

    if (!scan && string.IsNullOrWhiteSpace(profileName) && explicitAccounts.Length == 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERROR: provide either a profile name, --accounts <list>, or --scan");
        Console.ResetColor();
        return 1;
    }

    if (scan && (!string.IsNullOrWhiteSpace(profileName) || explicitAccounts.Length > 0))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERROR: --scan cannot be combined with profile or --accounts");
        Console.ResetColor();
        return 1;
    }

    if (!scan && !string.IsNullOrWhiteSpace(profileName) && explicitAccounts.Length > 0)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERROR: specify either a profile OR --accounts, not both");
        Console.ResetColor();
        return 1;
    }

    try
    {
        string sourceName;
        string[] accounts;
        string dataAreaId = DataAreaIdForEnv(settlEnv);
        DateTime? referenceDate;
        if (scan)
        {
            sourceName = "--scan";
            accounts = [];
            referenceDate = referenceDateOverride;
        }
        else if (explicitAccounts.Length > 0)
        {
            sourceName = $"--accounts ({explicitAccounts.Length})";
            accounts = explicitAccounts.Distinct().ToArray();
            referenceDate = referenceDateOverride;
        }
        else
        {
            var profile = profileLoader.Load(profileName!);
            sourceName = profile.Name;
            accounts = profile.Accounts.Distinct().ToArray();
            dataAreaId = profile.Extraction.DataAreaId;
            referenceDate = referenceDateOverride
                ?? profile.Extraction.ReferenceDate
                ?? profile.Extraction.ChangeOfSupplierDate;
        }

        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();

        var runner = new ReportMeterInfoRunner(customerClient, meteringPointClient, xellentLoader);
        var outputPath = $"report-meter-info-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(new ReportMeterInfoRunOptions
        {
            ProfileName = sourceName,
            Accounts = accounts,
            MeteringPoints = meteringPointsFilter,
            Scan = scan,
            DataAreaId = dataAreaId,
            ReferenceDate = referenceDate,
            IncludeChildren = includeChildren,
            IncludeNotInDelivery = includeNotInDelivery,
            MatchTypeOnly = reportMeterInfoMatchTypeOnly,
            ShowAll = reportMeterInfoShowAll,
            Env = settlEnv,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportMeterInfoCommand);


// === report-meter-history command ===
var reportMeterHistoryProfileArgument = new Argument<string>("profile") { Description = "Profile name (loads representative accounts)" };
var reportMeterHistoryOutputOption = new Option<string?>("--output") { Description = "Output CSV path (default: report-meter-history-{profile}-{yyyyMMdd}.csv in CWD)" };
var reportMeterHistoryIncludeSingleOption = new Option<bool>("--include-single-resolution") { Description = "Inkluder også MPs der kun har én resolution gennem hele historikken (default: kun MPs med ≥2 distinkte resolutioner)" };
var reportMeterHistoryDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: from profile or --env)" };
var reportMeterHistoryEnvOption = new Option<string>("--env") { Description = "(ignored — read-only Xellent command)", Hidden = true };

var reportMeterHistoryCommand = new Command(
    "report-meter-history",
    "Read-only CSV report of MeterReadingOccurrence-historik per el-ekstern MP (én række pr. resolution-periode). Bruges af Settl-dev til at oprette MeterCharacteristics-historik på MPs der har skiftet resolution.")
{
    reportMeterHistoryProfileArgument,
    reportMeterHistoryOutputOption,
    reportMeterHistoryIncludeSingleOption,
    reportMeterHistoryDataAreaOption,
    reportMeterHistoryEnvOption,
};

reportMeterHistoryCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(reportMeterHistoryProfileArgument)!;
    var outputOverride = parseResult.GetValue(reportMeterHistoryOutputOption);
    var includeSingle = parseResult.GetValue(reportMeterHistoryIncludeSingleOption);
    var dataAreaOverride = parseResult.GetValue(reportMeterHistoryDataAreaOption);

    try
    {
        var profile = profileLoader.Load(profileName);
        var accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();
        var dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId ?? DataAreaIdForEnv(settlEnv);

        var outputPath = outputOverride ?? $"report-meter-history-{profile.Name}-{DateTime.Now:yyyyMMdd}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var runner = new ReportMeterHistoryRunner(connectionString, baseIgnoredGroups);
        var exitCode = await runner.RunAsync(new ReportMeterHistoryRunOptions
        {
            ProfileName = profile.Name,
            Accounts = accounts,
            DataAreaId = dataAreaId,
            IncludeSingleResolution = includeSingle,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportMeterHistoryCommand);


// === export-eav-excel command ===
var eavAccountOption = new Option<string[]>("--account", "-a")
{
    Description = "Account number(s); repeat or comma-separate",
    AllowMultipleArgumentsPerToken = true
};
var eavMeteringPointsOption = new Option<string[]>("--metering-points")
{
    Description = "Metering point identification(s); repeat or comma-separate",
    AllowMultipleArgumentsPerToken = true
};
var eavProfileOption = new Option<string?>("--profile") { Description = "Profile name (loads accounts from profile)" };
var eavOutputOption = new Option<string>("--output", "-o") { Description = "Path to .xlsx output file", Required = true };
var eavEffectiveDateOption = new Option<DateTime?>("--effective-date") { Description = "Date written to the Date column (default: today)" };
var eavDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: derived from --env)" };
var eavLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Use legacy CVR+TechnicGroup4 merge for representation resolution" };
var eavReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date for extraction filters (default: profile.ReferenceDate or today)" };
var eavEnvOption = new Option<string>("--env") { Description = "(ignored — read-only Xellent command)", Hidden = true };

var exportEavExcelCommand = new Command(
    "export-eav-excel",
    "Export Settl bulk-import Excel for backfilling EstimatedAnnualVolume on already-migrated el-ekstern MPs (read-only)")
{
    eavAccountOption,
    eavMeteringPointsOption,
    eavProfileOption,
    eavOutputOption,
    eavEffectiveDateOption,
    eavDataAreaOption,
    eavLegacyTg4Option,
    eavReferenceDateOption,
    eavEnvOption,
};

exportEavExcelCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var accountsRaw = parseResult.GetValue(eavAccountOption) ?? [];
    var meteringPointsRaw = parseResult.GetValue(eavMeteringPointsOption) ?? [];
    var profileName = parseResult.GetValue(eavProfileOption);
    var outputPath = parseResult.GetValue(eavOutputOption)!;
    var effectiveDate = parseResult.GetValue(eavEffectiveDateOption);
    var dataAreaOverride = parseResult.GetValue(eavDataAreaOption);
    var legacyTg4 = parseResult.GetValue(eavLegacyTg4Option);
    var referenceDateOverride = parseResult.GetValue(eavReferenceDateOption);

    static string[] SplitCsv(string[] raw) => raw
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    var accounts = SplitCsv(accountsRaw);
    var meteringPoints = SplitCsv(meteringPointsRaw);

    try
    {
        var dataAreaId = dataAreaOverride ?? DataAreaIdForEnv(settlEnv);
        DateTime referenceDate = referenceDateOverride ?? DateTime.Today;
        DateTime? changeOfSupplierDate = null;
        if (!string.IsNullOrEmpty(profileName))
        {
            if (accounts.Length > 0 || meteringPoints.Length > 0)
            {
                Console.Error.WriteLine("ERROR: --profile cannot be combined with --account or --metering-points");
                return 1;
            }
            var profile = profileLoader.Load(profileName);
            accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();
            dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId;
            legacyTg4 = legacyTg4 || profile.Extraction.LegacyTg4Merge;
            referenceDate = referenceDateOverride
                ?? profile.Extraction.ReferenceDate
                ?? profile.Extraction.ChangeOfSupplierDate?.AddDays(-1)
                ?? DateTime.Today;
            changeOfSupplierDate = profile.Extraction.ChangeOfSupplierDate;
        }

        var runner = new ExportEavExcelRunner(xellentLoader);
        return await runner.RunAsync(new ExportEavExcelRunOptions
        {
            Accounts = accounts,
            MeteringPoints = meteringPoints,
            OutputPath = outputPath,
            EffectiveDate = effectiveDate.HasValue
                ? DateOnly.FromDateTime(effectiveDate.Value)
                : DateOnly.FromDateTime(DateTime.Today),
            ReferenceDate = referenceDate,
            DataAreaId = dataAreaId,
            LegacyTg4Merge = legacyTg4,
            ChangeOfSupplierDate = changeOfSupplierDate,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(exportEavExcelCommand);


// === export-exemption-excel command ===
var exemptionAccountOption = new Option<string[]>("--account", "-a")
{
    Description = "Account number(s); repeat or comma-separate",
    AllowMultipleArgumentsPerToken = true
};
var exemptionMeteringPointsOption = new Option<string[]>("--metering-points")
{
    Description = "Metering point identification(s); repeat or comma-separate",
    AllowMultipleArgumentsPerToken = true
};
var exemptionProfileOption = new Option<string?>("--profile") { Description = "Profile name (loads accounts from profile)" };
var exemptionOutputOption = new Option<string>("--output", "-o") { Description = "Path to .xlsx output file", Required = true };
var exemptionEffectiveDateOption = new Option<DateTime?>("--effective-date") { Description = "Date written to the Date column (default: today)" };
var exemptionDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: derived from --env)" };
var exemptionLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Use legacy CVR+TechnicGroup4 merge for representation resolution" };
var exemptionReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date for extraction filters (default: profile.ReferenceDate or today)" };
var exemptionDeliveryCategoryOption = new Option<string>("--delivery-category") { Description = "Xellent delivery category (default: El-ekstern). Run separately for V_Erh_UG categories (Vand/Varme/...)", DefaultValueFactory = _ => "El-ekstern" };
var exemptionTemplateSuffixOption = new Option<string>("--template-suffix") { Description = "Active contract template suffix that triggers the exemption (default: _Erh_UG)", DefaultValueFactory = _ => "_Erh_UG" };
var exemptionProductElementCodeOption = new Option<string>("--product-element-code") { Description = "Settl ProductElementCode written in the Excel (default: Fakturagebyr)", DefaultValueFactory = _ => "Fakturagebyr" };
var exemptionEnvOption = new Option<string>("--env") { Description = "(ignored — read-only Xellent command)", Hidden = true };

var exportExemptionExcelCommand = new Command(
    "export-exemption-excel",
    "Export Settl bulk-import Excel for backfilling 100% billing exemptions on already-migrated MPs whose active contract template matches a suffix (read-only)")
{
    exemptionAccountOption,
    exemptionMeteringPointsOption,
    exemptionProfileOption,
    exemptionOutputOption,
    exemptionEffectiveDateOption,
    exemptionDataAreaOption,
    exemptionLegacyTg4Option,
    exemptionReferenceDateOption,
    exemptionDeliveryCategoryOption,
    exemptionTemplateSuffixOption,
    exemptionProductElementCodeOption,
    exemptionEnvOption,
};

exportExemptionExcelCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var accountsRaw = parseResult.GetValue(exemptionAccountOption) ?? [];
    var meteringPointsRaw = parseResult.GetValue(exemptionMeteringPointsOption) ?? [];
    var profileName = parseResult.GetValue(exemptionProfileOption);
    var outputPath = parseResult.GetValue(exemptionOutputOption)!;
    var effectiveDate = parseResult.GetValue(exemptionEffectiveDateOption);
    var dataAreaOverride = parseResult.GetValue(exemptionDataAreaOption);
    var legacyTg4 = parseResult.GetValue(exemptionLegacyTg4Option);
    var referenceDateOverride = parseResult.GetValue(exemptionReferenceDateOption);
    var deliveryCategory = parseResult.GetValue(exemptionDeliveryCategoryOption)!;
    var templateSuffix = parseResult.GetValue(exemptionTemplateSuffixOption)!;
    var productElementCode = parseResult.GetValue(exemptionProductElementCodeOption)!;

    static string[] SplitCsv(string[] raw) => raw
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    var accounts = SplitCsv(accountsRaw);
    var meteringPoints = SplitCsv(meteringPointsRaw);

    try
    {
        var dataAreaId = dataAreaOverride ?? DataAreaIdForEnv(settlEnv);
        DateTime referenceDate = referenceDateOverride ?? DateTime.Today;
        DateTime? changeOfSupplierDate = null;
        if (!string.IsNullOrEmpty(profileName))
        {
            if (accounts.Length > 0 || meteringPoints.Length > 0)
            {
                Console.Error.WriteLine("ERROR: --profile cannot be combined with --account or --metering-points");
                return 1;
            }
            var profile = profileLoader.Load(profileName);
            accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();
            dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId;
            legacyTg4 = legacyTg4 || profile.Extraction.LegacyTg4Merge;
            referenceDate = referenceDateOverride
                ?? profile.Extraction.ReferenceDate
                ?? profile.Extraction.ChangeOfSupplierDate?.AddDays(-1)
                ?? DateTime.Today;
            changeOfSupplierDate = profile.Extraction.ChangeOfSupplierDate;
        }

        var runner = new ExportExemptionExcelRunner(xellentLoader);
        return await runner.RunAsync(new ExportExemptionExcelRunOptions
        {
            Accounts = accounts,
            MeteringPoints = meteringPoints,
            OutputPath = outputPath,
            EffectiveDate = effectiveDate.HasValue
                ? DateOnly.FromDateTime(effectiveDate.Value)
                : DateOnly.FromDateTime(DateTime.Today),
            ReferenceDate = referenceDate,
            DataAreaId = dataAreaId,
            LegacyTg4Merge = legacyTg4,
            DeliveryCategory = deliveryCategory,
            TemplateSuffix = templateSuffix,
            ProductElementCode = productElementCode,
            ChangeOfSupplierDate = changeOfSupplierDate,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(exportExemptionExcelCommand);


// === export-settlement-config command ===
var settlementConfigOutputOption = new Option<string>("--output", "-o") { Description = "Path to .json output file", Required = true };
var settlementConfigDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: derived from --env)" };
var settlementConfigLimitOption = new Option<int>("--limit") { Description = "Stop after N in-delivery metering points (0 = all). For quick sample runs" };
var settlementConfigExemptionsOption = new Option<bool>("--with-exemptions") { Description = "Read current Settl exemptions (one call per MP). A CVR customer with an exemption receives Erhverv_UG; SME and E18 production keep priority. Historical runs use exemptions only when the current customer matches the selected customer; ownership and date uncertainty are reported in exemptionReview" };
// Declared so the parser accepts it; the value itself is read from the raw args before the host is
// built (see top of file). Without this the command could only ever run against the default env.
var settlementConfigEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var settlementConfigNoComparisonOption = new Option<bool>("--no-comparison") { Description = "Compatibility switch; assignment and charging-ownership comparisons are no longer produced" };
var settlementConfigNoCustomerFactsOption = new Option<bool>("--no-customer-facts") { Description = "Leave out the optional customerFacts block. Proposals and metering-point coverage remain included" };
var settlementConfigDisableMergeOption = new Option<bool>("--disable-representative-merge") { Description = "Disable representative merging — each Xellent account is its own customer instead of merging on CVR+fingerprint (matches an all-active migration). The customer number still comes from Settl; this only changes the fallback for metering points Settl cannot name a customer for, and skips the resolver's expensive joins" };
var settlementConfigMpIdsOption = new Option<string[]>("--metering-points")
{
    Description = "Strict list of 18-digit metering point IDs; requires --cos-date. Use dated Xellent accounts at COS-1. Repeat or comma-separate; cannot combine with --limit",
    AllowMultipleArgumentsPerToken = true,
    Arity = ArgumentArity.OneOrMore
};
var settlementConfigCosDateOption = new Option<DateTime?>("--cos-date") { Description = "Danish supplier-change date (yyyy-MM-dd). Required with --metering-points: use this common date and Xellent contracts at COS-1, without Settl history. Without a list: select historical Settl customers and their dated portfolio. Default without a list: today's in-delivery population" };

var exportSettlementConfigCommand = new Command(
    "export-settlement-config",
    "Export proposed customer segments and settlement schedules as JSON from dated Xellent records and approved mapping rules. CVR or SME customers with dated E18 points receive Erhverv_ProduktionE18_UG. Includes source and current-assignment review. Read-only; no Settl changes.")
{
    settlementConfigOutputOption,
    settlementConfigDataAreaOption,
    settlementConfigLimitOption,
    settlementConfigExemptionsOption,
    settlementConfigEnvOption,
    settlementConfigNoComparisonOption,
    settlementConfigNoCustomerFactsOption,
    settlementConfigCosDateOption,
    settlementConfigDisableMergeOption,
    settlementConfigMpIdsOption,
};

exportSettlementConfigCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var outputPath = parseResult.GetValue(settlementConfigOutputOption)!;
    var dataAreaOverride = parseResult.GetValue(settlementConfigDataAreaOption);
    var limit = parseResult.GetValue(settlementConfigLimitOption);
    var withExemptions = parseResult.GetValue(settlementConfigExemptionsOption);
    var noComparison = parseResult.GetValue(settlementConfigNoComparisonOption);
    var noCustomerFacts = parseResult.GetValue(settlementConfigNoCustomerFactsOption);
    var cosDate = parseResult.GetValue(settlementConfigCosDateOption);
    var disableRepresentativeMerge = parseResult.GetValue(settlementConfigDisableMergeOption);
    var mpIds = parseResult.GetValue(settlementConfigMpIdsOption) ?? [];
    var selectedIds = mpIds.Length == 0 ? null : mpIds.SelectMany(s => s.Split(',',StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
    var selectedDate = cosDate is { } date ? DateOnly.FromDateTime(date) : (DateOnly?)null;

    try
    {
        SettlementConfigCoverage.Validate(selectedIds,selectedDate,limit);
        var config = SettlementConfigLoader.Load();
        Console.Error.WriteLine(
            $"Loaded {config.Schedules.Count} schedule + {config.Segments.Count} segment mapping(s) "
            + $"from {SettlementConfigLoader.DefaultPath} — missing entries fall back to the raw Xellent code");
        Console.Error.WriteLine(
            $"Candidate rules: {config.GroupSchedules?.Count ?? 0} group→schedule, "
            + $"{config.TemplateProducts?.Count ?? 0} template+product, "
            + $"settl-driven segments {(config.SettlSegments is null ? "not configured" : "configured")}");

        var needsSettl = selectedIds is null || withExemptions;
        var meteringPointClient = needsSettl ? serviceProvider.GetRequiredService<ISettlMeteringPointClient>() : null;
        // Current Settl assignment reads report missing values and failed reads independently of proposals.
        var runner = new ExportSettlementConfigRunner(
            meteringPointClient,
            xellentLoader,
            connectionString,
            log: null,
            customerClient: needsSettl ? serviceProvider.GetRequiredService<ISettlCustomerClient>() : null,
            contractClient: selectedIds is null ? serviceProvider.GetRequiredService<ISettlContractClient>() : null,
            settlementGroupClient: selectedIds is null ? serviceProvider.GetRequiredService<ISettlSettlementGroupClient>() : null,
            // Not part of the comparison: the payer listing is what names the customer behind a
            // metering point with no Xellent contract, so it stays even when --no-comparison is on.
            payerClient: needsSettl ? serviceProvider.GetRequiredService<ISettlPayerClient>() : null);
        return await runner.RunAsync(new ExportSettlementConfigRunOptions
        {
            OutputPath = outputPath,
            DataAreaId = dataAreaOverride ?? DataAreaIdForEnv(settlEnv),
            Environment = settlEnv,
            ScheduleMap = config.Schedules,
            SegmentMap = config.Segments,
            // The config is always handed over — the rules it carries decide the schedule of a
            // customer whose metering points disagree, and the segment of the unresolved ones.
            // The legacy comparison switch does not change the source proposal rules.
            Config = config,
            IncludeComparison = !noComparison,
            IncludeCustomerFacts = !noCustomerFacts,
            WithExemptions = withExemptions,
            ChangeOfSupplierDate = selectedDate,
            DisableRepresentativeMerge = disableRepresentativeMerge,
            Limit = limit,
            MeteringPointIds = selectedIds,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(exportSettlementConfigCommand);


// === export-customer-pricelists command ===
var cplProfileOption = new Option<string>("--profile", "-p")
{
    Description = "Profile name (loads accounts + ChangeOfSupplierDate)",
    Required = true
};
var cplOutputOption = new Option<string>("--output", "-o") { Description = "Path to .xlsx output file", Required = true };
var cplDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: from profile or --env)" };
var cplLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Force legacy CVR+TechnicGroup4 merge (default: from profile)" };
var cplDisableMergeOption = new Option<bool>("--disable-representative-merge") { Description = "Disable representative merging — one row group per account instead of per representative customer (matches an all-active migration). Overrides --legacy-tg4-merge" };
var cplReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date for extraction filters (default: profile.ReferenceDate or ChangeOfSupplierDate)" };
var cplEnvOption = new Option<string>("--env") { Description = "Target Settl env — determines CustomerNumber padding (production = 6 digits, production-vhel = 5, others = no padding)" };
var cplDiagnosticsOutputOption = new Option<string?>("--diagnostics-output") { Description = "Write a JSON document with what was left out of the Excel: products without a ProductElementCode mapping and rate codes without a price-list match, grouped with row counts, plus the run totals. Same content as the diagnostics log lines, in a form a host can read. Omit and no file is written." };

var exportCustomerPriceListsCommand = new Command(
    "export-customer-pricelists",
    "Export Settl bulk-import Excel (CustomerPriceListAssociation V2) from a profile's active ContractParts, using Mappings.ProductNum to resolve PriceListIdentifier (read-only)")
{
    cplProfileOption,
    cplOutputOption,
    cplDataAreaOption,
    cplLegacyTg4Option,
    cplDisableMergeOption,
    cplReferenceDateOption,
    cplEnvOption,
    cplDiagnosticsOutputOption,
};

exportCustomerPriceListsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(cplProfileOption)!;
    var outputPath = parseResult.GetValue(cplOutputOption)!;
    var dataAreaOverride = parseResult.GetValue(cplDataAreaOption);
    var legacyTg4 = parseResult.GetValue(cplLegacyTg4Option);
    var disableRepresentativeMerge = parseResult.GetValue(cplDisableMergeOption);
    var referenceDateOverride = parseResult.GetValue(cplReferenceDateOption);
    var diagnosticsOutput = parseResult.GetValue(cplDiagnosticsOutputOption);

    try
    {
        var profile = profileLoader.Load(profileName);

        var accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();
        var dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId ?? DataAreaIdForEnv(settlEnv);
        var referenceDate = referenceDateOverride
            ?? profile.Extraction.ReferenceDate
            ?? profile.Extraction.ChangeOfSupplierDate?.AddDays(-1)
            ?? DateTime.Today;

        var productElementMappings = config.GetSection("ProductElementMappings")
            .Get<List<ProductElementMappingRule>>() ?? [];

        // ChangeOfSupplierDate is the uniform association Start; falls back to ReferenceDate + 1
        // (ReferenceDate defaults to ChangeOfSupplierDate - 1) when the profile doesn't set it.
        var changeOfSupplierDate = profile.Extraction.ChangeOfSupplierDate ?? referenceDate.AddDays(1);

        var runner = new ExportCustomerPriceListsRunner(
            xellentLoader,
            serviceProvider.GetRequiredService<ISettlPriceListClient>());
        return await runner.RunAsync(new ExportCustomerPriceListsRunOptions
        {
            Accounts = accounts,
            OutputPath = outputPath,
            DataAreaId = dataAreaId,
            DeliveryCategory = profile.Extraction.DeliveryCategory ?? "El-ekstern",
            ReferenceDate = referenceDate,
            ChangeOfSupplierDate = changeOfSupplierDate,
            LegacyTg4Merge = legacyTg4 || profile.Extraction.LegacyTg4Merge,
            DisableRepresentativeMerge = disableRepresentativeMerge,
            ProductElementMappings = productElementMappings,
            Env = settlEnv,
            DiagnosticsPath = diagnosticsOutput,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(exportCustomerPriceListsCommand);


// === fix-settlement-groups command ===
var fixSgProfileOption = new Option<string>("--profile", "-p")
{
    Description = "Profile name (loads accounts + Mappings.SettlementGroup + ChangeOfSupplierDate)",
    Required = true
};
var fixSgAccountOption = new Option<string[]>("--account", "-a")
{
    Description = "Override: account number(s); repeat or comma-separate. Defaults to profile.Accounts.",
    AllowMultipleArgumentsPerToken = true
};
var fixSgDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: from profile or --env)" };
var fixSgLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Force legacy CVR+TechnicGroup4 merge (default: from profile)" };
var fixSgDryRunOption = new Option<bool>("--dry-run") { Description = "Extract + resolve groups, skip Settl mutations (prints planned changes only)" };
var fixSgEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, ...)" };
var fixSgOutputCsvOption = new Option<string?>("--output-csv") { Description = "Write semicolon-separated CSV (UTF-8 + BOM) with one row per (account, group): Account, BillingPlanNum, GroupName, Outcome (WouldAdd/AlreadyMember/NotMigrated), Reason. CSV is always complete — --limit/--offset only restrict the mutation phase. Pair with --dry-run for review-before-live." };
var fixSgLimitOption = new Option<int?>("--limit") { Description = "Maximum WouldAdd-pairs to actually post to Settl. CSV (when --output-csv is set) still lists all outcomes. Pairs outside the window are reported as 'Outside window' in the summary. Sliced across globally-sorted GroupName → Account so re-runs hit the same pairs." };
var fixSgOffsetOption = new Option<int>("--offset") { Description = "WouldAdd-pairs to skip before applying --limit. Default 0. Combine with --limit for batched roll-out." };

var fixSettlementGroupsCommand = new Command(
    "fix-settlement-groups",
    "Add already-migrated customers to existing Settl Settlement Groups based on profile.Mappings.SettlementGroup. Does NOT create groups. Verdo SettlementGroups are keyed by CustomerId — Settl rejects MP-based membership.")
{
    fixSgProfileOption, fixSgAccountOption, fixSgDataAreaOption,
    fixSgLegacyTg4Option, fixSgDryRunOption, fixSgEnvOption, fixSgOutputCsvOption,
    fixSgLimitOption, fixSgOffsetOption
};

fixSettlementGroupsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(fixSgProfileOption)!;
    var accountsRaw = parseResult.GetValue(fixSgAccountOption) ?? [];
    var dataAreaOverride = parseResult.GetValue(fixSgDataAreaOption);
    var legacyTg4Override = parseResult.GetValue(fixSgLegacyTg4Option);
    var dryRun = parseResult.GetValue(fixSgDryRunOption);
    var outputCsv = parseResult.GetValue(fixSgOutputCsvOption);
    var limit = parseResult.GetValue(fixSgLimitOption);
    var offset = parseResult.GetValue(fixSgOffsetOption);

    static string[] SplitCsv(string[] raw) => raw
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    try
    {
        var profile = profileLoader.Load(profileName);

        if (profile.Extraction.ChangeOfSupplierDate is null)
        {
            Console.Error.WriteLine($"ERROR: profile '{profileName}' has no Extraction.ChangeOfSupplierDate — required for fix-settlement-groups");
            return 1;
        }

        var settlementGroupMap = profile.Mappings?.SettlementGroup;
        if (settlementGroupMap is null || settlementGroupMap.Count == 0)
        {
            Console.Error.WriteLine($"ERROR: profile '{profileName}' has no Mappings.SettlementGroup");
            return 1;
        }

        var accounts = SplitCsv(accountsRaw);
        if (accounts.Length == 0) accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();

        var dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId ?? DataAreaIdForEnv(settlEnv);
        var legacyTg4 = legacyTg4Override || profile.Extraction.LegacyTg4Merge;

        Console.WriteLine($"fix-settlement-groups profile='{profileName}' env='{settlEnv}' dataarea='{dataAreaId}' " +
                          $"accounts={accounts.Length} groups={settlementGroupMap.Count} dry-run={dryRun}" +
                          (outputCsv != null ? $" output-csv='{outputCsv}'" : "") +
                          (limit.HasValue || offset > 0 ? $" limit={(limit?.ToString() ?? "unlimited")} offset={offset}" : ""));

        if (outputCsv != null && !dryRun)
            Console.Error.WriteLine("WARN: --output-csv without --dry-run will still mutate Settl; the CSV reflects what was attempted.");

        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var settlementGroupClient = serviceProvider.GetRequiredService<ISettlSettlementGroupClient>();

        var runner = new FixSettlementGroupsRunner(xellentLoader, customerClient, settlementGroupClient);
        return await runner.RunAsync(new FixSettlementGroupsRunOptions
        {
            Accounts = accounts,
            DataAreaId = dataAreaId,
            DeliveryCategory = profile.Extraction.DeliveryCategory ?? "El-ekstern",
            ReferenceDate = profile.Extraction.ChangeOfSupplierDate.Value.AddDays(-1),
            LegacyTg4Merge = legacyTg4,
            SettlementGroupMap = settlementGroupMap,
            DryRun = dryRun,
            OutputCsvPath = outputCsv,
            Limit = limit,
            Offset = offset,
            Env = settlEnv,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(fixSettlementGroupsCommand);

// === fix-payment-config command ===
var fixPcProfileOption = new Option<string>("--profile", "-p")
{
    Description = "Profile name (loads accounts + Extraction.ChangeOfSupplierDate)",
    Required = true
};
var fixPcAccountOption = new Option<string[]>("--account", "-a")
{
    Description = "Override: account number(s); repeat or comma-separate. Defaults to profile.Accounts.",
    AllowMultipleArgumentsPerToken = true
};
var fixPcMpOption = new Option<string[]>("--metering-points")
{
    Description = "Filter: only process these MPs in MP-phase. Comma-separate or repeat.",
    AllowMultipleArgumentsPerToken = true
};
var fixPcDataAreaOption = new Option<string?>("--data-area") { Description = "Override Xellent DataAreaId (default: from profile or --env)" };
var fixPcLegacyTg4Option = new Option<bool>("--legacy-tg4-merge") { Description = "Force legacy CVR+TechnicGroup4 merge (default: from profile)" };
var fixPcDryRunOption = new Option<bool>("--dry-run") { Description = "Resolve, lookup Settl, skip mutations (prints planned changes)" };
var fixPcSkipPayerOption = new Option<bool>("--skip-payer-phase") { Description = "Only run MP-phase (skip Payer-level backfill)" };
var fixPcSkipMpOption = new Option<bool>("--skip-mp-phase") { Description = "Only run Payer-phase (skip MP-level overrides)" };
var fixPcEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, ...)" };

var fixPaymentConfigCommand = new Command(
    "fix-payment-config",
    "Backfill PaymentConfiguration på Payer-niveau (OrderIdentifier/AgreementIdentifier/BuyerReferenceIdentifier) " +
    "og MP-niveau overrides (ElectronicNumber + samme 3) for kunder migreret før ea55399b. " +
    "Payer-PUT via raw-JSON round-trip; MP-PUT via typed UpdateBilling. El-ekstern only.")
{
    fixPcProfileOption, fixPcAccountOption, fixPcMpOption, fixPcDataAreaOption,
    fixPcLegacyTg4Option, fixPcDryRunOption, fixPcSkipPayerOption, fixPcSkipMpOption, fixPcEnvOption
};

fixPaymentConfigCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(fixPcProfileOption)!;
    var accountsRaw = parseResult.GetValue(fixPcAccountOption) ?? [];
    var mpsRaw = parseResult.GetValue(fixPcMpOption) ?? [];
    var dataAreaOverride = parseResult.GetValue(fixPcDataAreaOption);
    var legacyTg4Override = parseResult.GetValue(fixPcLegacyTg4Option);
    var dryRun = parseResult.GetValue(fixPcDryRunOption);
    var skipPayer = parseResult.GetValue(fixPcSkipPayerOption);
    var skipMp = parseResult.GetValue(fixPcSkipMpOption);

    static string[] SplitCsv(string[] raw) => raw
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    try
    {
        var profile = profileLoader.Load(profileName);

        if (profile.Extraction.ChangeOfSupplierDate is null)
        {
            Console.Error.WriteLine($"ERROR: profile '{profileName}' has no Extraction.ChangeOfSupplierDate — required for fix-payment-config");
            return 1;
        }

        var accounts = SplitCsv(accountsRaw);
        if (accounts.Length == 0) accounts = profile.Accounts.Distinct(StringComparer.Ordinal).ToArray();
        var meteringPointsFilter = SplitCsv(mpsRaw);

        var dataAreaId = dataAreaOverride ?? profile.Extraction.DataAreaId ?? DataAreaIdForEnv(settlEnv);
        var legacyTg4 = legacyTg4Override || profile.Extraction.LegacyTg4Merge;

        Console.WriteLine($"fix-payment-config profile='{profileName}' env='{settlEnv}' dataarea='{dataAreaId}' " +
                          $"accounts={accounts.Length} mp-filter={meteringPointsFilter.Length} dry-run={dryRun} " +
                          $"skip-payer={skipPayer} skip-mp={skipMp}");

        var payerClient = serviceProvider.GetRequiredService<ISettlPayerClient>();
        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();

        var runner = new FixPaymentConfigRunner(xellentLoader, payerClient, meteringPointClient, Console.WriteLine);
        return await runner.RunAsync(new FixPaymentConfigRunOptions
        {
            Accounts = accounts,
            MeteringPoints = meteringPointsFilter,
            DataAreaId = dataAreaId,
            DeliveryCategory = profile.Extraction.DeliveryCategory ?? "El-ekstern",
            ReferenceDate = profile.Extraction.ChangeOfSupplierDate.Value.AddDays(-1),
            LegacyTg4Merge = legacyTg4,
            DryRun = dryRun,
            SkipPayerPhase = skipPayer,
            SkipMpPhase = skipMp,
            Env = settlEnv,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(fixPaymentConfigCommand);

// === fix-consumer2-duplicate command ===
var fixC2dMpIdOption = new Option<string[]>("--mp-id")
{
    Description = "Metering point identification(s) or Settl GUID(s) to scope the run to. Comma-separate or repeat. Defaults to a full env-wide scan.",
    AllowMultipleArgumentsPerToken = true
};
var fixC2dLimitOption = new Option<int?>("--limit") { Description = "Maximum MPs with eligible duplicate Consumer versions to fix in one run." };
var fixC2dDryRunOption = new Option<bool>("--dry-run") { Description = "Print dated Consumer versions selected by the report rules, skip Settl mutations" };
var fixC2dEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var fixC2dFromReportOption = new Option<string?>("--from-report")
{
    Description = "report-consumer2-duplicate CSV; scopes the run to the SettlIds of rows with DuplicateConsumer2=Yes. Points are still read fresh from Settl. Cannot be combined with --mp-id."
};

var fixC2dClearFictitiousOption = new Option<bool>("--clear-fictitious-cpr2")
{
    Description = "Also clear consumer 2's CPR where it is the fictitious 1111110000 and consumer 1 has a real CPR. The name is kept. Needs --mp-id."
};

var fixConsumer2DuplicateCommand = new Command(
    "fix-consumer2-duplicate",
    "Use the report rules to select duplicate Consumer versions per delivery period, then " +
    "blank Name + NationalIdentificationNumber on the second party. VatIdentificationNumber and everything else is untouched.")
{
    fixC2dMpIdOption, fixC2dLimitOption, fixC2dDryRunOption, fixC2dEnvOption, fixC2dFromReportOption, fixC2dClearFictitiousOption
};

fixConsumer2DuplicateCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var mpIdsRaw = parseResult.GetValue(fixC2dMpIdOption) ?? [];
    var limit = parseResult.GetValue(fixC2dLimitOption);
    var dryRun = parseResult.GetValue(fixC2dDryRunOption);
    var fromReport = parseResult.GetValue(fixC2dFromReportOption);
    var clearFictitious = parseResult.GetValue(fixC2dClearFictitiousOption);

    var mpIds = mpIdsRaw
        .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    try
    {
        if (clearFictitious && (fromReport is not null || mpIds.Length == 0))
            throw new ArgumentException("--clear-fictitious-cpr2 needs --mp-id and cannot be combined with --from-report.");
        if (fromReport is not null)
        {
            if (mpIds.Length > 0) throw new ArgumentException("--from-report cannot be combined with --mp-id.");
            mpIds = Consumer2ReportInput.ReadDuplicateSettlIds(fromReport).Select(id => id.ToString()).ToArray();
            Console.WriteLine($"--from-report '{fromReport}': {mpIds.Length} metering points with DuplicateConsumer2=Yes");
            if (mpIds.Length == 0)
            {
                Console.WriteLine("Nothing to do: the report has no DuplicateConsumer2=Yes rows.");
                return 0;
            }
        }
        Console.WriteLine($"fix-consumer2-duplicate env='{settlEnv}' mp-ids={mpIds.Length} limit={(limit?.ToString() ?? "unlimited")} dry-run={dryRun} clear-fictitious-cpr2={clearFictitious}");

        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();

        var runner = new FixConsumer2DuplicateRunner(meteringPointClient, Console.WriteLine, serviceProvider.GetRequiredService<IConsumerVersionCleanup>());
        return await runner.RunAsync(new FixConsumer2DuplicateRunOptions
        {
            MeteringPointIds = mpIds,
            Limit = limit,
            DryRun = dryRun,
            ClearFictitiousCpr2 = clearFictitious,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(fixConsumer2DuplicateCommand);

// Separate read-only report; the existing cleanup options and behaviour are unchanged.
var reportC2MpOption = new Option<string[]>("--mp-id") { Description = "Metering point identifications or Settl GUIDs; default: all non-child points, including inactive points", AllowMultipleArgumentsPerToken = true };
var reportC2LimitOption = new Option<int?>("--limit") { Description = "Maximum duplicate candidates to report (positive)" };
var reportC2OutputOption = new Option<string>("--output") { Description = "CSV output path", Required = true };
var reportC2EnvOption = new Option<string>("--env") { Description = "Target environment" };
var reportConsumer2DuplicateCommand = new Command("report-consumer2-duplicate", "Read-only duplicate Consumer 2 report across all Consumer versions; flags the delivery prefix false (Na -> Na), then true (ChangeOfSupplier -> Na)")
{ reportC2MpOption, reportC2LimitOption, reportC2OutputOption, reportC2EnvOption };
reportConsumer2DuplicateCommand.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var ids = (parseResult.GetValue(reportC2MpOption) ?? [])
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        var limit = parseResult.GetValue(reportC2LimitOption);
        if (limit is <= 0) throw new ArgumentException("--limit must be positive.");
        using var output = new StreamWriter(parseResult.GetValue(reportC2OutputOption)!, false, new System.Text.UTF8Encoding(true));
        return await new ReportConsumer2DuplicateRunner(serviceProvider.GetRequiredService<ISettlMeteringPointClient>())
            { OutputPath = Path.GetFullPath(parseResult.GetValue(reportC2OutputOption)!) }
            .RunAsync(settlEnv, ids, limit, output, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
});
rootCommand.Subcommands.Add(reportConsumer2DuplicateCommand);

// === report command ===
var reportProfileArgument = new Argument<string>("profile") { Description = "Profile name" };
var reportOutputOption = new Option<string?>("--output") { Description = "Output directory for report.html (default: output/{profile})" };
var reportEnvOption = new Option<string>("--env") { Description = "Target environment" };
var reportReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date override (overrides Extraction.ReferenceDate and Extraction.ChangeOfSupplierDate in profile)" };
var reportAllActiveOption = new Option<bool>("--all-active") { Description = "Resolve scope from Xellent: every account with a non-terminated contract in the delivery category. Replaces the profile's Accounts list" };

var reportCommand = new Command("report", "Generate HTML migration report from Xellent extraction")
{
    reportProfileArgument,
    reportOutputOption,
    reportEnvOption,
    reportReferenceDateOption,
    reportAllActiveOption
};

reportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(reportProfileArgument)!;
    var outputPath = parseResult.GetValue(reportOutputOption) ?? Path.Combine("output", profileName);
    var reportReferenceDateOverride = parseResult.GetValue(reportReferenceDateOption);
    var reportAllActive = parseResult.GetValue(reportAllActiveOption);

    try
    {
        var profile = profileLoader.Load(profileName);
        var accounts = reportAllActive ? [] : profile.Accounts.Distinct().ToArray();

        var reportReferenceDate = reportReferenceDateOverride
            ?? profile.Extraction.ReferenceDate
            ?? profile.Extraction.ChangeOfSupplierDate
            ?? throw new InvalidOperationException(
                "--reference-date er påkrævet når profilen hverken har Extraction.ReferenceDate eller Extraction.ChangeOfSupplierDate");

        var reportOptions = new ReportRunOptions
        {
            ProfileName = profile.Name,
            Accounts = accounts,
            DataAreaId = profile.Extraction.DataAreaId,
            DeliveryCategory = profile.Extraction.DeliveryCategory,
            GroupOwners = profile.Extraction.GroupOwners,
            AccountOwners = profile.Extraction.AccountOwners,
            GroupNumContractMap = profile.Mappings?.GroupNum,
            AccountNumContractMap = profile.Mappings?.AccountNum,
            ProductNumContractMap = profile.Mappings?.ProductNum,
            GridAreaMap = profile.Mappings?.GridArea,
            OutputPath = outputPath,
            ReferenceDate = reportReferenceDate,
            LegacyTg4Merge = profile.Extraction.LegacyTg4Merge,
            LoadEntireDataArea = reportAllActive,
        };

        var runner = new ReportRunner(xellentLoader);
        return await runner.RunAsync(reportOptions, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportCommand);

// === compare command ===
var compareProfileArgument = new Argument<string>("profile") { Description = "Profile name to compare" };
var compareEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var compareVerboseOption = new Option<bool>("--verbose") { Description = "Show individual field diffs" };
var compareReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date override (overrides Extraction.ReferenceDate and Extraction.ChangeOfSupplierDate in profile)" };

var compareCommand = new Command("compare", "Compare extracted Xellent customers against Settl API")
{
    compareProfileArgument,
    compareEnvOption,
    compareVerboseOption,
    compareReferenceDateOption
};

compareCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(compareProfileArgument)!;
    var verbose = parseResult.GetValue(compareVerboseOption);
    var referenceDateOverride = parseResult.GetValue(compareReferenceDateOption);

    try
    {
        var profile = profileLoader.Load(profileName);
        var accounts = profile.Accounts.Distinct().ToArray();

        var resolvedDate = referenceDateOverride
            ?? profile.Extraction.ReferenceDate
            ?? profile.Extraction.ChangeOfSupplierDate
            ?? throw new InvalidOperationException(
                "--reference-date er påkrævet når profilen hverken har Extraction.ReferenceDate eller Extraction.ChangeOfSupplierDate");

        var compareOptions = new CompareRunOptions
        {
            ProfileName = profile.Name,
            Accounts = accounts,
            DataAreaId = profile.Extraction.DataAreaId,
            DeliveryCategory = profile.Extraction.DeliveryCategory,
            GridAreaMap = profile.Mappings?.GridArea,
            Verbose = verbose,
            ReferenceDate = resolvedDate,
            Env = settlEnv,
        };

        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();

        var runner = new CompareRunner(xellentLoader, customerClient);
        return await runner.RunAsync(compareOptions, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(compareCommand);

// === generate-seed command ===
var seedAccountArgument = new Argument<string[]>("accounts") { Description = "Account numbers to generate seed data for" };
var seedOutputOption = new Option<string>("--output") { Description = "Output directory (seed data written to <output>/<account>/)" };
var seedEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var seedPayerIdsOption = new Option<string[]>("--payer-ids") { Description = "Payer ID overrides (format: account:payerId)" };
var seedContractsOption = new Option<string[]>("--contracts") { Description = "Contract names to fetch MP mappings for" };
var seedReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date for Xellent queries" };

var generateSeedCommand = new Command("generate-seed", "Generate seed data for regression tests from Xellent production DB and Settl API")
{
    seedAccountArgument,
    seedOutputOption,
    seedEnvOption,
    seedPayerIdsOption,
    seedContractsOption,
    seedReferenceDateOption
};

generateSeedCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var accountNumbers = parseResult.GetValue(seedAccountArgument)!;
    var outputPath = parseResult.GetValue(seedOutputOption) ?? Directory.GetCurrentDirectory();
    var seedReferenceDate = parseResult.GetValue(seedReferenceDateOption);

    try
    {
        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var payerClient = serviceProvider.GetRequiredService<ISettlPayerClient>();
        var mpClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();
        var contractClient = serviceProvider.GetRequiredService<ISettlContractClient>();
        // Parse payer ID overrides (format: "account:payerId")
        var payerIdArgs = parseResult.GetValue(seedPayerIdsOption);
        Dictionary<string, string>? payerIdOverrides = null;
        if (payerIdArgs is { Length: > 0 })
        {
            payerIdOverrides = payerIdArgs
                .Select(p => p.Split(':'))
                .Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => p[1]);
        }

        var contractNames = parseResult.GetValue(seedContractsOption);

        var runner = new GenerateSeedRunner(connectionString, customerClient, payerClient, mpClient, contractClient);
        return await runner.RunAsync(new GenerateSeedRunOptions
        {
            AccountNumbers = accountNumbers,
            OutputPath = outputPath,
            PayerIdOverrides = payerIdOverrides,
            ContractNames = contractNames,
            ReferenceDate = seedReferenceDate,
            Env = settlEnv,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(generateSeedCommand);

// === generate-state command ===
var generateStateProfileArgument = new Argument<string[]>("profile")
{
    Description = "Profile name(s) to process; omit when using --all",
    Arity = ArgumentArity.ZeroOrMore
};
var generateStateProfilesDirOption = new Option<string>("--profiles-dir") { Description = "Directory containing profile JSON files" };
generateStateProfilesDirOption.Required = true;
var generateStateReferenceDateOption = new Option<DateTime>("--reference-date") { Description = "Reference date for active-contract filtering and invoice history cutoff (e.g. 2026-03-01)" };
generateStateReferenceDateOption.Required = true;
var generateStateOutputOption = new Option<string>("--output") { Description = "Output JSON file path (default: migration-state.json)" };
var generateStateNoInvoicesOption = new Option<bool>("--no-invoices") { Description = "Skip invoice step; produce tracking-only shape" };
var generateStateSkipActiveOption = new Option<bool>("--skip-active") { Description = "Exclude accounts whose most recent invoice is not Slutafregning" };
var generateStateAllOption = new Option<bool>("--all") { Description = "Process all profiles in the profiles directory" };
var generateStateOverwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite existing output file instead of merging" };
var generateStateEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };

var generateStateCommand = new Command("generate-state", "[DEPRECATED - use generate-state-from-settl] Generate migration-state.json (tracking + invoices) for one or more profiles")
{
    generateStateProfileArgument,
    generateStateProfilesDirOption,
    generateStateReferenceDateOption,
    generateStateOutputOption,
    generateStateNoInvoicesOption,
    generateStateSkipActiveOption,
    generateStateAllOption,
    generateStateOverwriteOption,
    generateStateEnvOption,
};

generateStateCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileNames = parseResult.GetValue(generateStateProfileArgument) ?? [];
    var profilesDir = Path.GetFullPath(parseResult.GetValue(generateStateProfilesDirOption)!);
    var referenceDate = parseResult.GetValue(generateStateReferenceDateOption);
    var output = parseResult.GetValue(generateStateOutputOption) ?? "migration-state.json";
    var noInvoices = parseResult.GetValue(generateStateNoInvoicesOption);
    var skipActive = parseResult.GetValue(generateStateSkipActiveOption);
    var all = parseResult.GetValue(generateStateAllOption);
    var overwrite = parseResult.GetValue(generateStateOverwriteOption);

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("WARNING: 'generate-state' is deprecated — use 'generate-state-from-settl' instead.");
    Console.ResetColor();

    if (!all && profileNames.Length == 0)
    {
        Console.WriteLine("ERROR: specify at least one profile name, or use --all");
        return 1;
    }

    if (all && profileNames.Length > 0)
    {
        Console.WriteLine("ERROR: --all cannot be combined with explicit profile names");
        return 1;
    }

    string[] resolvedProfiles;
    if (all)
    {
        resolvedProfiles = new ProfileLoader(profilesDir)
            .ListProfilesWithDescriptions()
            .Select(p => p.Name)
            .ToArray();

        if (resolvedProfiles.Length == 0)
        {
            Console.WriteLine($"No profiles found in {profilesDir}");
            return 1;
        }

        Console.WriteLine($"--all: {resolvedProfiles.Length} profile(s): {string.Join(", ", resolvedProfiles)}");
        Console.WriteLine();
    }
    else
    {
        resolvedProfiles = profileNames;
    }

    var runner = new GenerateStateRunner(connectionString, baseIgnoredGroups);
    return await runner.RunAsync(new GenerateStateRunOptions
    {
        ProfilesDirectory = profilesDir,
        ProfileNames = resolvedProfiles,
        ConnectionString = connectionString,
        OutputPath = output,
        ReferenceDate = referenceDate,
        NoInvoices = noInvoices,
        SkipActive = skipActive,
        Overwrite = overwrite,
        Env = settlEnv,
        HistoryYears = historyYears,
    }, cancellationToken);
});

rootCommand.Subcommands.Add(generateStateCommand);

// === generate-state-from-settl command ===
var generateStateFromSettlOutputOption = new Option<string>("--output") { Description = "Output JSON file path (default: migration-state.json). Always overwrites." };
var generateStateFromSettlReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Deprecated and ignored. Dates come from each metering point's first supplier change." };
var generateStateFromSettlSkipActiveOption = new Option<bool>("--skip-active") { Description = "Exclude accounts whose most recent invoice is not Slutafregning instead of failing." };
var generateStateFromSettlEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var generateStateFromSettlFlatOption = new Option<bool>("--flat-reference-date") { Description = "Retired: rejected because a flat date cannot verify invoice ownership." };
var generateStateFromSettlSkipWaterOption = new Option<bool>("--skip-water-census") { Description = "Skip the extra Settl read that names which excluded customers hold water/heat metering points." };
var generateStateFromSettlIncludeNotInDeliveryOption = new Option<DateTime?>("--include-not-in-delivery-cos") { Description = "Retired: rejected because a synthetic date cannot verify the first supplier change." };
var generateStateFromSettlIncludeActiveOption = new Option<bool>("--include-active") { Description = "Keep customers that are still being billed: skip the Slutafregning eligibility test entirely, leaving only the configured per-customer history window. Off by default (COS-driven mode only)." };
var generateStateFromSettlNoInvoicesOption = new Option<bool>("--no-invoices") { Description = "Keep SourceAccounts but skip the invoice history: no CUSTINVOICEJOUR query, no Slutafregning test, empty invoice lists. For consumers that only need account identity (Bifrost). Not usable for PDF upload." };
var generateStateFromSettlSkipUnresolvedOption = new Option<bool>("--skip-unresolved-source-accounts") { Description = "Omit metering points without a clear Settl customer and Xellent source accounts with unresolved ownership, including their invoices; report every omission." };
var generateStateFromSettlIncludeUnknownStartsOption = new Option<bool>("--include-unknown-delivery-starts") { Description = "Allow a dated first delivery whose Settl start reason is Unknown. MoveIn, conflicting reasons, and missing starts remain excluded. Offline use requires a new pull-state-sources capture." };
var generateStateFromSettlOfflineOption = new Option<bool>("--offline") { Description = "Generate state from locally captured source inputs without Settl or Xellent connections." };
var generateStateFromSettlSettlementCheckOption = new Option<bool>("--settlement-check") { Description = "Also write <state>-settlement-check.csv: per metering point in the state, whether the latest statement date Xellent billed for its COS-1 contract and agreement (EXU_LATESTBILLINGDATES.LATESTSTATEMENTDATE) is COS-1 (Settled / NotSettled / Missing). Read-only; the state is unchanged. Not with --offline." };
var generateStateFromSettlHandledIssuesOption = new Option<string>("--handled-issues") { Description = @"Semicolon CSV (Reason;SourceAccount;MeteringPoint;SettlCustomers;Note;HandledDate) of ownership issues already handled. Matching issues go to <state>-handled-issues.csv instead of the ownership-issues CSV; the state is unchanged. Default: C:\Services\DataMigration\data\handled-ownership-issues.csv when it exists." };

var generateStateFromSettlCommand = new Command("generate-state-from-settl", "Generate invoice state from each migrated metering point's first supplier change, its Settl customer, and Xellent source accounts.")
{
    generateStateFromSettlOutputOption,
    generateStateFromSettlReferenceDateOption,
    generateStateFromSettlSkipActiveOption,
    generateStateFromSettlEnvOption,
    generateStateFromSettlFlatOption,
    generateStateFromSettlSkipWaterOption,
    generateStateFromSettlIncludeNotInDeliveryOption,
    generateStateFromSettlIncludeActiveOption,
    generateStateFromSettlNoInvoicesOption,
    generateStateFromSettlSkipUnresolvedOption,
    generateStateFromSettlIncludeUnknownStartsOption,
    generateStateFromSettlOfflineOption,
    generateStateFromSettlHandledIssuesOption,
    generateStateFromSettlSettlementCheckOption,
};

generateStateFromSettlCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var output = parseResult.GetValue(generateStateFromSettlOutputOption) ?? "migration-state.json";
    var referenceDate = parseResult.GetValue(generateStateFromSettlReferenceDateOption);
    var skipActive = parseResult.GetValue(generateStateFromSettlSkipActiveOption);
    var flatReferenceDate = parseResult.GetValue(generateStateFromSettlFlatOption);
    var skipWaterCensus = parseResult.GetValue(generateStateFromSettlSkipWaterOption);
    var includeNotInDeliveryCos = parseResult.GetValue(generateStateFromSettlIncludeNotInDeliveryOption);
    var includeActive = parseResult.GetValue(generateStateFromSettlIncludeActiveOption);
    var noInvoices = parseResult.GetValue(generateStateFromSettlNoInvoicesOption);
    var skipUnresolvedSourceAccounts = parseResult.GetValue(generateStateFromSettlSkipUnresolvedOption);
    var includeUnknownDeliveryStarts = parseResult.GetValue(generateStateFromSettlIncludeUnknownStartsOption);
    var offline = parseResult.GetValue(generateStateFromSettlOfflineOption);
    var handledIssues = parseResult.GetValue(generateStateFromSettlHandledIssuesOption);
    var settlementCheck = parseResult.GetValue(generateStateFromSettlSettlementCheckOption);

    if (flatReferenceDate || includeNotInDeliveryCos.HasValue)
    {
        Console.Error.WriteLine("ERROR: invoice state now requires each point's confirmed first ChangeOfSupplier start; " +
            "--flat-reference-date and --include-not-in-delivery-cos cannot establish that ownership.");
        return 1;
    }

    if (referenceDate.HasValue)
        Console.Error.WriteLine("WARNING: --reference-date is ignored; each metering point's first supplier change supplies the date.");

    Console.WriteLine($"generate-state-from-settl  output={output}  env={settlEnv}" +
        "  mode=cos-driven" +
        (includeNotInDeliveryCos.HasValue ? $"  include-not-in-delivery-cos={includeNotInDeliveryCos:yyyy-MM-dd}" : "") +
        (includeActive ? "  include-active" : "") +
        (noInvoices ? "  no-invoices" : "") +
        (skipUnresolvedSourceAccounts ? "  skip-unresolved-source-accounts" : ""));
    if (includeUnknownDeliveryStarts)
        Console.WriteLine("  include-unknown-delivery-starts");
    Console.WriteLine();

    try
    {
        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var runner = new GenerateStateFromSettlRunner(
            customerClient,
            baseIgnoredGroups,
            serviceProvider.GetRequiredService<ISettlMeteringPointClient>(),
            serviceProvider.GetRequiredService<ISettlPayerClient>(),
            serviceProvider.GetRequiredService<ISettlCustomerLinkLookup>());
        return await runner.RunAsync(new GenerateStateFromSettlRunOptions
        {
            OutputPath = output,
            ReferenceDate = referenceDate,
            ConnectionString = offline ? null : connectionString,
            SkipActive = skipActive,
            Env = settlEnv,
            FlatReferenceDate = flatReferenceDate,
            SkipWaterCensus = skipWaterCensus,
            IncludeNotInDeliveryCos = includeNotInDeliveryCos,
            IncludeActive = includeActive,
            NoInvoices = noInvoices,
            SkipUnresolvedSourceAccounts = skipUnresolvedSourceAccounts,
            IncludeUnknownDeliveryStarts = includeUnknownDeliveryStarts,
            HistoryYears = historyYears,
            Offline = offline,
            HandledIssuesPath = handledIssues,
            SettlementCheck = settlementCheck,
        }, cancellationToken);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(generateStateFromSettlCommand);

// Capture source lookups once. State generation can then be rerun with different options offline.
var pullStateSourcesEnvOption = new Option<string>("--env")
    { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var pullStateSourcesOutputOption = new Option<string>("--output-dir")
    { Description = "Offline snapshot directory (default: environment state directory)" };
var pullStateSourcesOnlyOption = new Option<string>("--only")
    { Description = "Use case: contracts = AboRabat audit (Xellent + Settl, no invoices); xellent/settl = refresh one contract source; invoices = invoice state; all = both workflows (default)",
        DefaultValueFactory = _ => "all" };
var pullStateSourcesReferenceDateOption = new Option<DateTime?>("--reference-date")
    { Description = "Required for Xellent contract/charger evidence (--only all, contracts, or xellent)" };
var pullStateSourcesScopeOption = new Option<string>("--scope-file") { Description = "TXT file: one 1–6 digit customer account or 18 digit metering point per line; requires an explicit output directory" };
var pullStateSourcesSensitiveOption = new Option<bool>("--include-sensitive-data") { Description = "Capture additional customer identity and payer billing details in the offline snapshot" };
var pullStateSourcesRestoreFromOption = new Option<DateTime?>("--restore-parts-changed-from") { Description = "Optional source contract-part restore window start" };
var pullStateSourcesRestoreBeforeOption = new Option<DateTime?>("--restore-parts-changed-before") { Description = "Optional source contract-part restore window end (exclusive)" };
var pullStateSourcesCommand = new Command("pull-state-sources",
    "Save invoice and contract sources in one offline snapshot, or refresh selected sections.")
{
    pullStateSourcesEnvOption, pullStateSourcesOutputOption, pullStateSourcesOnlyOption,
    pullStateSourcesReferenceDateOption, pullStateSourcesScopeOption, pullStateSourcesSensitiveOption,
    pullStateSourcesRestoreFromOption, pullStateSourcesRestoreBeforeOption
};
pullStateSourcesCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var totalTimer = System.Diagnostics.Stopwatch.StartNew();
    var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
    var currentPhase = "validation and snapshot setup";
    var succeeded = false;
    var xellentRepairSaved = false;
    void NextPhase(string name)
    {
        Console.Error.WriteLine($"  [timing] {currentPhase}: {phaseTimer.Elapsed.TotalSeconds:F1}s");
        currentPhase = name;
        phaseTimer.Restart();
        Console.Error.WriteLine($"  [timing] Starting {name} (total {totalTimer.Elapsed.TotalSeconds:F1}s)");
    }
    Console.Error.WriteLine("  [timing] Starting pull-state-sources");
    try
    {
        var only = (parseResult.GetValue(pullStateSourcesOnlyOption) ?? "all").ToLowerInvariant();
        if (only is not ("all" or "invoices" or "contracts" or "xellent" or "settl"))
            throw new ArgumentException("--only must be all, invoices, contracts, xellent, or settl.");
        var output = parseResult.GetValue(pullStateSourcesOutputOption);
        var directory = string.IsNullOrWhiteSpace(output)
            ? StateSourceSnapshotStore.DefaultDirectory(settlEnv) : Path.GetFullPath(output);
        var pullReferenceDate = parseResult.GetValue(pullStateSourcesReferenceDateOption);
        if (only is ("all" or "contracts" or "xellent") && pullReferenceDate is null)
            throw new ArgumentException("--reference-date is required when pulling Xellent contract sources.");
        var scopePath = parseResult.GetValue(pullStateSourcesScopeOption);
        var sensitive = parseResult.GetValue(pullStateSourcesSensitiveOption);
        var restoreFrom = parseResult.GetValue(pullStateSourcesRestoreFromOption);
        var restoreBefore = parseResult.GetValue(pullStateSourcesRestoreBeforeOption);
        if ((restoreFrom is null) != (restoreBefore is null) || restoreFrom >= restoreBefore)
            throw new ArgumentException("Supply a valid pair of restore window bounds.");
        if (scopePath is not null && string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--scope-file requires --output-dir.");
        if (sensitive && only != "settl" && pullReferenceDate is null)
            throw new ArgumentException("--reference-date is required for sensitive Xellent capture.");
        StatePullScope? scope = null;
        var oldStore = StateSourceSnapshotStore.Open(directory, settlEnv);
        var exists = File.Exists(Path.Combine(directory, "manifest.json"));
        if (exists)
        {
            await oldStore.LoadManifestAsync(cancellationToken, false);
            xellentRepairSaved = oldStore.HasSection("repair-xellent") || oldStore.HasSection("repair-raw-settings");
        }
        if (!sensitive && exists && (oldStore.HasSection("repair-xellent") || oldStore.HasSection("repair-raw-settings") || oldStore.HasSection("repair-settl")))
            throw new ArgumentException("This snapshot contains opt-in details. Retain --include-sensitive-data or use a new output directory.");
        NextPhase("scope resolution");
        if (scopePath is not null)
        {
            var entries = StatePullScope.Parse(await File.ReadAllLinesAsync(scopePath, cancellationToken));
            if (exists)
            {
                scope = oldStore.Scope;
                if (scope is null || !scope.SameRequest(entries)) throw new ArgumentException("Scope differs from the saved snapshot; use a new directory.");
                if (only != "settl")
                {
                    var date = DateOnly.FromDateTime(pullReferenceDate ?? DateTime.Today);
                    if (scope.ReferenceDate != date) throw new ArgumentException("Scoped refresh must retain its reference date; use a new directory.");
                    var checkOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DataMigration.XellentData.XellentDbContext>()
                        .UseSqlServer(connectionString, o => o.UseCompatibilityLevel(110)).Options;
                    await using var checkDb = new DataMigration.XellentData.XellentDbContext(checkOptions);
                    var resolved = await StatePullScope.ResolveAsync(entries, checkDb, DataAreaIdForEnv(settlEnv), date, baseIgnoredGroups, cancellationToken);
                    if (!scope.SameResolution(resolved)) throw new ArgumentException("Resolved scope changed; capture into a new directory.");
                }
            }
            else
            {
                if (only == "settl") throw new ArgumentException("A first scoped capture requires Xellent scope resolution; --only settl needs an existing scoped snapshot.");
                var dbOptions = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DataMigration.XellentData.XellentDbContext>()
                    .UseSqlServer(connectionString, o => o.UseCompatibilityLevel(110)).Options;
                await using var db = new DataMigration.XellentData.XellentDbContext(dbOptions);
                scope = await StatePullScope.ResolveAsync(entries, db, DataAreaIdForEnv(settlEnv),
                    DateOnly.FromDateTime(pullReferenceDate ?? DateTime.Today), baseIgnoredGroups, cancellationToken);
            }
        }
        else if (exists && oldStore.Scope is not null) throw new ArgumentException("Scoped snapshot refresh requires its --scope-file.");
        NextPhase("snapshot initialization");
        var captureStore = StateSourceSnapshotStore.Open(directory, settlEnv, scope);
        await captureStore.BeginUpdateAsync(cancellationToken);
        var refreshXellent = only != "settl";
        var refreshSettl = only != "xellent";
        if (refreshXellent) xellentRepairSaved = false;
        await captureStore.InvalidateRepairAsync(refreshXellent, refreshSettl, cancellationToken);
        try
        {
            if (only is "all" or "invoices")
            {
                NextPhase("invoice source capture");
                var runner = new GenerateStateFromSettlRunner(
                    serviceProvider.GetRequiredService<ISettlCustomerClient>(), baseIgnoredGroups,
                    serviceProvider.GetRequiredService<ISettlMeteringPointClient>(),
                    serviceProvider.GetRequiredService<ISettlPayerClient>());
                await runner.PullSourcesAsync(settlEnv, connectionString, directory, cancellationToken, scope, deferCompletion: true);
            }
            if (only is not "invoices")
            {
                NextPhase("contract source capture");
                var source = only is "xellent" or "settl" ? only : "all";
                await new PullContractSourcesRunner(serviceProvider.GetRequiredService<ISettlContractClient>(),
                    serviceProvider.GetRequiredService<ISettlMeteringPointClient>())
                    .PullAsync(settlEnv, DataAreaIdForEnv(settlEnv), connectionString,
                        directory, source, pullReferenceDate is { } d ? DateOnly.FromDateTime(d) : null,
                        cancellationToken, scope, deferCompletion: true, rawRepairCapture: sensitive);
            }
            await captureStore.LoadManifestAsync(cancellationToken, false);
            await captureStore.MarkIncompleteAsync(cancellationToken);
            if (sensitive)
            {
                if (refreshXellent)
                {
                    NextPhase("Xellent customer/payer source copy");
                    await RepairRawXellent.CaptureAsync(captureStore, connectionString,
                    DataAreaIdForEnv(settlEnv), DateOnly.FromDateTime(pullReferenceDate!.Value), baseIgnoredGroups,
                    restoreFrom, restoreBefore, cancellationToken, BaseMappingLoader.Load(config));
                    xellentRepairSaved = true;
                }
                if (refreshSettl)
                {
                    NextPhase("Settl customer/payer evidence");
                    await CustomerPayerRepairCapture.CaptureSettlAsync(captureStore,
                    serviceProvider.GetRequiredService<ISettlCustomerClient>(), serviceProvider.GetRequiredService<ISettlPayerClient>(),
                    serviceProvider.GetRequiredService<ISettlMeteringPointClient>(), cancellationToken, serviceProvider.GetRequiredService<ISettlContractClient>());
                }
            }
            NextPhase("snapshot finalization");
            await captureStore.CompleteAsync(cancellationToken, includesUnknownDeliveryStarts: only is "all" or "invoices");
        }
        catch
        {
            await captureStore.MarkIncompleteAsync(CancellationToken.None);
            throw;
        }
        Console.WriteLine($"Offline snapshot ({only}): {directory}");
        succeeded = true;
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR during {currentPhase} after {phaseTimer.Elapsed.TotalSeconds:F1}s: {ex.Message}");
        if (xellentRepairSaved && currentPhase is ("Settl customer/payer evidence" or "contract source capture" or "snapshot finalization"))
            Console.Error.WriteLine("  Recovery: Xellent customer/payer snapshot data is saved. Retry with --only settl and the same --scope-file, --include-sensitive-data and --output-dir. Keep the snapshot folder; Xellent does not need to be read again.");
        Console.Error.WriteLine("  Snapshot not completed. Do not use it for Excel preparation until the capture succeeds.");
        return 1;
    }
    finally
    {
        Console.Error.WriteLine($"  [timing] {currentPhase}: {phaseTimer.Elapsed.TotalSeconds:F1}s");
        Console.Error.WriteLine($"  [timing] pull-state-sources {(succeeded ? "completed" : "failed")}: {totalTimer.Elapsed.TotalSeconds:F1}s total");
    }
});
rootCommand.Subcommands.Add(pullStateSourcesCommand);

var modelReports = new Option<string>("--report-dir") { Required = true };
var modelOutput = new Option<string>("--output-dir") { Required = true };
var modelCommand = new Command("build-migration-model", "Build a reusable offline model from completed non-dry-run migration reports")
    { modelReports, modelOutput };
modelCommand.SetAction(async (parse, ct) =>
{
    try { await MigrationRunModel.BuildAsync(parse.GetValue(modelReports)!, parse.GetValue(modelOutput)!, ct, Console.WriteLine); return 0; }
    catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
});
rootCommand.Subcommands.Add(modelCommand);

var repairInput = new Option<string>("--input-dir") { Required = true };
var repairModel = new Option<string>("--migration-model") { Required = true, Description = "Completed reusable migration-model.json" };
var repairScope = new Option<string>("--scope-file") { Required = true };
var repairTemplates = new Option<string>("--template-dir") { Required = true };
var repairOutput = new Option<string>("--output-dir") { Required = true };
var repairEnv = new Option<string>("--env");
var repairDrafts = new Option<bool>("--draft-imports") { Description = "Write marked draft copies for supported rows when the package is unresolved" };
var repairDeliveryHistory = new Option<bool>("--check-delivery-history") { Description = "Use saved Settl delivery history to end old MP links for move-in, move-out or supplier departure; hold unclear cases" };
var repairCommand = new Command("prepare-customer-payer-repair", "Prepare Create-only Settl Excel imports entirely offline")
    { repairInput, repairScope, repairTemplates, repairOutput, repairEnv, repairDrafts, repairModel, repairDeliveryHistory };
repairCommand.SetAction(async (parse, ct) =>
{
    try
    {
        var code = await CustomerPayerRepair.PrepareAsync(parse.GetValue(repairInput)!, settlEnv,
            parse.GetValue(repairScope)!, parse.GetValue(repairTemplates)!, parse.GetValue(repairOutput)!, ct, parse.GetValue(repairDrafts), parse.GetValue(repairModel), BaseMappingLoader.Load(config), parse.GetValue(repairDeliveryHistory));
        Console.WriteLine(code == 0 ? "Five import workbooks and report are ready." : "Repair is unresolved; inspect report.json. No import workbooks were released.");
        return code;
    }
    catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
});
rootCommand.Subcommands.Add(repairCommand);

var contractImpactInputOption = new Option<string>("--input-dir")
    { Description = "Directory containing a completed contract-source snapshot" };
var contractImpactEnvOption = new Option<string>("--env")
    { Description = "Snapshot environment (default: featuretest)" };
var contractImpactProductOption = new Option<string>("--product")
    { Description = "Xellent product / Settl contract name", DefaultValueFactory = _ => "V AboRabat" };
var contractImpactOutputOption = new Option<string>("--output")
    { Description = "New CSV report path" };
var contractImpactCommand = new Command("report-contract-impact",
    "Count contract-product candidates and actual Settl links using only offline snapshot files")
{
    contractImpactInputOption, contractImpactEnvOption, contractImpactProductOption, contractImpactOutputOption
};
contractImpactCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var input = parseResult.GetValue(contractImpactInputOption);
    if (string.IsNullOrWhiteSpace(input))
    {
        Console.Error.WriteLine("ERROR: --input-dir is required");
        return 1;
    }
    try
    {
        var environment = parseResult.GetValue(contractImpactEnvOption) ?? settlEnv;
        var product = parseResult.GetValue(contractImpactProductOption) ?? "V AboRabat";
        var snapshot = await StateSourceSnapshotStore.Open(input, environment)
            .ReadContractSourcesAsync(cancellationToken);
        var report = ContractImpactReport.Build(snapshot, product);
        Console.WriteLine($"Xellent MPs with '{product}' (live or logged): {report.XellentCandidates}");
        Console.WriteLine($"Settl MPs linked to '{product}': {report.NamedSettlLinks}");
        Console.WriteLine($"In both: {report.InBoth}");
        Console.WriteLine(snapshot.HasChargerAssignments
            ? $"Offline charger assignments: {snapshot.ChargerAssignments.Count}"
            : "Offline charger assignments: not captured in this snapshot");
        var output = parseResult.GetValue(contractImpactOutputOption);
        if (!string.IsNullOrWhiteSpace(output))
        {
            var path = Path.GetFullPath(output);
            if (File.Exists(path)) throw new IOException($"Output already exists: {path}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, report.Csv, cancellationToken);
            Console.WriteLine($"CSV: {path}");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
});
rootCommand.Subcommands.Add(contractImpactCommand);

var repairEnvOption = new Option<string>("--env") { Description = "Must be production" };
var repairInputOption = new Option<string>("--input-dir") { Description = "Completed offline state snapshot" };
var repairReferenceDateOption = new Option<DateTime?>("--reference-date")
    { Description = "Contract-part and charger reference date (default: the date captured in the offline snapshot)" };
var repairOutputOption = new Option<string>("--output") { Description = "Optional JSON plan file for API repair" };
var repairWorkbookOption = new Option<string>("--output-xlsx") { Description = "New Settl import workbook path" };
var repairReviewOption = new Option<string>("--review-csv") { Description = "New CSV report for all affected metering points" };
var repairVerificationOption = new Option<string>("--verification-csv")
    { Description = "Cell-by-cell verification CSV for the Settl workbook" };
var repairTemplateOption = new Option<string>("--template-xlsx")
{
    Description = "Blank Settl ContractMeteringPoints V2 workbook",
    DefaultValueFactory = _ => @"C:\Users\sen.makj\OneDrive - Verdo Holding A S\Skrivebord\Settl\ContractMeteringPoints_V2_2026-09-21.xlsx"
};
var prepareRepairCommand = new Command("prepare-aborabat-repair", "Find and plan supported UA repairs from offline data only")
{ repairEnvOption, repairInputOption, repairReferenceDateOption, repairOutputOption, repairWorkbookOption,
    repairReviewOption, repairVerificationOption, repairTemplateOption };
prepareRepairCommand.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var input = parseResult.GetValue(repairInputOption) ?? throw new ArgumentException("--input-dir is required");
        var workbook = parseResult.GetValue(repairWorkbookOption);
        var output = parseResult.GetValue(repairOutputOption);
        var reviewCsv = parseResult.GetValue(repairReviewOption);
        var verificationCsv = parseResult.GetValue(repairVerificationOption);
        if (workbook is null && output is null && reviewCsv is null)
            throw new ArgumentException("--output-xlsx, --review-csv or --output is required");
        if ((workbook is null) != (verificationCsv is null))
            throw new ArgumentException("--output-xlsx and --verification-csv must be supplied together.");
        if (output is not null && File.Exists(output)) throw new IOException($"Output already exists: {output}");
        if (workbook is not null && File.Exists(workbook)) throw new IOException($"Workbook output already exists: {workbook}");
        if (reviewCsv is not null && File.Exists(reviewCsv)) throw new IOException($"Review report already exists: {reviewCsv}");
        if (verificationCsv is not null && File.Exists(verificationCsv)) throw new IOException($"Verification report already exists: {verificationCsv}");
        var paths = new[] { output, workbook, reviewCsv, verificationCsv }.Where(p => p is not null).Select(p => Path.GetFullPath(p!)).ToList();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            throw new IOException("Output paths must differ.");
        var plan = await AborabatRepair.PrepareAsync(input,
            parseResult.GetValue(repairEnvOption) ?? settlEnv, cancellationToken,
            parseResult.GetValue(repairReferenceDateOption) is { } d ? DateOnly.FromDateTime(d) : null);
        Console.WriteLine($"Reference date: {plan.ReferenceDate:yyyy-MM-dd}. Prepared {plan.Items.Count} repairs; {plan.ExcludedCount} unresolved MPs excluded.");
        if (workbook is not null)
        {
            await AborabatRepair.ExportSettlImportWorkbookAsync(plan,
                parseResult.GetValue(repairTemplateOption)!, workbook, cancellationToken);
            Console.WriteLine($"Created {plan.Items.Sum(i => i.Targets.Count + i.ReplacedLinks.Count + 1)} Settl import rows: {Path.GetFullPath(workbook)}");
            await AborabatVerification.WriteAsync(plan, workbook, verificationCsv!, cancellationToken);
            Console.WriteLine($"Created cell-by-cell verification CSV: {Path.GetFullPath(verificationCsv!)}");
        }
        if (output is not null)
        {
            await AborabatRepair.WritePlanAsync(plan, output, cancellationToken);
            Console.WriteLine($"Saved API repair plan: {Path.GetFullPath(output)}");
        }
        if (reviewCsv is not null)
        {
            await AborabatRepair.WriteReviewCsvAsync(plan, reviewCsv, cancellationToken);
            Console.WriteLine($"Reported {plan.Review.Count} rows for {plan.Items.Count + plan.ExcludedCount} MPs: {Path.GetFullPath(reviewCsv)}");
        }
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
});
rootCommand.Subcommands.Add(prepareRepairCommand);

var applyRepairEnvOption = new Option<string>("--env") { Description = "Must be production" };
var applyRepairPlanOption = new Option<string>("--plan") { Description = "Offline repair plan JSON" };
var applyRepairReportOption = new Option<string>("--report") { Description = "New CSV journal file" };
var applyRepairExecuteOption = new Option<bool>("--execute") { Description = "Write changes to Settl" };
var applyRepairReviewedOption = new Option<string>("--reviewed-xlsx")
    { Description = "Reviewed proposal workbook required before execution" };
var applyRepairCommand = new Command("apply-aborabat-repair", "Apply planned UA contract links in Settl")
{ applyRepairEnvOption, applyRepairPlanOption, applyRepairReportOption, applyRepairExecuteOption,
    applyRepairReviewedOption };
applyRepairCommand.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var environment = parseResult.GetValue(applyRepairEnvOption) ?? settlEnv;
        if (environment != "production") throw new ArgumentException("--env production is required");
        var path = parseResult.GetValue(applyRepairPlanOption) ?? throw new ArgumentException("--plan is required");
        var plan = await AborabatRepair.ReadPlanAsync(path, cancellationToken);
        Console.WriteLine($"Plan: {plan.Items.Count} UA repairs; {plan.ExcludedCount} unresolved MPs excluded.");
        foreach (var group in plan.Items.SelectMany(i => i.Targets).GroupBy(t => t.ContractName).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        if (!parseResult.GetValue(applyRepairExecuteOption)) return 0;
        var reviewedWorkbook = parseResult.GetValue(applyRepairReviewedOption)
            ?? throw new ArgumentException("--reviewed-xlsx is required with --execute");
        AborabatVerification.VerifyReviewedWorkbook(reviewedWorkbook, plan);
        var report = parseResult.GetValue(applyRepairReportOption) ?? throw new ArgumentException("--report is required with --execute");
        var failures = await AborabatRepair.ApplyAsync(plan, report,
            serviceProvider.GetRequiredService<ISettlContractClient>(), cancellationToken);
        Console.WriteLine($"Results: {Path.GetFullPath(report)}; failed or skipped: {failures}");
        return failures == 0 ? 0 : 1;
    }
    catch (Exception ex) { Console.Error.WriteLine($"ERROR: {ex.Message}"); return 1; }
});
rootCommand.Subcommands.Add(applyRepairCommand);

// === compare-state command ===
var compareStateBeforeArgument = new Argument<string>("before")
{
    Description = "State JSON generated before the ownership fix"
};
var compareStateAfterArgument = new Argument<string>("after")
{
    Description = "State JSON generated after the ownership fix"
};
var compareStateOutputOption = new Option<string>("--output")
{
    Description = "New CSV path for the differences (must not already exist)"
};
var compareStateCommand = new Command("compare-state", "Compare customer, source-account and invoice ownership in two state JSON files; no Settl or Xellent calls")
{
    compareStateBeforeArgument,
    compareStateAfterArgument,
    compareStateOutputOption
};

compareStateCommand.SetAction(async (parseResult, cancellationToken) =>
{
    try
    {
        var beforePath = Path.GetFullPath(parseResult.GetValue(compareStateBeforeArgument)!);
        var afterPath = Path.GetFullPath(parseResult.GetValue(compareStateAfterArgument)!);
        var outputValue = parseResult.GetValue(compareStateOutputOption);
        if (string.IsNullOrWhiteSpace(outputValue))
        {
            Console.Error.WriteLine("ERROR: --output <new-csv-path> is required");
            return 1;
        }
        var outputPath = Path.GetFullPath(outputValue);
        if (!File.Exists(beforePath) || !File.Exists(afterPath))
        {
            Console.Error.WriteLine("ERROR: both state JSON files must exist");
            return 1;
        }
        if (string.Equals(beforePath, afterPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputPath, beforePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(outputPath, afterPath, StringComparison.OrdinalIgnoreCase)
            || File.Exists(outputPath))
        {
            Console.Error.WriteLine("ERROR: use two different input files and a new output CSV path");
            return 1;
        }

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var before = JsonSerializer.Deserialize<DataMigration.OpsCli.Data.MigrationStateDocument>(
            await File.ReadAllTextAsync(beforePath, cancellationToken), jsonOptions)
            ?? throw new InvalidDataException("Before state JSON is empty");
        var after = JsonSerializer.Deserialize<DataMigration.OpsCli.Data.MigrationStateDocument>(
            await File.ReadAllTextAsync(afterPath, cancellationToken), jsonOptions)
            ?? throw new InvalidDataException("After state JSON is empty");
        var differences = InvoiceStateComparer.Compare(before, after);

        await using (var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write))
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true)))
            await writer.WriteAsync(InvoiceStateComparer.ToCsv(differences).AsMemory(), cancellationToken);

        Console.WriteLine($"Before: {before.Accounts.Count:N0} customers, " +
            $"{before.Accounts.Sum(a => a.SourceAccounts.Count):N0} source assignments, " +
            $"{before.Accounts.Sum(a => a.Invoices.Count):N0} invoices");
        Console.WriteLine($"After:  {after.Accounts.Count:N0} customers, " +
            $"{after.Accounts.Sum(a => a.SourceAccounts.Count):N0} source assignments, " +
            $"{after.Accounts.Sum(a => a.Invoices.Count):N0} invoices");
        foreach (var group in differences.GroupBy(d => (d.Kind, d.Change))
            .OrderBy(g => g.Key.Kind, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Change, StringComparer.Ordinal))
            Console.WriteLine($"  {group.Key.Kind} {group.Key.Change}: {group.Count():N0}");
        Console.WriteLine($"Differences: {differences.Count:N0} row(s) -> {outputPath}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(compareStateCommand);

// === pbs-match command ===
// Both positionals are declared optional so the profile can be left out with --all-accounts.
// System.CommandLine fills positionals left to right, so a single value lands in 'profile' —
// the action re-derives which is which (see below) rather than trusting the slot.
var pbsProfileArgument = new Argument<string?>("profile")
{
    Description = "Profile name to determine which accounts to query (udelad sammen med --all-accounts)",
    Arity = ArgumentArity.ZeroOrOne
};
var pbsCsvFileArgument = new Argument<string?>("settl-csv")
{
    Description = "Path to Settl 'Payment Agreement' CSV export",
    Arity = ArgumentArity.ZeroOrOne
};
var pbsAllAccountsOption = new Option<bool>("--all-accounts")
{
    Description = "Kør uden profil: alle migrerede konti med en aktiv MobilePay-aftale [mb only]"
};
var pbsTypeOption = new Option<string>("--type") { Description = "Payment method type: pbs or mb (MobilePay). Required." };
var pbsOutputOption = new Option<string?>("--output", "-o") { Description = "Output directory for reports (default: ./output)" };
var pbsEnvOption = new Option<string>("--env") { Description = "Target environment" };
var pbsReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date override (overrides Extraction.ReferenceDate and Extraction.ChangeOfSupplierDate in profile) [PBS only]" };
var pbsIncludeTerminatedOption = new Option<bool>("--include-terminated") { Description = "Inkluder opsagte aftaler der blev opsagt EFTER COS — altsaa dem migreringen selv lukkede. PBS: Pbstransactioncode 4/5. MobilePay: Mobilepayagreementcanceldate." };

var pbsMatchCommand = new Command("payment-match", "Match Xellent payment contracts with Settl payment agreements by metering point (--type pbs|mb)")
{
    pbsProfileArgument,
    pbsCsvFileArgument,
    pbsAllAccountsOption,
    pbsTypeOption,
    pbsOutputOption,
    pbsEnvOption,
    pbsReferenceDateOption,
    pbsIncludeTerminatedOption
};

pbsMatchCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var firstPositional = parseResult.GetValue(pbsProfileArgument);
    var secondPositional = parseResult.GetValue(pbsCsvFileArgument);
    var allAccounts = parseResult.GetValue(pbsAllAccountsOption);
    var type = parseResult.GetValue(pbsTypeOption)!;
    var outputDir = parseResult.GetValue(pbsOutputOption);

    // With --all-accounts a single positional is the CSV; otherwise the pair is profile + CSV,
    // exactly as before.
    var profileName = allAccounts && secondPositional is null ? null : firstPositional;
    var csvFile = allAccounts && secondPositional is null ? firstPositional : secondPositional;

    try
    {
        if (string.IsNullOrWhiteSpace(csvFile))
            throw new ArgumentException(
                "Settl 'Payment Agreement' CSV mangler. E-nummeret ('External Customer Number') " +
                "findes kun i det eksport — det kan ikke hentes fra Settl-API'et.");

        if (allAccounts && type != "mb")
            throw new ArgumentException("--all-accounts understøttes kun med --type mb.");

        if (profileName is null && !allAccounts)
            throw new ArgumentException("Angiv en profil, eller kør med --all-accounts.");

        var profile = profileName is null ? null : profileLoader.Load(profileName);
        var accounts = profile?.Accounts.Distinct().ToArray();

        if (type == "mb")
        {
            // Settl-driven: the MP client supplies metering points + COS, which resolve the
            // owning Xellent account. Same chain as reconcile.
            var mobilePayMpClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();
            var runner = new MobilePayMatchRunner(connectionString, mobilePayMpClient);
            return await runner.RunAsync(
                profile, accounts, csvFile, outputDir,
                parseResult.GetValue(pbsIncludeTerminatedOption));
        }
        else if (type == "pbs")
        {
            // PBS still needs the profile: its reference date comes from the profile's extraction
            // settings, and there is no profile-free equivalent for it.
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(accounts);
            var pbsReferenceDateOverride = parseResult.GetValue(pbsReferenceDateOption);
            var pbsReferenceDate = pbsReferenceDateOverride
                ?? profile.Extraction.ReferenceDate
                ?? profile.Extraction.ChangeOfSupplierDate;

            var includeTerminated = parseResult.GetValue(pbsIncludeTerminatedOption);
            var runner = new PbsMatchRunner(connectionString);
            return await runner.RunAsync(profile, accounts, csvFile, outputDir, pbsReferenceDate, includeTerminated);
        }
        else
        {
            throw new ArgumentException($"--type skal være 'pbs' eller 'mb', fik: '{type}'");
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(pbsMatchCommand);

// === report-delivery-start command ===
var reportDeliveryStartEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production)" };
var reportDeliveryStartIncludeChildrenOption = new Option<bool>("--include-children") { Description = "Inkluder child-målepunkter (springer normalt over)" };
var reportDeliveryHistoryOption = new Option<bool>("--include-history") { Description = "Group actual delivery histories by state/reason pattern; export counts, examples and source responses" };
var reportDeliveryOutputOption = new Option<string?>("--output") { Description = "Output CSV path (history mode: patterns CSV plus .examples.csv and .sources.jsonl)" };

var reportDeliveryStartCommand = new Command("report-delivery-start", "Read-only TSV report: MeteringPointIdentification + DeliveryStatus.Start for alle MPs i Settl")
{
    reportDeliveryStartEnvOption,
    reportDeliveryStartIncludeChildrenOption,
    reportDeliveryHistoryOption,
    reportDeliveryOutputOption
};

reportDeliveryStartCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var includeChildren = parseResult.GetValue(reportDeliveryStartIncludeChildrenOption);

    try
    {
        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();
        var runner = new ReportDeliveryStartRunner(meteringPointClient);
        var history = parseResult.GetValue(reportDeliveryHistoryOption);
        var outputPath = parseResult.GetValue(reportDeliveryOutputOption)
            ?? $"report-delivery-{(history ? "patterns" : "start")}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        if (history)
        {
            var prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, Path.GetFileNameWithoutExtension(outputPath));
            var examplesPath = prefix + ".examples.csv";
            var sourcesPath = prefix + ".sources.jsonl";
            if (new[] { Path.GetFullPath(outputPath), examplesPath, sourcesPath }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
                throw new ArgumentException("Output path overlaps a companion file.");
            await using var patterns = new StreamWriter(outputPath, false, new System.Text.UTF8Encoding(true));
            await using var examples = new StreamWriter(examplesPath, false, new System.Text.UTF8Encoding(true));
            await using var sources = new StreamWriter(sourcesPath, false, System.Text.Encoding.UTF8);
            var result = await new ReportDeliveryPatternsRunner(meteringPointClient)
                .RunAsync(settlEnv, includeChildren, patterns, examples, sources, cancellationToken);
            Console.Error.WriteLine($"Patterns: {Path.GetFullPath(outputPath)}\nExamples: {examplesPath}\nSources: {sourcesPath}");
            return result;
        }
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(includeChildren, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.Error.WriteLine(ex.StackTrace);
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reportDeliveryStartCommand);

// === reconcile command (env-wide) ===
var reconcileEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var reconcileOutputOption = new Option<string?>("--output") { Description = "Output CSV file (default: timestamped reconcile-<env>-<n>.csv)" };
var reconcileMappingOption = new Option<string?>("--mapping") { Description = "Override path to the per-dataarea reconcile mapping file (default: profiles/reconcile/<dataarea>.json)" };
var reconcileCosFromOption = new Option<DateTime?>("--cos-from") { Description = "Only select Settl InDelivery MPs whose COS (change-of-supplier / delivery-period start) is on or after this date (yyyy-MM-dd, inclusive)" };
var reconcileCosToOption = new Option<DateTime?>("--cos-to") { Description = "Only select Settl InDelivery MPs whose COS (change-of-supplier / delivery-period start) is on or before this date (yyyy-MM-dd, inclusive)" };
var reconcileCommodityOption = new Option<CommodityFilter>("--commodity") { Description = "Which commodity MPs to enumerate from Settl: electricity (default), water, all", DefaultValueFactory = _ => CommodityFilter.Electricity };

var reconcileCommand = new Command("reconcile",
    "Read-only env-wide: rebuild the create-true intended payload for ALL parent InDelivery MPs in the env (no profile — selection from Settl, config from the per-dataarea reconcile mapping) and diff it 1:1 against actual Settl state, per representative.")
{
    reconcileEnvOption,
    reconcileOutputOption,
    reconcileMappingOption,
    reconcileCosFromOption,
    reconcileCosToOption,
    reconcileCommodityOption,
};

reconcileCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var outputOverride = parseResult.GetValue(reconcileOutputOption);
    var mappingOverride = parseResult.GetValue(reconcileMappingOption);
    var cosFrom = parseResult.GetValue(reconcileCosFromOption);
    var cosTo = parseResult.GetValue(reconcileCosToOption);
    var cosPeriod = new CosPeriod(
        cosFrom.HasValue ? DateOnly.FromDateTime(cosFrom.Value) : null,
        cosTo.HasValue ? DateOnly.FromDateTime(cosTo.Value) : null);
    var commodity = parseResult.GetValue(reconcileCommodityOption);

    try
    {
        var exemption = new BillingExemptionConfig(
            ProductElementId: Guid.TryParse(config["BillingExemption:ProductElementId"], out var peId) ? peId : null,
            ProductElementCode: config["BillingExemption:ProductElementCode"],
            TemplateSuffix: config["BillingExemption:TemplateSuffix"],
            Percentage: double.TryParse(config["BillingExemption:Percentage"], System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct : 1);

        var dataAreaId = DataAreaIdForEnv(settlEnv);
        var mappingLoader = new ReconcileMappingLoader(Path.Combine(profilesDirectory, "reconcile"));
        var mapping = mappingOverride != null
            ? mappingLoader.LoadFile(mappingOverride)
            : mappingLoader.Load(dataAreaId);
        // Env decides the dataarea authoritatively (the scan targets env's tenant regardless of
        // what the file declares).
        mapping.DataAreaId = dataAreaId;

        var reconcileProductElementMappings = config.GetSection("ProductElementMappings")
            .Get<List<ProductElementMappingRule>>() ?? [];

        var runner = new ReconcileRunner(
            apiOptions,
            serviceProvider.GetRequiredService<ISettlCustomerClient>(),
            serviceProvider.GetRequiredService<ISettlPayerClient>(),
            serviceProvider.GetRequiredService<ISettlMeteringPointClient>(),
            serviceProvider.GetRequiredService<ISettlContractClient>(),
            serviceProvider.GetRequiredService<ISettlSettlementGroupClient>(),
            connectionString,
            exemption,
            Console.Error.WriteLine,
            serviceProvider.GetRequiredService<ISettlPriceListClient>(),
            reconcileProductElementMappings,
            baseIgnoredGroups);

        var outputPath = outputOverride ?? $"reconcile-{settlEnv}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(mapping, settlEnv, outputFile, cosPeriod, cancellationToken, commodity);
        await outputFile.FlushAsync(cancellationToken);
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reconcileCommand);

// === reconcile-customer command (per-representative full-payload view) ===
// Read-only: rebuild the create-true intended payload for ONE already-migrated customer (its
// merge-group at COS) and diff it against Settl in full-payload mode — EVERY compared field, matched
// ones included (Kind=Match). The dashboard drill-down calls the same ScanRepresentativeAsync.
var reconcileCustEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var reconcileCustAccountsOption = new Option<string>("--accounts") { Description = "Comma-separated Xellent accountnums of the representative's merge-group (the MemberAccounts a reconcile row carries)", Required = true };
var reconcileCustCosOption = new Option<DateTime>("--cos") { Description = "Change-of-supplier date of the representative (yyyy-MM-dd); refDate = COS-1", Required = true };
var reconcileCustMappingOption = new Option<string?>("--mapping") { Description = "Override path to the per-dataarea reconcile mapping file (default: profiles/reconcile/<dataarea>.json)" };
var reconcileCustOutputOption = new Option<string?>("--output") { Description = "Output CSV file (default: timestamped reconcile-customer-<env>-<n>.csv)" };

var reconcileCustomerCommand = new Command("reconcile-customer",
    "Read-only: rebuild the create-true intended payload for ONE already-migrated customer (its merge-group at COS) and diff it against Settl in full-payload mode — every compared field, matched ones tagged Match. No env-wide pagination (seconds).")
{
    reconcileCustEnvOption,
    reconcileCustAccountsOption,
    reconcileCustCosOption,
    reconcileCustMappingOption,
    reconcileCustOutputOption,
};

reconcileCustomerCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var accountsRaw = parseResult.GetValue(reconcileCustAccountsOption)!;
    var cosValue = parseResult.GetValue(reconcileCustCosOption);
    var mappingOverride = parseResult.GetValue(reconcileCustMappingOption);
    var outputOverride = parseResult.GetValue(reconcileCustOutputOption);

    var accounts = accountsRaw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToList();
    if (accounts.Count == 0)
    {
        Console.Error.WriteLine("ERROR: --accounts must list at least one accountnum");
        return 1;
    }

    try
    {
        var exemption = new BillingExemptionConfig(
            ProductElementId: Guid.TryParse(config["BillingExemption:ProductElementId"], out var peId) ? peId : null,
            ProductElementCode: config["BillingExemption:ProductElementCode"],
            TemplateSuffix: config["BillingExemption:TemplateSuffix"],
            Percentage: double.TryParse(config["BillingExemption:Percentage"], System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct : 1);

        var dataAreaId = DataAreaIdForEnv(settlEnv);
        var mappingLoader = new ReconcileMappingLoader(Path.Combine(profilesDirectory, "reconcile"));
        var mapping = mappingOverride != null
            ? mappingLoader.LoadFile(mappingOverride)
            : mappingLoader.Load(dataAreaId);
        mapping.DataAreaId = dataAreaId;

        var reconcileProductElementMappings = config.GetSection("ProductElementMappings")
            .Get<List<ProductElementMappingRule>>() ?? [];

        var runner = new ReconcileRunner(
            apiOptions,
            serviceProvider.GetRequiredService<ISettlCustomerClient>(),
            serviceProvider.GetRequiredService<ISettlPayerClient>(),
            serviceProvider.GetRequiredService<ISettlMeteringPointClient>(),
            serviceProvider.GetRequiredService<ISettlContractClient>(),
            serviceProvider.GetRequiredService<ISettlSettlementGroupClient>(),
            connectionString,
            exemption,
            Console.Error.WriteLine,
            serviceProvider.GetRequiredService<ISettlPriceListClient>(),
            reconcileProductElementMappings,
            baseIgnoredGroups);

        var outputPath = outputOverride ?? $"reconcile-customer-{settlEnv}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunCustomerAsync(
            mapping, settlEnv, accounts, DateOnly.FromDateTime(cosValue), outputFile, cancellationToken);
        await outputFile.FlushAsync(cancellationToken);
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(reconcileCustomerCommand);

// === reconcile-apply command (write selected reconcile drift back to Settl) ===
// Verification harness for the ReconcileApplier engine (the dashboard calls the same engine on its
// in-memory selection). Reads a reconcile CSV, selects rows via the same axes the dashboard filters
// on (category/subtype/entity/kind/rep), and applies them per representative via a scoped capture.
var applyEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var applyFromOption = new Option<string>("--from") { Description = "Reconcile CSV to select rows from (produced by 'reconcile')", Required = true };
var applyMappingOption = new Option<string?>("--mapping") { Description = "Override path to the per-dataarea reconcile mapping file" };
var applyRepOption = new Option<string[]>("--rep") { Description = "Only apply rows under these representative account(s)", AllowMultipleArgumentsPerToken = true };
var applySubTypeOption = new Option<string[]>("--subtype") { Description = "Only apply rows with these SubType(s): Billing, Exemption, PriceList", AllowMultipleArgumentsPerToken = true };
var applyEntityOption = new Option<string[]>("--entity") { Description = "Only apply rows with these Entity types: Customer, Payer, MeteringPoint", AllowMultipleArgumentsPerToken = true };
var applyKindOption = new Option<string[]>("--kind") { Description = "Only apply rows with these Kind(s): FieldDiff, MissingInSettl, ...", AllowMultipleArgumentsPerToken = true };
var applyCategoryOption = new Option<string[]>("--category") { Description = "Only apply rows with these Category label(s)", AllowMultipleArgumentsPerToken = true };
var applyDryRunOption = new Option<bool>("--dry-run") { Description = "Validate + plan writes without mutating Settl" };
var applyForceProductionHotOption = new Option<bool>("--force-production-hot") { Description = "EXPLICIT ESCALATION: allow a HOT (non-dry-run) apply against a production env. Without this, production is always forced dry-run. Requires a selector and no --dry-run." };
var applyPriceListOutputOption = new Option<string?>("--pricelist-output") { Description = "Path for the PriceList re-import file (default: timestamped .xlsx in cwd)" };

var reconcileApplyCommand = new Command("reconcile-apply",
    "Write SELECTED reconcile drift rows back to Settl: PayerBilling + MP billing/payment-config (FieldDiff), MP exemptions + Customer→MP links + settlement-group membership (MissingInSettl), MP EstimatedAnnualVolume (FieldDiff), customer + payer core fields (FieldDiff, full-echo master PUT), missing whole entities (scoped create write-run), unintended contract→MP links (Anomaly → delete), and PriceList (re-import file). Per representative, rebuilds the create-true intended payload via a scoped capture (seconds) and overwrites the drifted fields with intended.")
{
    applyEnvOption, applyFromOption, applyMappingOption, applyRepOption, applySubTypeOption,
    applyEntityOption, applyKindOption, applyCategoryOption, applyDryRunOption,
    applyForceProductionHotOption, applyPriceListOutputOption,
};

reconcileApplyCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var fromPath = parseResult.GetValue(applyFromOption)!;
    var mappingOverride = parseResult.GetValue(applyMappingOption);
    var repSel = ToSet(parseResult.GetValue(applyRepOption));
    var subTypeSel = ToSet(parseResult.GetValue(applySubTypeOption));
    var entitySel = ToSet(parseResult.GetValue(applyEntityOption));
    var kindSel = ToSet(parseResult.GetValue(applyKindOption));
    var categorySel = ToSet(parseResult.GetValue(applyCategoryOption));
    var requestedDryRun = parseResult.GetValue(applyDryRunOption);
    var forceProductionHot = parseResult.GetValue(applyForceProductionHotOption);
    var priceListOutput = parseResult.GetValue(applyPriceListOutputOption);

    static HashSet<string>? ToSet(string[]? values)
        => values is { Length: > 0 } ? new HashSet<string>(values, StringComparer.OrdinalIgnoreCase) : null;

    try
    {
        if (!File.Exists(fromPath))
        {
            Console.Error.WriteLine($"ERROR: reconcile CSV not found: {fromPath}");
            return 1;
        }

        var csvRows = ReconcileApplySelection.ParseCsv(await File.ReadAllTextAsync(fromPath, cancellationToken));
        var hasSelector = repSel != null || subTypeSel != null || entitySel != null || kindSel != null || categorySel != null;

        // Production is dry-run by default; a HOT apply to production requires the explicit
        // --force-production-hot escalation flag (writes real customer data).
        var isProduction = settlEnv.StartsWith("production", StringComparison.OrdinalIgnoreCase);
        var dryRun = requestedDryRun || (isProduction && !forceProductionHot);
        if (isProduction && !requestedDryRun && !forceProductionHot)
            Console.Error.WriteLine($"[{settlEnv}] production → forced dry-run. Add --force-production-hot to escalate to a HOT apply.");
        if (isProduction && forceProductionHot && !requestedDryRun)
        {
            Console.Error.WriteLine($"*** [{settlEnv}] PRODUCTION HOT APPLY — escalation flag set. Writing real customer data to Settl. ***");
            Console.Error.WriteLine($"*** Selector required; review the dry-run output before this run. ***");
        }

        // Hot apply requires an explicit selector — never apply the whole reconcile result by accident.
        if (!hasSelector && !dryRun)
        {
            Console.Error.WriteLine("Refusing to HOT-apply with no selector. Narrow with --rep/--subtype/--entity/--kind/--category, or add --dry-run to preview all.");
            PrintMenu(csvRows);
            return 1;
        }

        var selected = csvRows.Where(r =>
            (repSel == null || repSel.Contains(r.RepresentativeAccount)) &&
            (subTypeSel == null || subTypeSel.Contains(string.IsNullOrEmpty(r.SubType) ? "Core" : r.SubType)) &&
            (entitySel == null || entitySel.Contains(r.Entity)) &&
            (kindSel == null || kindSel.Contains(r.Kind)) &&
            (categorySel == null || categorySel.Contains(r.Category)))
            .ToList();

        if (selected.Count == 0)
        {
            Console.Error.WriteLine("No rows matched the selector.");
            PrintMenu(csvRows);
            return 0;
        }

        var targets = ReconcileApplySelection.BuildTargets(selected, out var droppedNoCos);
        if (droppedNoCos > 0)
            Console.Error.WriteLine($"Dropped {droppedNoCos} selected row(s) whose representative has no parseable COS (cannot scope a capture).");
        if (targets.Count == 0)
        {
            Console.Error.WriteLine("Nothing to apply after grouping.");
            return 0;
        }

        var exemption = new BillingExemptionConfig(
            ProductElementId: Guid.TryParse(config["BillingExemption:ProductElementId"], out var peId) ? peId : null,
            ProductElementCode: config["BillingExemption:ProductElementCode"],
            TemplateSuffix: config["BillingExemption:TemplateSuffix"],
            Percentage: double.TryParse(config["BillingExemption:Percentage"], System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct : 1);

        var dataAreaId = DataAreaIdForEnv(settlEnv);
        var mappingLoader = new ReconcileMappingLoader(Path.Combine(profilesDirectory, "reconcile"));
        var mapping = mappingOverride != null ? mappingLoader.LoadFile(mappingOverride) : mappingLoader.Load(dataAreaId);
        mapping.DataAreaId = dataAreaId;

        var applyProductElementMappings = config.GetSection("ProductElementMappings")
            .Get<List<ProductElementMappingRule>>() ?? [];

        var applier = new ReconcileApplier(
            apiOptions,
            serviceProvider.GetRequiredService<ISettlCustomerClient>(),
            serviceProvider.GetRequiredService<ISettlPayerClient>(),
            serviceProvider.GetRequiredService<ISettlMeteringPointClient>(),
            serviceProvider.GetRequiredService<ISettlContractClient>(),
            serviceProvider.GetRequiredService<ISettlSettlementGroupClient>(),
            connectionString,
            exemption,
            Console.Error.WriteLine,
            serviceProvider.GetRequiredService<ISettlPriceListClient>(),
            applyProductElementMappings,
            baseIgnoredGroups);

        var result = await applier.ApplyAsync(
            mapping, new ReconcileApplyRequest(settlEnv, dryRun, targets, priceListOutput), cancellationToken);
        if (result.PriceListCsvPath != null)
            Console.WriteLine($"PriceList re-import file: {result.PriceListCsvPath}");

        Console.WriteLine($"=== reconcile-apply ({(dryRun ? "dry-run" : "HOT")}) — {targets.Count} representative(s), {selected.Count} row(s) ===");
        foreach (var r in result.Rows.OrderBy(r => r.RepresentativeAccount, StringComparer.Ordinal).ThenBy(r => r.Outcome))
            Console.WriteLine($"  [{r.Outcome}] {r.RepresentativeAccount} {r.Entity}/{r.SubType} {r.EntityKey} {r.Field}{(r.Detail != null ? $" — {r.Detail}" : "")}");
        Console.WriteLine($"--- Summary: Applied={result.Applied} WouldApply={result.WouldApply} NoLongerDrifts={result.NoLongerDrifts} NotFound={result.NotFound} Skipped={result.Skipped} Failed={result.Failed} ---");
        return result.Failed > 0 ? 1 : 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }

    static void PrintMenu(IReadOnlyList<ReconcileCsvRow> rows)
    {
        Console.Error.WriteLine($"Reconcile result: {rows.Count} row(s).");
        Console.Error.WriteLine("  By Category:");
        foreach (var g in rows.GroupBy(r => r.Category).OrderByDescending(g => g.Count()))
            Console.Error.WriteLine($"    {g.Count(),6}  {g.Key}");
        Console.Error.WriteLine("  By SubType:");
        foreach (var g in rows.GroupBy(r => string.IsNullOrEmpty(r.SubType) ? "Core" : r.SubType).OrderByDescending(g => g.Count()))
            Console.Error.WriteLine($"    {g.Count(),6}  {g.Key}");
    }
});

rootCommand.Subcommands.Add(reconcileApplyCommand);

// === extract-domain command ===
var extractDomainAccountsOption = new Option<string[]>("--accounts") { Description = "Account numbers (comma-separated or repeated)", AllowMultipleArgumentsPerToken = true };
var extractDomainCommodityOption = new Option<string>("--commodity") { Description = "Electricity, Water, Heat, Charge", DefaultValueFactory = _ => "Electricity" };
var extractDomainDataAreaOption = new Option<string?>("--data-area") { Description = "Override default DataAreaId" };
var extractDomainReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Override reference date (default: today UTC)" };
var extractDomainIncludeEndedSinceOption = new Option<DateTime?>("--include-ended-since") { Description = "Also include contracts/parts that ended on or after this date, even if ended before --reference-date (e.g. for an already-migrated population where Xellent contracts are end-dated at cutover)" };
var extractDomainRestorePartsFromOption = new Option<DateTime?>("--restore-parts-changed-from") { Description = "Start (inclusive) of a contract-part restore window: undoes EXU_CONTRACTPARTLOG end-date/delete changes made by a blank user inside this window. Must be set together with --restore-parts-changed-before." };
var extractDomainRestorePartsBeforeOption = new Option<DateTime?>("--restore-parts-changed-before") { Description = "End (exclusive) of the contract-part restore window. See --restore-parts-changed-from." };
var extractDomainOutputOption = new Option<string?>("--output") { Description = "Output JSON file (default: timestamped extract-domain-<n>.json)" };
var extractDomainValidateOption = new Option<bool>("--validate") { Description = "Run customer-validators and report issues" };

var extractDomainCommand = new Command("extract-domain", "Load CustomerBatch from Xellent via XellentDomainMapper, write JSON. Smoke-test of the domain mapper.")
{
    extractDomainAccountsOption,
    extractDomainCommodityOption,
    extractDomainDataAreaOption,
    extractDomainReferenceDateOption,
    extractDomainIncludeEndedSinceOption,
    extractDomainRestorePartsFromOption,
    extractDomainRestorePartsBeforeOption,
    extractDomainOutputOption,
    extractDomainValidateOption,
};

extractDomainCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var accountsRaw = parseResult.GetValue(extractDomainAccountsOption) ?? [];
    var accounts = accountsRaw
        .SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToArray();
    var commodityStr = parseResult.GetValue(extractDomainCommodityOption) ?? "Electricity";
    var dataAreaOverride = parseResult.GetValue(extractDomainDataAreaOption);
    var referenceDate = parseResult.GetValue(extractDomainReferenceDateOption);
    var includeEndedSince = parseResult.GetValue(extractDomainIncludeEndedSinceOption);
    var restorePartsFrom = parseResult.GetValue(extractDomainRestorePartsFromOption);
    var restorePartsBefore = parseResult.GetValue(extractDomainRestorePartsBeforeOption);
    var outputPath = parseResult.GetValue(extractDomainOutputOption);
    var validate = parseResult.GetValue(extractDomainValidateOption);

    if (!Enum.TryParse<DataMigration.Domain.Customers.Commodity>(commodityStr, ignoreCase: true, out var commodity))
    {
        Console.Error.WriteLine($"ERROR: invalid --commodity '{commodityStr}' (Electricity, Water, Heat, Charge)");
        return 1;
    }

    try
    {
        var dataAreaId = dataAreaOverride ?? DataAreaIdForEnv(settlEnv);
        var runner = new ExtractDomainRunner(xellentLoader);
        outputPath ??= $"extract-domain-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(new ExtractDomainRunOptions
        {
            Accounts = accounts,
            DataAreaId = dataAreaId,
            Commodity = commodity,
            ReferenceDate = referenceDate.HasValue ? DateOnly.FromDateTime(referenceDate.Value) : null,
            IncludeEndedSince = includeEndedSince.HasValue ? DateOnly.FromDateTime(includeEndedSince.Value) : null,
            RestorePartsChangedFrom = restorePartsFrom,
            RestorePartsChangedBefore = restorePartsBefore,
            Validate = validate,
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(extractDomainCommand);

// === show-settlement-basis command ===
var showSbMpOption = new Option<string>("--metering-point") { Description = "18-digit MP-id (Identification)" };
var showSbFromOption = new Option<DateTime>("--from") { Description = "Period start (yyyy-MM-dd)" };
var showSbToOption = new Option<DateTime>("--to") { Description = "Period end (yyyy-MM-dd, inclusive)" };
var showSbDataAreaOption = new Option<string?>("--data-area") { Description = "Override default DataAreaId" };
var showSbOutputOption = new Option<string?>("--output") { Description = "Output JSON file (default: timestamped show-settlement-basis-<n>.json)" };

var showSbCommand = new Command("show-settlement-basis", "Load SettlementBasis for one electricity MP and date range via XellentDomainMapper.LoadSettlementBasisAsync. Smoke-test of Domain.Engros.")
{
    showSbMpOption,
    showSbFromOption,
    showSbToOption,
    showSbDataAreaOption,
    showSbOutputOption
};

showSbCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var mpId = parseResult.GetValue(showSbMpOption);
    var fromDate = parseResult.GetValue(showSbFromOption);
    var toDate = parseResult.GetValue(showSbToOption);
    var dataAreaOverride = parseResult.GetValue(showSbDataAreaOption);
    var outputPath = parseResult.GetValue(showSbOutputOption);

    if (string.IsNullOrEmpty(mpId))
    {
        Console.Error.WriteLine("ERROR: --metering-point er påkrævet");
        return 1;
    }

    try
    {
        var dataAreaId = dataAreaOverride ?? DataAreaIdForEnv(settlEnv);
        var runner = new ShowSettlementBasisRunner(xellentLoader);
        outputPath ??= $"show-settlement-basis-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        await using var outputFile = new StreamWriter(outputPath, append: false, System.Text.Encoding.UTF8);
        var exitCode = await runner.RunAsync(new ShowSettlementBasisRunOptions
        {
            MeteringPointId = mpId,
            PeriodStart = DateTime.SpecifyKind(fromDate, DateTimeKind.Utc),
            PeriodEnd = DateTime.SpecifyKind(toDate, DateTimeKind.Utc),
            DataAreaId = dataAreaId
        }, outputFile, cancellationToken);
        await outputFile.FlushAsync();
        Console.Error.WriteLine($"Output written to {outputPath}");
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(showSbCommand);


var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
