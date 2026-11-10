namespace DataMigration.Domain.Customers;

public enum PaymentMedia
{
    None = 0,
    Pbs = 1,
    Fik = 2,
    OioXml = 3,
    MobilePay = 4
}

public static class PaymentMediaExtensions
{
    public static string ToLabel(this PaymentMedia media) => media switch
    {
        PaymentMedia.None => "Ingen",
        PaymentMedia.Pbs => "PBS",
        PaymentMedia.Fik => "FIK",
        PaymentMedia.OioXml => "OIOXML",
        PaymentMedia.MobilePay => "MobilePay",
        _ => $"Unknown ({(int)media})"
    };
}
