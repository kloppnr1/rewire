using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

/// <summary>Read-only duplicate report using the cleanup's exact detection rule and
/// the market-list delivery signals already exported by report-delivery-start.</summary>
public sealed class ReportConsumer2DuplicateRunner(ISettlMeteringPointClient client)
{
    public TextWriter ProgressOutput { get; init; } = Console.Error;
    public string? OutputPath { get; init; }
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);
    public async Task<int> RunAsync(string environment, string[] meteringPointIds, int? limit,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        using var progress = new Consumer2ProgressDisplay(ProgressOutput, ProgressInterval);
        progress.Message("Duplicate Consumer 2 report (up to 8 concurrent reads)");
        progress.Message($"Environment: {environment}");
        progress.Message(meteringPointIds.Length == 0
            ? "Scope: all non-child metering points, including inactive"
            : $"Scope: {meteringPointIds.Distinct(StringComparer.Ordinal).Count()} selected metering point IDs");
        if (OutputPath is not null) progress.Message($"Output: {OutputPath}");
        var candidates = new List<(Guid Id, MeteringPointInfoDto? Evidence)>();
        var errors = 0;
        if (meteringPointIds.Length > 0)
        {
            var resolved = 0;
            var requested = meteringPointIds.Distinct(StringComparer.Ordinal).ToArray();
            string ResolveStatus() => $"Resolving metering points: {resolved:N0} / {requested.Length:N0}   Read errors: {errors:N0}";
            progress.Update(ResolveStatus(), force: true);
            foreach (var value in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParse(value, out var id)) candidates.Add((id, null));
                else
                {
                    var (mp, error) = await progress.ReadAsync(
                        () => client.SearchByIdentificationAsync(value, cancellationToken), "metering-point lookup", ResolveStatus, cancellationToken);
                    if (mp is null)
                    {
                        progress.Message($"{value}: lookup failed ({error}); report incomplete.");
                        errors++;
                    }
                    else candidates.Add((mp.Id, mp));
                }
                resolved++;
                progress.Update(ResolveStatus());
            }
            progress.Update(ResolveStatus(), force: true);
        }
        else
        {
            const int pageSize = 500;
            var pages = 0;
            var loaded = 0;
            string LoadStatus() => $"Loading metering points: Pages: {pages:N0}   Loaded: {loaded:N0}   Selected: {candidates.Count:N0}";
            progress.Update(LoadStatus(), force: true);
            for (var skip = 0; ; skip += pageSize)
            {
                var offset = skip;
                var (page, _, error) = await progress.ReadAsync(
                    () => client.FindMeteringPointsAsync(pageSize, offset, false, cancellationToken), "market-list page", LoadStatus, cancellationToken);
                if (error is not null) throw new InvalidOperationException($"Market list failed at {skip}: {error}");
                pages++;
                loaded += page.Count;
                // Include points that have left delivery: their evidence is relevant to review.
                candidates.AddRange(page.Where(mp => mp.ParentMeteringPoint?.Id is null || mp.ParentMeteringPoint.Id == Guid.Empty)
                    .Select(mp => (mp.Id, (MeteringPointInfoDto?)mp)));
                progress.Update(LoadStatus(), force: true);
                if (page.Count < pageSize) break;
            }
        }

        var selected = candidates.DistinctBy(c => c.Id).ToList();
        progress.Message($"Loading complete: {selected.Count:N0} metering points to check.");
        await WriteRow(output, ["MeteringPointIdentification", "SettlId", "Environment", "Consumer1Name",
            "Consumer2Name", "InDelivery", "CurrentOrNextDeliveryStart", "CurrentOrNextDeliveryEnd",
            "CompletedDeliveryStart", "CompletedDeliveryEnd", "LastUpdatedUtc", "ChangeSignal", "UncertaintyReason"]);
        var matched = 0;
        var checkedCount = 0;
        var stoppedAtLimit = false;
        string CheckStatus() => $"Checking consumers: Checked: {Volatile.Read(ref checkedCount):N0} / {selected.Count:N0}   Duplicates: {matched:N0}   Read errors: {errors:N0}";
        progress.Update(CheckStatus(), force: true);
        for (var offset = 0; offset < selected.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit.HasValue && matched >= limit.Value) { stoppedAtLimit = true; break; }
            var size = Math.Min(8, selected.Count - offset);
            if (limit.HasValue) size = Math.Min(size, limit.Value - matched);
            var batch = selected.GetRange(offset, size);
            var results = await progress.ReadAsync(() => Task.WhenAll(batch.Select(async candidate =>
            {
                var (mp, error) = await client.GetByIdV4Async(candidate.Id, null, cancellationToken);
                var evidence = candidate.Evidence;
                string? evidenceError = null;
                if (mp is not null && FixConsumer2DuplicateRunner.FindDuplicateName(mp) is not null
                    && evidence is null && mp.Identification?.MeteringPointIdentification is { Length: > 0 } identification)
                {
                    (evidence, evidenceError) = await client.SearchByIdentificationAsync(identification, cancellationToken);
                    if (evidence?.Id != candidate.Id) { evidence = null; evidenceError = "Market lookup did not return the same Settl ID."; }
                }
                Interlocked.Increment(ref checkedCount);
                return (candidate.Id, Mp: mp, Error: error, Evidence: evidence, EvidenceError: evidenceError);
            })), "consumer read / delivery-evidence lookup (up to 8 concurrent)", CheckStatus, cancellationToken);
            offset += size;
            foreach (var result in results)
            {
                if (result.Mp is not { } mp)
                {
                    errors++;
                    progress.Message($"{result.Id}: consumer read failed ({result.Error}); report incomplete.");
                    continue;
                }
                if (FixConsumer2DuplicateRunner.FindDuplicateName(mp) is null) continue;
                matched++;
                var evidence = result.Evidence;
                var (signal, reason) = Assess(evidence, result.EvidenceError);
                await WriteRow(output, [mp.Identification?.MeteringPointIdentification ?? "", result.Id.ToString(), environment,
                    Name(mp.Consumer!.FirstConsumerParty!.Name!), Name(mp.Consumer.SecondConsumerParty!.Name!),
                    evidence?.InDelivery?.ToString() ?? "", Date(evidence?.CurrentOrNextDeliveryPeriod?.Start),
                    Date(evidence?.CurrentOrNextDeliveryPeriod?.End), Date(evidence?.MostRecentCompletedDeliveryPeriod?.Start),
                    Date(evidence?.MostRecentCompletedDeliveryPeriod?.End), Date(evidence?.LastUpdated), signal, reason]);
            }
            progress.Update(CheckStatus());
        }
        progress.Update(CheckStatus(), force: true);
        if (stoppedAtLimit) progress.Message($"Stopped at --limit {limit}: remaining metering points were not checked.");
        progress.Message(errors > 0 ? "Report incomplete: some metering points could not be read." : "Complete");
        progress.Message($"Checked: {checkedCount:N0}; duplicates: {matched:N0}; read errors: {errors:N0}.");
        if (OutputPath is not null) progress.Message($"Report: {OutputPath}");
        progress.Message("No Settl writes.");
        return errors > 0 ? 1 : 0;
    }

    internal static (string Signal, string Reason) Assess(MeteringPointInfoDto? evidence, string? error = null)
    {
        const string timing = "These fields do not establish whether changes happened after migration. Delivery dates are effective dates; LastUpdated describes the whole metering point and does not identify the changed field.";
        if (evidence?.InDelivery is null)
            return ("Unable to determine", $"Delivery evidence missing{(error is null ? "." : $": {error}")} {timing}");
        if (evidence.InDelivery == false || evidence.MostRecentCompletedDeliveryPeriod is not null)
            return ("Possible delivery change", timing);
        return ("No later-delivery signal in these fields", $"Absence of a signal does not prove unchanged Settl data. {timing}");
    }

    private static string Name(MeteringPointV4NameDto name) => name.FullName ?? $"{name.FirstName} {name.LastName}".Trim();
    private static string Date(DateTimeOffset? date) => date is null || date == default(DateTimeOffset) ? "" : date.Value.ToUniversalTime().ToString("O");
    private static Task WriteRow(TextWriter output, string[] cells) => output.WriteLineAsync(
        string.Join(';', cells.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
}
