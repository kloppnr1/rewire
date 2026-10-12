using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.Domain.Xellent;

namespace DataMigration.Domain.Xellent.Tests;

/// <summary>
/// Invariant tests for ApplyMpBillingOverrides: Order/Agreement/BuyerRef are a set —
/// they are either all on the Payer or all pushed to individual MPs, never split across both.
/// </summary>
public class XellentDomainMapper_BillingOverrideTests
{
    // ── Regression guard ──────────────────────────────────────────────────────────────────────
    // The original bug: per-field placement let OrderIdentifier land on MP while
    // AgreementIdentifier and BuyerReferenceIdentifier (both uniform) stayed on the Payer.
    // This verifies the set rule: when OrderId diverges, all three go to MP.

    [Fact]
    public void Regression_divergent_order_pushes_all_three_fields_to_MP_not_just_order()
    {
        // Payer was built with the set rule: OrderId diverged → payer carries none of the three.
        var payer = MakePayer("P1", ean: "5790001000001", contracts: ["C001", "C002"],
            orderId: null, agreementId: null, buyerRef: null);

        var customer = MakeCustomer(payer, [
            MakeContract("C001", mpId: "MP1", orderId: "ORD1", agreementId: "AGR", buyerRef: "REF"),
            MakeContract("C002", mpId: "MP2", orderId: "ORD2", agreementId: "AGR", buyerRef: "REF"),
        ]);

        var mps = new[] { MakeMp("MP1"), MakeMp("MP2") };
        var result = XellentDomainMapper.ApplyMpBillingOverrides(mps, [customer]);

        // MP1: gets OrderId from its contract (ORD1) + AgreementId + BuyerRef (payer null → pushed)
        var mp1Refs = result.Single(m => m.Identification == "MP1").BuyerInvoicing;
        Assert.Equal("ORD1", mp1Refs?.OrderIdentifier);
        Assert.Equal("AGR", mp1Refs?.AgreementIdentifier);
        Assert.Equal("REF", mp1Refs?.BuyerReferenceIdentifier);

        // MP2: same but ORD2
        var mp2Refs = result.Single(m => m.Identification == "MP2").BuyerInvoicing;
        Assert.Equal("ORD2", mp2Refs?.OrderIdentifier);
        Assert.Equal("AGR", mp2Refs?.AgreementIdentifier);
        Assert.Equal("REF", mp2Refs?.BuyerReferenceIdentifier);
    }

    // ── Set on payer → MPs inherit, get no override ───────────────────────────────────────────

    [Fact]
    public void When_payer_carries_all_three_fields_MPs_get_no_override()
    {
        var payer = MakePayer("P1", ean: "5790001000001", contracts: ["C001", "C002"],
            orderId: "ORD", agreementId: "AGR", buyerRef: "REF");

        var customer = MakeCustomer(payer, [
            MakeContract("C001", mpId: "MP1", orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
            MakeContract("C002", mpId: "MP2", orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
        ]);

        var mps = new[] { MakeMp("MP1"), MakeMp("MP2") };
        var result = XellentDomainMapper.ApplyMpBillingOverrides(mps, [customer]);

        // Payer carries all three → MPs have no override (they inherit from payer)
        Assert.Null(result.Single(m => m.Identification == "MP1").BuyerInvoicing);
        Assert.Null(result.Single(m => m.Identification == "MP2").BuyerInvoicing);
    }

    // ── EAN gate ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Payer_without_EAN_pushes_no_reference_fields_to_MPs()
    {
        // Payer has no EAN → BuyerInvoicing is null (returned null from BuildPayerReferences)
        var payer = MakePayer("P1", ean: null, contracts: ["C001"],
            orderId: null, agreementId: null, buyerRef: null);

        var customer = MakeCustomer(payer, [
            MakeContract("C001", mpId: "MP1", orderId: "ORD", agreementId: "AGR", buyerRef: "REF"),
        ]);

        var mps = new[] { MakeMp("MP1") };
        var result = XellentDomainMapper.ApplyMpBillingOverrides(mps, [customer]);

        // No EAN → no e-invoicing → nothing pushed to MP
        Assert.Null(result.Single().BuyerInvoicing);
    }

    // ── Partial absent fields don't create splits ─────────────────────────────────────────────

    [Fact]
    public void Absent_agreement_id_on_contract_does_not_prevent_order_and_buyerref_from_being_pushed()
    {
        // All three diverged (or were absent) → payer carries none.
        // Contract has OrderId and BuyerRef but no AgreementId.
        var payer = MakePayer("P1", ean: "5790001000001", contracts: ["C001"],
            orderId: null, agreementId: null, buyerRef: null);

        var customer = MakeCustomer(payer, [
            MakeContract("C001", mpId: "MP1", orderId: "ORD", agreementId: null, buyerRef: "REF"),
        ]);

        var mps = new[] { MakeMp("MP1") };
        var result = XellentDomainMapper.ApplyMpBillingOverrides(mps, [customer]);

        var refs = result.Single().BuyerInvoicing;
        Assert.Equal("ORD", refs?.OrderIdentifier);
        Assert.Null(refs?.AgreementIdentifier);      // absent on contract → not pushed
        Assert.Equal("REF", refs?.BuyerReferenceIdentifier);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static Payer MakePayer(string id, string? ean, IReadOnlyList<string> contracts,
        string? orderId, string? agreementId, string? buyerRef)
        => new(
            Identifier: id,
            IsPrimary: true,
            ContractNumbers: contracts,
            BuyerInvoicing: ean is null ? null : new BuyerInvoicingReferences(ean, orderId, agreementId, buyerRef),
            InvoiceChannel: ean is null ? InvoiceChannel.Pdf : InvoiceChannel.OioUbl,
            PaymentChannel: ean is null ? PaymentChannel.Fik : PaymentChannel.EInvoiceFik);

    private static Contract MakeContract(string number, string mpId, string? orderId, string? agreementId, string? buyerRef)
        => new(
            Number: number,
            Description: "Test",
            Start: new DateOnly(2024, 1, 1),
            Commodity: Commodity.Electricity,
            CustomerNumber: "ACC",
            Billing: new BillingSchedule(BillingFrequency.Monthly, "MND"),
            MeteringPoints: [new MeteringPointAssignment(mpId, Product: null!, Start: new DateOnly(2024, 1, 1))],
            PaymentTerm: null,
            BuyerInvoicing: new BuyerInvoicingReferences(null, orderId, agreementId, buyerRef));

    private static Customer MakeCustomer(Payer payer, IReadOnlyList<Contract> contracts)
    {
        var address = new Address("Fiktivvejewj", "1", null, null, "8000", "Aarhus", null, null, null, "DK", null);
        var contact = new ContactInfo("ubm@dw.example.dk", null, null, null);
        var billing = new CustomerBilling(DefaultEan: null, Pbs: null, MobilePay: null);
        return new Customer("ACC", "Test", null, CustomerType.Company, null, null, address, contact, billing, contracts)
        {
            Payers = [payer]
        };
    }

    private static MeteringPoint MakeMp(string mpId)
        => new(
            Identification: mpId,
            InstallationAddress: new Address("Fiktivvejewj", "1", null, null, "8000", "Aarhus", null, null, null, "DK", null),
            Commodity: Commodity.Electricity,
            Meter: null,
            ContactPoints: [],
            JointInvoiceGroupId: null,
            ConnectionStatus: ConnectionStatus.Connected,
            InDelivery: true,
            Grid: null,
            Start: new DateOnly(2024, 1, 1));
}
