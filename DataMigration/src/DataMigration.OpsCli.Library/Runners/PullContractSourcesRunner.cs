using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.OpsCli.Runners;

/// <summary>A standalone contract audit capture. It never reads or writes invoice data.</summary>
public sealed class PullContractSourcesRunner
{
    private readonly ISettlContractClient contractClient;
    private readonly ISettlMeteringPointClient? pointClient;

    public PullContractSourcesRunner(ISettlContractClient contractClient) : this(contractClient, null) { }
    public PullContractSourcesRunner(ISettlContractClient contractClient, ISettlMeteringPointClient? pointClient)
    {
        this.contractClient = contractClient;
        this.pointClient = pointClient;
    }

    public async Task PullAsync(string environment, string dataAreaId, string connectionString, string outputDirectory,
        string source = "all", DateOnly? referenceDate = null, CancellationToken ct = default, StatePullScope? scope = null, bool deferCompletion = false, bool rawRepairCapture = false)
    {
        if (source is not ("all" or "xellent" or "settl"))
            throw new ArgumentException("--source must be all, xellent, or settl.");
        if (source is ("all" or "xellent") && referenceDate is null)
            throw new ArgumentException("--reference-date is required when pulling Xellent contract sources.");
        using var captureTimer = new CaptureStage("Contract source capture");
        var store = StateSourceSnapshotStore.Open(outputDirectory, environment, scope);
        await store.BeginUpdateAsync(ct);
        if (source is "all" or "xellent")
        {
            using var xellentTimer = new CaptureStage("Xellent contract parts, logs and charger mapping");
            var dbOptions = new DbContextOptionsBuilder<XellentDbContext>()
                .UseSqlServer(connectionString, o => { o.UseCompatibilityLevel(110); o.CommandTimeout(300); })
                .Options;
            await using var context = new XellentDbContext(dbOptions);
            await GenerateStateFromSettlRunner.PullXellentContractSourcesAsync(context, store,
                connectionString, dataAreaId, referenceDate
                    ?? throw new ArgumentException("--reference-date is required."), ct, scope, rawRepairCapture);
            xellentTimer.Complete();
        }
        if (source is "all" or "settl")
        {
            using var settlTimer = new CaptureStage("Settl contract association inventory");
            await GenerateStateFromSettlRunner.PullSettlContractSourcesAsync(store, contractClient, ct, scope, pointClient);
            settlTimer.Complete();
        }
        if (!deferCompletion) await store.CompleteAsync(ct);
        captureTimer.Complete();
    }
}
