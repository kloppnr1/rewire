using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Xellent;
using DataMigration.Migration.Services.Extraction;

namespace DataMigration.Migration.Tests.Extraction;

public class CustomerFilterTests
{
    [Fact]
    public void Apply_NoLimitNoSelection_ReturnsUnchanged()
    {
        var load = LoadResult(Customer("A"), Customer("B"), Customer("C"));

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: null);

        Assert.Same(load, result.LoadResult);
        Assert.False(result.WasFiltered);
        Assert.Equal(3, result.OriginalCount);
        Assert.Equal(3, result.FilteredCount);
        Assert.Empty(result.MissingAccounts);
    }

    [Fact]
    public void Apply_LimitLargerThanTotal_ReturnsUnchanged()
    {
        var load = LoadResult(Customer("A"), Customer("B"));

        var result = CustomerFilter.Apply(load, accountLimit: 99, selectedAccounts: null);

        Assert.Same(load, result.LoadResult);
        Assert.False(result.WasFiltered);
    }

    [Fact]
    public void Apply_LimitZeroOrNegative_ReturnsUnchanged()
    {
        var load = LoadResult(Customer("A"), Customer("B"));

        Assert.Same(load, CustomerFilter.Apply(load, 0, null).LoadResult);
        Assert.Same(load, CustomerFilter.Apply(load, -1, null).LoadResult);
    }

    [Fact]
    public void Apply_Limit_TakesFirstNByOrdinalAccountNumber()
    {
        // Reverse insertion order so we verify OrderBy(AccountNumber, Ordinal) is applied,
        // not insertion order.
        var load = LoadResult(Customer("C"), Customer("A"), Customer("B"));

        var result = CustomerFilter.Apply(load, accountLimit: 2, selectedAccounts: null);

        Assert.True(result.WasFiltered);
        Assert.Equal(3, result.OriginalCount);
        Assert.Equal(2, result.FilteredCount);
        Assert.Equal(new[] { "A", "B" }, result.LoadResult.Customers.Select(c => c.AccountNumber));
    }

    [Fact]
    public void Apply_SelectedAccounts_MatchesExactlyByAccountNumber()
    {
        var load = LoadResult(Customer("A"), Customer("B"), Customer("C"));

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A", "C"]);

        Assert.True(result.WasFiltered);
        Assert.Equal(2, result.FilteredCount);
        Assert.Empty(result.MissingAccounts);
        Assert.Equal(new[] { "A", "C" }, result.LoadResult.Customers.Select(c => c.AccountNumber).OrderBy(x => x));
    }

    [Fact]
    public void Apply_SelectedAccounts_UnknownAccountsReportedAsMissing()
    {
        var load = LoadResult(Customer("A"), Customer("B"));

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A", "ZZZ", "QQQ"]);

        Assert.True(result.WasFiltered);
        Assert.Single(result.LoadResult.Customers);
        Assert.Equal("A", result.LoadResult.Customers[0].AccountNumber);
        Assert.Equal(new[] { "QQQ", "ZZZ" }, result.MissingAccounts);
    }

    [Fact]
    public void Apply_SelectedAccounts_MatchesEnvPaddedForm()
    {
        // Dashboard sends the production-padded CustomerNumber (943049), extract holds raw 43049.
        var load = LoadResult(Customer("43049"), Customer("B"));

        var result = CustomerFilter.Apply(
            load, accountLimit: null, selectedAccounts: ["943049"], env: "production");

        Assert.True(result.WasFiltered);
        Assert.Single(result.LoadResult.Customers);
        Assert.Equal("43049", result.LoadResult.Customers[0].AccountNumber);
        Assert.Empty(result.MissingAccounts);
    }

    [Fact]
    public void Apply_SelectedAccounts_MatchesRawFormEvenWithEnv()
    {
        // Raw Accountnum still matches when env normalisation is active (CLI --accounts 43049).
        var load = LoadResult(Customer("43049"), Customer("B"));

        var result = CustomerFilter.Apply(
            load, accountLimit: null, selectedAccounts: ["43049"], env: "production");

        Assert.Single(result.LoadResult.Customers);
        Assert.Equal("43049", result.LoadResult.Customers[0].AccountNumber);
        Assert.Empty(result.MissingAccounts);
    }

    [Fact]
    public void Apply_SelectedAccounts_PaddedFormUnmatchedWithoutEnv()
    {
        // No env → no normalisation → padded selection cannot match the raw extract.
        var load = LoadResult(Customer("43049"), Customer("B"));

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["943049"]);

        Assert.Empty(result.LoadResult.Customers);
        Assert.Equal(new[] { "943049" }, result.MissingAccounts);
    }

    [Fact]
    public void Apply_SelectedAccountsEmpty_FallsThroughToLimitOrNoOp()
    {
        var load = LoadResult(Customer("A"), Customer("B"));

        // Empty selected → ignored; limit kicks in
        var result = CustomerFilter.Apply(load, accountLimit: 1, selectedAccounts: Array.Empty<string>());

        Assert.True(result.WasFiltered);
        Assert.Equal(1, result.FilteredCount);
    }

    [Fact]
    public void Apply_SelectedAccountsTrumpsLimit()
    {
        var load = LoadResult(Customer("A"), Customer("B"), Customer("C"));

        var result = CustomerFilter.Apply(load, accountLimit: 1, selectedAccounts: ["B", "C"]);

        Assert.Equal(new[] { "B", "C" }, result.LoadResult.Customers.Select(c => c.AccountNumber).OrderBy(x => x));
    }

    [Fact]
    public void Apply_SlicesAccountGroupingsInSyncWithCustomers()
    {
        var load = LoadResult(Customer("A"), Customer("B"), Customer("C")) with
        {
            AccountGroupings = new[]
            {
                new AccountGrouping("A", ["A", "A2"], null, null, null, new Dictionary<string, string>()),
                new AccountGrouping("B", ["B"], null, null, null, new Dictionary<string, string>()),
                new AccountGrouping("C", ["C"], null, null, null, new Dictionary<string, string>()),
            }
        };

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A", "C"]);

        Assert.Equal(2, result.LoadResult.AccountGroupings.Count);
        Assert.Equal(new[] { "A", "C" },
            result.LoadResult.AccountGroupings.Select(g => g.RepresentativeAccount).OrderBy(x => x));
    }

    [Fact]
    public void Apply_SlicesFingerprintByRepresentativeInSyncWithCustomers()
    {
        var fp = (string acc) => new AccountFingerprint($"fp-{acc}", null, null, null, null, null);
        var load = LoadResult(Customer("A"), Customer("B"), Customer("C")) with
        {
            FingerprintByRepresentative = new Dictionary<string, AccountFingerprint>(StringComparer.Ordinal)
            {
                ["A"] = fp("A"),
                ["B"] = fp("B"),
                ["C"] = fp("C"),
            },
        };

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["B"]);

        Assert.Single(result.LoadResult.FingerprintByRepresentative);
        Assert.True(result.LoadResult.FingerprintByRepresentative.ContainsKey("B"));
    }

    [Fact]
    public void Apply_SlicesMeteringPointsAndMpKeyedCollections_ByKeptCustomerContractAssignments()
    {
        var mpA = new DataMigration.Domain.MeteringPoints.MeteringPoint(
            Identification: "MP-A",
            InstallationAddress: new Address("", null, null, null, "", "", null, null, null, "DK", null),
            Commodity: Commodity.Electricity,
            Meter: null,
            ContactPoints: Array.Empty<DataMigration.Domain.MeteringPoints.ContactPoint>(),
            JointInvoiceGroupId: null,
            ConnectionStatus: DataMigration.Domain.MeteringPoints.ConnectionStatus.Connected,
            InDelivery: false,
            Grid: null,
            Start: new DateOnly(2026, 1, 1));
        var mpB = mpA with { Identification = "MP-B" };
        var mpC = mpA with { Identification = "MP-C" };

        var custA = CustomerWithMp("A", "MP-A");
        var custB = CustomerWithMp("B", "MP-B");
        var custC = CustomerWithMp("C", "MP-C");

        var load = new XellentLoadResult(
            Customers: new[] { custA, custB, custC },
            MeteringPoints: new[] { mpA, mpB, mpC },
            OwnerClusters: Array.Empty<DataMigration.Domain.Customers.OwnerCluster>(),
            JointInvoiceGroups: Array.Empty<DataMigration.Domain.Customers.JointInvoiceGroup>(),
            SnapshotDate: new DateOnly(2026, 1, 1),
            MpOrderIdentifiers: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["MP-A"] = "oid-A",
                ["MP-B"] = "oid-B",
                ["MP-C"] = "oid-C",
            },
            AccountGroupings: Array.Empty<AccountGrouping>(),
            FingerprintByRepresentative: new Dictionary<string, AccountFingerprint>());

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A"]);

        Assert.Equal(new[] { "MP-A" }, result.LoadResult.MeteringPoints.Select(mp => mp.Identification));
        Assert.Equal(new[] { "MP-A" }, result.LoadResult.MpOrderIdentifiers.Keys.OrderBy(x => x));
    }

    [Fact]
    public void Apply_SlicesEvChargersByMeteringPoint_ToKeptMeteringPoints()
    {
        var load = LoadResultWithMps("A", "B") with
        {
            EvChargersByMeteringPoint = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
            {
                ["MP-A"] = [Charger("inst-A", "A")],
                ["MP-B"] = [Charger("inst-B", "B")],
            },
        };

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A"]);

        Assert.Equal(new[] { "MP-A" }, result.LoadResult.EvChargersByMeteringPoint.Keys);
    }

    [Fact]
    public void Apply_SlicesEvChargersByAccount_ToKeptAccounts()
    {
        var load = LoadResultWithMps("A", "B") with
        {
            EvChargersByAccount = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
            {
                ["A"] = [Charger("inst-A", "A")],
                ["B"] = [Charger("inst-B", "B")],
            },
        };

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A"]);

        Assert.Equal(new[] { "A" }, result.LoadResult.EvChargersByAccount.Keys);
    }

    [Fact]
    public void Apply_KeepsEvChargersOnMergedMemberAccounts()
    {
        // Ladestander-kontrakten hænger under A2, som er sammenlagt med repræsentanten A. A er med
        // i kørslen, så A2's ladestander skal også være det — den hører til den samme Settl-kunde.
        var load = LoadResultWithMps("A", "B") with
        {
            AccountGroupings =
            [
                new AccountGrouping("A", ["A", "A2"], null, null, null, new Dictionary<string, string>()),
                new AccountGrouping("B", ["B"], null, null, null, new Dictionary<string, string>()),
            ],
            EvChargersByAccount = new Dictionary<string, IReadOnlyList<EvCharger>>(StringComparer.Ordinal)
            {
                ["A2"] = [Charger("inst-A2", "A2")],
                ["B"] = [Charger("inst-B", "B")],
            },
        };

        var result = CustomerFilter.Apply(load, accountLimit: null, selectedAccounts: ["A"]);

        Assert.Equal(new[] { "A2" }, result.LoadResult.EvChargersByAccount.Keys);
    }

    private static EvCharger Charger(string installation, string account) =>
        new(
            InstallationNumber: installation,
            CustomerAccount: account,
            ContractNumber: $"c-{account}",
            Address: "Fiktivgadec 90",
            Parts:
            [
                new EvChargerPart("Charge_Abo", EvChargerPartKind.Subscription, "OAboLejMEL",
                    new DateOnly(2025, 3, 19), null)
            ]);

    /// <summary>Load-resultat hvor hver konto har ét målepunkt ved navn MP-{konto}.</summary>
    private static XellentLoadResult LoadResultWithMps(params string[] accounts) =>
        LoadResult([.. accounts.Select(a => CustomerWithMp(a, $"MP-{a}"))]);

    private static Customer CustomerWithMp(string accountnum, string mpId) =>
        Customer(accountnum) with
        {
            Contracts = new[]
            {
                new Contract(
                    Number: $"c-{accountnum}",
                    Description: "",
                    Start: new DateOnly(2026, 1, 1),
                    Commodity: Commodity.Electricity,
                    CustomerNumber: accountnum,
                    Billing: new BillingSchedule(BillingFrequency.Monthly, "M01"),
                    MeteringPoints: new[]
                    {
                        new MeteringPointAssignment(
                            MeteringPointId: mpId,
                            Product: new DataMigration.Domain.Products.Product(Code: "P", Name: "P", Commodity: Commodity.Electricity),
                            Start: new DateOnly(2026, 1, 1),
                            EndDate: new DateOnly(1900, 1, 1))
                    })
            }
        };

    // ── helpers ─────────────────────────────────────────────────────────────

    private static XellentLoadResult LoadResult(params Customer[] customers) =>
        new(
            Customers: customers,
            MeteringPoints: Array.Empty<DataMigration.Domain.MeteringPoints.MeteringPoint>(),
            OwnerClusters: Array.Empty<DataMigration.Domain.Customers.OwnerCluster>(),
            JointInvoiceGroups: Array.Empty<DataMigration.Domain.Customers.JointInvoiceGroup>(),
            SnapshotDate: new DateOnly(2026, 1, 1),
            MpOrderIdentifiers: new Dictionary<string, string?>(),
            AccountGroupings: Array.Empty<AccountGrouping>(),
            FingerprintByRepresentative: new Dictionary<string, AccountFingerprint>());

    private static Customer Customer(string accountnum) =>
        new(
            AccountNumber: accountnum,
            Name: accountnum,
            AdditionalName: null,
            Type: CustomerType.Person,
            Cvr: null,
            Cpr: null,
            Address: new Address("", null, null, null, "", "", null, null, null, "DK", null),
            Contact: ContactInfo.Empty,
            Billing: new CustomerBilling(DefaultEan: null, Pbs: null, MobilePay: null),
            Contracts: Array.Empty<Contract>());
}
