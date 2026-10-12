using DataMigration.SettlApi.Clients;

namespace DataMigration.Migration.Services;

/// <summary>
/// Allocates Settl CustomerNumbers for synthetic owner-customers. Linear scan from
/// <c>StartFrom</c> upwards; first number that <c>SearchByCustomerNumberAsync</c>
/// reports as vacant wins. Run-local cache prevents two clusters in the same
/// migration from being given the same number before either has been POST'ed.
/// </summary>
public class OwnerNumberAllocator
{
    public const int StartFrom = 900000;
    public const int MaxScan = 10_000;

    private readonly ISettlCustomerClient _customerClient;
    private readonly HashSet<string> _allocatedThisRun = new(StringComparer.Ordinal);
    private int _nextProbe = StartFrom;

    public OwnerNumberAllocator(ISettlCustomerClient customerClient)
    {
        _customerClient = customerClient;
    }

    public async Task<(string? CustomerNumber, string? Error)> AllocateAsync(
        bool dryRun, CancellationToken cancellationToken)
    {
        for (int i = 0; i < MaxScan; i++)
        {
            var candidate = _nextProbe.ToString();
            _nextProbe++;
            if (_allocatedThisRun.Contains(candidate)) continue;

            var (existing, error) = await _customerClient.SearchByCustomerNumberAsync(
                candidate, cancellationToken: cancellationToken);
            if (error != null)
                return (null, $"Settl search failed for owner-CustomerNumber probe {candidate}: {error}");
            if (existing == null)
            {
                _allocatedThisRun.Add(candidate);
                return (candidate, null);
            }
        }
        return (null, $"No vacant CustomerNumber found in [{StartFrom}, {StartFrom + MaxScan})");
    }
}
