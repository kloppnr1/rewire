using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// Runs a policy-check across a full batch of <see cref="Customer"/>s and emits zero or more
/// <see cref="ValidationEntry"/>s. Use for cross-customer rules that require comparing accounts
/// within the same CVR group — impossible with the per-customer <see cref="ICustomerValidator"/>.
/// </summary>
public interface ICustomerBatchValidator
{
    IEnumerable<ValidationEntry> Validate(IReadOnlyList<Customer> customers);
}
