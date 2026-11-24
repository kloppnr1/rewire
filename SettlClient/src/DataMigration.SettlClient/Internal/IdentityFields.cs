using DataMigration.Domain.Customers;

namespace DataMigration.SettlClient.Internal;

/// <summary>
/// The identity pair Settl accepts on a party, decided by the customer's CVR. A non-empty
/// CVR means a Company: VAT only. No CVR means a Person: CPR only. Settl rejects a Company
/// that carries a <c>NationalIdentificationNumber</c>
/// (<c>ValidationRules.PayerType.MustBeEmptyWhen</c>), and the Domain sets
/// <see cref="CustomerType.Company"/> exactly when the CVR is non-empty, so the two agree.
/// Shared by <see cref="CustomerTranslator"/> and <see cref="PayerTranslator"/> so the rule
/// cannot drift between them again.
/// </summary>
internal static class IdentityFields
{
    public static (string? VatIdentificationNumber, string? NationalIdentificationNumber) For(Customer customer) =>
        !string.IsNullOrEmpty(customer.Cvr)
            ? (customer.Cvr, null)
            : (null, customer.Cpr);
}
