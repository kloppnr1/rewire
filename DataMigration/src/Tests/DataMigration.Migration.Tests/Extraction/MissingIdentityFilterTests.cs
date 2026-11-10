using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Products;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services.Extraction;

namespace DataMigration.Migration.Tests.Extraction;

public class MissingIdentityFilterTests
{
    [Fact]
    public void Apply_EveryCustomerIdentified_ReturnsUnchanged()
    {
        var load = LoadResult(Customer("A", cvr: "36665030"), Customer("B", cpr: "0101801234"));

        var result = MissingIdentityFilter.Apply(load);

        Assert.Same(load, result.LoadResult);
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void Apply_NeitherCvrNorCpr_ExcludesCustomer()
    {
        var load = LoadResult(Customer("A", cvr: "36665030"), Customer("B"));

        var result = MissingIdentityFilter.Apply(load);

        Assert.Equal(new[] { "A" }, result.LoadResult.Customers.Select(c => c.AccountNumber));
        Assert.Equal("B", Assert.Single(result.Excluded).AccountNumber);
    }

    [Fact]
    public void Apply_CprOnly_IsKept()
    {
        var result = MissingIdentityFilter.Apply(LoadResult(Customer("A", cpr: "0101801234")));

        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void Apply_BlankStringsCountAsMissing()
    {
        // Mapperen normaliserer til null, men en tom eller mellemrums-streng må ikke fiktivvejdht
        // igennem som en identifikation.
        var result = MissingIdentityFilter.Apply(LoadResult(Customer("A", cvr: "", cpr: "  ")));

        Assert.Empty(result.LoadResult.Customers);
        Assert.Single(result.Excluded);
    }

    [Fact]
    public void Apply_SecondDisponentAlone_DoesNotIdentifyCustomer()
    {
        // Disponent 2 kommer med som oplysning, men Settls identifikation bygges på disponent 1.
        var customer = Customer("A") with { Cvr2 = "87654321", Cpr2 = "0101801234" };

        var result = MissingIdentityFilter.Apply(LoadResult(customer));

        var excluded = Assert.Single(result.Excluded);
        Assert.Equal("87654321", excluded.SecondaryCvr);
        Assert.Equal("0101801234", excluded.SecondaryCpr);
    }

    [Fact]
    public void Apply_ExcludedRowCarriesNameAddressAndMeteringPointCount()
    {
        var customer = Customer("A") with
        {
            Name = "Hansens Værksted",
            Address = new Address("Fiktivvejif 1", null, null, null, "8000", "Aarhus", null, null, null, "DK", null),
            Contracts = [Contract("C1", "571313100000000001"), Contract("C2", "571313100000000002")],
        };

        var excluded = Assert.Single(MissingIdentityFilter.Apply(LoadResult(customer)).Excluded);

        Assert.Equal("A", excluded.AccountNumber);
        Assert.Equal("Hansens Værksted", excluded.Name);
        Assert.Equal("Fiktivvejif 1", excluded.StreetName);
        Assert.Equal("8000", excluded.PostalCode);
        Assert.Equal("Aarhus", excluded.City);
        Assert.Equal(2, excluded.MeteringPointCount);
    }

    [Fact]
    public void Apply_NothingCauseWithFictiveCprOff_IsExcludedAsBefore()
    {
        var result = MissingIdentityFilter.Apply(LoadResult(Customer("B")));

        Assert.Empty(result.LoadResult.Customers);
        Assert.Single(result.Excluded);
        Assert.Empty(result.FictiveCprAssigned);
    }

    [Fact]
    public void Apply_NothingCauseWithFictiveCprOn_IsKeptWithFictiveCpr()
    {
        var result = MissingIdentityFilter.Apply(LoadResult(Customer("B")), assignFictiveCprForNothingCause: true);

        var kept = Assert.Single(result.LoadResult.Customers);
        Assert.Equal("B", kept.AccountNumber);
        Assert.Equal("1111110000", kept.Cpr);
        Assert.Empty(result.Excluded);
        Assert.Equal("B", Assert.Single(result.FictiveCprAssigned).AccountNumber);
    }

    [Fact]
    public void Apply_FictiveCprOn_OtherCausesStillExcluded()
    {
        // Code 2 in the alternative number is a real CVR sitting unread — the fictive CPR
        // must not paper over that; it stays excluded like before.
        var customer = Customer("C") with
        {
            RawIdentification = new RawIdentification(Cvr: null, Cpr: null, AlternativeNumber: "36665030", AlternativeNumberCode: 2),
        };

        var result = MissingIdentityFilter.Apply(LoadResult(customer), assignFictiveCprForNothingCause: true);

        Assert.Empty(result.LoadResult.Customers);
        Assert.Equal("C", Assert.Single(result.Excluded).AccountNumber);
        Assert.Empty(result.FictiveCprAssigned);
    }

    [Fact]
    public void Apply_SlicesRepAndMpKeyedSiblingsAlongWithTheCustomer()
    {
        var kept = Customer("A", cvr: "36665030") with { Contracts = [Contract("C1", "MP-A")] };
        var dropped = Customer("B") with { Contracts = [Contract("C2", "MP-B")] };

        var load = LoadResult(kept, dropped) with
        {
            MeteringPoints = [MeteringPoint("MP-A"), MeteringPoint("MP-B")],
            AccountGroupings =
            [
                new AccountGrouping("A", ["A"], null, null, "36665030", new Dictionary<string, string>()),
                new AccountGrouping("B", ["B"], null, null, null, new Dictionary<string, string>()),
            ],
            FingerprintByRepresentative = new Dictionary<string, AccountFingerprint>
            {
                ["A"] = new AccountFingerprint("a", "A", null, "", "", "", null),
                ["B"] = new AccountFingerprint("b", "B", null, "", "", "", null),
            },
            MpOrderIdentifiers = new Dictionary<string, string?> { ["MP-A"] = "x", ["MP-B"] = "y" },
        };

        var sliced = MissingIdentityFilter.Apply(load).LoadResult;

        Assert.Equal(new[] { "MP-A" }, sliced.MeteringPoints.Select(mp => mp.Identification));
        Assert.Equal(new[] { "A" }, sliced.AccountGroupings.Select(g => g.RepresentativeAccount));
        Assert.Equal(new[] { "A" }, sliced.FingerprintByRepresentative.Keys);
        Assert.Equal(new[] { "MP-A" }, sliced.MpOrderIdentifiers.Keys);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static XellentLoadResult LoadResult(params Customer[] customers) =>
        new(
            Customers: customers,
            MeteringPoints: Array.Empty<DataMigration.Domain.MeteringPoints.MeteringPoint>(),
            OwnerClusters: Array.Empty<DataMigration.Domain.Customers.OwnerCluster>(),
            JointInvoiceGroups: Array.Empty<JointInvoiceGroup>(),
            SnapshotDate: new DateOnly(2026, 1, 1),
            MpOrderIdentifiers: new Dictionary<string, string?>(),
            AccountGroupings: Array.Empty<AccountGrouping>(),
            FingerprintByRepresentative: new Dictionary<string, AccountFingerprint>());

    private static Customer Customer(string accountnum, string? cvr = null, string? cpr = null) =>
        new(
            AccountNumber: accountnum,
            Name: accountnum,
            AdditionalName: null,
            Type: string.IsNullOrEmpty(cvr) ? CustomerType.Person : CustomerType.Company,
            Cvr: cvr,
            Cpr: cpr,
            Address: new Address("", null, null, null, "", "", null, null, null, "DK", null),
            Contact: ContactInfo.Empty,
            Billing: new CustomerBilling(DefaultEan: null, Pbs: null, MobilePay: null),
            Contracts: Array.Empty<Contract>());

    private static Contract Contract(string contractNum, string meteringPointId) =>
        new(
            Number: contractNum,
            Description: contractNum,
            Start: new DateOnly(2026, 1, 1),
            Commodity: Commodity.Electricity,
            CustomerNumber: "A",
            Billing: new BillingSchedule(BillingFrequency.Monthly, "M01"),
            MeteringPoints:
            [
                new MeteringPointAssignment(
                    MeteringPointId: meteringPointId,
                    Product: new Product(Code: "P", Name: "P", Commodity: Commodity.Electricity),
                    Start: new DateOnly(2026, 1, 1),
                    EndDate: new DateOnly(1900, 1, 1))
            ]);

    private static DataMigration.Domain.MeteringPoints.MeteringPoint MeteringPoint(string identification) =>
        new(
            Identification: identification,
            InstallationAddress: new Address("", null, null, null, "", "", null, null, null, "DK", null),
            Commodity: Commodity.Electricity,
            Meter: null,
            ContactPoints: Array.Empty<DataMigration.Domain.MeteringPoints.ContactPoint>(),
            JointInvoiceGroupId: null,
            ConnectionStatus: DataMigration.Domain.MeteringPoints.ConnectionStatus.Connected,
            InDelivery: true,
            Grid: null,
            Start: new DateOnly(2026, 1, 1));
}
