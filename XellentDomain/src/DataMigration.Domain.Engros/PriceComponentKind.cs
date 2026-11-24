namespace DataMigration.Domain.Engros;

/// <summary>
/// Classifies what a <see cref="PriceComponent"/> represents. Used by consumers
/// when displaying the settlement basis breakdown.
///
/// <para>Energy is intentionally NOT a kind — its rate varies per concrete date-hour
/// (Monday 14:00 ≠ Tuesday 14:00 because the spot price changes daily) which doesn't
/// fit <see cref="HourlyRates"/>'s 24-clock-hour shape. The energy rate lives on
/// <see cref="ConsumptionHour.EnergyRate"/> instead.</para>
/// </summary>
public enum PriceComponentKind
{
    /// <summary>DSO net-tarif (grid tariff). Often time-of-use; owner is typically the grid operator.</summary>
    GridTariff,

    /// <summary>State levies and taxes (elafgift, PSO).</summary>
    StateTax,

    /// <summary>Supplier margin attached to a specific product.</summary>
    ProductMargin
}
