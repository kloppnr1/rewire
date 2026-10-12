using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.Tests.Services;

public class AborabatApplyTests
{
    [Fact]
    public async Task Older_single_target_plan_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "aborabat-old-plan-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Version\":1}");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.ReadPlanAsync(path, CancellationToken.None));
            Assert.Contains("outdated", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Plan_created_before_log_filter_is_rejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "aborabat-old-plan-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, "{\"Version\":2}");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.ReadPlanAsync(path, CancellationToken.None));
            Assert.Contains("outdated", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Plan_created_before_bounded_restoration_is_rejected(int version)
    {
        var path = Path.Combine(Path.GetTempPath(), "aborabat-old-plan-" + Guid.NewGuid() + ".json");
        try
        {
            await File.WriteAllTextAsync(path, $"{{\"Version\":{version}}}");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AborabatRepair.ReadPlanAsync(path, CancellationToken.None));
            Assert.Contains("outdated", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Replacement_plan_is_rejected_before_API_access_or_report_creation()
    {
        var old = Guid.NewGuid();
        var association = Guid.NewGuid();
        var target = Guid.NewGuid();
        var plan = new AborabatRepairPlan(6, "production", "snapshot", DateTimeOffset.UtcNow, 0,
            [new("MP1", old, association, [new(target, "V Variabel", new(2027, 1, 1), DateOnly.MaxValue)])]);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AborabatRepair.ApplyAsync(plan, path, null!, CancellationToken.None));
        Assert.Contains("Excel", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Adds_all_targets_before_deleting_and_can_resume_after_a_failed_add()
    {
        var oldContract = Guid.NewGuid();
        var firstContract = Guid.NewGuid();
        var secondContract = Guid.NewGuid();
        var oldAssociation = Guid.NewGuid();
        var client = new FakeContractClient(oldContract, oldAssociation, firstContract, secondContract)
        {
            FailOnAddNumber = 2
        };
        var plan = new AborabatRepairPlan(6, "production", "snapshot", DateTimeOffset.UtcNow, 0,
            [new("MP1", oldContract, oldAssociation,
                [new(firstContract, "V Variabel UA", new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31)),
                 new(secondContract, "V Kvartal UA", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))])]);
        var directory = Path.Combine(Path.GetTempPath(), "aborabat-apply-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(1, await AborabatRepair.ApplyAsync(plan, Path.Combine(directory, "first.csv"), client,
                CancellationToken.None));
            Assert.Empty(client.Removed);
            Assert.Single(client.Links[firstContract]);
            Assert.Empty(client.Links[secondContract]);

            client.FailOnAddNumber = null;
            Assert.Equal(0, await AborabatRepair.ApplyAsync(plan, Path.Combine(directory, "retry.csv"), client,
                CancellationToken.None));
            Assert.Single(client.Removed);
            Assert.Equal((oldContract, oldAssociation), client.Removed[0]);
            Assert.Single(client.Links[firstContract]);
            Assert.Single(client.Links[secondContract]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FakeContractClient : ISettlContractClient
    {
        private readonly Guid oldContract;
        public Dictionary<Guid, List<ContractMeteringPointInfoDto>> Links { get; }
        public int? FailOnAddNumber { get; set; }
        public int AddNumber { get; private set; }
        public List<(Guid ContractId, Guid AssociationId)> Removed { get; } = [];

        public FakeContractClient(Guid oldContract, Guid oldAssociation, Guid firstContract, Guid secondContract)
        {
            this.oldContract = oldContract;
            Links = new()
            {
                [oldContract] =
                [
                    new ContractMeteringPointInfoDto
                    {
                        Id = oldAssociation,
                        MeteringPointId = Guid.NewGuid(),
                        MeteringPointIdentification = "MP1",
                        Start = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        End = new DateTimeOffset(2020, 12, 31, 0, 0, 0, TimeSpan.Zero)
                    }
                ],
                [firstContract] = [],
                [secondContract] = []
            };
        }

        public Task<(List<ContractInfoDto>?, string?)> FindContractsAsync(string? commodityType = null, CancellationToken ct = default)
            => Task.FromResult(((List<ContractInfoDto>?)null, (string?)null));
        public Task<(List<ContractInfoDto>?, string?)> FindContractsByMeteringPointAsync(Guid meteringPointId, CancellationToken ct = default)
            => Task.FromResult(((List<ContractInfoDto>?)null, (string?)null));
        public Task<(List<ContractLineInfoDto>?, string?)> GetContractLinesAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(((List<ContractLineInfoDto>?)null, (string?)null));
        public Task<(List<ContractMeteringPointInfoDto>?, string?)> GetContractMeteringPointsAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(((List<ContractMeteringPointInfoDto>?)Links[id], (string?)null));
        public Task<(List<CustomerContractWithMeteringPointsDto>?, string?)> GetCustomerMeteringPointContractsAsync(Guid customerId, CancellationToken ct = default)
            => Task.FromResult(((List<CustomerContractWithMeteringPointsDto>?)null, (string?)null));
        public Task<(bool, string?)> AddContractMeteringPointsAsync(Guid id, AddContractMeteringPointsRequest request, CancellationToken ct = default)
        {
            AddNumber++;
            if (AddNumber == FailOnAddNumber) return Task.FromResult((false, (string?)"simulated failure"));
            foreach (var item in request.ContractMeteringPoints)
                Links[id].Add(new ContractMeteringPointInfoDto
                {
                    Id = Guid.NewGuid(),
                    MeteringPointId = item.MeteringPointId,
                    MeteringPointIdentification = item.MeteringPointIdentification,
                    Start = new DateTimeOffset(item.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                    End = item.End is null || item.End == DateOnly.MaxValue ? DateTimeOffset.MaxValue
                        : new DateTimeOffset(item.End!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                });
            return Task.FromResult((true, (string?)null));
        }
        public Task<(bool, string?)> RemoveContractMeteringPointAsync(Guid contractId, Guid linkId, CancellationToken ct = default)
        {
            Removed.Add((contractId, linkId));
            Links[oldContract].RemoveAll(l => l.Id == linkId);
            return Task.FromResult((true, (string?)null));
        }
    }
}
