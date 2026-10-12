using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;
using DataMigration.OpsCli.Data;

namespace DataMigration.OpsCli.Tests.Runners;

public class GenerateStateFromSettlCustomerLookupTests
{
    [Fact]
    public void Skipping_unknown_settl_owner_excludes_every_point_sharing_its_xellent_account()
    {
        var cos = new DateOnly(2026, 6, 11);
        var unknown = new SettlMpWithCos("UNKNOWN", Guid.NewGuid(), cos, new MeteringPointInfoDto());
        var shared = new SettlMpWithCos("SHARED", Guid.NewGuid(), cos, new MeteringPointInfoDto());
        var safe = new SettlMpWithCos("SAFE", Guid.NewGuid(), cos, new MeteringPointInfoDto());

        var result = GenerateStateFromSettlRunner.OmitUnresolvedMeteringPoints(
            [unknown, shared, safe], new HashSet<string> { "UNKNOWN" },
            [("UNKNOWN", "417016"), ("SHARED", "417016"), ("SAFE", "503281")]);

        Assert.Equal("SAFE", Assert.Single(result.Kept).Identification);
        Assert.Equal("417016", Assert.Single(result.ExcludedSources));
        Assert.Equal(2, result.OmittedCount);
        Assert.Equal("SharesXellentAccountWithUnresolvedMeteringPoint",
            Assert.Single(result.SharedSourceIssues).Reason);
    }

    [Fact]
    public void Excluded_source_is_not_reintroduced_by_a_representative_group()
    {
        var state = new MigrationStateDocument { Accounts =
            [new MigratedAccount { SettlAccountNumber = "503281" }] };
        var evidence = new GenerateStateOfflineCache.OwnershipEvidence(
            "SAFE", "503281", "503280", "503281", new DateOnly(2026, 6, 11),
            ["503280", "417016"]);

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(
            state, [evidence], "production", ["417016"]);

        Assert.Empty(result.Issues);
        Assert.Equal(["503280"], Assert.Single(state.Accounts).SourceAccounts);
    }

    [Fact]
    public void Skipping_conflicting_claims_removes_the_shared_source_from_both_customers()
    {
        var state = new MigrationStateDocument { Accounts =
        [
            new MigratedAccount { SettlAccountNumber = "503281" },
            new MigratedAccount { SettlAccountNumber = "417016" }
        ] };
        var evidence = new[]
        {
            new GenerateStateOfflineCache.OwnershipEvidence("MP1", "503281", "503281", "503281",
                new DateOnly(2026, 6, 11), ["503281"]),
            new GenerateStateOfflineCache.OwnershipEvidence("MP2", "417016", "503281", "417016",
                new DateOnly(2026, 6, 11), ["503281"])
        };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(
            state, evidence, "production", skipUnresolvedSources: true);

        Assert.Equal("XellentSourceClaimedByMultipleCustomers", Assert.Single(result.Issues).Reason);
        Assert.All(state.Accounts, a => Assert.Empty(a.SourceAccounts));
    }

    [Fact]
    public void Skipping_a_mismatched_claim_also_removes_an_earlier_claim_to_the_same_source()
    {
        var state = new MigrationStateDocument { Accounts =
        [
            new MigratedAccount { SettlAccountNumber = "503281" },
            new MigratedAccount { SettlAccountNumber = "417016" }
        ] };
        var evidence = new[]
        {
            new GenerateStateOfflineCache.OwnershipEvidence("MP1", "503281", "503281", "503281",
                new DateOnly(2026, 6, 11), ["503281"]),
            new GenerateStateOfflineCache.OwnershipEvidence("MP2", "417016", "503281", "503281",
                new DateOnly(2026, 6, 11), ["503281"])
        };

        var result = GenerateStateFromSettlRunner.ApplyOwnershipEvidence(
            state, evidence, "production", skipUnresolvedSources: true);

        Assert.Equal("XellentAccountDoesNotMatchSettlCustomer", Assert.Single(result.Issues).Reason);
        Assert.All(state.Accounts, a => Assert.Empty(a.SourceAccounts));
    }

    [Fact]
    public void Settl_only_point_is_audited_before_payer_lookup_but_for_origin_remains_in_scope()
    {
        var cos = new DateOnly(2026, 6, 11);
        var settlOnly = new SettlMpWithCos("SETTL-ONLY", Guid.NewGuid(), cos,
            new MeteringPointInfoDto());
        var forOrigin = new SettlMpWithCos("FOR-ORIGIN", Guid.NewGuid(), cos,
            new MeteringPointInfoDto());

        var (included, audit) = GenerateStateFromSettlRunner.FilterForXellentOrigin(
            [settlOnly, forOrigin], new HashSet<string> { forOrigin.Identification }, []);

        Assert.Equal(forOrigin.Id, Assert.Single(included).Id);
        var excluded = Assert.Single(audit);
        Assert.Equal(settlOnly.Identification, excluded.MeteringPoint);
        Assert.Equal(cos, excluded.SelectedCos);
        Assert.Equal("NoForXellentOrigin", excluded.Disposition);
    }

    [Fact]
    public async Task Cos_driven_lookup_keeps_historical_customer_even_with_no_invoices()
    {
        var mp = Guid.NewGuid();
        var historicalCustomer = Guid.NewGuid();
        var currentCustomer = Guid.NewGuid();
        var cos = new DateOnly(2026, 5, 1);
        var payer = new DatedPayerClient(mp, cos, historicalCustomer, currentCustomer,
            "278435-1");
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer);
        var mps = new[] { new SettlMpWithCos("571313115200285419", mp, cos, new MeteringPointInfoDto()) };
        var customerNumbers = new Dictionary<Guid, string>
        {
            [historicalCustomer] = "278435", [currentCustomer] = "000006"
        };
        var atCos = await runner.ResolveCustomerByMeteringPointAsync(
            mps, customerNumbers, CancellationToken.None);
        Assert.Equal("278435", atCos[mp]);
        Assert.DoesNotContain("000006", atCos.Values);
        Assert.Equal(cos.ToDateTime(TimeOnly.MinValue), Assert.Single(payer.SearchDates));

        // The COS-driven population filter runs before the --no-invoices early return.
        var cosByCustomer = CosStatePlanner.MinCosByCustomer(mps.Select(m => (atCos[m.Id], m.Cos)));
        Assert.True(cosByCustomer.ContainsKey("278435"));
        Assert.False(cosByCustomer.ContainsKey("000006"));

        payer.SearchDates.Clear();
        var current = await runner.ResolveCustomerByMeteringPointAsync(
            mps, customerNumbers, CancellationToken.None, useCosDate: false);
        Assert.Equal("000006", current[mp]);
        Assert.Null(Assert.Single(payer.SearchDates));
    }

    [Fact]
    public async Task Five_digit_payer_identifier_uses_six_digit_parent_customer_number()
    {
        var mp = Guid.NewGuid();
        var cos = new DateOnly(2026, 5, 1);
        var customer = Guid.NewGuid();
        var payer = new DatedPayerClient(mp, cos, customer, Guid.NewGuid(), "10010");
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer);
        var issues = new List<SourceAccountOwnershipIssue>();
        var result = await runner.ResolveCustomerByMeteringPointAsync(
            [new SettlMpWithCos("MP", mp, cos, new MeteringPointInfoDto())],
            new Dictionary<Guid, string> { [customer] = "910010" }, CancellationToken.None, issues);
        Assert.Equal("910010", result[mp]);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Missing_parent_customer_number_is_reported_and_not_assigned()
    {
        var mp = Guid.NewGuid();
        var cos = new DateOnly(2026, 5, 1);
        var payer = new DatedPayerClient(mp, cos, Guid.NewGuid(), Guid.NewGuid(), "438765-1");
        var runner = new GenerateStateFromSettlRunner(null!, payerClient: payer);
        var issues = new List<SourceAccountOwnershipIssue>();
        var result = await runner.ResolveCustomerByMeteringPointAsync(
            [new SettlMpWithCos("MP", mp, cos, new MeteringPointInfoDto())],
            new Dictionary<Guid, string>(), CancellationToken.None, issues);
        Assert.Empty(result);
        Assert.Equal("MissingOrInvalidSettlCustomerNumber", Assert.Single(issues).Reason);
        Assert.Equal("MP", issues[0].MeteringPoint);
    }

    private sealed class DatedPayerClient(
        Guid mp, DateOnly cos, Guid historicalCustomer, Guid currentCustomer,
        string historicalIdentifier = "278435") : ISettlPayerClient
    {
        public List<DateTime?> SearchDates { get; } = [];

        public Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(
            IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken ct = default)
        {
            SearchDates.Add(searchDate);
            var customer = searchDate == cos.ToDateTime(TimeOnly.MinValue)
                ? historicalCustomer : currentCustomer;
            return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>((
                [new PayerForMeteringPointsDto
                {
                    Payer = new PayerResponse { CustomerId = customer, IsPrimaryCustomerPayer = true,
                        PayerInformation = new PayerInformationDto { PayerIdentifier =
                            customer == historicalCustomer ? historicalIdentifier : "000006" } },
                    MeteringPoints = [new PayerMeteringPointDto { MeteringPointId = mp }]
                }], null));
        }

        public Task<(bool Success, string? Error)> CreateAsync(CreatePayersRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(string payerIdentification, string countryCode = "DK", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(Guid payerId, DateTime? effectiveDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> UpdateRawAsync(Guid payerId, string updateRequestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> UpdatePayerBillingAsync(Guid payerId, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> billingPatches, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(bool Success, string? Error)> DeleteAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(Guid customerId, string countryCode = "DK", CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Json, string? Error)> GetRawByIdAsync(Guid payerId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
