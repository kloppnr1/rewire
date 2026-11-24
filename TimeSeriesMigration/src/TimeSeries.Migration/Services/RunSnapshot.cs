using System.Text.Json;

namespace TimeSeries.Cli.Services;

/// <summary>
/// What a batch run reads from Settl and Xellent, kept on disk so a later run can reuse it instead of
/// asking again — neither side changes while the migration runs (2026-09). One folder per environment,
/// one JSON-lines file per kind of read; each line is <c>{"key":…,"value":…}</c> and a later line for
/// the same key wins, so a run adds to the snapshot as it goes and an aborted run keeps what it read.
/// Only successful reads are stored. Time series points are not stored: they are read fresh.
/// </summary>
public sealed class RunSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly bool _reuse;
    private readonly bool _fetchMissing;
    private readonly object _gate = new();
    private int _reused;
    private int _fetched;
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _stores = new(StringComparer.Ordinal);

    /// <param name="directory">Folder holding the snapshot files; created when missing.</param>
    /// <param name="reuse">Answer reads from the snapshot when it has them. Without it every read goes to
    /// the source and the snapshot is only written.</param>
    /// <param name="fetchMissing">When false, a cache miss returns an error and never invokes the source read.</param>
    public RunSnapshot(string directory, bool reuse, bool fetchMissing = true)
    {
        DirectoryPath = directory;
        _reuse = reuse;
        _fetchMissing = fetchMissing;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath { get; }

    /// <summary>Whether a cache miss may be read from the source and appended to this snapshot.</summary>
    public bool FetchMissing => _fetchMissing;

    /// <summary>Reads answered from the snapshot.</summary>
    public int Reused => _reused;

    /// <summary>Reads that went to the source (and were stored when they succeeded).</summary>
    public int Fetched => _fetched;

    /// <summary>How many keys the snapshot holds for a kind.</summary>
    public int CountKeys(string kind)
    {
        lock (_gate)
            return Store(kind).Count;
    }

    /// <summary>Whether the snapshot holds this key (regardless of <c>reuse</c>).</summary>
    public bool Contains(string kind, string key)
    {
        lock (_gate)
            return Store(kind).ContainsKey(key);
    }

    /// <summary>Every key the snapshot holds for a kind, with its value (regardless of <c>reuse</c>).</summary>
    public List<(string Key, T Value)> All<T>(string kind)
    {
        List<KeyValuePair<string, JsonElement>> pairs;
        lock (_gate)
            pairs = Store(kind).ToList();
        return pairs.Select(p => (p.Key, p.Value.Deserialize<T>(Json)!)).ToList();
    }

    public bool TryGet<T>(string kind, string key, out T? value)
    {
        value = default;
        JsonElement element;
        lock (_gate)
        {
            if (!_reuse || !Store(kind).TryGetValue(key, out element))
                return false;
        }
        value = element.Deserialize<T>(Json);
        Interlocked.Increment(ref _reused);
        return true;
    }

    public void Put<T>(string kind, string key, T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        var line = JsonSerializer.Serialize(new Entry(key, element), Json) + "\n";
        lock (_gate)
        {
            Store(kind)[key] = element;
            File.AppendAllText(PathOf(kind), line);
        }
    }

    /// <summary>
    /// Drops the stored reads whose key matches, so the next read goes to the source again. The kind's file
    /// is rewritten without them (through a temp file and an atomic move). Returns how many were dropped.
    /// </summary>
    public int Forget(string kind, Func<string, bool> match)
    {
        lock (_gate)
        {
            var store = Store(kind);
            var keys = store.Keys.Where(match).ToList();
            if (keys.Count == 0)
                return 0;
            foreach (var key in keys)
                store.Remove(key);
            var path = PathOf(kind);
            File.WriteAllLines(path + ".tmp",
                store.Select(pair => JsonSerializer.Serialize(new Entry(pair.Key, pair.Value), Json)));
            File.Move(path + ".tmp", path, overwrite: true);
            return keys.Count;
        }
    }

    /// <summary>The snapshot's answer, or the source's — stored when it has no error.</summary>
    public async Task<(T? Value, string? Error)> GetOrFetchAsync<T>(
        string kind, string key, Func<Task<(T? Value, string? Error)>> fetch)
    {
        if (TryGet<T>(kind, key, out var cached))
            return (cached, null);
        if (!_fetchMissing)
            return (default, $"snapshot has no '{kind}' entry for key '{key}'");
        Interlocked.Increment(ref _fetched);
        var (value, error) = await fetch();
        if (error == null)
            Put(kind, key, value);
        return (value, error);
    }

    /// <summary>Counts a read that went to the source outside <see cref="GetOrFetchAsync{T}"/>.</summary>
    public void CountFetched(int reads = 1) => Interlocked.Add(ref _fetched, reads);

    private string PathOf(string kind) => Path.Combine(DirectoryPath, kind + ".jsonl");

    private Dictionary<string, JsonElement> Store(string kind)
    {
        if (_stores.TryGetValue(kind, out var store))
            return store;
        store = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var path = PathOf(kind);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                // A run killed mid-write can leave a torn last line; it is simply not in the snapshot.
                Entry? entry;
                try { entry = JsonSerializer.Deserialize<Entry>(line, Json); }
                catch (JsonException) { continue; }
                if (entry != null)
                    store[entry.Key] = entry.Value.Clone();
            }
        }
        _stores[kind] = store;
        return store;
    }

    private sealed record Entry(string Key, JsonElement Value);
}
