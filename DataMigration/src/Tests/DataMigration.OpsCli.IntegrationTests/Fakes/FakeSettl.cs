using DataMigration.OpsCli.Services;
using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Configuration;
using DataMigration.SettlApi.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataMigration.OpsCli.IntegrationTests.Fakes;

/// <summary>The real typed Settl clients, wired to one <see cref="FakeSettlApi"/>.</summary>
public sealed class FakeSettl : IDisposable
{
    private readonly HttpClient _http;

    public FakeSettl(FakeSettlApi? api = null)
    {
        Api = api ?? new FakeSettlApi();
        _http = new HttpClient(Api, disposeHandler: false);
        ApiOptions = new SettlApiOptions { BaseUrl = FakeSettlApi.BaseUrl, SubscriptionKey = "fake" };
        var options = Options.Create(ApiOptions);
        var auth = new FakeAuth();
        CustomerClient = new SettlCustomerClient(_http, auth, options, NullLogger<SettlCustomerClient>.Instance);
        PayerClient = new SettlPayerClient(_http, auth, options, NullLogger<SettlPayerClient>.Instance);
        MeteringPointClient = new SettlMeteringPointClient(_http, auth, options, NullLogger<SettlMeteringPointClient>.Instance);
        ContractClient = new SettlContractClient(_http, auth, options, NullLogger<SettlContractClient>.Instance);
        SettlementGroupClient = new SettlSettlementGroupClient(_http, auth, options, NullLogger<SettlSettlementGroupClient>.Instance);
        RawReader = new SettlRawReader(_http, auth, options, NullLogger<SettlRawReader>.Instance);
        CustomerLinkLookup = new SettlCustomerLinkLookup(_http, auth, options, NullLogger<SettlCustomerLinkLookup>.Instance);
    }

    public FakeSettlApi Api { get; }
    public SettlApiOptions ApiOptions { get; }
    public ISettlCustomerClient CustomerClient { get; }
    public ISettlPayerClient PayerClient { get; }
    public ISettlMeteringPointClient MeteringPointClient { get; }
    public ISettlContractClient ContractClient { get; }
    public ISettlSettlementGroupClient SettlementGroupClient { get; }
    public ISettlRawReader RawReader { get; }
    public ISettlCustomerLinkLookup CustomerLinkLookup { get; }

    /// <summary>An electricity metering point as a migration leaves it: created through the v4 create.</summary>
    public async Task<Guid> SeedMeteringPointAsync(string identification, DateOnly deliveryStart, bool inDelivery = true)
    {
        var id = Guid.NewGuid();
        MeteringPointClient.SetDeliveryCategory(DataMigration.Core.MigrationConstants.DeliveryCategoryElectricity);
        var (ok, error) = await MeteringPointClient.CreateV4Async(new DataMigration.SettlApi.Models.CreateMeteringPointsV4Request
        {
            MeteringPointCreateObjectDtos =
            [
                new()
                {
                    Id = id,
                    Identification = new() { Start = deliveryStart, MeteringPointIdentification = identification },
                    MeterCharacteristics = new() { Start = deliveryStart, MeteringPointType = "Consumption", MeteringPointSubType = "Physical" },
                    ConnectionInfo = new() { Start = deliveryStart, ConnectionStatus = "Connected" },
                    DeliveryStatus = new() { Start = deliveryStart, InDelivery = inDelivery },
                },
            ],
        });
        if (!ok) throw new InvalidOperationException($"Seeding metering point {identification} failed: {error}");
        return id;
    }

    public void Dispose()
    {
        _http.Dispose();
        Api.Dispose();
    }

    private sealed class FakeAuth : ISettlAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fake-token");
        public void InvalidateToken() { }
    }
}
