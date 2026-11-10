namespace DataMigration.Domain.Customers;

/// <summary>
/// How often a customer is billed. <see cref="Other"/> covers special-case plans
/// (e.g. quarterly with monthly on-account, energy-rebate plans) that don't map
/// cleanly to a single frequency — consumers needing the precise billing rhythm
/// inspect <see cref="BillingSchedule.PlanCode"/>.
/// </summary>
public enum BillingFrequency
{
    Monthly,
    Quarterly,
    SemiAnnually,
    Annually,
    Other
}
