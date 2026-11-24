using System.Security.Cryptography;
using System.Text.Json;
using DataMigration.OpsCli.Runners;
using DataMigration.SettlApi.Models;
using DataMigration.Domain.Customers;

namespace DataMigration.OpsCli.Services;

/// <summary>Local external-input snapshot for repeatable offline state generation.</summary>
public static class GenerateStateOfflineCache
{
    private const int CaptureVersion = 3;
    public sealed record DeliveryHistoryRecord(string MeteringPoint, Guid Id,
        bool? ListedInDelivery, TimePeriodDto? CurrentOrNextDeliveryPeriod,
        TimePeriodDto? MostRecentCompletedDeliveryPeriod, GetDeliveryStatusesResponse? Statuses,
        string? Error, DateOnly? FirstDate, string Disposition);

    internal sealed record OwnershipEvidence(string MeteringPoint, string SettlCustomerNumber,
        string SourceAccount, string Representative, DateOnly Cos,
        string[] RepresentativeMembers);

    internal sealed record RunInputs(string StateSeedJson,
        IReadOnlyDictionary<string, DateOnly> CosByCustomer,
        IReadOnlyList<OwnershipEvidence> Ownership,
        IReadOnlyList<string> QueriedSourceAccounts,
        IReadOnlyList<Invoice> Invoices,
        IReadOnlyList<ExcludedCompanyInvoice> ExcludedInvoices,
        IReadOnlyList<DeliveryStartAudit>? DeliveryAudit = null,
        IReadOnlyList<SourceAccountOwnershipIssue>? InitialOwnershipIssues = null,
        IReadOnlyList<string>? ExcludedSourceAccounts = null);

    private sealed record Manifest(int Version, DateTimeOffset CapturedAt, string Environment,
        DateTime? ReferenceDate, bool HasInvoiceRows,
        string DeliveryHistorySha256, int DeliveryHistoryCount, string InputsSha256);

    private sealed record IncompleteManifest(int Version, DateTimeOffset CapturedAt,
        string Environment, DateTime? ReferenceDate, string Status,
        string? DeliveryHistorySha256, int DeliveryHistoryCount);

    internal static string DirectoryFor(GenerateStateFromSettlRunOptions options) =>
        options.OfflineCacheDirectory ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!,
            "generate-state-from-settl-offline", options.Env ?? "unknown");

    /// <summary>Persists complete external inputs as soon as they are fetched, before output validation.</summary>
    internal static async Task CaptureDataAsync(GenerateStateFromSettlRunOptions options,
        IReadOnlyList<DeliveryHistoryRecord> deliveryHistories, RunInputs inputs,
        CancellationToken ct)
    {
        var directory = DirectoryFor(options);
        Directory.CreateDirectory(directory);
        var deliveryPath = Path.Combine(directory, "delivery-histories.json");
        var tempDelivery = deliveryPath + ".new";
        await using (var output = File.Create(tempDelivery))
            await JsonSerializer.SerializeAsync(output, deliveryHistories, cancellationToken: ct);
        File.Move(tempDelivery, deliveryPath, overwrite: true);
        await using var deliveryStream = File.OpenRead(deliveryPath);
        var deliveryHash = Convert.ToHexString(await SHA256.HashDataAsync(deliveryStream, ct));

        var inputsPath = Path.Combine(directory, "inputs.json");
        var tempInputs = inputsPath + ".new";
        await using (var output = File.Create(tempInputs))
            await JsonSerializer.SerializeAsync(output, inputs, cancellationToken: ct);
        File.Move(tempInputs, inputsPath, overwrite: true);
        await using var inputsStream = File.OpenRead(inputsPath);
        var inputsHash = Convert.ToHexString(await SHA256.HashDataAsync(inputsStream, ct));

        var manifest = new Manifest(CaptureVersion, DateTimeOffset.UtcNow,
            options.Env ?? "unknown", options.ReferenceDate,
            !options.NoInvoices, deliveryHash, deliveryHistories.Count, inputsHash);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var tempManifest = manifestPath + ".new";
        await File.WriteAllTextAsync(tempManifest, JsonSerializer.Serialize(manifest), ct);
        File.Move(tempManifest, manifestPath, overwrite: true);
        Console.Error.WriteLine($"  Offline data: {directory} " +
            $"({deliveryHistories.Count} delivery histories, {inputs.Invoices.Count} invoice rows)");
    }

    /// <summary>Preserves fetched evidence when external inputs are still incomplete.</summary>
    internal static async Task CaptureFailureAsync(GenerateStateFromSettlRunOptions options,
        IReadOnlyList<DeliveryHistoryRecord>? deliveryHistories,
        CancellationToken ct)
    {
        var directory = Path.Combine(DirectoryFor(options), "incomplete");
        Directory.CreateDirectory(directory);
        string? deliveryHash = null;
        var deliveryPath = Path.Combine(directory, "delivery-histories.json");
        if (deliveryHistories is not null)
        {
            var tempDelivery = deliveryPath + ".new";
            await using (var output = File.Create(tempDelivery))
                await JsonSerializer.SerializeAsync(output, deliveryHistories, cancellationToken: ct);
            File.Move(tempDelivery, deliveryPath, overwrite: true);
            await using var deliveryStream = File.OpenRead(deliveryPath);
            deliveryHash = Convert.ToHexString(await SHA256.HashDataAsync(deliveryStream, ct));
        }
        else if (File.Exists(deliveryPath)) File.Delete(deliveryPath);

        var manifest = new IncompleteManifest(CaptureVersion, DateTimeOffset.UtcNow,
            options.Env ?? "unknown", options.ReferenceDate, "Incomplete",
            deliveryHash, deliveryHistories?.Count ?? 0);
        var manifestPath = Path.Combine(directory, "manifest.json");
        var tempManifest = manifestPath + ".new";
        await File.WriteAllTextAsync(tempManifest, JsonSerializer.Serialize(manifest), ct);
        File.Move(tempManifest, manifestPath, overwrite: true);
        Console.Error.WriteLine($"  Incomplete offline evidence: {directory} " +
            $"({deliveryHistories?.Count ?? 0} metering-point delivery histories; not replayable)");
    }

    public static async Task<int> ReplayAsync(GenerateStateFromSettlRunOptions options,
        CancellationToken ct = default)
    {
        var directory = DirectoryFor(options);
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            var incompletePath = Path.Combine(directory, "incomplete", "manifest.json");
            Console.Error.WriteLine(File.Exists(incompletePath)
                ? $"ERROR: only incomplete offline evidence exists at {Path.GetDirectoryName(incompletePath)}. " +
                  "The run stopped before all ownership and invoice inputs were captured; complete a live run first."
                : $"ERROR: no offline capture for {options.Env} at {directory}. Run generate-state-from-settl live once.");
            return 1;
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(manifestPath, ct));
        if (manifest is null || manifest.Version != CaptureVersion || manifest.Environment != (options.Env ?? "unknown")
            || options.ReferenceDate is { } requested && requested != manifest.ReferenceDate)
        {
            Console.Error.WriteLine("ERROR: offline capture version, environment, or reference date does not match. Run live once.");
            return 1;
        }
        if (!manifest.HasInvoiceRows && !options.NoInvoices)
        {
            Console.Error.WriteLine("ERROR: these offline inputs were collected with --no-invoices. Use --no-invoices or collect invoice rows live.");
            return 1;
        }
        var deliveryPath = Path.Combine(directory, "delivery-histories.json");
        if (!File.Exists(deliveryPath))
        {
            Console.Error.WriteLine("ERROR: offline data is missing delivery histories. Collect data live.");
            return 1;
        }
        await using (var deliveryStream = File.OpenRead(deliveryPath))
        {
            var deliveryHash = Convert.ToHexString(await SHA256.HashDataAsync(deliveryStream, ct));
            if (deliveryHash != manifest.DeliveryHistorySha256)
            {
                Console.Error.WriteLine("ERROR: offline delivery history checksum failed. Collect data live.");
                return 1;
            }
            deliveryStream.Position = 0;
            var histories = await JsonSerializer.DeserializeAsync<DeliveryHistoryRecord[]>(deliveryStream,
                cancellationToken: ct);
            if (histories is null || histories.Length != manifest.DeliveryHistoryCount)
            {
                Console.Error.WriteLine("ERROR: offline delivery history count does not match. Collect data live.");
                return 1;
            }
            foreach (var history in histories)
            {
                var (date, disposition) = history.Error is not null || history.Statuses is null
                    ? ((DateOnly?)null, "DeliveryStatusLookupFailed")
                    : MoveInDeliveryFilter.FirstDelivery(history.Statuses.DeliveryStatuses);
                if (date != history.FirstDate || disposition != history.Disposition)
                {
                    Console.Error.WriteLine($"ERROR: first delivery decision changed for {history.MeteringPoint}. " +
                        "Collect the affected source data live.");
                    return 1;
                }
            }
        }
        var target = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var inputsPath = Path.Combine(directory, "inputs.json");
        if (!File.Exists(inputsPath))
        {
            Console.Error.WriteLine("ERROR: offline data is missing ownership and invoice inputs. Collect data live.");
            return 1;
        }
        await using (var inputsStream = File.OpenRead(inputsPath))
        {
            var actualInputsHash = Convert.ToHexString(await SHA256.HashDataAsync(inputsStream, ct));
            if (actualInputsHash != manifest.InputsSha256)
            {
                Console.Error.WriteLine("ERROR: offline ownership and invoice input checksum failed. Collect data live.");
                return 1;
            }
            inputsStream.Position = 0;
            var inputs = await JsonSerializer.DeserializeAsync<RunInputs>(inputsStream,
                cancellationToken: ct);
            if (inputs is null)
            {
                Console.Error.WriteLine("ERROR: offline ownership and invoice inputs are invalid. Collect data live.");
                return 1;
            }
            var replayOptions = new GenerateStateFromSettlRunOptions
            {
                OutputPath = target, Env = manifest.Environment,
                ReferenceDate = manifest.ReferenceDate, HistoryYears = options.HistoryYears,
                IncludeActive = options.IncludeActive, SkipActive = options.SkipActive,
                NoInvoices = options.NoInvoices,
                SkipUnresolvedSourceAccounts = options.SkipUnresolvedSourceAccounts
            };
            var runner = new GenerateStateFromSettlRunner(null!);
            if (await runner.ReplayFromInputsAsync(inputs, replayOptions, ct) != 0) return 1;
        }
        Console.WriteLine($"Offline data collected {manifest.CapturedAt:u} ({manifest.Environment}, " +
            $"reference-date={manifest.ReferenceDate:yyyy-MM-dd}, " +
            $"include-active={options.IncludeActive}, skip-active={options.SkipActive}, " +
            $"no-invoices={options.NoInvoices}, skip-unresolved-source-accounts={options.SkipUnresolvedSourceAccounts}, " +
            $"delivery-histories={manifest.DeliveryHistoryCount}) " +
            $"written to {options.OutputPath}");
        return 0;
    }
}
