using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Clients;

public interface ISettlDebtorClient
{
    /// <summary>
    /// Returns account summary for a customer: total balances per currency, DebtorId, and DebtorIdentifier.
    /// GET /debtors/api/v1/debtorAccountEntries/customerAccountInfo?CustomerId={id}
    /// </summary>
    Task<(CustomerAccountInfoDto? Result, string? Error)> GetCustomerAccountInfoAsync(
        Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns paginated account entries (invoices) for a customer with their payment lines.
    /// GET /debtors/api/v1/debtorAccountEntries/findEntriesForCustomer?CustomerId={id}&amp;Skip=N&amp;Limit=N&amp;DoCount=true
    /// </summary>
    Task<(List<DebtorAccountEntryDto> Items, int TotalCount, string? Error)> FindEntriesForCustomerAsync(
        Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single account entry by its Settl id.
    /// GET /debtors/api/v1/debtorAccountEntries/{id}
    /// </summary>
    Task<(DebtorAccountEntryDto? Entry, string? Error)> GetEntryByIdAsync(
        Guid id, CancellationToken cancellationToken = default);
}
