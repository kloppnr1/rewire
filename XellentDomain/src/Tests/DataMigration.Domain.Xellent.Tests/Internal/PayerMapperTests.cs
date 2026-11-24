using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent.Internal;

namespace DataMigration.Domain.Xellent.Tests.Internal;

public class PayerMapperTests
{
    [Fact]
    public void No_contract_ean_returns_only_default_payer()
    {
        var customer = MakeCustomer(customerLevelEan: "5790001000001");
        var contracts = MakeContracts(["5790001000001", "5790001000001"]);

        var payers = PayerMapper.Build(customer, contracts);

        Assert.Single(payers);
        Assert.Equal("352639", payers[0].Identifier);
        Assert.Equal("5790001000001", payers[0].BuyerInvoicing?.ElectronicNumber);
        Assert.True(payers[0].IsPrimary);
        Assert.Equal(new[] { "C001", "C002" }, payers[0].ContractNumbers);
    }

    [Fact]
    public void Distinct_contract_eans_fan_out_one_payer_per_unique_ean()
    {
        var customer = MakeCustomer(customerLevelEan: "5798008161063");
        var contracts = MakeContracts(["5798008161063", "5798008160950", "5790001266844", null, ""]);

        var payers = PayerMapper.Build(customer, contracts);

        // Default + 2 extras (5798008160950 + 5790001266844). 5798008161063 == customer-level → covered by default.
        Assert.Equal(3, payers.Count);
        Assert.Equal("352639-1", payers[0].Identifier);
        Assert.True(payers[0].IsPrimary);
        Assert.Equal("5798008161063", payers[0].BuyerInvoicing?.ElectronicNumber);

        var extras = payers.Skip(1).ToList();
        Assert.All(extras, p => Assert.False(p.IsPrimary));

        // First-occurrence order is preserved; default occupies -1, extras start at -2
        Assert.Equal("352639-2", extras[0].Identifier);
        Assert.Equal("5798008160950", extras[0].BuyerInvoicing?.ElectronicNumber);
        Assert.Equal("352639-3", extras[1].Identifier);
        Assert.Equal("5790001266844", extras[1].BuyerInvoicing?.ElectronicNumber);

        // Contracts with default-matching EAN + null/empty EAN belong to default
        Assert.Equal(new[] { "C001", "C004", "C005" }, payers[0].ContractNumbers);
        Assert.Equal(new[] { "C002" }, extras[0].ContractNumbers);
        Assert.Equal(new[] { "C003" }, extras[1].ContractNumbers);
    }

    [Fact]
    public void Null_customer_level_ean_still_emits_default_plus_contract_eans()
    {
        var customer = MakeCustomer(customerLevelEan: null);
        var contracts = MakeContracts(["5790001000001", "5790001000002"]);

        var payers = PayerMapper.Build(customer, contracts);

        Assert.Equal(3, payers.Count);
        Assert.True(payers[0].IsPrimary);
        Assert.Null(payers[0].BuyerInvoicing);  // default has no EAN, no rep contract → null entire reference blob
        Assert.Empty(payers[0].ContractNumbers);  // both contracts have non-null EAN that differs from default's null
        Assert.Contains(payers, p => p.BuyerInvoicing?.ElectronicNumber == "5790001000001");
        Assert.Contains(payers, p => p.BuyerInvoicing?.ElectronicNumber == "5790001000002");
    }

    [Fact]
    public void Uniform_reference_fields_land_on_payer()
    {
        var customer = MakeCustomer(customerLevelEan: "5790001000001");
        var contracts = new[]
        {
            MakeContract("C001", "5790001000001", orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
            MakeContract("C002", "5790001000001", orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
        };

        var payers = PayerMapper.Build(customer, contracts);

        var refs = Assert.Single(payers).BuyerInvoicing;
        Assert.Equal("5790001000001", refs?.ElectronicNumber);
        Assert.Equal("ORD", refs?.OrderIdentifier);
        Assert.Equal("AGR", refs?.AgreementIdentifier);
        Assert.Equal("REF", refs?.BuyerReferenceIdentifier);
    }

    [Fact]
    public void Any_divergent_field_removes_all_reference_fields_from_payer()
    {
        // OrderIdentifier diverges (ORD1 vs ORD2); AgreementIdentifier and BuyerReferenceIdentifier
        // are uniform. Set rule: since any one diverges, none of the three land on the Payer.
        var customer = MakeCustomer(customerLevelEan: "5790001000001");
        var contracts = new[]
        {
            MakeContract("C001", "5790001000001", orderId: "ORD1", agreementId: "AGR", buyerRef: "REF"),
            MakeContract("C002", "5790001000001", orderId: "ORD2", agreementId: "AGR", buyerRef: "REF"),
        };

        var refs = Assert.Single(PayerMapper.Build(customer, contracts)).BuyerInvoicing;

        Assert.Null(refs?.OrderIdentifier);          // diverges → not on payer
        Assert.Null(refs?.AgreementIdentifier);      // uniform but set diverged → also not on payer
        Assert.Null(refs?.BuyerReferenceIdentifier); // uniform but set diverged → also not on payer
    }

    [Fact]
    public void Blank_on_some_contracts_counts_as_divergent_and_removes_all_reference_fields_from_payer()
    {
        // OrderIdentifier is blank on C002 (mixed blank/non-blank = divergent).
        // AgreementIdentifier and BuyerReferenceIdentifier are uniform. Set rule: none on payer.
        var customer = MakeCustomer(customerLevelEan: "5790001000001");
        var contracts = new[]
        {
            MakeContract("C001", "5790001000001", orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
            MakeContract("C002", "5790001000001", orderId: null, agreementId: "AGR", buyerRef: "REF"),
        };

        var refs = Assert.Single(PayerMapper.Build(customer, contracts)).BuyerInvoicing;

        Assert.Null(refs?.OrderIdentifier);          // blank on C002 → divergent → not on payer
        Assert.Null(refs?.AgreementIdentifier);      // uniform but set diverged → also not on payer
        Assert.Null(refs?.BuyerReferenceIdentifier); // uniform but set diverged → also not on payer
    }

    [Fact]
    public void Field_blank_everywhere_is_not_divergent_and_does_not_affect_other_fields()
    {
        // AgreementIdentifier is blank on both contracts (absent, not divergent).
        // OrderIdentifier and BuyerReferenceIdentifier are uniform — they land on payer.
        var customer = MakeCustomer(customerLevelEan: "5790001000001");
        var contracts = new[]
        {
            MakeContract("C001", "5790001000001", orderId: "ORD", agreementId: null, buyerRef: "REF"),
            MakeContract("C002", "5790001000001", orderId: "ORD", agreementId: null, buyerRef: "REF"),
        };

        var refs = Assert.Single(PayerMapper.Build(customer, contracts)).BuyerInvoicing;

        Assert.Equal("ORD", refs?.OrderIdentifier);          // uniform → on payer
        Assert.Null(refs?.AgreementIdentifier);              // blank everywhere → absent, not on payer
        Assert.Equal("REF", refs?.BuyerReferenceIdentifier); // uniform → on payer
    }

    [Fact]
    public void No_ean_payer_drops_reference_fields_even_when_contracts_populate_them()
    {
        // Customer with no EAN → default payer has no electronic number. The contracts still carry
        // order/agreement/buyer references, but without an EAN there is no e-invoicing, so they
        // must be ignored entirely (no reference blob on the payer).
        var customer = MakeCustomer(customerLevelEan: null);
        var contracts = new[]
        {
            MakeContract("C001", ean: null, orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
            MakeContract("C002", ean: null, orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
        };

        var payers = PayerMapper.Build(customer, contracts);

        var payer = Assert.Single(payers);
        Assert.True(payer.IsPrimary);
        Assert.Null(payer.BuyerInvoicing);   // no EAN → references dropped despite contract values
    }

    [Fact]
    public void Extra_payer_with_ean_gets_oioubl_channels_when_default_has_no_ean()
    {
        // Customer's default EAN is null → default payer gets PBS_BS/FIK fallback.
        // Payer2 has its own EAN → must get OioUbl/EInvoiceFik (not inherit default's channels).
        var customer = MakeCustomer(customerLevelEan: null);
        var contracts = MakeContracts(["5790001000001"]);

        var payers = PayerMapper.Build(customer, contracts);

        Assert.Equal(2, payers.Count);
        Assert.Equal(InvoiceChannel.Pdf, payers[0].InvoiceChannel);     // default: no EAN, email present → PDF
        Assert.Equal(PaymentChannel.Fik, payers[0].PaymentChannel);
        Assert.Equal(InvoiceChannel.OioUbl, payers[1].InvoiceChannel);  // Payer2: has EAN → OioUbl
        Assert.Equal(PaymentChannel.EInvoiceFik, payers[1].PaymentChannel);
    }

    [Fact]
    public void Payer_with_ean_inherits_pbs_channel_when_customer_has_pbs()
    {
        // PBS trumps EAN — even extra payer with EAN gets PBS_BS/BS when customer has PBS.
        var customer = MakeCustomer(customerLevelEan: null, pbs: new PbsAgreement(123456));
        var contracts = MakeContracts(["5790001000001"]);

        var payers = PayerMapper.Build(customer, contracts);

        Assert.Equal(2, payers.Count);
        Assert.All(payers, p => Assert.Equal(InvoiceChannel.PbsBs, p.InvoiceChannel));
        Assert.All(payers, p => Assert.Equal(PaymentChannel.Bs, p.PaymentChannel));
    }

    private static Customer MakeCustomer(string? customerLevelEan, PbsAgreement? pbs = null, MobilePaySubscription? mobilePay = null)
    {
        var address = new Address(
            StreetName: "Rådhusvej", BuildingNumber: "3", Floor: null, Apartment: null,
            PostalCode: "3200", City: "Helsinge", StreetCode: null, CitySubDivision: null,
            MunicipalityCode: null, CountryCode: "DK", Dar: null);

        var contact = new ContactInfo("test@example.com", null, null, null);

        var billing = new CustomerBilling(
            DefaultEan: customerLevelEan,
            Pbs: pbs,
            MobilePay: mobilePay);

        return new Customer(
            AccountNumber: "352639",
            Name: "Gribskov Kommune",
            AdditionalName: null,
            Type: CustomerType.Company,
            Cvr: "29188440",
            Cpr: null,
            Address: address,
            Contact: contact,
            Billing: billing,
            Contracts: Array.Empty<Contract>());
    }

    private static IReadOnlyList<Contract> MakeContracts(string?[] contractEans)
    {
        var list = new List<Contract>(contractEans.Length);
        int i = 0;
        foreach (var ean in contractEans)
        {
            list.Add(new Contract(
                Number: $"C{++i:000}",
                Description: "Test",
                Start: new DateOnly(2024, 1, 1),
                Commodity: Commodity.Electricity,
                CustomerNumber: "352639",
                Billing: new BillingSchedule(BillingFrequency.Monthly, "MND"),
                MeteringPoints: Array.Empty<MeteringPointAssignment>(),
                PaymentTerm: null,
                BuyerInvoicing: ean is null ? null : new BuyerInvoicingReferences(ElectronicNumber: ean)));
        }
        return list;
    }

    private static Contract MakeContract(string number, string? ean, string? orderId, string? agreementId, string? buyerRef)
        => new(
            Number: number,
            Description: "Test",
            Start: new DateOnly(2024, 1, 1),
            Commodity: Commodity.Electricity,
            CustomerNumber: "352639",
            Billing: new BillingSchedule(BillingFrequency.Monthly, "MND"),
            MeteringPoints: Array.Empty<MeteringPointAssignment>(),
            PaymentTerm: null,
            BuyerInvoicing: new BuyerInvoicingReferences(ean, orderId, agreementId, buyerRef));
}
