using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Runners;

/// <summary>Read-only duplicate report using the cleanup's exact detection rule and
/// the market-list delivery signals already exported by report-delivery-start.</summary>
public sealed class ReportConsumer2DuplicateRunner(ISettlMeteringPointClient client)
{
    public async Task<int> RunAsync(string environment, string[] meteringPointIds, int? limit,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        var candidates = new List<(Guid Id, MeteringPointInfoDto? Evidence)>();
        var errors = 0;
        if (meteringPointIds.Length > 0)
        {
            foreach (var value in meteringPointIds.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Guid.TryParse(value, out var id)) candidates.Add((id, null));
                else
                {
                    var (mp, error) = await client.SearchByIdentificationAsync(value, cancellationToken);
                    if (mp is null)
                    {
                        Console.Error.WriteLine($"{value}: lookup failed ({error}); report incomplete.");
                        errors++;
                    }
                    else candidates.Add((mp.Id, mp));
                }
            }
        }
        else
        {
            const int pageSize = 500;
            for (var skip = 0; ; skip += pageSize)
            {
                var (page, _, error) = await client.FindMeteringPointsAsync(pageSize, skip, false, cancellationToken);
                if (error is not null) throw new InvalidOperationException($"Market list failed at {skip}: {error}");
                // Include points that have left delivery: their evidence is relevant to review.
                candidates.AddRange(page.Where(mp => mp.ParentMeteringPoint?.Id is null || mp.ParentMeteringPoint.Id == Guid.Empty)
                    .Select(mp => (mp.Id, (MeteringPointInfoDto?)mp)));
                if (page.Count < pageSize) break;
            }
        }

        await WriteRow(output, ["MeteringPointIdentification", "SettlId", "Environment", "Consumer1Name",
            "Consumer2Name", "InDelivery", "CurrentOrNextDeliveryStart", "CurrentOrNextDeliveryEnd",
            "CompletedDeliveryStart", "CompletedDeliveryEnd", "LastUpdatedUtc", "ChangeSignal", "UncertaintyReason"]);
        var matched = 0;
        foreach (var candidate in candidates.DistinctBy(c => c.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit.HasValue && matched >= limit.Value) break;
            var (mp, error) = await client.GetByIdV4Async(candidate.Id, null, cancellationToken);
            if (mp is null)
            {
                Console.Error.WriteLine($"{candidate.Id}: consumer read failed ({error}); report incomplete.");
                errors++;
                continue;
            }
            if (FixConsumer2DuplicateRunner.FindDuplicateName(mp) is null) continue;
            matched++;
            var evidence = candidate.Evidence;
            string? evidenceError = null;
            if (evidence is null && mp.Identification?.MeteringPointIdentification is { Length: > 0 } identification)
            {
                (evidence, evidenceError) = await client.SearchByIdentificationAsync(identification, cancellationToken);
                if (evidence?.Id != candidate.Id) { evidence = null; evidenceError = "Market lookup did not return the same Settl ID."; }
            }
            var (signal, reason) = Assess(evidence, evidenceError);
            await WriteRow(output, [mp.Identification?.MeteringPointIdentification ?? "", candidate.Id.ToString(), environment,
                Name(mp.Consumer!.FirstConsumerParty!.Name!), Name(mp.Consumer.SecondConsumerParty!.Name!),
                evidence?.InDelivery?.ToString() ?? "", Date(evidence?.CurrentOrNextDeliveryPeriod?.Start),
                Date(evidence?.CurrentOrNextDeliveryPeriod?.End), Date(evidence?.MostRecentCompletedDeliveryPeriod?.Start),
                Date(evidence?.MostRecentCompletedDeliveryPeriod?.End), Date(evidence?.LastUpdated), signal, reason]);
        }
        Console.Error.WriteLine($"Reported {matched} duplicate candidates; {errors} read errors. No Settl writes.");
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
