namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Where a candidate metering point is, as its delivery point records it. Compared with the charger
/// installation's address by <see cref="EvChargerAddressMatch"/> — first by DAR id, then by street
/// and house number.
/// </summary>
/// <param name="DarId">
/// <c>EXU_DELPOINT.ADDRESSID</c>. Blank or all-zero in Xellent for some points; those are not compared.
/// </param>
/// <param name="Street">
/// Street name: <c>EXU_STREET.STREETNAME</c> reached through the delivery point's street code, with
/// <c>EXU_DELPOINT.ADDRESSSTREET</c> as the fallback (blank on every delivery point seen so far).
/// </param>
/// <param name="HouseNumber"><c>EXU_DELPOINT.HOUSENUMSTART</c>.</param>
/// <param name="HouseLetter"><c>EXU_DELPOINT.HOUSELETTERSTART</c>.</param>
internal sealed record EvChargerDelpointAddress(
    string? DarId,
    string? Street,
    int? HouseNumber,
    string? HouseLetter);

/// <summary>
/// One electricity contract part carrying a charger product, and the E17 consumption metering point
/// it points at. The only path from a charger to the electricity it charges from.
/// </summary>
/// <param name="PostalCode">
/// The metering point's delpoint postal code (<c>EXU_DELPOINT.ZIPCODEID</c>). Used only to tell
/// candidates apart when an account has more than one.
/// </param>
/// <param name="Address">
/// The metering point's delpoint address. Tells candidates apart when the postal code alone cannot,
/// and says whether a linked charger sits at its metering point's address.
/// </param>
internal sealed record EvChargerElectricityLinkRow(
    string AccountNumber,
    string MeteringPointId,
    string ProductCode,
    string? PostalCode = null,
    EvChargerDelpointAddress? Address = null);

/// <summary>
/// An electricity link row before the contract-part restore has had its say: the same as
/// <see cref="EvChargerElectricityLinkRow"/> plus the part's log key and end date, so the restored
/// end date can be put on and the date rule re-run in memory. Only built when a restore is on.
/// </summary>
internal sealed record EvChargerLinkPartRow(
    string AccountNumber,
    string MeteringPointId,
    string? PostalCode,
    ContractPartLogKey Key,
    DateTime EndDate,
    EvChargerDelpointAddress? Address = null)
{
    public EvChargerElectricityLinkRow ToLinkRow() => new(AccountNumber, MeteringPointId, Key.ProductNum, PostalCode, Address);
}

/// <summary>
/// One contract part on a charger installation, as read from Xellent. Flat on purpose: the grouping
/// into installations happens in memory, where the ambiguity rule can be applied at the same time.
/// </summary>
/// <param name="Address">
/// The installation's formatted address (<c>EXU_INSTALLATION.INSTADDRESSFORMATTED</c>): street and
/// house number, then optional floor/side, then postal code and city, comma-separated.
/// </param>
/// <param name="PostalCode">
/// The installation's postal code (<c>EXU_INSTALLATION.ZIPCODEID</c>). Compared with the candidate
/// metering points' postal codes when the account offers more than one.
/// </param>
/// <param name="DarId">
/// The installation's DAR id (<c>EXU_INSTALLATION.ADDRESSID</c>). Most charger installations carry
/// none; when both sides have one, it settles the address comparison on its own.
/// </param>
internal sealed record EvChargerPartRow(
    string AccountNumber,
    string ContractNumber,
    string InstallationNumber,
    string? Address,
    string? PostalCode,
    string DeliveryCategory,
    string ProductCode,
    DateTime StartDate,
    DateTime EndDate,
    string? DarId = null);
