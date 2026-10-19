using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

public class FixConsumer2DuplicateRunOptions
{
    /// <summary>
    /// Explicit metering point identification(s) or Settl GUID(s) to scope the run to.
    /// Empty means a full env-wide scan via <see cref="SettlMeteringPointScanner"/>.
    /// </summary>
    public string[] MeteringPointIds { get; set; } = [];

    /// <summary>Cap on how many matching MPs get fixed in one run. Applied after filtering to
    /// the report delivery-period rules, regardless of whether the candidate set came from
    /// <see cref="MeteringPointIds"/> or a full scan.</summary>
    public int? Limit { get; set; }

    public bool DryRun { get; set; }
}

/// <summary>Uses the report decision for each delivery period; writes each selected Consumer version once.</summary>
public class FixConsumer2DuplicateRunner
{
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly Action<string> _log;
    private readonly IConsumerVersionCleanup? _cleanup;
    public TextWriter ProgressOutput { get; init; } = Console.Error;
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    public FixConsumer2DuplicateRunner(
        ISettlMeteringPointClient meteringPointClient,
        Action<string>? log = null, IConsumerVersionCleanup? cleanup = null)
    {
        _meteringPointClient = meteringPointClient;
        _log = log ?? Console.WriteLine;
        _cleanup = cleanup ?? meteringPointClient as IConsumerVersionCleanup;
    }

    public async Task<int> RunAsync(FixConsumer2DuplicateRunOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Limit is <= 0) throw new ArgumentOutOfRangeException(nameof(options.Limit));
        if (!options.DryRun && _cleanup is null) throw new InvalidOperationException("Dated Consumer cleanup client is not configured.");
        using var progress = new Consumer2ProgressDisplay(ProgressOutput, ProgressInterval);
        void Log(string message) { progress.Dispose(); _log(message); }
        var errors = 0;
        Log(options.DryRun ? "Duplicate Consumer 2 dry-run (up to 8 concurrent reads; no writes)"
            : "Duplicate Consumer 2 cleanup (sequential updates)");
        List<Guid> candidateIds;

        if (options.MeteringPointIds.Length > 0)
        {
            Log($"=== Metering point filter: {options.MeteringPointIds.Length} ===");
            candidateIds = [];
            foreach (var raw in options.MeteringPointIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParse(raw, out var guid))
                {
                    candidateIds.Add(guid);
                    continue;
                }

                var (mp, err) = await progress.ReadAsync(
                    () => _meteringPointClient.SearchByIdentificationAsync(raw, cancellationToken),
                    "metering-point lookup", () => "Resolving selected metering points...", cancellationToken);
                if (mp == null)
                {
                    errors++;
                    Log($"  {raw}: not found in Settl ({err}) — skipping");
                    continue;
                }
                candidateIds.Add(mp.Id);
            }
        }
        else
        {
            Log("Loading metering points (full scan, all delivery states)...");
            var loading = "Loading metering points...";
            progress.Update(loading, force: true);
            var selection = await progress.ReadAsync(() => SettlMeteringPointScanner.SelectAsync(
                _meteringPointClient, includeChildren: false, includeNotInDelivery: true,
                progress: message =>
                {
                    loading = message.TrimStart('\r').TrimStart();
                    progress.Update(loading, force: true);
                }, cancellationToken), "metering-point scan", () => loading, cancellationToken);
            Log($"Scanned {selection.Scanned} metering points → {selection.MeteringPointIds.Count} candidates for filtering");
            candidateIds = selection.MeteringPointIds;
        }

        candidateIds = candidateIds.Distinct().ToList();
        var matched = 0;
        var fixedCount = 0;
        var failedWrites = 0;
        var checkedCount = 0;
        var versionReads = 0;
        string Status() => $"Checked: {Volatile.Read(ref checkedCount):N0} / {candidateIds.Count:N0}   Matched: {matched:N0}   Fixed versions: {fixedCount:N0}   Failed writes: {failedWrites:N0}   Errors: {errors:N0}   Versions read: {Volatile.Read(ref versionReads):N0}";
        async Task<(ConsumerVersionEvidence Versions, ConsumerDeliveryEvidence Delivery)> Read(Guid id)
        {
            var (json, error) = await _meteringPointClient.GetDeliveryStatusesRawAsync(id, cancellationToken);
            var versions = await ConsumerVersionEvidence.ReadAsync(_meteringPointClient, id,
                () => Interlocked.Increment(ref versionReads), cancellationToken);
            return (versions, ConsumerDeliveryEvidence.Parse(json, error));
        }
        progress.StartPhase();
        for (var offset = 0; offset < candidateIds.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Limit.HasValue && matched >= options.Limit.Value)
            {
                Log($"--limit {options.Limit.Value} reached - stopping scan; remaining points were not checked");
                break;
            }
            var size = Math.Min(options.DryRun ? 8 : 1, candidateIds.Count - offset);
            if (options.Limit.HasValue) size = Math.Min(size, options.Limit.Value - matched);
            var results = await progress.ReadAsync(() => Task.WhenAll(candidateIds.GetRange(offset, size).Select(async id =>
            {
                var evidence = await Read(id);
                Interlocked.Increment(ref checkedCount);
                return (Id: id, evidence.Versions, evidence.Delivery);
            })), "delivery-history / consumer reads", Status, cancellationToken);
            offset += size;
            foreach (var result in results)
            {
                var warnings = result.Versions.Errors.Concat(result.Delivery.Errors).Distinct().ToList();
                if (result.Delivery.Periods.Count == 0) warnings.Add("No delivery periods returned.");
                var decisions = ConsumerDuplicateSelection.For(result.Delivery, result.Versions);
                if (decisions.Any(d => d.Duplicate is null)) warnings.Add("Consumer selection is incomplete.");
                if (warnings.Count > 0)
                {
                    errors++;
                    Log($"  {result.Id}: skipped: {string.Join(" | ", warnings)}");
                    continue;
                }
                var targets = decisions.Where(d => d.Duplicate == true).Select(d => d.Correlation.Latest!)
                    .DistinctBy(v => ConsumerDeliveryEvidence.Utc(v.Start!.Value)).ToArray();
                if (targets.Length == 0) continue;
                matched++;
                foreach (var target in targets)
                {
                    var start = ConsumerDeliveryEvidence.Utc(target.Start!.Value).UtcDateTime;
                    var identification = target.Point?.Identification?.MeteringPointIdentification ?? result.Id.ToString();
                    var label = $"{identification} ({result.Id}): Consumer {ConsumerReportDates.Format(new DateTimeOffset(start))}";
                    if (options.DryRun)
                    {
                        Log($"  {label} - would blank second party Name + NationalIdentificationNumber ({target.Reason})");
                        continue;
                    }
                    // Re-read the histories so a newly added version cannot silently change selection.
                    var fresh = await progress.ReadAsync(() => Read(result.Id), "rechecking Consumer selection", Status, cancellationToken);
                    var stillSelected = fresh.Versions.Errors.Count == 0 && fresh.Delivery.Errors.Count == 0
                        && ConsumerDuplicateSelection.For(fresh.Delivery, fresh.Versions).Any(d => d.Duplicate == true
                            && ConsumerDeliveryEvidence.Utc(d.Correlation.Latest!.Start!.Value).UtcDateTime == start);
                    if (!stillSelected)
                    {
                        errors++;
                        Log($"  {label}: skipped: selection changed or evidence is incomplete.");
                        continue;
                    }
                    var (outcome, error) = await progress.ReadAsync(() => _cleanup!.BlankAsync(result.Id, start, cancellationToken),
                        "dated Consumer update and verification", Status, cancellationToken);
                    switch (outcome)
                    {
                        case ConsumerCleanupOutcome.Fixed:
                            fixedCount++; Log($"  {label}: fixed - verified on the same dated version"); break;
                        case ConsumerCleanupOutcome.NotSent:
                            errors++; Log($"  {label}: skipped before write - {error}"); break;
                        case ConsumerCleanupOutcome.Rejected:
                            failedWrites++; Log($"  {label}: FAILED - Settl rejected the write: {error}"); break;
                        default:
                            failedWrites++; Log($"  {label}: FAILED - write sent but not verified: {error}"); break;
                    }
                }
            }
            progress.Update(Status());
        }
        progress.Update(Status(), force: true);
        Log("--- Summary ---");
        Log($"  Checked:  {checkedCount}");
        Log($"  Matched:  {matched}");
        Log($"  Fixed versions: {fixedCount}");
        if (!options.DryRun) Log($"  Failed writes: {failedWrites}");
        Log($"  {(options.DryRun ? "Read errors" : "Errors")}: {errors}");
        if (options.DryRun) Log("  (dry-run - no writes)");
        return errors > 0 || failedWrites > 0 ? 1 : 0;
    }

    /// <summary>
    /// Candidate detection only — the authoritative equality check lives in
    /// <see cref="ISettlMeteringPointClient.BlankDuplicateSecondConsumerNameAsync"/>. Private MPs
    /// (<c>CustomerType == "Person"</c>) with a second party whose name deep-equals the first
    /// party's (FullName + FirstName + LastName, blank/whitespace-normalized) are candidates.
    /// </summary>
    internal static string? FindDuplicateName(MeteringPointV4Dto mp)
    {
        var consumer = mp.Consumer;
        if (consumer == null || !string.Equals(consumer.CustomerType, "Person", StringComparison.Ordinal))
            return null;

        var first = consumer.FirstConsumerParty?.Name;
        var second = consumer.SecondConsumerParty?.Name;
        if (first == null || second == null)
            return null;

        if (!NamesEqual(first, second))
            return null;

        return first.FullName ?? $"{first.FirstName} {first.LastName}".Trim();
    }

    internal const string SameNameReason = "Same name";
    internal const string NationalIdWithoutNameReason = "National id without name";

    /// <summary>
    /// Why the second Consumer party should be blanked, or null when it should not. Private MPs only:
    /// either the second party's name equals the first's (<see cref="FindDuplicateName"/>), or the
    /// second party has a national identification number but no name. Settl's metering-point read
    /// never carries national ids, so <paramref name="secondHasNationalId"/> comes from the separate
    /// national-id read (see <see cref="NeedsNationalIdCheck"/>).
    /// </summary>
    internal static string? Consumer2Reason(MeteringPointV4Dto mp, bool? secondHasNationalId = null)
    {
        if (FindDuplicateName(mp) is not null) return SameNameReason;
        if (!NeedsNationalIdCheck(mp)) return null;
        var fromPoint = !string.IsNullOrWhiteSpace(mp.Consumer!.SecondConsumerParty?.NationalIdentificationNumber);
        return secondHasNationalId == true || fromPoint ? NationalIdWithoutNameReason : null;
    }

    /// <summary>True when only consumer 2's national id decides whether this version is selected:
    /// a private MP that is not a same-name duplicate and whose second party has no name.</summary>
    internal static bool NeedsNationalIdCheck(MeteringPointV4Dto mp)
    {
        var consumer = mp.Consumer;
        if (consumer?.Start is null || !string.Equals(consumer.CustomerType, "Person", StringComparison.Ordinal)
            || FindDuplicateName(mp) is not null)
            return false;
        var name = consumer.SecondConsumerParty?.Name;
        return name == null || Normalize(name.FullName) == "" && Normalize(name.FirstName) == "" && Normalize(name.LastName) == "";
    }

    /// <summary>True when consumer 2 on a private MP has a name. DataHub rejects a customer update that carries a
    /// name with a blank CPR (BRS-015), so the report flags such versions. Report only: the fix never selects them.</summary>
    internal static bool HasSecondName(MeteringPointV4Dto mp)
    {
        var consumer = mp.Consumer;
        if (consumer?.Start is null || !string.Equals(consumer.CustomerType, "Person", StringComparison.Ordinal)) return false;
        var name = consumer.SecondConsumerParty?.Name;
        return name != null && (Normalize(name.FullName) != "" || Normalize(name.FirstName) != "" || Normalize(name.LastName) != "");
    }

    private static bool NamesEqual(MeteringPointV4NameDto a, MeteringPointV4NameDto b) =>
        Normalize(a.FullName) == Normalize(b.FullName)
        && Normalize(a.FirstName) == Normalize(b.FirstName)
        && Normalize(a.LastName) == Normalize(b.LastName);

    private static string Normalize(string? s) => (s ?? "").Trim();
}
