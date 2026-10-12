using DataMigration.Domain.Customers;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Maps Xellent <c>Exu_BillingPlan</c> code to <see cref="BillingFrequency"/>.
/// Codes from `.claude/skills/xellent-products` (DATAAREAID='hol'). Unknown codes
/// fall through to <see cref="BillingFrequency.Other"/> — caller can inspect the
/// raw <c>PlanCode</c> on <see cref="BillingSchedule"/> for special-case handling.
/// </summary>
internal static class BillingFrequencyMapper
{
    public static BillingFrequency Map(string? planCode) => planCode?.Trim() switch
    {
        "Måned." or "Md+MdAC" or "MD+3MdAC" => BillingFrequency.Monthly,
        "Kvartal" or "KvartalAC" => BillingFrequency.Quarterly,
        "½Årlig" or "½År_Bagud" => BillingFrequency.SemiAnnually,
        "Årlig" => BillingFrequency.Annually,
        _ => BillingFrequency.Other
    };
}
