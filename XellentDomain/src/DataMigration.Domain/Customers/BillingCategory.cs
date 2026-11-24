namespace DataMigration.Domain.Customers;

public enum BillingCategory
{
    None = 0,
    Settlement = 1,
    Aconto = 2,
    SettlementAndAconto = 3,
    Final = 4,
    PureFinal = 5
}

public static class BillingCategoryExtensions
{
    public static string ToLabel(this BillingCategory category) => category switch
    {
        BillingCategory.None => "Ingen",
        BillingCategory.Settlement => "Opgørelse",
        BillingCategory.Aconto => "Aconto",
        BillingCategory.SettlementAndAconto => "Opgørelse og aconto",
        BillingCategory.Final => "Slutafregning",
        BillingCategory.PureFinal => "Ren slutopgørelse",
        _ => $"Unknown ({(int)category})"
    };
}
