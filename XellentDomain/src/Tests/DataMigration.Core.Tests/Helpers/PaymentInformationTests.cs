using DataMigration.Core.Helpers;

namespace DataMigration.Core.Tests.Helpers;

/// <summary>
/// Tests for PaymentInformation helper class — critical for Payer CSV export.
///
/// Expected return values match the Settl API channel codes used in SettlExtractionService:
///   GetPaymentChannel  → "MobilePay" | "BS"    | "OIOUBL_FIK" | "FIK"
///   GetInvoiceChannel  → "MobilePay" | "PBS_BS"| "OioUbl"     | "PDF" | "PBS_BS" (default)
///   GetPayoutChannel   →              "PBS"    | "OioUbl"     | "FIK"
///
/// EndDate sentinel is 1900-01-01 (Xellent's "no end date" convention) — a MobilePay
/// agreement is active when its cancel date equals that sentinel.
///
/// PBS is active when Pbsagreementnum != 0 AND Pbstransactioncode is 1 or 2.
/// Other transaction codes mean the agreement is on file but inactive (cancelled,
/// pending etc.) — treat as no PBS.
/// </summary>
public class PaymentInformationTests
{
    private static readonly DateTime EndDate = new DateTime(1900, 1, 1);
    private static readonly DateTime PastDate = new DateTime(2020, 1, 1);

    private const int PbsActive = 2;
    private const int PbsInactive = 0;

    #region GetPaymentChannel Tests

    [Fact]
    public void GetPaymentChannel_ActiveMobilePay_ReturnsMobilePay()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "");

        Assert.Equal("MobilePay", result);
    }

    [Fact]
    public void GetPaymentChannel_CancelledMobilePay_FallsThroughToPbs()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: PastDate,
            invoiceEan: "");

        Assert.Equal("BS", result);
    }

    [Fact]
    public void GetPaymentChannel_PbsAgreementExists_ReturnsBS()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");

        Assert.Equal("BS", result);
    }

    [Fact]
    public void GetPaymentChannel_PbsAgreementInactive_FallsThroughToFik()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");

        Assert.Equal("FIK", result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GetPaymentChannel_PbsTransactionCodeActive_ReturnsBS(int code)
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: code,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");

        Assert.Equal("BS", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    public void GetPaymentChannel_PbsTransactionCodeInactive_DoesNotReturnBS(int code)
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: code,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");

        Assert.Equal("FIK", result);
    }

    [Fact]
    public void GetPaymentChannel_ZeroPbsAgreementWithEan_ReturnsOioUblFik()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123");

        Assert.Equal("OIOUBL_FIK", result);
    }

    [Fact]
    public void GetPaymentChannel_EanWithWhitespace_ReturnsOioUblFik()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "  1234567890123  ");

        Assert.Equal("OIOUBL_FIK", result);
    }

    [Fact]
    public void GetPaymentChannel_NoPaymentMethod_ReturnsFik()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");

        Assert.Equal("FIK", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPaymentChannel_EmptyOrWhitespaceEan_ReturnsFik(string? ean)
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: ean!);

        Assert.Equal("FIK", result);
    }

    [Fact]
    public void GetPaymentChannel_MobilePayHasHigherPriorityThanBs()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "");

        Assert.Equal("MobilePay", result);
    }

    [Fact]
    public void GetPaymentChannel_BsHasHigherPriorityThanOioUblFik()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123");

        Assert.Equal("BS", result);
    }

    [Fact]
    public void GetPaymentChannel_AllMethodsPresent_MobilePayWins()
    {
        var result = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "1234567890123");

        Assert.Equal("MobilePay", result);
    }

    #endregion

    #region GetInvoiceChannel Tests

    [Fact]
    public void GetInvoiceChannel_ActiveMobilePay_ReturnsMobilePay()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "",
            invoiceEmail: null);

        Assert.Equal("MobilePay", result);
    }

    [Fact]
    public void GetInvoiceChannel_CancelledMobilePay_FallsThroughToPbsBs()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: PastDate,
            invoiceEan: "",
            invoiceEmail: null);

        Assert.Equal("PBS_BS", result);
    }

    [Fact]
    public void GetInvoiceChannel_PbsAgreement_ReturnsPbsBs()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: null);

        Assert.Equal("PBS_BS", result);
    }

    [Fact]
    public void GetInvoiceChannel_PbsAgreementInactive_FallsThroughToDefault()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: null);

        // Inactive PBS => no PBS_BS short-circuit; no EAN/email => default PBS_BS
        Assert.Equal("PBS_BS", result);
    }

    [Fact]
    public void GetInvoiceChannel_PbsAgreementInactive_FallsThroughToOioUbl()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123",
            invoiceEmail: null);

        Assert.Equal("OioUbl", result);
    }

    [Fact]
    public void GetInvoiceChannel_PbsAgreementInactive_FallsThroughToPdf()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "test@example.com");

        Assert.Equal("PDF", result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GetInvoiceChannel_PbsTransactionCodeActive_ReturnsPbsBs(int code)
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: code,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123",
            invoiceEmail: "test@example.com");

        Assert.Equal("PBS_BS", result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    public void GetInvoiceChannel_PbsTransactionCodeInactive_DoesNotReturnPbsBs(int code)
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: code,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123",
            invoiceEmail: null);

        Assert.Equal("OioUbl", result);
    }

    [Fact]
    public void GetInvoiceChannel_EanNumber_ReturnsOioUbl()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123",
            invoiceEmail: null);

        Assert.Equal("OioUbl", result);
    }

    [Fact]
    public void GetInvoiceChannel_Email_ReturnsPdf()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "test@example.com");

        Assert.Equal("PDF", result);
    }

    [Fact]
    public void GetInvoiceChannel_NoMethod_DefaultsToPbsBs()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: null);

        Assert.Equal("PBS_BS", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetInvoiceChannel_EmptyEmail_FallsThroughToPbsBs(string? email)
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: email);

        Assert.Equal("PBS_BS", result);
    }

    [Fact]
    public void GetInvoiceChannel_MobilePayHasHigherPriorityThanPbsBs()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "",
            invoiceEmail: null);

        Assert.Equal("MobilePay", result);
    }

    [Fact]
    public void GetInvoiceChannel_PbsBsHasHigherPriorityThanOioUbl()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123",
            invoiceEmail: null);

        Assert.Equal("PBS_BS", result);
    }

    [Fact]
    public void GetInvoiceChannel_OioUblHasHigherPriorityThanPdf()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "1234567890123",
            invoiceEmail: "test@example.com");

        Assert.Equal("OioUbl", result);
    }

    [Fact]
    public void GetInvoiceChannel_PdfHasHigherPriorityThanDefault()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "test@example.com");

        Assert.Equal("PDF", result);
    }

    [Fact]
    public void GetInvoiceChannel_AllMethodsPresent_MobilePayWins()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "1234567890123",
            invoiceEmail: "test@example.com");

        Assert.Equal("MobilePay", result);
    }

    [Fact]
    public void GetInvoiceChannel_EanWithWhitespace_ReturnsOioUbl()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "  1234567890123  ",
            invoiceEmail: null);

        Assert.Equal("OioUbl", result);
    }

    [Fact]
    public void GetInvoiceChannel_PbsAndEmail_PbsBsWins()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 12345,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "test@example.com");

        Assert.Equal("PBS_BS", result);
    }

    [Fact]
    public void GetInvoiceChannel_NullMobilePayCancelDate_DoesNotReturnMobilePay()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "test@example.com");

        Assert.Equal("PDF", result);
    }

    [Fact]
    public void GetInvoiceChannel_MobilePayWithWrongEndDate_DoesNotReturnMobilePay()
    {
        var result = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "MP123456",
            mobilePayAgreementCancelDate: new DateTime(2154, 12, 30),
            invoiceEan: "",
            invoiceEmail: "test@example.com");

        Assert.Equal("PDF", result);
    }

    #endregion

    #region GetPayoutChannel Tests

    [Fact]
    public void GetPayoutChannel_PbsAgreement_ReturnsPbs()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 12345, pbsTransactionCode: PbsActive, invoiceEan: "");
        Assert.Equal("PBS", result);
    }

    [Fact]
    public void GetPayoutChannel_PbsAgreementInactive_FallsThroughToFik()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 12345, pbsTransactionCode: PbsInactive, invoiceEan: "");
        Assert.Equal("FIK", result);
    }

    [Fact]
    public void GetPayoutChannel_PbsAgreementInactive_FallsThroughToOioUbl()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 12345, pbsTransactionCode: PbsInactive, invoiceEan: "1234567890123");
        Assert.Equal("OioUbl", result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GetPayoutChannel_PbsTransactionCodeActive_ReturnsPbs(int code)
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 12345, pbsTransactionCode: code, invoiceEan: "");
        Assert.Equal("PBS", result);
    }

    [Fact]
    public void GetPayoutChannel_EanNumber_ReturnsOioUbl()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 0, pbsTransactionCode: PbsInactive, invoiceEan: "1234567890123");
        Assert.Equal("OioUbl", result);
    }

    [Fact]
    public void GetPayoutChannel_NoMethod_ReturnsFik()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 0, pbsTransactionCode: PbsInactive, invoiceEan: "");
        Assert.Equal("FIK", result);
    }

    [Fact]
    public void GetPayoutChannel_PbsHasHigherPriorityThanOioUbl()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 12345, pbsTransactionCode: PbsActive, invoiceEan: "1234567890123");
        Assert.Equal("PBS", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetPayoutChannel_EmptyEan_ReturnsFik(string? ean)
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 0, pbsTransactionCode: PbsInactive, invoiceEan: ean!);
        Assert.Equal("FIK", result);
    }

    [Fact]
    public void GetPayoutChannel_EanWithWhitespace_ReturnsOioUbl()
    {
        var result = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 0, pbsTransactionCode: PbsInactive, invoiceEan: "  1234567890123  ");
        Assert.Equal("OioUbl", result);
    }

    #endregion

    #region Real-World Scenarios

    [Fact]
    public void RealWorld_ActiveMobilePay_AllMethodsConsistent()
    {
        var paymentChannel = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "MP987654",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "");
        var invoiceChannel = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "MP987654",
            mobilePayAgreementCancelDate: EndDate,
            invoiceEan: "",
            invoiceEmail: "customer@example.com");

        Assert.Equal("MobilePay", paymentChannel);
        Assert.Equal("MobilePay", invoiceChannel);
    }

    [Fact]
    public void RealWorld_CompanyWithPbsAndEmail_CorrectChannels()
    {
        var paymentChannel = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 54321,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");
        var invoiceChannel = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 54321,
            pbsTransactionCode: PbsActive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "company@example.com");
        var payoutChannel = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 54321, pbsTransactionCode: PbsActive, invoiceEan: "");

        Assert.Equal("BS", paymentChannel);
        Assert.Equal("PBS_BS", invoiceChannel);
        Assert.Equal("PBS", payoutChannel);
    }

    [Fact]
    public void RealWorld_PersonWithNoPaymentSetup_DefaultChannels()
    {
        var paymentChannel = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");
        var invoiceChannel = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "person@example.com");
        var payoutChannel = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 0, pbsTransactionCode: PbsInactive, invoiceEan: "");

        Assert.Equal("FIK", paymentChannel);
        Assert.Equal("PDF", invoiceChannel);
        Assert.Equal("FIK", payoutChannel);
    }

    [Fact]
    public void RealWorld_PublicCompanyWithEan_OioUblChannels()
    {
        var paymentChannel = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "5798000123456");
        var invoiceChannel = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 0,
            pbsTransactionCode: PbsInactive,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "5798000123456",
            invoiceEmail: null);
        var payoutChannel = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 0, pbsTransactionCode: PbsInactive, invoiceEan: "5798000123456");

        Assert.Equal("OIOUBL_FIK", paymentChannel);
        Assert.Equal("OioUbl", invoiceChannel);
        Assert.Equal("OioUbl", payoutChannel);
    }

    [Fact]
    public void RealWorld_PbsOnFileButCancelled_TreatedAsNoPbs()
    {
        // Real Xellent scenario: customer has Pbsagreementnum but Pbstransactioncode=0
        // (e.g. cancelled). Migration must NOT route them to BS/PBS_BS/PBS.
        var paymentChannel = PaymentInformation.GetPaymentChannel(
            pbsAgreement: 99999,
            pbsTransactionCode: 0,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "");
        var invoiceChannel = PaymentInformation.GetInvoiceChannel(
            pbsAgreement: 99999,
            pbsTransactionCode: 0,
            mobilePayAgreement: "",
            mobilePayAgreementCancelDate: null,
            invoiceEan: "",
            invoiceEmail: "person@example.com");
        var payoutChannel = PaymentInformation.GetPayoutChannel(
            pbsAgreement: 99999, pbsTransactionCode: 0, invoiceEan: "");

        Assert.Equal("FIK", paymentChannel);
        Assert.Equal("PDF", invoiceChannel);
        Assert.Equal("FIK", payoutChannel);
    }

    #endregion
}
