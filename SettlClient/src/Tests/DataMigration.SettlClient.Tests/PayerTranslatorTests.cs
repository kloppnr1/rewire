using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Products;
using DataMigration.SettlClient.Internal;

namespace DataMigration.SettlClient.Tests;

public class PayerTranslatorTests
{
    [Fact]
    public void Build_maps_payer_fields_one_to_one_to_dto()
    {
        var customer = MakeCustomer();
        var payer = new Payer(
            Identifier: "352639-5790001000099",
            IsPrimary: false,
            ContractNumbers: ["C001", "C002"],
            BuyerInvoicing: new BuyerInvoicingReferences(ElectronicNumber: "5790001000099"),
            InvoiceChannel: InvoiceChannel.OioUbl,
            PaymentChannel: PaymentChannel.EInvoiceFik);

        var dto = PayerTranslator.Build(customer, payer, Guid.NewGuid());

        Assert.Equal("352639-5790001000099", dto.PayerInformation!.PayerIdentifier);
        Assert.Equal("5790001000099", dto.PayerBilling!.ElectronicNumber);
        Assert.False(dto.IsPrimaryCustomerPayer);
        Assert.Equal("Company", dto.PayerBilling.PayerType);
        Assert.Equal("29188440", dto.PayerBilling.VatIdentificationNumber);
        Assert.Equal("test@example.com", dto.PayerBilling.Email);
        Assert.Equal("Rådhusvej", dto.PayerInformation.PostalAddress!.PhysicalAddress!.StreetName);
        Assert.Equal("OioUbl", dto.PayerBilling.InvoiceChannel);
        Assert.Equal("OIOUBL_FIK", dto.PayerBilling.PaymentChannel);
    }

    [Fact]
    public void Build_renders_MobilePay_as_PDF_invoice_channel_when_payer_has_email()
    {
        // Settl's InvoiceChannel enum has no MobilePay value (PaymentChannel does). With an
        // email on the payer the translator downgrades the invoice channel to PDF while
        // keeping payment as MobilePay.
        var customer = MakeCustomer(email: "test@example.com");
        var payer = MakeMobilePayPayer();

        var dto = PayerTranslator.Build(customer, payer, Guid.NewGuid());

        Assert.Equal("PDF", dto.PayerBilling!.InvoiceChannel);
        Assert.Equal("MobilePay", dto.PayerBilling.PaymentChannel);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Build_renders_MobilePay_as_PBS_BS_invoice_channel_when_payer_has_no_email(string? email)
    {
        // Settl rejects PDF without an invoice email (ValidationRules.InvoiceChannel.EmptyInvoiceCcEmail),
        // so the MobilePay downgrade falls back to PBS_BS — the same rule the Domain applies.
        var customer = MakeCustomer(email: email);
        var payer = MakeMobilePayPayer();

        var dto = PayerTranslator.Build(customer, payer, Guid.NewGuid());

        Assert.Equal("PBS_BS", dto.PayerBilling!.InvoiceChannel);
        Assert.Equal("MobilePay", dto.PayerBilling.PaymentChannel);
    }

    [Fact]
    public void Build_sends_only_VAT_for_a_Company_customer_even_when_a_CPR_is_present()
    {
        // Settl rejects a Company payer carrying a NationalIdentificationNumber
        // (ValidationRules.PayerType.MustBeEmptyWhen). The CVR decides: Company → VAT only.
        var customer = MakeCustomer(type: CustomerType.Company, cvr: "29188440", cpr: "0101701234");
        var payer = MakeMobilePayPayer();

        var dto = PayerTranslator.Build(customer, payer, Guid.NewGuid());

        Assert.Equal("Company", dto.PayerBilling!.PayerType);
        Assert.Equal("29188440", dto.PayerBilling.VatIdentificationNumber);
        Assert.Null(dto.PayerBilling.NationalIdentificationNumber);
    }

    [Fact]
    public void Build_sends_only_CPR_for_a_Person_customer_without_CVR()
    {
        var customer = MakeCustomer(type: CustomerType.Person, cvr: null, cpr: "0101701234");
        var payer = MakeMobilePayPayer();

        var dto = PayerTranslator.Build(customer, payer, Guid.NewGuid());

        Assert.Equal("Person", dto.PayerBilling!.PayerType);
        Assert.Equal("0101701234", dto.PayerBilling.NationalIdentificationNumber);
        Assert.Null(dto.PayerBilling.VatIdentificationNumber);
    }

    private static Payer MakeMobilePayPayer() => new(
        Identifier: "352639",
        IsPrimary: true,
        ContractNumbers: ["C001"],
        BuyerInvoicing: null,
        InvoiceChannel: InvoiceChannel.MobilePay,
        PaymentChannel: PaymentChannel.MobilePay);

    private static Customer MakeCustomer(
        CustomerType type = CustomerType.Company,
        string? cvr = "29188440",
        string? cpr = "",
        string? email = "test@example.com")
    {
        var address = new Address(
            StreetName: "Rådhusvej",
            BuildingNumber: "3",
            Floor: string.Empty,
            Apartment: string.Empty,
            PostalCode: "3200",
            City: "Helsinge",
            StreetCode: string.Empty,
            CitySubDivision: string.Empty,
            MunicipalityCode: string.Empty,
            CountryCode: "DK",
            Dar: null!);

        var contact = new ContactInfo(
            Email: email,
            Phone: string.Empty,
            Cellphone: string.Empty,
            Fax: string.Empty);

        var contracts = new List<Contract>
        {
            new(
                Number: "C001",
                Description: "Test",
                Start: new DateOnly(2024, 1, 1),
                Commodity: Commodity.Electricity,
                CustomerNumber: "352639",
                Billing: new BillingSchedule(BillingFrequency.Monthly, "MND"),
                MeteringPoints: [],
                PaymentTerm: null,
                BuyerInvoicing: new BuyerInvoicingReferences(ElectronicNumber: "5798008161063")),
        };

        return new Customer(
            AccountNumber: "352639",
            Name: "Gribskov Kommune",
            AdditionalName: string.Empty,
            Type: type,
            Cvr: cvr,
            Cpr: cpr,
            Address: address,
            Contact: contact,
            Billing: new CustomerBilling(DefaultEan: "5798008161063", Pbs: null, MobilePay: null),
            Contracts: contracts);
    }
}
