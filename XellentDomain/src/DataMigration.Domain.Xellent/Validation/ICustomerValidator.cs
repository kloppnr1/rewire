using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// Runs a single policy-check against a built <see cref="Customer"/> and emits zero or more
/// <see cref="ValidationEntry"/>s. Implementations are stateless and reusable.
///
/// <para>Validators see the post-mapper <see cref="Customer"/> only — they do not have access
/// to original Xellent values. Shape-errors (invalid CVR, invalid email, invalid DAR Guid)
/// are handled by the mapper before validators run.</para>
/// </summary>
public interface ICustomerValidator
{
    IEnumerable<ValidationEntry> Validate(Customer customer);
}
