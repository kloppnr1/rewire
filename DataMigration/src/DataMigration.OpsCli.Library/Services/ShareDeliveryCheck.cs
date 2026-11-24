using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DataMigration.OpsCli.Services;

/// <summary>
/// Checks a share delivery written by <see cref="ExportShareDelivery"/>, so a person can tell at a
/// glance whether the newest run is complete and fresh: every file the manifest lists is there with
/// the right size and checksum, the line counts match the manifest, every customer has a unique
/// number and no read errors, every metering point id has 18 digits, and the run is recent.
/// Read-only: it never changes the share. Prints ids and counts only, no customer data.
/// </summary>
public sealed class ShareDeliveryCheck(Func<DateTimeOffset> clock, Action<string> log)
{
    private static readonly Regex RunFolder = new(@"^\d{4}-\d{2}-\d{2}T\d{6}Z$", RegexOptions.Compiled);
    private static readonly Regex Identification = new(@"^\d{18}$", RegexOptions.Compiled);

    /// <summary>Returns 0 when the run is fine, 1 when there is a problem (each one is logged).</summary>
    public int Run(string root, string environment, TimeSpan maxAge, string? runName = null)
    {
        var envDir = Path.Combine(root, environment);
        if (!Directory.Exists(envDir))
            return Fail([$"no delivery folder {envDir}"]);

        var runs = Directory.GetDirectories(envDir).Select(Path.GetFileName).OfType<string>()
            .Where(n => RunFolder.IsMatch(n)).Order(StringComparer.Ordinal).ToList();
        var failedRuns = Directory.GetDirectories(envDir).Select(Path.GetFileName).OfType<string>()
            .Count(n => n.EndsWith(".failed", StringComparison.Ordinal));
        var name = runName ?? runs.LastOrDefault(r => File.Exists(Path.Combine(envDir, r, ExportShareDelivery.ReadyFile)));
        if (name == null)
            return Fail([$"no published run (with {ExportShareDelivery.ReadyFile}) in {envDir}"]);

        var problems = new List<string>();
        var runDir = Path.Combine(envDir, name);
        log($"Run: {name} in {envDir}");

        if (!File.Exists(Path.Combine(runDir, ExportShareDelivery.ReadyFile)))
            problems.Add($"{ExportShareDelivery.ReadyFile} is missing: the run is not published");
        var manifestPath = Path.Combine(runDir, ExportShareDelivery.ManifestFile);
        if (!File.Exists(manifestPath))
            return Fail([.. problems, $"{ExportShareDelivery.ManifestFile} is missing"]);
        JsonNode manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        }
        catch (JsonException ex)
        {
            return Fail([.. problems, $"{ExportShareDelivery.ManifestFile} is not JSON: {ex.Message}"]);
        }

        foreach (var file in manifest["files"]?.AsArray() ?? [])
        {
            var fileName = (string?)file?["name"] ?? "?";
            var path = Path.Combine(runDir, fileName);
            if (!File.Exists(path))
            {
                problems.Add($"{fileName} is listed in the manifest but missing");
                continue;
            }
            using var stream = File.OpenRead(path);
            if (stream.Length != (long?)file?["bytes"])
                problems.Add($"{fileName} has {stream.Length} bytes, the manifest says {file?["bytes"]}");
            else if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), (string?)file?["sha256"], StringComparison.OrdinalIgnoreCase))
                problems.Add($"{fileName} does not match its checksum in the manifest");
        }

        var (customers, customerProblems, meteringPoints) = CheckCustomers(Path.Combine(runDir, ExportShareDelivery.CustomersFile));
        problems.AddRange(customerProblems);
        if (customers != (int?)manifest["customers"])
            problems.Add($"{ExportShareDelivery.CustomersFile} has {customers} customer(s), the manifest says {manifest["customers"]}");

        var deltaPath = Path.Combine(runDir, ExportShareDelivery.DeltaFile);
        var deltaLines = File.Exists(deltaPath) ? File.ReadLines(deltaPath).Count(l => l.Length > 0) : 0;
        var expectedDelta = ((int?)manifest["added"] ?? 0) + ((int?)manifest["changed"] ?? 0) + ((int?)manifest["removed"] ?? 0);
        if (deltaLines != expectedDelta)
            problems.Add($"{ExportShareDelivery.DeltaFile} has {deltaLines} line(s), the manifest says {expectedDelta}");

        if (DateTimeOffset.TryParse((string?)manifest["finishedUtc"], out var finished) && clock() - finished > maxAge)
            problems.Add($"the newest run finished {finished:u}, more than {maxAge.TotalHours:0} hours ago");
        if (File.Exists(Path.Combine(envDir, ExportShareDelivery.LockFile)))
            log($"Note: {ExportShareDelivery.LockFile} exists: a run is going now, or one crashed and the file must be removed");

        log($"  customers:       {customers}");
        log($"  metering points: {meteringPoints}");
        log($"  delta:           {manifest["added"]} added, {manifest["changed"]} changed, {manifest["removed"]} removed (since {manifest["previousRun"]?.ToString() ?? "nothing, first run"})");
        log($"  payment CSV:     {manifest["paymentAgreements"]?["file"]?.ToString() ?? "none"}");
        log($"  trial:           {((bool?)manifest["trial"] == true ? "yes" : "no")}");
        log($"  published runs:  {runs.Count}, failed runs kept: {failedRuns}");

        return problems.Count == 0 ? Ok() : Fail(problems);
    }

    private static (int Customers, List<string> Problems, int MeteringPoints) CheckCustomers(string path)
    {
        var problems = new List<string>();
        if (!File.Exists(path)) return (0, [$"{ExportShareDelivery.CustomersFile} is missing"], 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int count = 0, meteringPoints = 0, lineNumber = 0, withErrors = 0, badIds = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            if (line.Length == 0) continue;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                problems.Add($"line {lineNumber} is not JSON");
                continue;
            }
            count++;
            var number = node?["customerNumber"]?.ToString();
            if (string.IsNullOrEmpty(number)) problems.Add($"line {lineNumber} has no customer number");
            else if (!seen.Add(number)) problems.Add($"customer {number} appears twice");
            if (node?["errors"] is JsonArray { Count: > 0 }) withErrors++;
            foreach (var mp in node?["meteringPoints"] as JsonArray ?? [])
            {
                meteringPoints++;
                // Shaped output carries the identification; raw output carries it in billing.
                var id = (mp?["meteringPointId"] is JsonValue v && !Guid.TryParse(v.ToString(), out _) ? v.ToString() : null)
                         ?? mp?["billing"]?["MeteringPointIdentification"]?.ToString();
                if (id == null || !Identification.IsMatch(id)) badIds++;
            }
        }
        if (withErrors > 0) problems.Add($"{withErrors} customer(s) carry read errors");
        if (badIds > 0) problems.Add($"{badIds} metering point(s) without an 18-digit identification");
        if (count == 0) problems.Add("no customers in the run");
        return (count, problems, meteringPoints);
    }

    private int Ok()
    {
        log("OK: the run is complete and consistent");
        return 0;
    }

    private int Fail(IEnumerable<string> problems)
    {
        foreach (var p in problems) log($"PROBLEM: {p}");
        log("NOT OK: see the problems above");
        return 1;
    }
}
