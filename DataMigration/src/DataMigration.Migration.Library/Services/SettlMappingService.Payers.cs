using DataMigration.SettlApi.Models;
using DataMigration.SettlClient.Internal;

namespace DataMigration.Migration.Services;

/// <summary>
/// Payer extraction. Projects Domain <see cref="DataMigration.Domain.Customers.Customer"/> objects
/// from the preloaded <see cref="DataMigration.Domain.Xellent.XellentLoadResult"/> to
/// <see cref="CreatePayerDto"/> via <see cref="PayerTranslator"/>.
/// </summary>
public partial class SettlMappingService
{
    public Task<(List<CreatePayerDto> Payers, Dictionary<string, Guid> CustomerIdLookup)> ExtractPayersAsync(
        string[] accountNumbers,
        Dictionary<string, Guid> customerIdByAccountNum)
    {
        var customerIdLookup = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var payers = new List<CreatePayerDto>(_loadResult.Customers.Count);
        foreach (var customer in _loadResult.Customers)
        {
            var customerId = customerIdByAccountNum.GetValueOrDefault(customer.AccountNumber);
            customerIdLookup[customer.AccountNumber] = customerId;
            payers.AddRange(customer.Payers.Select(p => PayerTranslator.Build(customer, p, customerId)));
        }
        return Task.FromResult((payers, customerIdLookup));
    }
}
