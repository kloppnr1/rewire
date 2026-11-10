namespace DataMigration.OpsCli.Reporting.PaymentMatchMp;

/// <summary>
/// The Xellent scope the MP-list payment match reads. Ported from the dashboard, which always read
/// data area "hol" and electricity deliveries.
/// </summary>
public static class PaymentMatchMpXellent
{
    public const string DataAreaId = "hol";
    public const string DeliveryCategory = "El-ekstern";
}
