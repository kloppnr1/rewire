using System.CommandLine;
using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.Migration.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Configuration — --env arg takes precedence over DOTNET_ENVIRONMENT
var envArgIndex = Array.IndexOf(args, "--env");
var settlEnv = envArgIndex >= 0 && envArgIndex + 1 < args.Length
    ? args[envArgIndex + 1]
    : Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "featuretest";
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);
var config = configBuilder.Build();

// Overlay environment-specific credentials from user-secrets (SettlApi:Environments:{env}:*)
var envSection = config.GetSection($"SettlApi:Environments:{settlEnv}");
if (envSection.Exists())
{
    configBuilder.AddInMemoryCollection(
        envSection.GetChildren().SelectMany(FlattenSection("SettlApi"))
    );
    config = configBuilder.Build();
}

Console.WriteLine($"[env: {settlEnv}]");

// Env-specific Xellent override: SettlApi:Environments:{env}:XellentDatabase wins over ConnectionStrings:XellentDatabase.
// Featuretest/dev Settl is populated from AXDB50_TEST; production Settl from AXDB50.
var envXellent = config[$"SettlApi:Environments:{settlEnv}:XellentDatabase"];
var connectionString = !string.IsNullOrEmpty(envXellent)
    ? envXellent
    : config.GetConnectionString("XellentDatabase")
      ?? throw new InvalidOperationException("Connection string 'XellentDatabase' not found in configuration");

var apiOptions = config.GetSection(SettlApiOptions.SectionName).Get<SettlApiOptions>() ?? new SettlApiOptions();

static async Task<(XellentLoadResult, WarningCollector)> LoadDomainAsync(
    string connStr, CreateOptions opts, CancellationToken ct)
{
    Console.WriteLine("--- Loading Domain data ---");
    Console.Write("  Loading... ");
    var (loadResult, warnings) = await MigrationDomainLoad.LoadAsync(connStr, opts, ct);
    Console.WriteLine($"{loadResult.Customers.Count} customers, {loadResult.MeteringPoints.Count} MPs");
    Console.WriteLine();
    return (loadResult, warnings);
}

// Flatten a config section into key-value pairs with a custom prefix
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

// Culture-independent timestamp parsing for the restore window. The window bounds are millisecond
// timestamps compared against EXU_CONTRACTPARTLOG rows, so they are read as ISO 8601 whatever the
// machine locale is — a Danish-locale reading of "2026-08-31" would silently land elsewhere.
// The bounds are taken as text and converted here rather than by a typed option, so an unreadable
// value is reported by the command validator together with the rest of the window check instead of
// throwing out of the parser.
static DateTime? ParseIsoDateTime(string? text)
    => DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : null;

var infrastructureSection = config.GetSection("Infrastructure");
var profilesDirectory = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    infrastructureSection["ProfilesDirectory"] ?? "profiles"));
var profileLoader = new ProfileLoader(profilesDirectory);

// DI setup
var services = new ServiceCollection();
services.Configure<SettlApiOptions>(config.GetSection(SettlApiOptions.SectionName));
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Warning);
});
services.AddHttpClient<ISettlAuthService, SettlAuthService>();
services.AddHttpClient<ISettlCustomerClient, SettlCustomerClient>();
services.AddHttpClient<ISettlPayerClient, SettlPayerClient>();
services.AddHttpClient<ISettlMeteringPointClient, SettlMeteringPointClient>();
services.AddHttpClient<ISettlContractClient, SettlContractClient>();
services.AddHttpClient<ISettlSettlementGroupClient, SettlSettlementGroupClient>();

var serviceProvider = services.BuildServiceProvider();

// Root command
var rootCommand = new RootCommand("Settl API Migration CLI");

// === create command ===
var createProfileArgument = new Argument<string>("profile")
{
    Description = "Profile name to run (optional with --all-active, which supplies the scope itself)",
    Arity = ArgumentArity.ZeroOrOne
};
var createAllActiveOption = new Option<bool>("--all-active") { Description = "Resolve scope from Xellent: every account with a non-terminated contract in the delivery category. Replaces the profile's Accounts list" };
var createDeliveryCategoryOption = new Option<string?>("--delivery-category") { Description = "Delivery category override (required with --all-active when no profile is given)" };
var createDryRunOption = new Option<bool>("--dry-run") { Description = "Extract data, skip API writes (still reads Settl for existing-detection)" };
var createOfflineOption = new Option<bool>("--offline") { Description = "Fully offline dry-run: zero Settl API calls, every entity reported as would-create (implies --dry-run)" };
var createEnvOption = new Option<string>("--env") { Description = "Target environment (featuretest, production, production-hev, production-vhel)" };
var createReferenceDateOption = new Option<DateTime?>("--reference-date") { Description = "Reference date override (overrides Extraction.ReferenceDate and Extraction.ChangeOfSupplierDate in profile)" };
// Contract-part restore window for the 2026-08-31 Xellent batch incident. Two options rather than
// one range so the half-open boundary is written out; a half-set window is rejected by the command
// validator below, before anything is read from Xellent.
var createRestorePartsFromOption = new Option<string?>("--restore-parts-changed-from")
{
    Description = "Start (inclusive) of the contract-part restore window, ISO 8601 e.g. 2026-08-31T09:13:49.000. " +
                  "Undoes the end dates and deletions an unattended Xellent batch wrote inside the window. " +
                  "Must be given together with --restore-parts-changed-before. Bridge until Xellent is corrected at source"
};
var createRestorePartsBeforeOption = new Option<string?>("--restore-parts-changed-before")
{
    Description = "End (exclusive) of the contract-part restore window, ISO 8601 e.g. 2026-08-31T09:59:33.000. " +
                  "Must be given together with --restore-parts-changed-from"
};
var createForceOption = new Option<bool>("--force") { Description = "Continue past per-customer failures (default: stop at first failure)" };
var createLimitOption = new Option<int?>("--limit") { Description = "Dry-run: process only first N customers after merge (full extract still runs)" };
var createNoReadCacheOption = new Option<bool>("--no-read-cache") { Description = "Disable the up-front Settl bulk read-cache; do one lookup per customer/payer/MP instead (slower; for diagnosing cache issues)" };
var createMpOnlyOption = new Option<bool>("--mp-only") { Description = "Opret kun målepunkterne og stop der — før kunder, betalere, ejer-kunder, billing-config, fritagelser og koblinger. Genoptag kørslen bagefter for at få resten med. Kan ikke kombineres med --dry-run eller --offline" };
var createMpBatchSizeOption = new Option<int?>("--mp-batch-size") { Description = "Hvor mange el-målepunkter der sendes i ét opret-kald før kunde-løkken (standard 500). Et fejlet bundt halveres ned til enkeltmålepunkter, så et højt tal koster tid, aldrig rigtighed" };
var createSearchConcurrencyOption = new Option<int?>("--search-concurrency") { Description = "Hvor mange målepunkts-opslag mod Settl der må være undervejs på én gang (standard 8). Skru ned hvis Settl svarer 429; 1 giver ét opslag ad gangen. Ændrer hverken tælling eller rækkefølge" };
var createNoAddressEnrichOption = new Option<bool>("--no-address-enrich") { Description = "Send the extracted address as-is; do not let Settl derive municipality code, street code and DAR identifier from it" };
var createSkipAddressValidationOption = new Option<bool>("--skip-address-validation") { Description = "Create metering points even when no DAR entry matches the address exactly (default: a mismatch fails the MP)" };
var createAssignFictiveCprOption = new Option<bool>("--assign-fictive-cpr") { Description = "El-ekstern only: an account with genuinely no CVR/CPR anywhere in Xellent is migrated with the fictive CPR 1111110000 instead of being excluded (default: excluded, as before)" };

var createCommand = new Command("create", "Run full migration: customers, payers, metering points, contracts")
{
    createProfileArgument,
    createAllActiveOption,
    createDeliveryCategoryOption,
    createDryRunOption,
    createOfflineOption,
    createEnvOption,
    createReferenceDateOption,
    createRestorePartsFromOption,
    createRestorePartsBeforeOption,
    createForceOption,
    createLimitOption,
    createNoReadCacheOption,
    createMpOnlyOption,
    createMpBatchSizeOption,
    createSearchConcurrencyOption,
    createNoAddressEnrichOption,
    createSkipAddressValidationOption,
    createAssignFictiveCprOption,
};

// Half a restore window is never a run: it would extract unrestored and look like a success.
createCommand.Validators.Add(result =>
{
    var fromText = result.GetValue(createRestorePartsFromOption);
    var beforeText = result.GetValue(createRestorePartsBeforeOption);
    if (fromText is null && beforeText is null) return;

    if (fromText is null || beforeText is null)
    {
        result.AddError("--restore-parts-changed-from and --restore-parts-changed-before must be given together: " +
                        "the contract-part restore runs over a bounded window, never open-ended.");
        return;
    }

    var from = ParseIsoDateTime(fromText);
    var before = ParseIsoDateTime(beforeText);
    if (from is null || before is null)
    {
        result.AddError("--restore-parts-changed-from and --restore-parts-changed-before must be ISO 8601 " +
                        $"timestamps, e.g. 2026-08-31T09:13:49.000. Could not read '{(from is null ? fromText : beforeText)}'.");
        return;
    }
    if (from >= before)
        result.AddError($"Restore window is empty or inverted: --restore-parts-changed-from {from:O} " +
                        $"is not before --restore-parts-changed-before {before:O}.");
});

createCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetValue(createProfileArgument);
    var allActive = parseResult.GetValue(createAllActiveOption);
    var deliveryCategoryOverride = parseResult.GetValue(createDeliveryCategoryOption);
    var dryRun = parseResult.GetValue(createDryRunOption);
    var offline = parseResult.GetValue(createOfflineOption);
    var referenceDateOverride = parseResult.GetValue(createReferenceDateOption);
    // Both bounds are already validated as a pair by the command validator above.
    var restorePartsFrom = ParseIsoDateTime(parseResult.GetValue(createRestorePartsFromOption));
    var restorePartsBefore = ParseIsoDateTime(parseResult.GetValue(createRestorePartsBeforeOption));
    var force = parseResult.GetValue(createForceOption);
    var limit = parseResult.GetValue(createLimitOption);
    var noReadCache = parseResult.GetValue(createNoReadCacheOption);
    var mpBatchSize = parseResult.GetValue(createMpBatchSizeOption);
    var searchConcurrency = parseResult.GetValue(createSearchConcurrencyOption);
    var mpOnly = parseResult.GetValue(createMpOnlyOption);
    var noAddressEnrich = parseResult.GetValue(createNoAddressEnrichOption);
    var skipAddressValidation = parseResult.GetValue(createSkipAddressValidationOption);
    var assignFictiveCpr = parseResult.GetValue(createAssignFictiveCprOption);

    try
    {
        if (string.IsNullOrWhiteSpace(profileName) && !allActive)
            throw new InvalidOperationException("Angiv et profilnavn, eller brug --all-active til at hente scope fra Xellent");

        var profile = string.IsNullOrWhiteSpace(profileName)
            ? new ProfileOptions
            {
                Name = "all-active",
                Extraction = new ExtractionOptions
                {
                    DeliveryCategory = deliveryCategoryOverride
                        ?? throw new InvalidOperationException(
                            "--delivery-category er påkrævet når --all-active bruges uden profil")
                }
            }
            : profileLoader.Load(profileName!);

        if (!string.IsNullOrWhiteSpace(deliveryCategoryOverride))
            profile.Extraction.DeliveryCategory = deliveryCategoryOverride;

        // Shared appsettings mapping is the base; a profile's own keys win over it.
        profile.Mappings = BaseMappingLoader.Merge(BaseMappingLoader.Load(config), profile.Mappings);

        var resolvedDate = referenceDateOverride
            ?? profile.Extraction.EffectiveReferenceDate
            ?? throw new InvalidOperationException(
                "--reference-date er påkrævet når profilen hverken har Extraction.ReferenceDate eller Extraction.ChangeOfSupplierDate");

        var exemptionConfig = new BillingExemptionConfig(
            ProductElementId: Guid.TryParse(config["BillingExemption:ProductElementId"], out var peId) ? peId : null,
            ProductElementCode: config["BillingExemption:ProductElementCode"],
            TemplateSuffix: config["BillingExemption:TemplateSuffix"],
            Percentage: double.TryParse(config["BillingExemption:Percentage"], System.Globalization.CultureInfo.InvariantCulture, out var pct) ? pct : 1);

        var createOptions = CreateOptionsFactory.Build(
            profile, settlEnv, resolvedDate, exemptionConfig, IgnoredGroupsLoader.Load(config),
            dryRun: dryRun, continueOnError: force, accountLimit: limit, offline: offline);
        createOptions.BulkCacheReads = !noReadCache;
        if (mpBatchSize is > 0) createOptions.MeteringPointCreateBatchSize = mpBatchSize.Value;
        if (searchConcurrency is > 0) createOptions.SearchConcurrency = searchConcurrency.Value;
        createOptions.MeteringPointsOnly = mpOnly;
        createOptions.EnrichAddress = !noAddressEnrich;
        createOptions.SkipAddressValidation = skipAddressValidation;
        createOptions.AssignFictiveCprForNoIdentity = assignFictiveCpr;
        createOptions.RestorePartsChangedFrom = restorePartsFrom;
        createOptions.RestorePartsChangedBefore = restorePartsBefore;
        if (restorePartsFrom is { } rpFrom && restorePartsBefore is { } rpBefore)
            Console.WriteLine(
                $"--- Contract-part restore ON: changes logged in [{rpFrom:yyyy-MM-dd HH:mm:ss.fff}, " +
                $"{rpBefore:yyyy-MM-dd HH:mm:ss.fff}) are undone at extraction time ---\n");

        if (allActive)
        {
            // Kontolisten ryger ud — extract'ets egne prædikater (dataområde, leveringskategori,
            // referencedato) udtrykker præcis dette scope, og at sende de 45.000 ids tilbage som
            // query-parametre er det SQL Server nægter at lægge en plan for. Sammenlægningen slås
            // fra samme sted: all-active er én Settl-kunde per Xellent-konto, og lod man den stå,
            // gav samme scope forskelligt resultat alt efter om kørslen blev startet fra
            // kommandolinjen eller gennem dashboardets batch-vej. Gælder også med profil — det er
            // scopet der afgør det. Ejer-klyngerne (OwnerCluster/TG4) er upåvirkede.
            CreateOptionsFactory.ApplyAllActiveScope(createOptions);

            // Counted separately so the run still reports its size up front. Read-only.
            var activeAt = profile.Extraction.ChangeOfSupplierDate ?? resolvedDate.AddDays(1);
            Console.WriteLine($"--- Scope: {profile.Extraction.DataAreaId}/{profile.Extraction.DeliveryCategory}, aktive pr. {activeAt:yyyy-MM-dd} ---");
            Console.Write("  Counting... ");
            // The restore window goes in as well: the scope is applied to the extraction's output,
            // so a scope resolved without it would cut out exactly the contracts the restore let in.
            var scopeWarnings = new WarningCollector();
            var scope = await ActiveAccountSelector.SelectAsync(
                connectionString,
                profile.Extraction.DeliveryCategory,
                activeAt,
                profile.Extraction.DataAreaId,
                cancellationToken,
                restorePartsFrom,
                restorePartsBefore,
                scopeWarnings);
            Console.WriteLine($"{scope.Length} accounts");
            foreach (var info in scopeWarnings.GetInfos()) Console.WriteLine($"  {info}");
            Console.WriteLine();

            if (scope.Length == 0)
                throw new InvalidOperationException(
                    "--all-active matchede 0 konti — kontrollér --delivery-category og reference-dato");

            if (profile.Mappings is null)
                Console.WriteLine("WARNING: ingen Mappings — ProductNum/SettlementGroup mappes ikke. Angiv en profil hvis det ikke er meningen.\n");
        }
        else if (createOptions.Accounts.Length == 0)
        {
            throw new InvalidOperationException(
                $"Profilen '{profile.Name}' har ingen Accounts. Tilføj konti, eller kør med --all-active");
        }

        var customerClient = serviceProvider.GetRequiredService<ISettlCustomerClient>();
        var payerClient = serviceProvider.GetRequiredService<ISettlPayerClient>();
        var meteringPointClient = serviceProvider.GetRequiredService<ISettlMeteringPointClient>();
        var contractClient = serviceProvider.GetRequiredService<ISettlContractClient>();
        var settlementGroupClient = serviceProvider.GetRequiredService<ISettlSettlementGroupClient>();

        var (loadResult, warnings) = await LoadDomainAsync(connectionString, createOptions, cancellationToken);

        var runner = new CreateRunner(apiOptions, customerClient, payerClient, meteringPointClient, contractClient, settlementGroupClient, warnings);
        runner.OnLogLine = Console.WriteLine;
        var (exitCode, _) = await runner.RunAsync(createOptions, loadResult, cancellationToken);
        return exitCode;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"ERROR: {ex.Message}");
        Console.ResetColor();
        return 1;
    }
});

rootCommand.Subcommands.Add(createCommand);

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
