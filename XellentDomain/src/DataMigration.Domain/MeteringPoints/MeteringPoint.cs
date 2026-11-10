using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;

namespace DataMigration.Domain.MeteringPoints;

/// <summary>
/// A metering point — a place where consumption is measured. Stamdata (no inherent period);
/// the relation to a customer/contract is captured by <see cref="MeteringPointAssignment"/>,
/// not on the MP itself.
///
/// <para>The <see cref="Grid"/> field discriminates between grid-connected electricity MPs
/// (DataHub-registered, <c>Grid</c> populated) and installation-based MPs for water, heat
/// and EV charging (<c>Grid = null</c>). Use <see cref="Commodity"/> for branching when
/// you need to distinguish between commodity types.</para>
///
/// <para><see cref="JointInvoiceGroupId"/> is a foreign key to a
/// <see cref="JointInvoiceGroup"/> when this MP is billed jointly with others; null for
/// stand-alone MPs.</para>
///
/// <para><see cref="BillingExemptionTemplate"/> is non-null when one of the MP's active
/// contracts is on a billing-fee-exempt contract template — it holds the matched Xellent
/// contract-template code (<c>Contracttemplatenum</c>). The criterion (a template-name suffix)
/// is supplied via <c>MapperOptions.BillingExemptionTemplateSuffix</c>; null means no exemption.
/// Consumers read this field instead of re-deriving the suffix match themselves.</para>
/// </summary>
public sealed record MeteringPoint(
    string Identification,
    Address InstallationAddress,
    Commodity Commodity,
    Meter? Meter,
    IReadOnlyList<ContactPoint> ContactPoints,
    string? JointInvoiceGroupId,
    ConnectionStatus ConnectionStatus,
    bool InDelivery,
    GridConnection? Grid,
    DateOnly Start,
    double? EstimatedAnnualVolumeKwh = null,
    string? BillingName = null,
    BuyerInvoicingReferences? BuyerInvoicing = null,
    string? BillingExemptionTemplate = null);
