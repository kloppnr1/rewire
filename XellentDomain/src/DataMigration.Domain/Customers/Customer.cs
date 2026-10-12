using DataMigration.Domain.Common;

namespace DataMigration.Domain.Customers;

/// <summary>
/// A customer — the legal entity that holds contracts and gets billed. Identified by
/// <see cref="AccountNumber"/>. Customer is an aggregate root: <see cref="Contracts"/>
/// are part of the customer's identity in the supplier-perspective domain. Each contract
/// in turn carries its <see cref="MeteringPointAssignment"/>s and its own
/// <see cref="Contract.Billing"/> schedule. Use
/// <see cref="MeteringPointAssignment.MeteringPointId"/> to look up the actual
/// <c>MeteringPoint</c> in the loader's result.
/// </summary>
public sealed record Customer(
    string AccountNumber,
    string Name,
    string? AdditionalName,
    CustomerType Type,
    string? Cvr,
    string? Cpr,
    Address Address,
    ContactInfo Contact,
    CustomerBilling Billing,
    IReadOnlyList<Contract> Contracts)
{
    /// <summary>
    /// All payers (billing parties) belonging to this customer — always at least one
    /// (the primary). Populated by the loader after <see cref="Contracts"/> is known;
    /// defaults to empty so existing direct constructions stay valid.
    /// </summary>
    public IReadOnlyList<Payer> Payers { get; init; } = Array.Empty<Payer>();

    /// <summary>
    /// Second authorized person (disponent 2). CVR from <c>ExuCvrDisponent2</c>;
    /// null when no second disponent exists. Name lives in <see cref="AdditionalName"/>.
    /// </summary>
    public string? Cvr2 { get; init; }

    /// <summary>
    /// CPR for second authorized person (disponent 2) from <c>ExuCprDisponent2</c>;
    /// null when no second disponent exists.
    /// </summary>
    public string? Cpr2 { get; init; }

    /// <summary>
    /// The identification fields as the source system holds them, before <see cref="Cvr"/> and
    /// <see cref="Cpr"/> are normalised. Lets a consumer tell an empty field apart from an
    /// unusable one — see <see cref="Customers.RawIdentification"/>. Defaults to
    /// <see cref="RawIdentification.None"/> so existing direct constructions stay valid.
    /// </summary>
    public RawIdentification RawIdentification { get; init; } = RawIdentification.None;
}
