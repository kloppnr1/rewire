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
    /// private + duplicate-name candidates, regardless of whether the candidate set came from
    /// <see cref="MeteringPointIds"/> or a full scan.</summary>
    public int? Limit { get; set; }

    public bool DryRun { get; set; }
}

/// <summary>
/// Business rule: for private metering points (<c>Consumer.CustomerType == "Person"</c>) in Settl,
/// when <c>Consumer.FirstConsumerParty.Name</c> deep-equals <c>Consumer.SecondConsumerParty.Name</c>,
/// blank <c>Name</c> and <c>NationalIdentificationNumber</c> on the second party.
///
/// <para>Candidate detection reads <see cref="ISettlMeteringPointClient.GetByIdV4Async"/> (the only
/// typed read path that carries <c>Consumer.CustomerType</c> and both consumer parties' names — the
/// v3 <c>GetByIdAsync</c>/<c>MeteringPointCurrentDto</c> path used by <c>report-meteringpoints</c>
/// does not expose Consumer detail at all).</para>
///
/// <para>The actual mutation goes through <see cref="ISettlMeteringPointClient.BlankDuplicateSecondConsumerNameAsync"/>
/// (package <c>NewSettlement.SettlClient.Api</c> ≥ 0.7.49), which does a raw-JSON round-trip PUT —
/// the v3 master PUT is a full replace, so a partial typed body would drop unmodelled sections
/// (<c>SystemService</c>, <c>DistributionSystemOperator</c>, <c>Renewable</c>, most of
/// <c>Consumer</c>). That method owns the equality check itself and is a safe no-op when the names
/// don't match or there's no second party, so it is called unconditionally per candidate — the
/// filtering here only decides which MPs to report/attempt, not whether the write is safe.</para>
///
/// <para><b>No caching anywhere in this path.</b> <see cref="ISettlMeteringPointClient"/> is wired
/// up in <c>Program.cs</c> via a plain <c>AddHttpClient</c> typed client — <c>BulkCachingMeteringPointClient</c>
/// (in <c>DataMigration.Migration.Library</c>, used only by MigrationCli's <c>create</c>) is never
/// applied here, and even there it only caches <c>SearchByIdentificationAsync</c>; <c>GetByIdV4Async</c>
/// and <c>BlankDuplicateSecondConsumerNameAsync</c> are pass-through everywhere. Every read in this
/// runner, including the post-write verification below, is a fresh HTTP round-trip. A reported
/// "fixed" is therefore never trusted on the write call's own success flag alone — the runner
/// re-reads immediately after the write and only counts a fix once the duplicate is actually gone,
/// because a success response does not by itself prove the effective document changed the way
/// expected (version/effective-date mismatches or a concurrent write can leave old data in place or
/// bring it back).</para>
/// </summary>
public class FixConsumer2DuplicateRunner
{
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly Action<string> _log;
    public TextWriter ProgressOutput { get; init; } = Console.Error;
    internal TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(5);

    public FixConsumer2DuplicateRunner(
        ISettlMeteringPointClient meteringPointClient,
        Action<string>? log = null)
    {
        _meteringPointClient = meteringPointClient;
        _log = log ?? Console.WriteLine;
    }

    public async Task<int> RunAsync(FixConsumer2DuplicateRunOptions options, CancellationToken cancellationToken = default)
    {
        using var progress = new Consumer2ProgressDisplay(ProgressOutput, ProgressInterval);
        void Log(string message) { progress.Dispose(); _log(message); }
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

        var matched = 0;
        var fixedCount = 0;
        var errors = 0;
        var checkedCount = 0;

        string Status() => $"Checked: {Volatile.Read(ref checkedCount):N0} / {candidateIds.Count:N0}   Matched: {matched:N0}   Fixed: {fixedCount:N0}   Errors: {errors:N0}";
        progress.Update(Status(), force: true);
        if (options.DryRun)
        {
            for (var offset = 0; offset < candidateIds.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.Limit.HasValue && matched >= options.Limit.Value)
                {
                    Log($"--limit {options.Limit.Value} reached - stopping scan; remaining points were not checked");
                    break;
                }
                var size = Math.Min(8, candidateIds.Count - offset);
                if (options.Limit.HasValue) size = Math.Min(size, options.Limit.Value - matched);
                var batch = candidateIds.GetRange(offset, size);
                var results = await progress.ReadAsync(() => Task.WhenAll(batch.Select(async id =>
                {
                    var (mp, error) = await _meteringPointClient.GetByIdV4Async(id, null, cancellationToken);
                    Interlocked.Increment(ref checkedCount);
                    return (Id: id, Mp: mp, Error: error);
                })), "consumer reads (up to 8 concurrent)", Status, cancellationToken);
                offset += size;
                foreach (var result in results)
                {
                    if (result.Mp is not { } mp)
                    {
                        errors++;
                        Log($"  {result.Id}: GetByIdV4 failed: {result.Error}");
                        continue;
                    }
                    var duplicateName = FindDuplicateName(mp);
                    if (duplicateName is null) continue;
                    matched++;
                    var identification = mp.Identification?.MeteringPointIdentification ?? result.Id.ToString();
                    Log($"  {identification} ({result.Id}): duplicate name '{duplicateName}' on FirstConsumerParty/SecondConsumerParty - would blank second party Name + NationalIdentificationNumber");
                }
                progress.Update(Status());
            }
        }

        if (!options.DryRun)
        {
            foreach (var id in candidateIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (options.Limit.HasValue && matched >= options.Limit.Value)
                {
                    Log($"--limit {options.Limit.Value} reached — stopping scan");
                    break;
                }

                var (mp, err) = await progress.ReadAsync(
                    () => _meteringPointClient.GetByIdV4Async(id, null, cancellationToken), "consumer read", Status, cancellationToken);
                checkedCount++;
                progress.Update(Status());
                if (mp == null)
                {
                    if (options.MeteringPointIds.Length > 0)
                    {
                        Log($"  {id}: GetByIdV4 failed: {err}");
                        errors++;
                    }
                    continue;
                }

                var duplicateName = FindDuplicateName(mp);
                if (duplicateName == null)
                    continue;

                matched++;
                var identification = mp.Identification?.MeteringPointIdentification ?? id.ToString();

                var (success, fixErr) = await progress.ReadAsync(
                    () => _meteringPointClient.BlankDuplicateSecondConsumerNameAsync(id, cancellationToken), "consumer update", Status, cancellationToken);
                if (!success)
                {
                    Log($"  {identification} ({id}): FAILED — {fixErr}");
                    errors++;
                    continue;
                }

                // BlankDuplicateSecondConsumerNameAsync reporting success only means the PATCH round-trip
                // did not error — it does not prove the effective document actually lost the duplicate
                // (e.g. a version/effective-date mismatch between the write and a later re-read, or a
                // concurrent write racing this one, can leave the duplicate in place or reintroduce it on
                // a new dated version). Re-read fresh — never from any cache — right after the write and
                // fail loudly instead of reporting "fixed" on unverified trust.
                var (verifyMp, verifyErr) = await progress.ReadAsync(
                    () => _meteringPointClient.GetByIdV4Async(id, null, cancellationToken), "post-write verification", Status, cancellationToken);
                if (verifyMp == null)
                {
                    Log($"  {identification} ({id}): PATCH reported success but post-write verification read failed: {verifyErr} — treating as FAILED");
                    errors++;
                    continue;
                }

                var stillDuplicate = FindDuplicateName(verifyMp);
                if (stillDuplicate != null)
                {
                    Log($"  {identification} ({id}): PATCH reported success but a fresh re-read still shows duplicate name '{stillDuplicate}' — treating as FAILED, NOT counted as fixed");
                    errors++;
                    continue;
                }

                Log($"  {identification} ({id}): fixed — second party '{duplicateName}' blanked (verified on fresh re-read)");
                fixedCount++;
            }

        }

        progress.Update(Status(), force: true);
        Log("--- Summary ---");
        Log($"  Checked:  {checkedCount}");
        Log($"  Matched:  {matched}");
        if (options.DryRun)
        {
            Log($"  Read errors: {errors}");
            Log($"  (dry-run — no writes)");
        }
        else
        {
            Log($"  Fixed:    {fixedCount}");
            Log($"  Errors:   {errors}");
        }

        return errors > 0 ? 1 : 0;
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

    private static bool NamesEqual(MeteringPointV4NameDto a, MeteringPointV4NameDto b) =>
        Normalize(a.FullName) == Normalize(b.FullName)
        && Normalize(a.FirstName) == Normalize(b.FirstName)
        && Normalize(a.LastName) == Normalize(b.LastName);

    private static string Normalize(string? s) => (s ?? "").Trim();
}
