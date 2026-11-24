using DataMigration.Core;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Validation;

/// <summary>
/// Composes a list of <see cref="ICustomerValidator"/> instances and runs them all against
/// a built <see cref="Customer"/>. Consumer (CreateRunner, OpsCli runners) decides which
/// validators to include and what to do with the resulting <see cref="ValidationEntry"/>s.
///
/// <para>The <see cref="RunAndCollect"/> adapter forwards each entry to an
/// <see cref="IWarningCollector"/> as a structured <see cref="ValidationEntry"/> (matching the
/// existing report schema in <c>MigrationSummaryWriter</c>), and — unless the entry sets
/// <see cref="ValidationEntry.LogAsMessage"/> to false — also as a free-text Error/Warning
/// matching the existing migration log format.</para>
/// </summary>
public sealed class CustomerValidationRunner
{
    private readonly IReadOnlyList<ICustomerValidator> _validators;

    public CustomerValidationRunner(IReadOnlyList<ICustomerValidator> validators)
    {
        _validators = validators ?? throw new ArgumentNullException(nameof(validators));
    }

    /// <summary>Runs every validator and returns the union of issues. Pure — no side effects.</summary>
    public IEnumerable<ValidationEntry> Run(Customer customer)
    {
        foreach (var validator in _validators)
            foreach (var entry in validator.Validate(customer))
                yield return entry;
    }

    /// <summary>
    /// Runs every validator and forwards each issue to <paramref name="warnings"/>: Error/Warning
    /// severity → matching collector method, plus an <see cref="IWarningCollector.AddValidationEntry"/>
    /// call so the structured entry surfaces in the migration report.
    /// </summary>
    public void RunAndCollect(Customer customer, IWarningCollector warnings)
    {
        foreach (var entry in Run(customer))
            Forward(entry, warnings);
    }

    /// <summary>
    /// Runs a set of <see cref="ICustomerBatchValidator"/>s across <paramref name="customers"/>
    /// and forwards each issue to <paramref name="warnings"/> using the same format as
    /// <see cref="RunAndCollect"/>.
    /// </summary>
    public void RunBatchAndCollect(
        IReadOnlyList<ICustomerBatchValidator> batchValidators,
        IReadOnlyList<Customer> customers,
        IWarningCollector warnings)
    {
        foreach (var validator in batchValidators)
            foreach (var entry in validator.Validate(customers))
                Forward(entry, warnings);
    }

    private static void Forward(ValidationEntry entry, IWarningCollector warnings)
    {
        warnings.AddValidationEntry(entry);

        if (!entry.LogAsMessage)
            return;

        var message = $"{entry.EntityType} {entry.EntityId}: {entry.Pattern} [Xellent: {entry.XellentField}]";
        if (string.Equals(entry.Severity, "Error", StringComparison.OrdinalIgnoreCase))
            warnings.AddError(message);
        else
            warnings.AddWarning(message);
    }
}
