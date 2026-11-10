namespace DataMigration.Domain.Customers;

/// <summary>
/// The customer's identification fields exactly as the source system holds them — no validation,
/// no normalisation, no fallback between fields.
///
/// <para>These are NOT duplicates of <see cref="Customer.Cvr"/>/<see cref="Customer.Cpr"/>. Those
/// two are the migrated values: a CVR that isn't 8 digits, or a CPR that isn't 10, becomes null
/// there, which makes "the field was empty" and "the field held something unusable" look identical
/// afterwards. Consumers that have to explain WHY a customer ends up without identification — the
/// skipped-customer section of the create report — need the values before that collapse.</para>
///
/// <para>Nothing here decides anything. A value in <see cref="AlternativeNumber"/> is carried out
/// as-is even when <see cref="AlternativeNumberCode"/> says it is a CVR; whether such a customer
/// should be migrated is a separate decision that has not been made.</para>
/// </summary>
/// <param name="Cvr">CVR for the first authorized person, from <c>CustTable.EXU_CVR_DISPONENT1</c>.</param>
/// <param name="Cpr">CPR for the first authorized person, from <c>CustTable.EXU_CPR_DISPONENT1</c>.</param>
/// <param name="AlternativeNumber">
/// <c>CustTable.EXU_ALTERNATIVECUSTOMERNUMBER</c> — a second identification number, sometimes
/// written with a <c>PNR=</c> prefix.</param>
/// <param name="AlternativeNumberCode">
/// <c>CustTable.EXU_ALTERNATIVECUSTOMERCODE</c>, which says what
/// <paramref name="AlternativeNumber"/> is: 6 = CPR, 2 = CVR. 0 when nothing is registered.</param>
public sealed record RawIdentification(
    string? Cvr,
    string? Cpr,
    string? AlternativeNumber,
    int AlternativeNumberCode)
{
    /// <summary>No source values known — used by customers built outside a loader (tests, fixtures).</summary>
    public static readonly RawIdentification None = new(null, null, null, 0);
}
