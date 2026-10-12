using System.Text.Json;

namespace DataMigration.Migration.Configuration;

/// <summary>
/// Loads the per-dataarea <see cref="ReconcileMappingOptions"/> for env-wide reconcile. Default
/// layout is one file per dataarea under a <c>reconcile</c> directory (e.g.
/// <c>profiles/reconcile/hol.json</c>); an explicit path can be passed to override.
/// </summary>
public class ReconcileMappingLoader
{
    private readonly string _mappingDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public ReconcileMappingLoader(string mappingDirectory)
    {
        _mappingDirectory = mappingDirectory;
    }

    /// <summary>Load the mapping for a dataarea from <c>{mappingDirectory}/{dataAreaId}.json</c>.</summary>
    public ReconcileMappingOptions Load(string dataAreaId)
        => LoadFile(Path.Combine(_mappingDirectory, $"{dataAreaId}.json"));

    /// <summary>Load the mapping from an explicit file path.</summary>
    public ReconcileMappingOptions LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Reconcile mapping not found (looked in {path})");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ReconcileMappingOptions>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize reconcile mapping: {path}");
    }
}
