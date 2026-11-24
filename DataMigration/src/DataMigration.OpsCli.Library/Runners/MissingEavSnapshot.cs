using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

/// <summary>
/// Everything export-missing-eav-excel read from Settl in one run, so the run can be repeated without reading
/// Settl again (<c>--offline</c>). Xellent is not in it: an offline run still reads Xellent live, so a changed
/// migration rule is applied to the same Settl data. Ids, dates and meter settings only.
/// </summary>
public sealed class MissingEavSnapshotData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset TakenUtc { get; set; }
    public string Environment { get; set; } = string.Empty;

    /// <summary>Settl's whole metering point list, when the run read it (no <c>--metering-points</c>).</summary>
    public List<MissingEavCandidate>? List { get; set; }

    /// <summary>Metering points looked up by identification; null value = Settl has none.</summary>
    public Dictionary<string, MissingEavCandidate?> ByIdentification { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<Guid, List<DeliveryStatusDto>> DeliveryStatuses { get; set; } = [];
    public Dictionary<Guid, List<DateTime>> VersionStarts { get; set; } = [];

    /// <summary>Meter versions by <see cref="VersionKey"/>.</summary>
    public Dictionary<string, MeterVersion> Versions { get; set; } = new(StringComparer.Ordinal);

    public static string VersionKey(Guid id, DateTime at) =>
        $"{id:D}|{DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public async Task SaveAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var stream = File.Create(path + ".tmp");
        await JsonSerializer.SerializeAsync(stream, this, Options, cancellationToken);
        await stream.DisposeAsync();
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static async Task<MissingEavSnapshotData> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var data = await JsonSerializer.DeserializeAsync<MissingEavSnapshotData>(stream, Options, cancellationToken)
                   ?? throw new InvalidDataException($"{path} is empty");
        if (data.Version != CurrentVersion)
            throw new InvalidDataException($"{path} is snapshot version {data.Version}; this OpsCli reads version {CurrentVersion}. Take a new one.");
        return data;
    }
}

/// <summary>Passes every call to the live reader and keeps the answers for <see cref="MissingEavSnapshotData"/>.</summary>
public sealed class RecordingMissingEavSettlReader(IMissingEavSettlReader live) : IMissingEavSettlReader
{
    private List<MissingEavCandidate>? _list;
    private readonly ConcurrentDictionary<string, MissingEavCandidate?> _byIdentification = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, List<DeliveryStatusDto>> _statuses = new();
    private readonly ConcurrentDictionary<Guid, List<DateTime>> _starts = new();
    private readonly ConcurrentDictionary<string, MeterVersion> _versions = new(StringComparer.Ordinal);

    public async Task<(List<MissingEavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken)
    {
        var result = await live.ListAsync(cancellationToken);
        if (result.Error == null && result.Items != null) _list = [.. result.Items];
        return result;
    }

    public async Task<(MissingEavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken)
    {
        var result = await live.GetAsync(identification, cancellationToken);
        if (result.Error == null) _byIdentification[identification] = result.Item;
        return result;
    }

    public async Task<(List<DateTime>? Starts, string? Error)> VersionStartsAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await live.VersionStartsAsync(id, cancellationToken);
        if (result.Error == null && result.Starts != null) _starts[id] = [.. result.Starts];
        return result;
    }

    public async Task<(MeterVersion? Version, string? Error)> VersionAtAsync(Guid id, DateTime at, CancellationToken cancellationToken)
    {
        var result = await live.VersionAtAsync(id, at, cancellationToken);
        if (result.Error == null && result.Version != null) _versions[MissingEavSnapshotData.VersionKey(id, at)] = result.Version;
        return result;
    }

    public async Task<(IReadOnlyList<DeliveryStatusDto>? Statuses, string? Error)> DeliveryStatusesAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await live.DeliveryStatusesAsync(id, cancellationToken);
        if (result.Error == null && result.Statuses != null) _statuses[id] = [.. result.Statuses];
        return result;
    }

    public MissingEavSnapshotData Snapshot(DateTimeOffset takenUtc, string environment) => new()
    {
        TakenUtc = takenUtc,
        Environment = environment,
        List = _list,
        ByIdentification = new Dictionary<string, MissingEavCandidate?>(_byIdentification, StringComparer.Ordinal),
        DeliveryStatuses = new Dictionary<Guid, List<DeliveryStatusDto>>(_statuses),
        VersionStarts = new Dictionary<Guid, List<DateTime>>(_starts),
        Versions = new Dictionary<string, MeterVersion>(_versions, StringComparer.Ordinal),
    };
}

/// <summary>
/// Answers from a <see cref="MissingEavSnapshotData"/> instead of Settl. Anything the snapshot does not hold is an
/// error, so an offline run never quietly treats missing data as "Settl has nothing".
/// </summary>
public sealed class SnapshotMissingEavSettlReader(MissingEavSnapshotData snapshot) : IMissingEavSettlReader
{
    private const string Hint = "not in the snapshot; take a new one with a live run";

    public Task<(List<MissingEavCandidate>? Items, string? Error)> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<(List<MissingEavCandidate>?, string?)>(snapshot.List != null
            ? ([.. snapshot.List], null)
            : (null, $"the whole metering point list is {Hint} (it was taken with --metering-points)"));

    public Task<(MissingEavCandidate? Item, string? Error)> GetAsync(string identification, CancellationToken cancellationToken)
    {
        if (snapshot.ByIdentification.TryGetValue(identification, out var item))
            return Task.FromResult<(MissingEavCandidate?, string?)>((item, null));
        var fromList = snapshot.List?.FirstOrDefault(c => c.Identification == identification);
        return Task.FromResult<(MissingEavCandidate?, string?)>(snapshot.List != null
            ? (fromList, null)
            : (null, $"metering point {identification} is {Hint}"));
    }

    public Task<(List<DateTime>? Starts, string? Error)> VersionStartsAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<(List<DateTime>?, string?)>(snapshot.VersionStarts.TryGetValue(id, out var starts)
            ? ([.. starts], null)
            : (null, $"meter history of {id} is {Hint}"));

    public Task<(MeterVersion? Version, string? Error)> VersionAtAsync(Guid id, DateTime at, CancellationToken cancellationToken) =>
        Task.FromResult<(MeterVersion?, string?)>(snapshot.Versions.TryGetValue(MissingEavSnapshotData.VersionKey(id, at), out var version)
            ? (version, null)
            : (null, $"meter entry of {id} at {at:u} is {Hint}"));

    public Task<(IReadOnlyList<DeliveryStatusDto>? Statuses, string? Error)> DeliveryStatusesAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<(IReadOnlyList<DeliveryStatusDto>?, string?)>(snapshot.DeliveryStatuses.TryGetValue(id, out var statuses)
            ? (statuses, null)
            : (null, $"delivery statuses of {id} are {Hint}"));
}

/// <summary>
/// export-missing-eav-excel with a Settl snapshot. A live run reads Settl and saves what it read as
/// <c>&lt;workbook&gt;-settl-snapshot.json</c> next to the workbook. An offline run (<c>--offline &lt;snapshot&gt;</c>)
/// reads Settl from that file instead and Xellent live. An offline run refuses a snapshot from another environment
/// or older than <c>maxAge</c>: an Update row replaces the meter version in Settl, so a workbook for import must not
/// come from old Settl data.
/// </summary>
public static class ExportMissingEavWithSnapshot
{
    public static string SnapshotPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? "",
            $"{Path.GetFileNameWithoutExtension(outputPath)}-settl-snapshot.json");

    public static async Task<int> RunAsync(ExportMissingEavExcelRunOptions options, IMissingEavSettlReader live,
        IMigrationOriginReader origin, Action<string> log, Func<DateTimeOffset> clock, string environment,
        string? offlineSnapshot, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        if (offlineSnapshot != null)
        {
            MissingEavSnapshotData snapshot;
            try
            {
                snapshot = await MissingEavSnapshotData.LoadAsync(offlineSnapshot, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                log($"ERROR: cannot read snapshot {offlineSnapshot}: {ex.Message}");
                return 1;
            }
            if (!string.Equals(snapshot.Environment, environment, StringComparison.OrdinalIgnoreCase))
            {
                log($"ERROR: the snapshot is from {snapshot.Environment}, this run is --env {environment}");
                return 1;
            }
            var age = clock() - snapshot.TakenUtc;
            if (age > maxAge)
            {
                log($"ERROR: the snapshot was taken {snapshot.TakenUtc:u}, {age.TotalHours:0} hours ago (limit {maxAge.TotalHours:0}). " +
                    "Run without --offline for a fresh one, or raise --snapshot-max-age-hours for analysis only.");
                return 1;
            }
            log($"Settl from snapshot {Path.GetFullPath(offlineSnapshot)}, taken {snapshot.TakenUtc:u} ({age.TotalHours:0.0} hours ago); Xellent read live");
            return await new ExportMissingEavExcelRunner(new SnapshotMissingEavSettlReader(snapshot), origin, log)
                .RunAsync(options, cancellationToken);
        }

        var takenUtc = clock();
        var recorder = new RecordingMissingEavSettlReader(live);
        var code = await new ExportMissingEavExcelRunner(recorder, origin, log).RunAsync(options, cancellationToken);
        if (code != 0) return code;
        var path = SnapshotPath(options.OutputPath);
        await recorder.Snapshot(takenUtc, environment).SaveAsync(path, cancellationToken);
        log($"Settl snapshot (for --offline): {path}");
        return 0;
    }
}
