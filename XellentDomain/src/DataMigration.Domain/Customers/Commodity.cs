namespace DataMigration.Domain.Customers;

/// <summary>
/// What is being delivered. <see cref="EvCharging"/> represents electric-vehicle charging
/// (Xellent <c>Charge</c>/<c>Charge_Abo</c>) — installation-based like water and heat,
/// distinct from grid-connected <see cref="Electricity"/>.
/// </summary>
public enum Commodity
{
    Electricity,
    Water,
    Heat,
    EvCharging
}
