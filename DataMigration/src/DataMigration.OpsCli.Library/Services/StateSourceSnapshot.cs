using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataMigration.Domain.Customers;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

internal sealed record StatePayerLookup(DateOnly Date, Guid[] MeteringPointIds,
    List<PayerForMeteringPointsDto>? Items, string? Error);

internal sealed record StateMpAccount(string MeteringPoint, string Account);

internal sealed record StateXellentEvidence(string MeteringPoint, string SourceAccount,
    string Representative, DateOnly Cos, string[] RepresentativeMembers);

internal sealed record ContractSourceSnapshot(
    List<XellentContractPartRow> LiveParts,
    List<XellentContractPartRow> LoggedParts,
    List<SettlContractLinkRow> SettlLinks)
{
    public List<XellentChargeSubscriptionRow> ChargeSubscriptions { get; init; } = [];
    public List<XellentChargerAssignmentRow> ChargerAssignments { get; init; } = [];
    public bool HasChargerAssignments { get; init; }
}

internal sealed record XellentChargerAssignmentRow(string? MeteringPoint,
    DataMigration.Domain.Customers.EvCharger Charger, DateOnly ReferenceDate)
{
    // Null identifies snapshots made before address evidence was captured.
    public XellentChargerAddressEvidence? AddressEvidence { get; init; }
}

internal sealed record XellentChargerAddressEvidence(string? InstallationDarId,
    string? InstallationAddress, string? InstallationPostalCode,
    string? InstallationRecord, List<XellentMpAddressCandidate> MeteringPointCandidates);

internal sealed record XellentMpAddressCandidate(string AgreementNumber, string DeliveryPointNumber,
    string? Street, int? HouseNumber, string? HouseLetter, string? PostalCode,
    string? DarId, string SourceRecord);

/// <summary>Source lookup results, including candidates whose Settl owner is unresolved.</summary>
internal sealed class StateSourceSnapshot
{
    public bool IncludesUnknownDeliveryStarts { get; set; }
    public List<CustomerInfoDto> Customers { get; set; } = [];
    public List<SettlMpWithCos> MeteringPoints { get; set; } = [];
    public List<GenerateStateOfflineCache.DeliveryHistoryRecord> DeliveryHistories { get; set; } = [];
    public List<StatePayerLookup> PayerLookups { get; set; } = [];
    public List<StateMpAccount> XellentMpAccounts { get; set; } = [];
    public List<string> ForXellentMeteringPoints { get; set; } = [];
    public List<string> MehXellentMeteringPoints { get; set; } = [];
    public List<StateXellentEvidence> XellentEvidence { get; set; } = [];
    public List<Invoice> Invoices { get; set; } = [];
    public List<ExcludedCompanyInvoice> ExcludedInvoices { get; set; } = [];
}

/// <summary>Batch-friendly JSON Lines store; a section is committed only after its file is complete.</summary>
internal sealed class StateSourceSnapshotStore(string directory, string environment, StatePullScope? scope = null)
{
    private const int Version = 1;
    private sealed record Section(string File, int Count, string Sha256);
    private sealed record Manifest(int Version, string Environment, DateTimeOffset StartedAt,
        bool Complete, Dictionary<string, Section> Sections)
    {
        public bool IncludesUnknownDeliveryStarts { get; init; }
        public StatePullScope? Scope { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
    }

    private Manifest _manifest = new(Version, environment, DateTimeOffset.UtcNow, false, []) { Scope = scope };

    public static string DefaultDirectory(string env) => Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data")),
        "generate-state-from-settl-offline", env);

    public static StateSourceSnapshotStore Open(string? directory, string env, StatePullScope? scope = null) =>
        new(directory ?? DefaultDirectory(env), env, scope);

    public string DirectoryPath => directory;
    internal string Environment => environment;

    public async Task BeginAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        await WriteManifestAsync(ct);
    }

    public async Task BeginUpdateAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path))
        {
            if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                throw new IOException($"Contract output directory contains files but no manifest: {directory}");
            await BeginAsync(ct);
            return;
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct));
        if (manifest is null || manifest.Version != Version || manifest.Environment != environment)
            throw new InvalidOperationException("Offline source data version or environment does not match.");
        if ((scope is null) != (manifest.Scope is null) || scope is not null && !scope.SameResolution(manifest.Scope!))
            throw new InvalidOperationException("Snapshot scope differs; use a new output directory.");
        _manifest = manifest with { Complete = false, UpdatedAt = DateTimeOffset.UtcNow };
        await WriteManifestAsync(ct);
    }

    public async Task WriteAsync<T>(string name, IReadOnlyCollection<T> rows, CancellationToken ct)
    {
        var file = name + ".jsonl";
        var path = Path.Combine(directory, file);
        var pending = path + ".new";
        await using (var stream = File.Create(pending))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(row));
            }
        }
        File.Move(pending, path, overwrite: true);
        await using var input = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct));
        _manifest.Sections[name] = new Section(file, rows.Count, hash);
        await WriteManifestAsync(ct);
    }

    public async Task CompleteAsync(CancellationToken ct, bool includesUnknownDeliveryStarts = false)
    {
        _manifest = _manifest with
        {
            Complete = true,
            IncludesUnknownDeliveryStarts = includesUnknownDeliveryStarts || _manifest.IncludesUnknownDeliveryStarts
        };
        await WriteManifestAsync(ct);
    }

    public async Task<StateSourceSnapshot> ReadAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) throw new InvalidOperationException($"No offline source data at {directory}.");
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct));
        if (manifest is null || manifest.Version != Version || manifest.Environment != environment)
            throw new InvalidOperationException("Offline source data version or environment does not match.");
        if (!manifest.Complete)
            throw new InvalidOperationException($"Offline source data is incomplete at {directory}; run pull-state-sources again.");
        _manifest = manifest;
        return new StateSourceSnapshot
        {
            IncludesUnknownDeliveryStarts = manifest.IncludesUnknownDeliveryStarts,
            Customers = await ReadSectionAsync<CustomerInfoDto>("settl-customers", ct),
            MeteringPoints = await ReadSectionAsync<SettlMpWithCos>("settl-metering-points", ct),
            DeliveryHistories = await ReadSectionAsync<GenerateStateOfflineCache.DeliveryHistoryRecord>("settl-delivery-histories", ct),
            PayerLookups = await ReadSectionAsync<StatePayerLookup>("settl-payers", ct),
            XellentMpAccounts = await ReadSectionAsync<StateMpAccount>("xellent-mp-accounts", ct),
            ForXellentMeteringPoints = await ReadSectionAsync<string>("xellent-for-mps", ct),
            MehXellentMeteringPoints = await ReadSectionAsync<string>("xellent-meh-mps", ct),
            XellentEvidence = await ReadSectionAsync<StateXellentEvidence>("xellent-representatives", ct),
            Invoices = await ReadSectionAsync<Invoice>("xellent-invoices", ct),
            ExcludedInvoices = await ReadSectionAsync<ExcludedCompanyInvoice>("xellent-excluded-invoices", ct)
        };
    }

    public async Task<ContractSourceSnapshot> ReadContractSourcesAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, "manifest.json");
        if (!File.Exists(path)) throw new InvalidOperationException($"No offline source data at {directory}.");
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct));
        if (manifest is null || manifest.Version != Version || manifest.Environment != environment || !manifest.Complete)
            throw new InvalidOperationException("Offline source data is incomplete or its environment does not match.");
        _manifest = manifest;
        return new ContractSourceSnapshot(
            await ReadSectionAsync<XellentContractPartRow>("xellent-contract-parts", ct),
            await ReadSectionAsync<XellentContractPartRow>("xellent-contract-part-logs", ct),
            await ReadSectionAsync<SettlContractLinkRow>("settl-contract-links", ct))
        {
            ChargeSubscriptions = await ReadSectionAsync<XellentChargeSubscriptionRow>(
                "xellent-charge-subscriptions", ct),
            HasChargerAssignments = _manifest.Sections.ContainsKey("xellent-charger-assignments"),
            ChargerAssignments = _manifest.Sections.ContainsKey("xellent-charger-assignments")
                ? await ReadSectionAsync<XellentChargerAssignmentRow>("xellent-charger-assignments", ct)
                : []
        };
    }

    internal async Task<List<T>> ReadSectionAsync<T>(string name, CancellationToken ct)
    {
        if (!_manifest.Sections.TryGetValue(name, out var section))
            throw new InvalidOperationException($"Offline source data is missing {name}.");
        var path = Path.Combine(directory, section.File);
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (hash != section.Sha256)
            throw new InvalidOperationException($"Offline source data checksum failed: {name}.");
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var rows = new List<T>();
        while (await reader.ReadLineAsync(ct) is { } line)
            rows.Add(JsonSerializer.Deserialize<T>(line)
                ?? throw new InvalidOperationException($"Invalid offline {name} record."));
        if (rows.Count != section.Count)
            throw new InvalidOperationException($"Offline source data count failed: {name}.");
        return rows;
    }

    internal async Task LoadManifestAsync(CancellationToken ct, bool requireComplete = true)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct));
        if (manifest is null || manifest.Version != Version || manifest.Environment != environment)
            throw new InvalidOperationException("Offline source version or environment does not match.");
        if (requireComplete && !manifest.Complete) throw new InvalidOperationException("Offline snapshot is incomplete.");
        _manifest = manifest;
    }

    internal StatePullScope? Scope => _manifest.Scope;
    internal DateTimeOffset StartedAt => _manifest.StartedAt;
    internal async Task InvalidateRepairAsync(bool xellent, bool settl, CancellationToken ct)
    {
        if (xellent) _manifest.Sections.Remove("repair-xellent");
        if (xellent)
            foreach (var name in _manifest.Sections.Keys.Where(n => n.StartsWith("repair-raw-", StringComparison.Ordinal)).ToArray())
                _manifest.Sections.Remove(name);
        if (settl) _manifest.Sections.Remove("repair-settl");
        _manifest = _manifest with { Complete = false };
        await WriteManifestAsync(ct);
    }
    internal bool HasSection(string name) => _manifest.Sections.ContainsKey(name);
    internal async Task MarkIncompleteAsync(CancellationToken ct)
    {
        await LoadManifestAsync(ct, false);
        _manifest = _manifest with { Complete = false };
        await WriteManifestAsync(ct);
    }

    private async Task WriteManifestAsync(CancellationToken ct)
    {
        var path = Path.Combine(directory, "manifest.json");
        var pending = path + ".new";
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(_manifest), ct);
        File.Move(pending, path, overwrite: true);
    }
}
