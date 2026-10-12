namespace DataMigration.Domain.Customers;

/// <summary>
/// What a charger contract part bills for. Xellent keeps the two apart by delivery category —
/// <c>Charge</c> is the consumption itself, <c>Charge_Abo</c> the subscription that goes with the
/// charging box — and a reader deciding what to do with a charger needs to see which of the two
/// (or both) the customer actually holds.
/// </summary>
public enum EvChargerPartKind
{
    /// <summary>Consumption part (Xellent delivery category <c>Charge</c>).</summary>
    Consumption,

    /// <summary>Subscription part (Xellent delivery category <c>Charge_Abo</c>).</summary>
    Subscription
}

/// <summary>
/// One contract part on a charger installation: what it bills for, which product carries it, and
/// over which period.
///
/// <para><see cref="DeliveryCategory"/> is the raw Xellent value and <see cref="Kind"/> the same
/// fact as a decision. Both are carried for the same reason <c>XellentPbsContract</c> carries a
/// source kind beside a source delivery category: reports print the raw value, code branches on
/// the enum.</para>
/// </summary>
/// <param name="DeliveryCategory">Raw Xellent delivery category — <c>Charge</c> or <c>Charge_Abo</c>.</param>
/// <param name="Kind">The same distinction as an enum.</param>
/// <param name="ProductCode">Xellent <c>PRODUCTNUM</c> on the contract part.</param>
/// <param name="StartDate">Inclusive start of the part.</param>
/// <param name="EndDate">
/// Inclusive end of the part, or <c>null</c> where Xellent's sentinel says there is none. Not a
/// half-open <c>Period</c>: the dates come straight off the contract part, where the end date is
/// the last day in force, and translating them would invite an off-by-one in every report.
/// </param>
public sealed record EvChargerPart(
    string DeliveryCategory,
    EvChargerPartKind Kind,
    string ProductCode,
    DateOnly StartDate,
    DateOnly? EndDate);

/// <summary>
/// A charger installation (ladestander) on a customer's own charger contract, with the parts that
/// were in force at the extraction's reference date.
///
/// <para>The charger sits on a contract of its own with no reference back to the electricity
/// contract it belongs to — no shared main contract, no shared meter, and 91% of charger
/// installations carry no DAR address. The link is made from the electricity side instead, through
/// the charger products <c>V LadVariP</c>/<c>V LadVarUA</c>, and lives in the two lookups on
/// <c>XellentLoadResult</c> rather than on this record: whether a charger could be tied to a
/// metering point is a property of the account's data, not of the charger. When the account offers
/// several candidate metering points, the installation's postal code is compared with theirs, and
/// the address when the postal code leaves more than one; a single match ties the charger, anything
/// else leaves it on the account.</para>
/// </summary>
/// <param name="InstallationNumber">Xellent <c>EXU_INSTALLATION.INSTNUM</c>.</param>
/// <param name="CustomerAccount">Account the charger contract hangs under (Xellent Accountnum, not a Settl customer number).</param>
/// <param name="ContractNumber">The charger's own contract.</param>
/// <param name="Address">
/// Installation address as Xellent formats it (<c>INSTADDRESSFORMATTED</c>). One string; the
/// postal code Xellent keeps beside it (<c>ZIPCODEID</c>) is used for the metering-point tie-break
/// and is not carried here.
/// </param>
/// <param name="Parts">Parts in force at the reference date, ordered by start date.</param>
/// <param name="AddressMatchesMeteringPoint">
/// True when the charger is tied to a metering point and the installation's address was confirmed
/// to be that metering point's — by DAR id when both sides carry one, else by street and house
/// number. Computed for every tied charger, whatever settled the tie. False when the addresses
/// differ, when either side is too blank to compare, and always for a charger left on its account.
/// Several chargers on one metering point are a known arrangement when every one of them carries
/// this flag (one household, several charging boxes); without it, the consumer has to decide.
/// </param>
public sealed record EvCharger(
    string InstallationNumber,
    string CustomerAccount,
    string ContractNumber,
    string? Address,
    IReadOnlyList<EvChargerPart> Parts,
    bool AddressMatchesMeteringPoint = false)
{
    /// <summary>True when the installation carries at least one <c>Charge</c> part.</summary>
    public bool HasConsumption => Parts.Any(p => p.Kind == EvChargerPartKind.Consumption);

    /// <summary>True when the installation carries at least one <c>Charge_Abo</c> part.</summary>
    public bool HasSubscription => Parts.Any(p => p.Kind == EvChargerPartKind.Subscription);
}
