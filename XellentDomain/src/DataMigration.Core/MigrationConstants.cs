namespace DataMigration.Core;

/// <summary>
/// Centralized constants used across the data migration solution.
/// </summary>
public static class MigrationConstants
{
    // Xellent database identifiers
    public const string DataAreaId = "hol";
    public const int AddressTableId = 2303;

    // Country code
    public const string CountryCode = "DK";

    // Delivery categories (Xellent)
    public const string DeliveryCategoryElectricity = "El-ekstern";
    public const string DeliveryCategoryWater = "Vand";
    public const string DeliveryCategoryHeating = "Varme";
    public const string DeliveryCategoryCharge = "Charge";

    /// <summary>
    /// Subscription that goes with a charger installation. Sits beside <see cref="DeliveryCategoryCharge"/>
    /// on the same charger contract; the EV-charging extraction reads only <c>Charge</c>, so this value
    /// is used by the charger↔metering-point lookup alone.
    /// </summary>
    public const string DeliveryCategoryChargeSubscription = "Charge_Abo";

    // Utility installation markers (EXU_UTILITYINSTALLATION.TECHNICGROUP1)
    public const string WaterStoppedMarkerPrefix = "Pilot 1";

    // Settl API commodity types
    public static string MapCommodityType(string deliveryCategory) => deliveryCategory switch
    {
        "Vand" => "Water",
        "Varme" => "Heating",
        _ => "Electricity"
    };

    // Sentinel dates (Xellent conventions)
    // Different tables use different sentinel values for "no end date"
    public static readonly DateTime EndDateNull = new(1900, 1, 1);       // Used in contracts, contract parts, meter types
    public static readonly DateTime EndDateFuture = new(2154, 12, 31);   // Used in billing methods, time series versions

    // Settl API sentinel: "no end date" for metering point associations
    public static readonly DateOnly MeteringPointEndDate = new(9999, 12, 31);
}
