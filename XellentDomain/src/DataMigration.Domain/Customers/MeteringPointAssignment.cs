using DataMigration.Domain.Products;

namespace DataMigration.Domain.Customers;

/// <summary>
/// A metering point assigned to a contract with a specific product.
/// <see cref="Start"/> is when the product takes effect; it may be in the future relative to
/// the snapshot date when an assignment is scheduled but not yet active. Consumers compare
/// <see cref="Start"/> to the snapshot date to distinguish active from future assignments.
///
/// <para>The same metering point may appear multiple times in
/// <see cref="Contract.MeteringPoints"/> with different products and start dates — typically
/// one currently active and one or more scheduled for the future.</para>
/// </summary>
public sealed record MeteringPointAssignment(
    string MeteringPointId,
    Product Product,
    DateOnly Start,
    DateOnly EndDate = default,
    string AmrCode = "");
