using DataMigration.Migration.Configuration;
using DataMigration.Migration.Runners;
using DataMigration.OpsCli.Runners;
using DataMigration.OpsCli.Services.Reconcile;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Runners;

/// <summary>
/// The delete-only apply path for a metering point linked to a contract the plan never intended
/// (Kind = Anomaly). These rows carry no intended payload, so the applier never runs the scoped
/// Xellent capture — which is what makes them unit-testable with fake Settl clients only.
/// </summary>
public class ReconcileApplyContractLinkAnomalyTests
{
    private static readonly Guid WrongContract = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string MpId = "571313179104952730";

    private sealed class FakeContractClient : ISettlContractClient
    {
        public List<ContractMeteringPointInfoDto> Links { get; init; } = [];
        public bool RemoveSucceeds { get; init; } = true;
        public string? RemoveError { get; init; }
        public List<(Guid ContractId, Guid LinkId)> Removed { get; } = [];

        public Task<(List<ContractInfoDto>?, string?)> FindContractsAsync(string? commodityType = null, CancellationToken ct = default)
            => Task.FromResult(((List<ContractInfoDto>?)null, (string?)null));
        public Task<(List<ContractInfoDto>?, string?)> FindContractsByMeteringPointAsync(Guid meteringPointId, CancellationToken ct = default)
            => Task.FromResult(((List<ContractInfoDto>?)null, (string?)null));
        public Task<(List<ContractLineInfoDto>?, string?)> GetContractLinesAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(((List<ContractLineInfoDto>?)null, (string?)null));
        public Task<(List<ContractMeteringPointInfoDto>?, string?)> GetContractMeteringPointsAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(((List<ContractMeteringPointInfoDto>?)Links, (string?)null));
        public Task<(List<CustomerContractWithMeteringPointsDto>?, string?)> GetCustomerMeteringPointContractsAsync(Guid customerId, CancellationToken ct = default)
            => Task.FromResult(((List<CustomerContractWithMeteringPointsDto>?)null, (string?)null));
        public Task<(bool, string?)> AddContractMeteringPointsAsync(Guid id, AddContractMeteringPointsRequest r, CancellationToken ct = default)
            => Task.FromResult((true, (string?)null));
        public Task<(bool, string?)> RemoveContractMeteringPointAsync(Guid contractId, Guid linkId, CancellationToken ct = default)
        {
            Removed.Add((contractId, linkId));
            return Task.FromResult((RemoveSucceeds, RemoveError));
        }
    }

    private static ReconcileApplier Applier(FakeContractClient contracts) => new(
        new SettlApiOptions(),
        new MockCustomerClient(), new MockPayerClient(), new MockMeteringPointClient(),
        contracts, new MockSettlementGroupClient(),
        connectionString: "unused — anomaly rows need no capture",
        exemption: new BillingExemptionConfig(null, null, null, 0),
        log: _ => { });

    private static ReconcileApplyRequest Request(bool dryRun) => new(
        Env: "featuretest",
        DryRun: dryRun,
        Targets:
        [
            new ReconcileApplyTarget("526001", ["526001"], new DateOnly(2026, 1, 1),
            [
                new ReconcileApplyRow("MeteringPoint", "ContractLink", MpId,
                    $"ContractLink[{WrongContract}]", "Anomaly"),
            ]),
        ]);

    private static ContractMeteringPointInfoDto Link(Guid linkId) => new()
    {
        Id = linkId,
        MeteringPointId = Guid.NewGuid(),
        MeteringPointIdentification = MpId,
        Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Dry_run_reports_WouldApply_with_the_delete_action_and_writes_nothing()
    {
        var contracts = new FakeContractClient { Links = [Link(Guid.NewGuid())] };

        var result = await Applier(contracts).ApplyAsync(new ReconcileMappingOptions(), Request(dryRun: true));

        var row = Assert.Single(result.Rows);
        Assert.Equal(ApplyOutcome.WouldApply, row.Outcome);
        Assert.Contains("delete", row.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WrongContract.ToString(), row.Detail!);
        Assert.Empty(contracts.Removed);
    }

    [Fact]
    public async Task Hot_run_deletes_the_link_and_reports_Applied()
    {
        var linkId = Guid.NewGuid();
        var contracts = new FakeContractClient { Links = [Link(linkId)] };

        var result = await Applier(contracts).ApplyAsync(new ReconcileMappingOptions(), Request(dryRun: false));

        var row = Assert.Single(result.Rows);
        Assert.Equal(ApplyOutcome.Applied, row.Outcome);
        Assert.Equal((WrongContract, linkId), Assert.Single(contracts.Removed));
    }

    [Fact]
    public async Task Link_already_gone_reports_NoLongerDrifts_instead_of_failing()
    {
        var contracts = new FakeContractClient(); // contract holds no links at all

        var result = await Applier(contracts).ApplyAsync(new ReconcileMappingOptions(), Request(dryRun: false));

        var row = Assert.Single(result.Rows);
        Assert.Equal(ApplyOutcome.NoLongerDrifts, row.Outcome);
        Assert.Empty(contracts.Removed);
    }

    [Fact]
    public async Task Failed_delete_reports_Failed_with_Settls_message()
    {
        var contracts = new FakeContractClient
        {
            Links = [Link(Guid.NewGuid())],
            RemoveSucceeds = false,
            RemoveError = "409 Conflict: link is invoiced",
        };

        var result = await Applier(contracts).ApplyAsync(new ReconcileMappingOptions(), Request(dryRun: false));

        var row = Assert.Single(result.Rows);
        Assert.Equal(ApplyOutcome.Failed, row.Outcome);
        Assert.Contains("409 Conflict: link is invoiced", row.Detail!);
    }

    [Fact]
    public async Task Water_link_resolves_through_the_V_prefixed_Settl_identification()
    {
        var linkId = Guid.NewGuid();
        var contracts = new FakeContractClient
        {
            Links =
            [
                new ContractMeteringPointInfoDto
                {
                    Id = linkId,
                    MeteringPointId = Guid.NewGuid(),
                    MeteringPointIdentification = "V104229901", // Settl water form
                    End = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero),
                },
            ],
        };
        var request = new ReconcileApplyRequest("featuretest", false,
        [
            new ReconcileApplyTarget("526001", ["526001"], new DateOnly(2026, 1, 1),
            [
                // Drift rows key water MPs on the bare Xellent Instnum.
                new ReconcileApplyRow("MeteringPoint", "ContractLink", "1042299",
                    $"ContractLink[{WrongContract}]", "Anomaly"),
            ]),
        ]);

        var result = await Applier(contracts).ApplyAsync(new ReconcileMappingOptions(), request);

        Assert.Equal(ApplyOutcome.Applied, Assert.Single(result.Rows).Outcome);
        Assert.Equal((WrongContract, linkId), Assert.Single(contracts.Removed));
    }

    [Fact]
    public async Task Unparseable_contract_guid_is_skipped_not_deleted()
    {
        var contracts = new FakeContractClient { Links = [Link(Guid.NewGuid())] };
        var request = new ReconcileApplyRequest("featuretest", false,
        [
            new ReconcileApplyTarget("526001", ["526001"], new DateOnly(2026, 1, 1),
            [
                new ReconcileApplyRow("MeteringPoint", "ContractLink", MpId, "ContractLink[not-a-guid]", "Anomaly"),
            ]),
        ]);

        var result = await Applier(contracts).ApplyAsync(new ReconcileMappingOptions(), request);

        Assert.Equal(ApplyOutcome.Skipped, Assert.Single(result.Rows).Outcome);
        Assert.Empty(contracts.Removed);
    }
}
