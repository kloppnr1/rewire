using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Services;

public sealed record SettlementCurrentCustomerEvidence(string? CustomerNumber, string ReadStatus,
    IReadOnlyList<string>? CustomerNumbers = null);

/// <summary>Confirms current customer ownership without primary-payer or source-order guessing.</summary>
public static class SettlementCurrentCustomerSource
{
    private static readonly TimeZoneInfo DanishZone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    public static async Task<IReadOnlyDictionary<string, SettlementCurrentCustomerEvidence>> ReadAsync(
        IReadOnlyDictionary<string, Guid> points, IReadOnlyDictionary<Guid, string>? customerNumbers,
        Func<IReadOnlyList<Guid>, DateTime, CancellationToken, Task<(List<PayerForMeteringPointsDto>? Payers, string? Error)>> readPayers,
        DateTime at, CancellationToken cancellationToken = default,
        Action<SettlementConfigProgress>? progress = null)
    {
        var result = new Dictionary<string, SettlementCurrentCustomerEvidence>(StringComparer.Ordinal);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at, DanishZone));
        var done = 0;
        progress?.Invoke(new("CurrentPayers", 0, points.Count, "meteringPoints"));
        foreach (var batch in points.Chunk(200))
        {
            List<PayerForMeteringPointsDto>? payers;
            string? error;
            try { (payers, error) = await readPayers(batch.Select(p => p.Value).Distinct().ToArray(), at, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { payers = null; error = "Current payer read failed"; }
            foreach (var point in batch)
            {
                if (payers is null || error is not null || customerNumbers is null)
                { result[point.Key] = new(null, "ReadFailed"); continue; }
                var active = payers.Where(p => (p.MeteringPoints ?? []).Any(m => m.MeteringPointId == point.Value
                    && m.Start <= day && day < m.End)).ToArray();
                var unidentified = active.Any(p => p.Payer?.CustomerId is null || p.Payer.CustomerId == Guid.Empty);
                var owners = active.Select(p => p.Payer?.CustomerId)
                    .Where(id => id.HasValue && id.Value != Guid.Empty).Select(id => id!.Value).Distinct().ToArray();
                var numbers = owners.Select(id => customerNumbers.GetValueOrDefault(id)).OfType<string>()
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                result[point.Key] = owners.Length > 1 ? new(null, "Ambiguous", numbers)
                    : unidentified || owners.Length == 0 || numbers.Length == 0 ? new(null, "Missing", numbers)
                    : new(numbers[0], "Read", numbers);
            }
            done += batch.Length;
            progress?.Invoke(new("CurrentPayers", done, points.Count, "meteringPoints"));
        }
        return result;
    }
}
