using DataMigration.SettlApi.Models;
using DataMigration.SettlClient.Internal;

namespace DataMigration.Migration.Services;

/// <summary>
/// Customer extraction. Projects Domain <see cref="DataMigration.Domain.Customers.Customer"/> objects
/// from the preloaded <see cref="DataMigration.Domain.Xellent.XellentLoadResult"/> to
/// <see cref="CreateCustomerDto"/> via <see cref="CustomerTranslator"/>.
/// </summary>
public partial class SettlMappingService
{
    public Task<List<CreateCustomerDto>> ExtractCustomersAsync(string[] accountNumbers) =>
        Task.FromResult(_loadResult.Customers.Select(CustomerTranslator.Build).ToList());
}
