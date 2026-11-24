using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataMigration.OpsCli.Services.Reconcile;

/// <summary>
/// A completed reconcile scan plus the metadata needed to display and age it. This is the cacheable
/// unit: a scan is expensive (env-wide Settl pagination + one Xellent load per account-COS + per-
/// representative diffs), so hosts (the dashboard) persist the last result and serve it on load,
/// re-scanning only on explicit refresh. <see cref="GeneratedAtUtc"/> drives the "last updated"
/// display. Categories are not stored — derive them on read via <see cref="DriftCategorizer"/>.
/// </summary>
public sealed record ReconcileResult(
    string Env,
    string DataAreaId,
    DateTime GeneratedAtUtc,
    IReadOnlyList<ReconcileDriftRow> Rows);

/// <summary>
/// Persists/loads <see cref="ReconcileResult"/> as JSON, one file per env, in a host-chosen
/// directory. Shared by every reconcile host (the OpsCli CLI and the dashboard's scan service) via
/// the NuGet package so the cache format stays identical across producers and consumers.
/// </summary>
public sealed class ReconcileResultStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        // DriftKind as its name (stable, human-readable) rather than an ordinal that silently shifts
        // if the enum is reordered.
        Converters = { new JsonStringEnumConverter() },
        Encoder = JsonOutput.HumanReadable,
    };

    private readonly string _directory;

    public ReconcileResultStore(string directory) => _directory = directory;

    /// <summary>Cache file for an env, e.g. <c>reconcile-cache-production.json</c>.</summary>
    public string PathFor(string env) => Path.Combine(_directory, $"reconcile-cache-{Sanitize(env)}.json");

    /// <summary>Writes the result, overwriting any previous cache for its env. Creates the directory.</summary>
    public async Task SaveAsync(ReconcileResult result, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        // Write to a temp file then move, so a reader never observes a half-written cache.
        var finalPath = PathFor(result.Env);
        var tempPath = finalPath + ".tmp";
        await using (var stream = File.Create(tempPath))
            await JsonSerializer.SerializeAsync(stream, result, Json, ct);
        File.Move(tempPath, finalPath, overwrite: true);
    }

    /// <summary>Reads the cached result for an env, or null when none has been written yet.</summary>
    public async Task<ReconcileResult?> LoadAsync(string env, CancellationToken ct = default)
    {
        var path = PathFor(env);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ReconcileResult>(stream, Json, ct);
    }

    private static string Sanitize(string env) =>
        string.Concat((env ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}
