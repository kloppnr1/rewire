using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

/// <summary>Read-only duplicate report with one row per delivery period, using its newest effective Consumer version.</summary>
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
        progress.Message("Duplicate Consumer 2 report: all Consumer versions (up to 8 concurrent reads)");
        progress.Message($"Environment: {environment}");
        progress.Message("Delivery pattern flag: oldest periods false (Na -> Na), then true (ChangeOfSupplier -> Na); later periods unrestricted.");
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
        await WriteRow(output, ["MeteringPointIdentification", "SettlId", "DeliveryPatternMatch",
            "DeliveryStartDk", "DeliveryEndDk", "InDelivery", "DeliveryStartReason", "DeliveryStopReason",
            "ConsumerVersionCount", "Consumer1Name", "Consumer2Name", "ConsumerStartDk", "ConsumerEndDk",
            "DuplicateConsumer2", "AnyDuplicateConsumer2", "ConsumerWarning", "DeliveryWarning"]);
        var matched = 0;
        var checkedCount = 0;
        var versionReads = 0;
        var prefixMatches = 0;
        var duplicateVersions = 0;
        var stoppedAtLimit = false;
        string CheckStatus() => $"Checking consumers: Checked: {Volatile.Read(ref checkedCount):N0} / {selected.Count:N0}   Duplicates: {matched:N0}   Prefix matches: {Volatile.Read(ref prefixMatches):N0}   Versions read: {Volatile.Read(ref versionReads):N0}   Duplicate versions: {duplicateVersions:N0}   Read errors: {errors:N0}";
        progress.StartPhase();
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
                var (json, historyError) = await client.GetDeliveryStatusesRawAsync(candidate.Id, cancellationToken);
                var prefix = DeliveryHistoryPrefix.Assess(json, historyError);
                ConsumerVersionEvidence versions;
                ConsumerDeliveryEvidence? delivery;
                if (prefix.Matches == true) Interlocked.Increment(ref prefixMatches);
                versions = await ConsumerVersionEvidence.ReadAsync(client, candidate.Id,
                    () => Interlocked.Increment(ref versionReads), cancellationToken);
                delivery = ConsumerDeliveryEvidence.Parse(json, historyError);
                Interlocked.Increment(ref checkedCount);
                return (candidate.Id, Versions: versions, Evidence: candidate.Evidence, Delivery: delivery, PrefixError: prefix.Warning, PatternMatch: prefix.Matches);
            })), "delivery-history / consumer read (up to 8 concurrent)", CheckStatus, cancellationToken);
            offset += size;
            foreach (var result in results)
            {
                var messages = result.Versions.Errors.Concat(result.Delivery?.Errors ?? []).ToList();
                if (result.PrefixError is { } prefixError && !messages.Contains(prefixError)) messages.Add(prefixError);
                if (result.Versions.Versions.Any(v => v.Duplicate) && result.Delivery?.Periods.Count == 0 && result.Delivery.Errors.Count == 0)
                    messages.Add("No delivery periods returned; Consumer correlation unavailable.");
                errors += messages.Count;
                foreach (var message in messages)
                    progress.Message($"{result.Id}: {message}; report incomplete.");
                var affected = result.Versions.Versions.Any(v => v.Duplicate);
                if (!affected && messages.Count == 0) continue;
                if (affected)
                {
                    matched++;
                    duplicateVersions += result.Versions.Versions.Count(v => v.Duplicate);
                }
                var identification = result.Versions.Versions.Select(v => v.Point?.Identification?.MeteringPointIdentification)
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? result.Evidence?.MeteringPointIdentification ?? "(missing)";
                var pattern = result.PatternMatch is null ? "Unknown" : result.PatternMatch.Value ? "Yes" : "No";
                var deliveryWarnings = (result.Delivery?.Errors ?? []).Concat(result.PrefixError is null ? [] : new[] { result.PrefixError });
                var deliveryWarning = string.Join(" | ", deliveryWarnings.Distinct());
                var periods = result.Delivery?.Periods ?? [];
                var firstPeriod = periods.Count > 0 && periods.All(p => p.Start is not null)
                    && periods.Count(p => p.Start == periods[0].Start) == 1 ? periods[0] : null;
                foreach (var period in periods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var correlation = DeliveryConsumerCorrelation.For(period, result.Versions);
                    var version = correlation.Latest;
                    var mp = version?.Point;
                    await WriteRow(output, [identification, result.Id.ToString(), pattern,
                        DeliveryDate(period.Start, period.RawStart, false), DeliveryDate(period.End, period.RawEnd, true),
                        period.State, period.StartReason, period.StopReason,
                        correlation.Count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown",
                        Name(mp?.Consumer?.FirstConsumerParty?.Name), Name(mp?.Consumer?.SecondConsumerParty?.Name),
                        version is null ? "" : VersionDate(version.Start), version is null ? "" : version.End is null ? "(open)" : VersionDate(version.End, true),
                        firstPeriod is null ? "Unknown" : ReferenceEquals(period, firstPeriod) ? "No" : mp?.Consumer is null ? "Unknown" : version!.Duplicate ? "Yes" : "No",
                        correlation.AnyDuplicate is null ? "Unknown" : correlation.AnyDuplicate.Value ? "Yes" : "No", correlation.Warning, deliveryWarning]);
                }
                if (periods.Count == 0)
                    await WriteRow(output, [identification, result.Id.ToString(), pattern, "", "", "", "", "", "Unknown",
                        "", "", "", "", "Unknown", "Unknown", "Consumer correlation unavailable: " + string.Join(" | ", result.Versions.Errors.Distinct()),
                        deliveryWarning.Length > 0 ? deliveryWarning : "No delivery periods returned; Consumer correlation unavailable."]);
            }
            progress.Update(CheckStatus());
        }
        progress.Update(CheckStatus(), force: true);
        if (stoppedAtLimit) progress.Message($"Stopped at --limit {limit}: remaining metering points were not checked.");
        progress.Message(errors > 0 ? "Report incomplete: some metering points could not be read." : "Complete");
        progress.Message($"Checked: {checkedCount:N0}; duplicates: {matched:N0}; read errors: {errors:N0}; versions read: {versionReads:N0}; duplicate versions: {duplicateVersions:N0}; delivery-prefix matches: {prefixMatches:N0}.");
        if (OutputPath is not null) progress.Message($"Report: {OutputPath}");
        progress.Message("No Settl writes.");
        return errors > 0 ? 1 : 0;
    }

    private static string Name(MeteringPointV4NameDto? name) => name is null ? "" : name.FullName ?? $"{name.FirstName} {name.LastName}".Trim();
    private static string VersionDate(DateTime? date, bool end = false) => date is null ? "(missing)" : ConsumerReportDates.Format(ConsumerDeliveryEvidence.Utc(date.Value), end);
    private static string DeliveryDate(DateTimeOffset? date, string raw, bool end) => date is { } value
        ? ConsumerReportDates.Format(value, end)
        : raw is "null" or "(missing)" ? end ? "(open)" : "(missing)" : "(invalid)";
    private static Task WriteRow(TextWriter output, string[] cells) => output.WriteLineAsync(
        string.Join(';', cells.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
}
