using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using TimeSeries.Cli.Runners;
using TimeSeries.Cli.Services;

// Resolve --env early (before System.CommandLine parses) so config can be overlaid for the chosen env.
var envArgIndex = Array.IndexOf(args, "--env");
var settlEnv = envArgIndex >= 0 && envArgIndex + 1 < args.Length
    ? args[envArgIndex + 1]
    : Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "featuretest";

var config = LoadConfiguration(settlEnv);
Console.WriteLine($"[env: {settlEnv}]");

// Shared options
var accountOption = new Option<string?>("--account") { Description = "Xellent account number" };
var meteringPointOption = new Option<string?>("--metering-point") { Description = "Xellent metering point ID" };
var startOption = new Option<DateTime?>("--start") { Description = "Start date (yyyy-MM-dd)" };
var endOption = new Option<DateTime?>("--end") { Description = "End date (yyyy-MM-dd)" };
var deliveryCategoryOption = new Option<string?>("--delivery-category") { Description = "Delivery category (default: El-ekstern)" };
var envOption = new Option<string>("--env") { Description = "Target environment: featuretest, production, production-hev, production-vhel (default: featuretest)" };

// Extract-specific
var simulateOption = new Option<bool>("--simulate-missing") { Description = "Fill gaps with simulated data" };

// Post-specific
var dryRunOption = new Option<bool>("--dry-run") { Description = "Preview what would be posted without making any changes" };
var forceOption = new Option<bool>("--force") { Description = "Continue past per-MP failures (default: stop at first failure). Run still exits non-zero if any MP failed." };
var skipResolutionChangeOption = new Option<bool>("--skip-resolution-change") { Description = "Skip metering points with a resolution change (typically Hour → Quarter). Skipped MPs are logged but do not cause a non-zero exit." };
var skipIfExistingPointsOption = new Option<bool>("--skip-if-existing-points") { Description = "Pre-check Settl via points-summary before POST. If any matched TS already has points in the window, the whole MP is skipped. Skipped MPs are logged but do not cause a non-zero exit." };
var allOption = new Option<bool>("--all") { Description = "Post every El metering point in Settl that is in delivery; the account is whoever held it in Xellent the day before delivery starts. Mutually exclusive with --account/--metering-point/--start/--end/--delivery-category. Child metering points are never posted." };
var cosFromOption = new Option<DateOnly?>("--cos-from") { Description = "With --all: only metering points whose delivery in Settl starts on or after this date (yyyy-MM-dd)" };
var cosToOption = new Option<DateOnly?>("--cos-to") { Description = "With --all: only metering points whose delivery in Settl starts on or before this date (yyyy-MM-dd)" };
var skipOption = new Option<int?>("--skip") { Description = "With --all: pass over the first N metering points in range (by identification); combine with --limit to run in batches" };
var limitOption = new Option<int?>("--limit") { Description = "With --all: stop once N metering points are sent (ready, in a dry run); skipped, excluded, blocked and done ones do not count" };
var offlineOption = new Option<bool>("--offline") { Description = "With --all: read Settl and Xellent from the environment's snapshot (made by the snapshot command) instead of asking them again. Missing entries are read live and added. Time series points are always read fresh; posts always go to Settl." };
var allowSwitchesOption = new Option<bool>("--allow-switches") { Description = "With --all: also send metering points whose reading periods switch resolution inside the window. Without it they are blocked until Settl confirms their series." };
// Snapshot-specific
var parallelOption = new Option<int?>("--parallel") { Description = "Settl reads per metering point run this many at a time (default 6)" };
var refreshOption = new Option<bool>("--refresh") { Description = "Throw the stored reads away and read everything again (done marks are kept). Without it, only what is missing is read." };
var refreshBlockedOption = new Option<bool>("--refresh-blocked") { Description = "Read again only the metering points the last post --all listed in its blocked-*.csv files (after someone fixed a group); everything else stays as stored. Minutes, not hours." };

// === extract command ===
var extractCommand = new Command("extract", "Extract time series from Xellent to JSON files")
{
    accountOption, meteringPointOption, startOption, endOption,
    deliveryCategoryOption, simulateOption, envOption
};

extractCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var (connectionString, outputDir, years) = LoadXellentConfig(config);
    var runner = new TimeSeriesRunner(connectionString, outputDir, years);
    return await runner.RunExtractAsync(new ExtractOptions
    {
        AccountNumber = parseResult.GetValue(accountOption),
        MeteringPointId = parseResult.GetValue(meteringPointOption),
        StartDate = parseResult.GetValue(startOption),
        EndDate = parseResult.GetValue(endOption),
        DeliveryCategory = parseResult.GetValue(deliveryCategoryOption),
        SimulateMissing = parseResult.GetValue(simulateOption)
    });
});

// === post command ===
var postCommand = new Command("post", "Extract time series from Xellent and POST to Settl API")
{
    accountOption, meteringPointOption, startOption, endOption,
    deliveryCategoryOption, envOption, dryRunOption, forceOption, skipResolutionChangeOption, skipIfExistingPointsOption, allOption, cosFromOption, cosToOption, skipOption, limitOption, offlineOption, allowSwitchesOption
};

postCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var all = parseResult.GetValue(allOption);
    var (connectionString, outputDir, years) = LoadXellentConfig(config);

    if (all)
    {
        var conflicting = new[]
        {
            (parseResult.GetValue(accountOption), "--account"),
            (parseResult.GetValue(meteringPointOption), "--metering-point"),
            (parseResult.GetValue(deliveryCategoryOption), "--delivery-category"),
        }.Where(t => t.Item1 != null).Select(t => t.Item2)
        .Concat(parseResult.GetValue(startOption) != null ? ["--start"] : Array.Empty<string>())
        .Concat(parseResult.GetValue(endOption) != null ? ["--end"] : Array.Empty<string>())
        .ToArray();
        if (conflicting.Length > 0)
        {
            Console.Error.WriteLine($"ERROR: --all is mutually exclusive with: {string.Join(", ", conflicting)}");
            return 1;
        }
    }
    else if (parseResult.GetValue(cosFromOption) != null || parseResult.GetValue(cosToOption) != null || parseResult.GetValue(skipOption) != null
             || parseResult.GetValue(limitOption) != null || parseResult.GetValue(offlineOption) || parseResult.GetValue(allowSwitchesOption))
    {
        Console.Error.WriteLine("ERROR: --cos-from, --cos-to, --skip, --limit, --offline and --allow-switches only apply with --all");
        return 1;
    }

    await using var provider = BuildSettlServices(config);
    var runner = BuildSettlRunner(provider, connectionString, outputDir, years);

    if (all)
    {
        // One line per metering point goes to a log file; the console shows the counts per state,
        // updating in place, and the summary at the end.
        Directory.CreateDirectory(outputDir);
        var logPath = Path.GetFullPath(Path.Combine(outputDir, $"post-{settlEnv}-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        using var runConsole = RunConsole.ForPostAll(logPath);
        runner.OnLogLine = runConsole.Line;
        runner.OnStatus = runConsole.Status;
        runner.OnStepProgress = null;
        Console.WriteLine($"Details per metering point: {logPath}");
        return await runner.RunPostAllAsync(
            settlEnv,
            parseResult.GetValue(dryRunOption),
            parseResult.GetValue(forceOption),
            parseResult.GetValue(skipResolutionChangeOption),
            parseResult.GetValue(skipIfExistingPointsOption),
            parseResult.GetValue(cosFromOption),
            parseResult.GetValue(cosToOption),
            parseResult.GetValue(limitOption),
            skip: parseResult.GetValue(skipOption),
            offline: parseResult.GetValue(offlineOption),
            cancellationToken: cancellationToken,
            allowSwitches: parseResult.GetValue(allowSwitchesOption));
    }

    return await runner.RunPostAsync(new PostOptions
    {
        AccountNumber = parseResult.GetValue(accountOption),
        MeteringPointId = parseResult.GetValue(meteringPointOption),
        StartDate = parseResult.GetValue(startOption),
        EndDate = parseResult.GetValue(endOption),
        DeliveryCategory = parseResult.GetValue(deliveryCategoryOption),
        Environment = settlEnv,
        DryRun = parseResult.GetValue(dryRunOption),
        ContinueOnError = parseResult.GetValue(forceOption),
        SkipResolutionChange = parseResult.GetValue(skipResolutionChangeOption),
        SkipIfExistingPoints = parseResult.GetValue(skipIfExistingPointsOption)
    }, cancellationToken);
});

// === snapshot command ===
var snapshotCommand = new Command("snapshot",
    "Read everything post --all needs from Settl and Xellent (not the time series points) and store it under " +
    "<Output:BaseDirectory>/snapshot/<env>, for post --all --offline. Keeps what is already stored; delete the folder to start over.")
{
    envOption, parallelOption, refreshOption, refreshBlockedOption
};

snapshotCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var (connectionString, outputDir, years) = LoadXellentConfig(config);
    await using var provider = BuildSettlServices(config);
    var runner = BuildSettlRunner(provider, connectionString, outputDir, years);
    // Details go to a log file; the console shows the lines, the step progress in place and the summary.
    Directory.CreateDirectory(outputDir);
    var logPath = Path.GetFullPath(Path.Combine(outputDir, $"snapshot-{settlEnv}-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
    using var runConsole = RunConsole.ForSnapshot(logPath);
    runner.OnLogLine = runConsole.Line;
    runner.OnStepProgress = runConsole.StepProgress;
    Console.WriteLine($"Details: {logPath}");
    return await runner.RunSeedSnapshotAsync(settlEnv, parseResult.GetValue(parallelOption) ?? 6, parseResult.GetValue(refreshOption),
        cancellationToken, refreshBlocked: parseResult.GetValue(refreshBlockedOption));
});

// === resolution-changes command ===
var outOption = new Option<string?>("--out") { Description = "CSV file to write (default: <Output:BaseDirectory>/resolution-changes-<env>.csv)" };
var offlineListOption = new Option<bool>("--offline") { Description = "Read Settl and Xellent from the environment's snapshot instead of asking them" };
var resolutionChangesCommand = new Command("resolution-changes",
    "Write the CSV for Settl to create missing time series: one row per reading-period resolution switch inside the post " +
    "window (either direction) when Settl lacks the Hour or Quarter series. Several switches are flagged with a warning. Read-only.")
{
    envOption, outOption, offlineListOption
};

resolutionChangesCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var (connectionString, outputDir, years) = LoadXellentConfig(config);
    await using var provider = BuildSettlServices(config);
    var runner = BuildSettlRunner(provider, connectionString, outputDir, years);
    return await runner.RunResolutionChangesAsync(settlEnv, parseResult.GetValue(offlineListOption), parseResult.GetValue(outOption),
        cancellationToken);
});

// === requarter command ===
var requarterOutOption = new Option<string?>("--out") { Description = "CSV file to write (default: <Output:BaseDirectory>/requarter-<env>.csv)" };
var requarterParallelOption = new Option<int>("--parallel")
{
    Description = "How many metering points to ask Settl about at a time (default 6)",
    DefaultValueFactory = _ => 6
};
var requarterCommand = new Command("requarter",
    "List the done metering points posted as hourly values where Xellent's reading period is 15-minute and Settl's " +
    "Quarter series has no values. Read-only: sends nothing and changes no done marks.")
{
    envOption, requarterOutOption, requarterParallelOption
};

requarterCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var (connectionString, outputDir, years) = LoadXellentConfig(config);
    await using var provider = BuildSettlServices(config);
    var runner = BuildSettlRunner(provider, connectionString, outputDir, years);
    return await runner.RunRequarterAsync(settlEnv, parseResult.GetValue(requarterParallelOption),
        parseResult.GetValue(requarterOutOption), cancellationToken);
});

// === switch-import command ===
var switchImportInOption = new Option<string?>("--in") { Description = "The resolution-switches file to read (default: <Output:BaseDirectory>/resolution-switches-<env>.csv, written by post --all)" };
var switchImportParallelOption = new Option<int>("--parallel")
{
    Description = "How many metering points to ask Settl about at a time (default 6)",
    DefaultValueFactory = _ => 6
};
var switchImportCommand = new Command("switch-import",
    "Write Settl's import workbooks (MeteringPointMeters_V8, TimeSeries_V1) for the resolution switches post --all listed: " +
    "the old resolution from Settl's earliest meter entry, the new one from the DataHub message's date. " +
    "Read-only: it changes nothing in Settl.")
{
    envOption, switchImportInOption, switchImportParallelOption
};

switchImportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var outputDir = config["Output:BaseDirectory"] ?? "output";
    await using var provider = BuildSettlServices(config);
    var reader = new SettlSwitchImportReader(provider.GetRequiredService<ISettlMeteringPointClient>(),
        provider.GetRequiredService<ISettlTimeSeriesMeteringPointClient>());
    var runner = new SwitchImportRunner(reader, Console.WriteLine);
    return await runner.RunAsync(settlEnv,
        parseResult.GetValue(switchImportInOption) ?? Path.Combine(outputDir, $"resolution-switches-{settlEnv}.csv"),
        outputDir, parseResult.GetValue(switchImportParallelOption), DateOnly.FromDateTime(DateTime.Today), cancellationToken);
});

// === progress command ===
var progressWatchOption = new Option<bool>("--watch") { Description = "Repaint the report in place every --refresh seconds until Ctrl+C" };
var progressRefreshOption = new Option<int>("--refresh")
{
    Description = "Seconds between repaints with --watch (default 10)",
    DefaultValueFactory = _ => 10
};
var progressCommand = new Command("progress",
    "Show how far a (running) snapshot is: phase, progress in the current step, done marks and what remains. " +
    "Read-only: reads <Output:BaseDirectory>/snapshot/<env> only, safe to run from another session while snapshot runs.")
{
    envOption, progressWatchOption, progressRefreshOption
};

progressCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var outputDir = config["Output:BaseDirectory"] ?? "output";
    return await SnapshotProgressCommand.RunAsync(Path.Combine(outputDir, "snapshot", settlEnv), settlEnv,
        parseResult.GetValue(progressWatchOption), parseResult.GetValue(progressRefreshOption), cancellationToken);
});

var rootCommand = new RootCommand("Time Series Migration CLI — extract from Xellent, post to Settl");
rootCommand.Subcommands.Add(resolutionChangesCommand);
rootCommand.Subcommands.Add(requarterCommand);
rootCommand.Subcommands.Add(extractCommand);
rootCommand.Subcommands.Add(postCommand);
rootCommand.Subcommands.Add(snapshotCommand);
rootCommand.Subcommands.Add(progressCommand);
rootCommand.Subcommands.Add(switchImportCommand);

var parseResultTop = rootCommand.Parse(args);
return await parseResultTop.InvokeAsync();

static ServiceProvider BuildSettlServices(IConfiguration config)
{
    var services = new ServiceCollection();
    services.AddSingleton<IConfiguration>(config);
    // HttpClient logs four lines per request at Information — most of a run's output. The runner logs
    // failed Settl calls itself, so only the client's warnings and errors are kept.
    services.AddLogging(b => b.AddConsole()
        .SetMinimumLevel(LogLevel.Information)
        .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning));
    services.Configure<SettlApiOptions>(config.GetSection(SettlApiOptions.SectionName));
    services.AddHttpClient<ISettlAuthService, SettlAuthService>();
    services.AddHttpClient<ISettlTimeSeriesClient, SettlTimeSeriesClient>();
    services.AddHttpClient<ISettlTimeSeriesMeteringPointClient, SettlTimeSeriesMeteringPointClient>();
    services.AddHttpClient<ISettlMeteringPointClient, SettlMeteringPointClient>();
    services.AddHttpClient<ISettlCustomerClient, SettlCustomerClient>();
    services.AddHttpClient<ISettlPayerClient, SettlPayerClient>();
    return services.BuildServiceProvider();
}

static TimeSeriesRunner BuildSettlRunner(IServiceProvider provider, string connectionString, string outputDir, int years) => new(
    connectionString,
    outputDir,
    years,
    settlTimeSeriesClient: provider.GetRequiredService<ISettlTimeSeriesClient>(),
    settlMeteringPointClient: provider.GetRequiredService<ISettlTimeSeriesMeteringPointClient>(),
    settlCustomerClient: provider.GetRequiredService<ISettlCustomerClient>(),
    settlPayerClient: provider.GetRequiredService<ISettlPayerClient>())
{
    OnStepProgress = ConsoleProgress()
};

// Progress of a long step as one console line that updates in place (at most every 200 ms, and always at
// the end). Redirected output gets an ordinary line every 1,000 items instead, since \r means nothing there.
static Action<string, int, int> ConsoleProgress()
{
    var gate = new object();
    var lastWrite = DateTime.MinValue;
    return (step, done, total) =>
    {
        lock (gate)
        {
            var last = done >= total;
            if (Console.IsOutputRedirected)
            {
                if (last || done % 1000 == 0)
                    Console.WriteLine($"  {step}: {done:N0}/{total:N0}");
                return;
            }
            if (!last && DateTime.UtcNow - lastWrite < TimeSpan.FromMilliseconds(200))
                return;
            lastWrite = DateTime.UtcNow;
            Console.Write($"\r  {step}: {done:N0}/{total:N0} ({(total == 0 ? 100 : done * 100 / total)}%)   ");
            if (last)
                Console.WriteLine();
        }
    };
}

static IConfiguration LoadConfiguration(string env)
{
    var configBuilder = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile("appsettings.local.json", optional: true)
        .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true);

    var config = configBuilder.Build();

    var envSection = config.GetSection($"SettlApi:Environments:{env}");
    if (!envSection.Exists())
    {
        var available = config.GetSection("SettlApi:Environments").GetChildren()
            .Select(c => c.Key).ToArray();
        throw new InvalidOperationException(
            $"Unknown environment '{env}'. Available: {string.Join(", ", available)}");
    }

    configBuilder.AddInMemoryCollection(
        envSection.GetChildren().SelectMany(FlattenSection("SettlApi"))
    );
    config = configBuilder.Build();

    var envXellent = config[$"SettlApi:Environments:{env}:XellentDatabase"];
    if (!string.IsNullOrEmpty(envXellent))
    {
        configBuilder.AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("ConnectionStrings:XellentDatabase", envXellent)
        });
        config = configBuilder.Build();
    }

    return config;
}

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

static (string ConnectionString, string OutputDir, int Years) LoadXellentConfig(IConfiguration config)
{
    var connectionString = config.GetConnectionString("XellentDatabase")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:XellentDatabase");

    var outputDir = config["Output:BaseDirectory"] ?? "output";
    var years = int.TryParse(config["TimeSeries:DefaultYears"], out var y) ? y : 3;

    return (connectionString, outputDir, years);
}

public partial class Program;

/// <summary>
/// The console of a long command (post --all, snapshot): every log line goes to the log file; the console
/// shows the command's own lines and one status line that updates in place. From the summary line on,
/// everything is shown except the per-item lists ("  - …"), which stay in the file.
/// </summary>
sealed class RunConsole(string logPath, string summaryPrefix, Func<string, bool> showBeforeSummary) : IDisposable
{
    private readonly string _logPath = logPath;
    private readonly StreamWriter _file = new(logPath) { AutoFlush = true };
    private readonly object _gate = new();
    private DateTime _lastStatus = DateTime.MinValue;
    private int _statusLength;
    private bool _summary;
    private bool _listNoted;

    // post --all: lines before the per-metering-point part that say what the run is about to do.
    private static readonly string[] PostSetupPrefixes =
        ["Already done:", "Excluded by hand:", "Migration:", "Payer 6#####", "Payer check", "Limit of", "Aborting run"];

    public static RunConsole ForPostAll(string logPath) =>
        new(logPath, "Run completed:", line => PostSetupPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)));

    // snapshot logs only a handful of lines before its summary; all of them are shown.
    public static RunConsole ForSnapshot(string logPath) => new(logPath, "Snapshot seed completed:", _ => true);

    public void Line(string line)
    {
        lock (_gate)
        {
            _file.WriteLine(line);
            if (line.StartsWith(summaryPrefix, StringComparison.Ordinal))
                _summary = true;
            var listItem = line.StartsWith("  - ", StringComparison.Ordinal);
            if (_summary && listItem && !_listNoted)
            {
                _listNoted = true;
                ClearStatus();
                Console.WriteLine($"  (listed in {_logPath})");
            }
            var show = _summary ? !listItem : showBeforeSummary(line);
            if (!show)
                return;
            ClearStatus();
            Console.WriteLine(line);
        }
    }

    public void Status(string status)
    {
        lock (_gate)
        {
            if (Console.IsOutputRedirected || _summary)
                return;
            if (DateTime.UtcNow - _lastStatus < TimeSpan.FromMilliseconds(200))
                return;
            _lastStatus = DateTime.UtcNow;
            var text = "  " + status;
            Console.Write("\r" + text.PadRight(_statusLength));
            _statusLength = text.Length;
        }
    }

    /// <summary>A long step's progress (snapshot): the same in-place line.</summary>
    public void StepProgress(string step, int done, int total) =>
        Status($"{step}: {done:N0}/{total:N0} ({(total == 0 ? 100 : done * 100 / total)}%)");

    private void ClearStatus()
    {
        if (_statusLength == 0)
            return;
        Console.Write("\r" + new string(' ', _statusLength) + "\r");
        _statusLength = 0;
    }

    public void Dispose()
    {
        lock (_gate)
            ClearStatus();
        _file.Dispose();
    }
}
