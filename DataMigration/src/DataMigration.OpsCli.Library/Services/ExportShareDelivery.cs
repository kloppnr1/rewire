using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DataMigration.OpsCli.Services;

public sealed class ExportShareDeliveryOptions
{
    /// <summary>The share folder, e.g. <c>\\server\share\settl-export</c>. Each environment gets its own folder below it.</summary>
    public string Root { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;

    /// <summary>Published runs to keep; older ones (and failed ones beyond this count) are deleted.</summary>
    public int Keep { get; set; } = 14;

    /// <summary>The Settl payment agreement CSV the export reads. It must exist and be younger than
    /// <see cref="PaymentAgreementsMaxAge"/>, so a run never publishes stale payment data.</summary>
    public string? PaymentAgreementsCsv { get; set; }

    public TimeSpan PaymentAgreementsMaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Written to the manifest: what one line in customers.jsonl is.</summary>
    public string Shape { get; set; } = string.Empty;

    /// <summary>The run covers only some customers (a trial); written to the manifest.</summary>
    public bool Trial { get; set; }
}

/// <summary>
/// Delivers one export run to a share for another system to pick up (the SonWin migration).
///
/// <para>Layout under <c>Root/Environment</c>: one folder per run, named by its UTC start
/// (<c>yyyy-MM-ddTHHmmssZ</c>), holding <c>customers.jsonl</c> (every customer, one per line),
/// <c>delta.jsonl</c> (added, changed and removed customers against the previous published run),
/// <c>manifest.json</c> and, written last, <c>_READY</c>. A reader takes only folders with
/// <c>_READY</c>. The previous run's line hashes are kept in <c>state/</c>.</para>
///
/// <para>The run is written to a <c>.tmp-</c> folder and renamed when complete. A failed export
/// (read errors included) is renamed to <c>.failed</c> without <c>_READY</c> and leaves the state
/// alone, so the next delta is against the last published run. A <c>.lock</c> file stops two runs
/// overlapping; a run that crashed leaves it behind and the next run refuses to start until it is
/// removed by hand.</para>
/// </summary>
public sealed class ExportShareDelivery(Func<DateTimeOffset> clock, Action<string> log)
{
    public const string CustomersFile = "customers.jsonl";
    public const string DeltaFile = "delta.jsonl";
    public const string ManifestFile = "manifest.json";
    public const string ReadyFile = "_READY";
    public const string LockFile = ".lock";
    private const string StateFile = "customers.json";
    private const int SchemaVersion = 1;

    private static readonly Regex RunFolder = new(@"^\d{4}-\d{2}-\d{2}T\d{6}Z(\.failed)?$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JsonOutput.HumanReadable };

    /// <summary>
    /// Runs <paramref name="export"/> into a new run folder and publishes it. The export writes JSON
    /// Lines to the path it is given and returns its exit code; anything but 0 is a failed run.
    /// Returns 0 when published, otherwise the export's code or 1.
    /// </summary>
    public async Task<int> RunAsync(ExportShareDeliveryOptions options,
        Func<string, CancellationToken, Task<int>> export, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Root) || string.IsNullOrWhiteSpace(options.Environment))
        {
            log("ERROR: the share root and the environment are required");
            return 1;
        }
        var started = clock();
        var envDir = Path.Combine(options.Root, options.Environment);
        var stateDir = Path.Combine(envDir, "state");
        Directory.CreateDirectory(stateDir);

        JsonObject? paymentAgreements = null;
        if (options.PaymentAgreementsCsv is { Length: > 0 } csv)
        {
            if (!File.Exists(csv))
            {
                log($"ERROR: payment agreement file {csv} not found");
                return 1;
            }
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(csv), TimeSpan.Zero);
            if (started - written > options.PaymentAgreementsMaxAge)
            {
                log($"ERROR: payment agreement file {csv} was written {written:u}, more than " +
                    $"{options.PaymentAgreementsMaxAge.TotalHours:0} hours ago; export a fresh one from Settl");
                return 1;
            }
            paymentAgreements = new JsonObject { ["file"] = Path.GetFileName(csv), ["writtenUtc"] = written.ToString("O") };
        }

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Path.Combine(envDir, LockFile), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            log($"ERROR: {Path.Combine(envDir, LockFile)} exists: another run is going, or one crashed. " +
                "Remove the file when no run is going.");
            return 1;
        }

        await using (lockStream)
        {
            await lockStream.WriteAsync(Encoding.UTF8.GetBytes($"{started:O} pid {System.Environment.ProcessId}\n"), ct);
            await lockStream.FlushAsync(ct);

            var name = started.UtcDateTime.ToString("yyyy-MM-dd'T'HHmmss'Z'");
            var tmp = Path.Combine(envDir, ".tmp-" + name);
            var final = Path.Combine(envDir, name);
            if (Directory.Exists(final) || Directory.Exists(tmp))
            {
                log($"ERROR: run folder {name} already exists");
                return 1;
            }
            Directory.CreateDirectory(tmp);

            var exitCode = await export(Path.Combine(tmp, CustomersFile), ct);
            if (exitCode != 0)
            {
                Directory.Move(tmp, final + ".failed");
                log($"Export failed (exit {exitCode}); kept as {name}.failed, nothing published");
                Prune(envDir, options.Keep);
                return exitCode;
            }

            var previous = await LoadStateAsync(stateDir, ct);
            var (current, counts, error) = await WriteDeltaAsync(tmp, previous?.Hashes, ct);
            if (error != null)
            {
                Directory.Move(tmp, final + ".failed");
                log($"ERROR: {error}; kept as {name}.failed, nothing published");
                Prune(envDir, options.Keep);
                return 1;
            }

            var manifest = new JsonObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["environment"] = options.Environment,
                ["shape"] = options.Shape,
                ["trial"] = options.Trial,
                ["run"] = name,
                ["previousRun"] = previous?.Run,
                ["startedUtc"] = started.ToString("O"),
                ["finishedUtc"] = clock().ToString("O"),
                ["customers"] = counts.Customers,
                ["added"] = counts.Added,
                ["changed"] = counts.Changed,
                ["removed"] = counts.Removed,
                ["paymentAgreements"] = paymentAgreements,
                ["files"] = new JsonArray(
                    FileEntry(Path.Combine(tmp, CustomersFile)),
                    FileEntry(Path.Combine(tmp, DeltaFile))),
            };
            await File.WriteAllTextAsync(Path.Combine(tmp, ManifestFile), manifest.ToJsonString(Indented), ct);

            Directory.Move(tmp, final);
            await File.WriteAllTextAsync(Path.Combine(final, ReadyFile), name + "\n", ct);

            // After publishing: a crash between the two leaves the old state, so the next delta repeats
            // this run's changes rather than missing them.
            await SaveStateAsync(stateDir, name, current, ct);
            log($"Published {name}: {counts.Customers} customer(s), {counts.Added} added, " +
                $"{counts.Changed} changed, {counts.Removed} removed");
            Prune(envDir, options.Keep);
            return 0;
        }
    }

    private sealed record State(string Run, Dictionary<string, string> Hashes);

    private sealed record Counts(int Customers, int Added, int Changed, int Removed);

    /// <summary>
    /// Hashes every line of customers.jsonl by customer number and writes delta.jsonl: the full line
    /// for an added or changed customer, the number alone for a removed one.
    /// </summary>
    private static async Task<(Dictionary<string, string> Current, Counts Counts, string? Error)> WriteDeltaAsync(
        string runDir, Dictionary<string, string>? previous, CancellationToken ct)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        int added = 0, changed = 0;
        await using var delta = new StreamWriter(Path.Combine(runDir, DeltaFile), false, new UTF8Encoding(false)) { NewLine = "\n" };
        using var reader = new StreamReader(Path.Combine(runDir, CustomersFile), Encoding.UTF8);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (line.Length == 0) continue;
            string? key;
            try
            {
                var node = JsonNode.Parse(line);
                key = node?["customerNumber"]?.ToString() ?? node?["customerId"]?.ToString();
            }
            catch (JsonException ex)
            {
                return (current, new Counts(0, 0, 0, 0), $"{CustomersFile} line {lineNumber} is not JSON: {ex.Message}");
            }
            if (string.IsNullOrEmpty(key))
                return (current, new Counts(0, 0, 0, 0), $"{CustomersFile} line {lineNumber} has no customer number");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
            if (!current.TryAdd(key, hash))
                return (current, new Counts(0, 0, 0, 0), $"customer {key} appears twice in {CustomersFile}");

            string? op = previous == null || !previous.TryGetValue(key, out var before) ? "added"
                : before != hash ? "changed" : null;
            if (op == null) continue;
            if (op == "added") added++; else changed++;
            await delta.WriteLineAsync($"{{\"op\":\"{op}\",\"customerNumber\":{JsonSerializer.Serialize(key)},\"customer\":{line}}}");
        }

        var removed = 0;
        foreach (var key in (previous?.Keys ?? Enumerable.Empty<string>()).Where(k => !current.ContainsKey(k)).Order(StringComparer.Ordinal))
        {
            removed++;
            await delta.WriteLineAsync($"{{\"op\":\"removed\",\"customerNumber\":{JsonSerializer.Serialize(key)}}}");
        }
        return (current, new Counts(current.Count, added, changed, removed), null);
    }

    private static async Task<State?> LoadStateAsync(string stateDir, CancellationToken ct)
    {
        var path = Path.Combine(stateDir, StateFile);
        if (!File.Exists(path)) return null;
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in node["customers"]!.AsObject()) hashes[key] = value!.GetValue<string>();
        return new State(node["run"]!.GetValue<string>(), hashes);
    }

    private static async Task SaveStateAsync(string stateDir, string run, Dictionary<string, string> hashes, CancellationToken ct)
    {
        var customers = new JsonObject();
        foreach (var (key, hash) in hashes.OrderBy(kv => kv.Key, StringComparer.Ordinal)) customers[key] = hash;
        var path = Path.Combine(stateDir, StateFile);
        await File.WriteAllTextAsync(path + ".tmp", new JsonObject { ["run"] = run, ["customers"] = customers }.ToJsonString(), ct);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static JsonObject FileEntry(string path)
    {
        using var stream = File.OpenRead(path);
        return new JsonObject
        {
            ["name"] = Path.GetFileName(path),
            ["bytes"] = stream.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(stream)),
        };
    }

    /// <summary>Keeps the newest <paramref name="keep"/> published runs and the newest
    /// <paramref name="keep"/> failed ones; older run folders are deleted.</summary>
    private void Prune(string envDir, int keep)
    {
        if (keep <= 0) return;
        var runs = new DirectoryInfo(envDir).GetDirectories().Where(d => RunFolder.IsMatch(d.Name)).ToList();
        foreach (var group in runs.GroupBy(d => d.Name.EndsWith(".failed", StringComparison.Ordinal)))
            foreach (var old in group.OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(keep))
            {
                try
                {
                    old.Delete(recursive: true);
                }
                catch (IOException ex)
                {
                    log($"WARNING: could not delete old run {old.Name}: {ex.Message}");
                }
            }
    }
}
