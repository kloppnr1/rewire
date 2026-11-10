namespace DataMigration.Migration.Services.MeteringPointStrategy;

/// <summary>
/// Categorises Xellent <c>DeliveryCategory</c> for the create pipeline. The historic
/// strategy-pattern wrapper has been retired (XellentDomainMapper now branches internally);
/// only the category set itself survives as a shared lookup used by
/// <see cref="SettlMappingService"/> and ops-runners that need to distinguish electricity
/// MPs from installation-based ones.
/// </summary>
public static class MeteringPointStrategyFactory
{
    /// <summary>
    /// Delivery categories that use the delpoint-based (electricity) API path.
    /// </summary>
    public static readonly HashSet<string> DelpointCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "El-ekstern", "El-Ekstern", "El", "FI", "Tele", "Supply", "Service"
    };
}
