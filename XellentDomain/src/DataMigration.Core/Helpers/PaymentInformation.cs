namespace DataMigration.Core.Helpers;

public static class PaymentInformation
{
    private static readonly DateTime EndDate = new DateTime(1900, 1, 1);

    /// <summary>
    /// PBS is active when an agreement number is present AND the transaction code is 1 or 2.
    /// Both codes mean "active" and are interchangeable for every downstream channel decision;
    /// other codes mean the agreement is on file but inactive (cancelled, pending, etc.).
    /// </summary>
    public static bool IsPbsActive(int pbsAgreement, int pbsTransactionCode)
        => pbsAgreement != 0 && (pbsTransactionCode == 1 || pbsTransactionCode == 2);

    public static string GetPaymentChannel(
        int pbsAgreement,
        int pbsTransactionCode,
        string mobilePayAgreement,
        DateTime? mobilePayAgreementCancelDate,
        string invoiceEan)
    {
        if (!string.IsNullOrEmpty(mobilePayAgreement) && mobilePayAgreementCancelDate == EndDate)
            return "MobilePay";

        if (IsPbsActive(pbsAgreement, pbsTransactionCode))
            return "BS";

        if (!string.IsNullOrEmpty(invoiceEan?.Trim()))
            return "OIOUBL_FIK";

        return "FIK";
    }

    public static string GetInvoiceChannel(
        int pbsAgreement,
        int pbsTransactionCode,
        string mobilePayAgreement,
        DateTime? mobilePayAgreementCancelDate,
        string invoiceEan,
        string? invoiceEmail)
    {
        if (!string.IsNullOrEmpty(mobilePayAgreement) && mobilePayAgreementCancelDate == EndDate)
            return "MobilePay";

        if (IsPbsActive(pbsAgreement, pbsTransactionCode))
            return "PBS_BS";

        if (!string.IsNullOrEmpty(invoiceEan?.Trim()))
            return "OioUbl";

        if (!string.IsNullOrEmpty(invoiceEmail?.Trim()))
            return "PDF";

        return "PBS_BS";
    }

    public static string GetPayoutChannel(int pbsAgreement, int pbsTransactionCode, string invoiceEan)
    {
        if (IsPbsActive(pbsAgreement, pbsTransactionCode))
            return "PBS";
        if (!string.IsNullOrEmpty(invoiceEan?.Trim()))
            return "OioUbl";
        return "FIK";
    }
}
