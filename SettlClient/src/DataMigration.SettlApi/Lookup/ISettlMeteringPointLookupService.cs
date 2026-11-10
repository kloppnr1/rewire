namespace DataMigration.SettlApi.Lookup;

/// <summary>
/// Read-only Settl lookup: projects essential customer + metering-point properties from the
/// Settl API into <see cref="SettlMeteringPointView"/>. Depends only on the Settl API clients,
/// so it is reusable from any consumer (ops CLI, dashboard web API, ad-hoc tools).
///
/// <para>Note: Settl exposes no metering-point → customer reverse link. Identification-based
/// lookups therefore return MP data with blank customer fields unless the caller supplies the
/// customer number; only the customer-number path resolves the full customer.</para>
/// </summary>
public interface ISettlMeteringPointLookupService
{
    /// <summary>Look up many MPs by identification. Optional per-request customer number fills the customer columns.</summary>
    Task<IReadOnlyList<SettlMeteringPointView>> LookupByIdentificationsAsync(
        IReadOnlyList<SettlMpLookupRequest> requests,
        bool includeMissing,
        CancellationToken cancellationToken = default);

    /// <summary>Look up a single MP by identification (customer columns blank — no reverse link in Settl).</summary>
    Task<SettlMeteringPointView?> LookupByIdentificationAsync(
        string meteringPointIdentification,
        CancellationToken cancellationToken = default);

    /// <summary>Look up a customer by number and return one view per metering point linked to it.</summary>
    Task<IReadOnlyList<SettlMeteringPointView>> LookupByCustomerNumberAsync(
        string customerNumber,
        CancellationToken cancellationToken = default);
}
