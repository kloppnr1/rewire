using DataMigration.Core;
using DataMigration.OpsCli.IntegrationTests.Fakes;
using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// A small Settl tenant for the API contract tests, on <see cref="FakeSettlApi"/>. Everything is
/// written through the real typed clients — the same calls create makes — and read back through
/// them, so each contract test pins the response shape our code depends on.
///
/// <para><b>What these tests no longer prove:</b> that the live Settl API still answers in that
/// shape. They pin the assumption; a Settl change only shows up against the real environment.</para>
///
/// <para>Contents (all synthetic):</para>
/// <list type="bullet">
/// <item>a dedicated company customer <see cref="CustomerNumber"/> with its payer <see cref="PayerIdentifier"/>;</item>
/// <item>a "migrated" customer + payer owning three electricity metering points (billing and
/// payment configuration, two billing versions on the first one), each linked to an
/// electricity contract;</item>
/// <item>a water contract with one open-ended metering-point link, and one settlement group.</item>
/// </list>
/// </summary>
public class ContractTestProvisioningFixture : IAsyncLifetime
{
    // CustomerNumber in the 9XXXXX band — outside the real Xellent account range.
    public const string CustomerNumber          = "901001";
    public const string PayerIdentifier         = "901001-CT";
    public const string VatIdentificationNumber = "29658149"; // synthetic — not a real Danish CVR

    private static readonly Guid DesiredCustomerId = new("ca000001-0000-0000-0000-000000000001");
    private static readonly Guid DesiredPayerId    = new("cb000001-0000-0000-0000-000000000001");

    // The "migrated" customer and its metering points.
    private static readonly Guid MigratedCustomerId = new("ca000002-0000-0000-0000-000000000002");
    private static readonly Guid MigratedPayerId = new("cb000002-0000-0000-0000-000000000002");
    private static readonly string[] MigratedMpIdentifications =
        ["571313100000000011", "571313100000000012", "571313100000000013"];
    private const string WaterMpIdentification = "V00000000000000021";

    public FakeSettl Settl { get; } = new();

    public Guid CustomerId { get; private set; }
    public Guid PayerId    { get; private set; }
    public Guid   MpId         { get; private set; }
    public string MpNationalId { get; private set; } = "";
    public Guid MpPayerId    { get; private set; }
    public Guid MpCustomerId { get; private set; }
    public Guid FirstContractId { get; private set; }
    public Guid FirstWaterContractId { get; private set; }

    public async Task InitializeAsync()
    {
        SeedTenant();
        await SeedMigratedCustomerAsync();
        await EnsureCustomer();
        await EnsurePayer();
        await DiscoverMp();
        await DiscoverFirstContract();
        await DiscoverFirstWaterContract();
    }

    private void SeedTenant()
    {
        var api = Settl.Api;
        api.MarketBalanceAreas.Add((new Guid("ab000001-0000-0000-0000-000000000001"), "DK1"));
        api.GridAreas.Add((new Guid("ac000001-0000-0000-0000-000000000001"), "131"));
        api.Contracts.Add(new FakeSettlApi.FakeContract(new Guid("cc000001-0000-0000-0000-000000000001"), "V Variabel", "Electricity"));
        api.Contracts.Add(new FakeSettlApi.FakeContract(new Guid("cc000002-0000-0000-0000-000000000002"), "Vand Standard", "Water"));
        api.SettlementGroups.Add(new FakeSettlApi.FakeSettlementGroup(new Guid("cd000001-0000-0000-0000-000000000001"), "Privat"));
        api.CustomerSegments.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["Id"] = "ce000001-0000-0000-0000-000000000001", ["Name"] = "Privat",
        });
    }

    /// <summary>What a migration run leaves behind for one customer, written the way create writes it.</summary>
    private async Task SeedMigratedCustomerAsync()
    {
        var start = new DateOnly(2024, 1, 1);
        var (ok, err) = await Settl.CustomerClient.CreateAsync(new CreateCustomersRequest
        {
            Customers = [new CreateCustomerDto
            {
                Id = MigratedCustomerId, CustomerNumber = "900100", CustomerType = "Person",
                Address = new CustomerAddressDto
                {
                    Start = start,
                    PostalAddress = new PostalAddressDto
                    {
                        Name1 = new ConsumerNameDto { FullName = "Fiktiv Kunde" },
                        PhysicalAddress = new PhysicalAddressDto { StreetName = "Fiktivvej", BuildingNumber = "1", PostalCode = "8900", City = "Randers C" },
                    },
                },
            }],
        });
        Check(ok, err);
        // Segments are assigned in Settl after migration; the catalog entry seeded above.
        Settl.Api.Customers[MigratedCustomerId]["CustomerSegmentId"] = "ce000001-0000-0000-0000-000000000001";
        (ok, err) = await Settl.PayerClient.CreateAsync(new CreatePayersRequest
        {
            Payers = [new CreatePayerDto
            {
                Id = MigratedPayerId, CustomerId = MigratedCustomerId,
                PayerInformation = new PayerInformationDto { Start = start, PayerIdentifier = "900100" },
                PayerBilling = new CreatePayerBillingDto { Start = start, Email = "betaler@example.invalid" },
            }],
        });
        Check(ok, err);

        var mp = Settl.MeteringPointClient;
        mp.SetDeliveryCategory(MigrationConstants.DeliveryCategoryElectricity);
        var mpIds = MigratedMpIdentifications.Select((_, i) => new Guid($"cf00000{i + 1}-0000-0000-0000-00000000000{i + 1}")).ToArray();
        (ok, err) = await mp.CreateV4Async(new CreateMeteringPointsV4Request
        {
            MeteringPointCreateObjectDtos = [.. MigratedMpIdentifications.Select((ident, i) => new MeteringPointCreateObjectV4Dto
            {
                Id = mpIds[i],
                Identification = new MeteringPointIdentifiersDto { Start = start, MeteringPointIdentification = ident, InstallationNumber = ident[^10..] },
                InstallationAddressCreateObject = new MeteringPointInstallationAddressCreateObjectV4Dto
                {
                    Start = start,
                    Address = new PhysicalAddressDto { StreetName = "Fiktivvej", BuildingNumber = $"{i + 1}", PostalCode = "8900", City = "Randers C" },
                },
                MeterCharacteristics = new MeteringPointMeterCharacteristicsDto
                {
                    Start = start, MeteringPointType = "Consumption", MeteringPointSubType = "Physical",
                    EstimatedAnnualVolume = new QuantityDto { Unit = "kWh", Kind = "Na", Value = 4000 },
                    Meters = [new MeteringPointMeterDto { MeterIdentifier = $"M{i + 1}", MeterReadingOccurrence = "Hourly", MultiplicationFactor = 1 }],
                },
                Grid = new MeteringPointGridDto
                {
                    Start = start, MarketBalanceAreaId = Settl.Api.MarketBalanceAreas[0].Id, MeteringGridAreaId = Settl.Api.GridAreas[0].Id,
                },
                SettlementMethod = new MeteringPointSettlementMethodDto { SettlementMethod = "FlexSettled" },
                ConnectionInfo = new MeteringPointConnectionInfoDto { Start = start, ConnectionStatus = "Connected" },
                DeliveryStatus = new MeteringPointDeliveryStatusDto { Start = start, InDelivery = true },
            })],
        });
        Check(ok, err);

        for (var i = 0; i < mpIds.Length; i++)
        {
            (ok, err) = await mp.UpdateBillingAsync(mpIds[i], new UpdateBillingMeteringPointRequest
            {
                BillingConfiguration = new BillingConfigurationDto { Start = start, LifeTime = "UntilNextHistoricalEntry", Currency = "DKK" },
                PaymentConfiguration = new MeteringPointPaymentConfigurationDto { Start = start, BillingType = "Standard" },
            });
            Check(ok, err);
        }
        // A second billing version on the first metering point (the multi-version reconcile case).
        (ok, err) = await mp.UpdateBillingAsync(mpIds[0], new UpdateBillingMeteringPointRequest
        {
            BillingConfiguration = new BillingConfigurationDto { Start = new DateOnly(2025, 1, 1), LifeTime = "UntilNextHistoricalEntry", Currency = "DKK", JointInvoice = true },
        });
        Check(ok, err);

        (ok, err) = await Settl.CustomerClient.AddMeteringPointsAsync(MigratedCustomerId, new AddCustomerMeteringPointsRequest
        {
            CustomerMeteringPoints = [.. mpIds.Select((id, i) => new CustomerMeteringPointDto
                { MeteringPointId = id, ExternalIdentifier = MigratedMpIdentifications[i], Start = start })],
        });
        Check(ok, err);
        (ok, err) = await Settl.PayerClient.AddMeteringPointsAsync(MigratedPayerId, new AddPayerMeteringPointsRequest
        {
            MeteringPoints = [.. mpIds.Select((id, i) => new PayerMeteringPointDto
                { MeteringPointId = id, ExternalIdentifier = MigratedMpIdentifications[i], Start = start })],
        });
        Check(ok, err);
        (ok, err) = await Settl.ContractClient.AddContractMeteringPointsAsync(Settl.Api.Contracts[0].Id, new AddContractMeteringPointsRequest
        {
            ContractMeteringPoints = [.. mpIds.Select((id, i) => new ContractMeteringPointDto
            {
                MeteringPointId = id, Start = start, MeteringPointIdentification = MigratedMpIdentifications[i],
                InstallationNumber = MigratedMpIdentifications[i][^10..], Category = "Consumption",
            })],
        });
        Check(ok, err);
        // Water/heat links are sent open-ended (DateOnly.MaxValue → Settl's 9999 sentinel).
        (ok, err) = await Settl.ContractClient.AddContractMeteringPointsAsync(Settl.Api.Contracts[1].Id, new AddContractMeteringPointsRequest
        {
            ContractMeteringPoints = [new ContractMeteringPointDto
            {
                MeteringPointId = new Guid("cf000009-0000-0000-0000-000000000009"), Start = start, End = DateOnly.MaxValue,
                MeteringPointIdentification = WaterMpIdentification, Category = "Consumption",
            }],
        });
        Check(ok, err);
    }

    private async Task EnsureCustomer()
    {
        var (existing, _) = await Settl.CustomerClient.SearchByCustomerNumberAsync(CustomerNumber);
        if (existing != null)
        {
            CustomerId = existing.Id;
            return;
        }

        var (ok, err) = await Settl.CustomerClient.CreateAsync(new CreateCustomersRequest
        {
            Customers =
            [
                new CreateCustomerDto
                {
                    Id = DesiredCustomerId,
                    CustomerNumber = CustomerNumber,
                    CustomerType = "Company",
                    CountryCode = "DK",
                    VatIdentificationNumber = VatIdentificationNumber,
                    Address = new CustomerAddressDto
                    {
                        Start = new DateOnly(2020, 1, 1),
                        PostalAddress = new PostalAddressDto
                        {
                            Name1 = new ConsumerNameDto { FullName = "Settl Contract Test A/S" },
                            PhysicalAddress = new PhysicalAddressDto
                            {
                                PostalCode = "8900",
                                City = "Randers C",
                                CountryCode = "DK",
                            },
                        },
                    },
                },
            ],
        });
        if (!ok)
            throw new InvalidOperationException($"Create contract-test customer {CustomerNumber} failed: {err}");

        CustomerId = DesiredCustomerId;
    }

    private async Task EnsurePayer()
    {
        var (existing, _) = await Settl.PayerClient.SearchByPayerIdentificationAsync(PayerIdentifier);
        if (existing != null)
        {
            PayerId = existing.Id;
            return;
        }

        var (ok, err) = await Settl.PayerClient.CreateAsync(new CreatePayersRequest
        {
            Payers =
            [
                new CreatePayerDto
                {
                    Id = DesiredPayerId,
                    CustomerId = CustomerId,
                    CountryCode = "DK",
                    IsPrimaryCustomerPayer = true,
                    PayerInformation = new PayerInformationDto
                    {
                        Start = new DateOnly(2020, 1, 1),
                        PayerIdentifier = PayerIdentifier,
                        PostalAddress = new PostalAddressDto
                        {
                            Name1 = new ConsumerNameDto { FullName = "Contract Test Payer" },
                            PhysicalAddress = new PhysicalAddressDto
                            {
                                PostalCode = "8900",
                                City = "Randers C",
                                CountryCode = "DK",
                            },
                        },
                    },
                    PayerBilling = new CreatePayerBillingDto
                    {
                        Start                   = new DateOnly(2020, 1, 1),
                        Email                   = "contract-test@settl-test.local",
                        InvoiceChannel          = "PDF",
                        PaymentChannel          = "FIK",
                        PayerType               = "Company",
                        VatIdentificationNumber = VatIdentificationNumber,
                        ElectronicNumber        = "5790001970682", // synthetic EAN for field-presence tests
                    },
                },
            ],
        });
        if (!ok)
            throw new InvalidOperationException($"Create contract-test payer {PayerIdentifier} failed: {err}");

        PayerId = DesiredPayerId;
    }

    private async Task DiscoverMp()
    {
        var (mps, _, mpErr) = await Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 1);
        if (mps == null || mps.Count == 0)
            throw new InvalidOperationException($"No billing MPs in the fake tenant: {mpErr}");

        MpId         = mps[0].Id;
        MpNationalId = mps[0].MeteringPointIdentification ?? "";

        var (payersForMp, _) = await Settl.PayerClient.FindPayersForMeteringPointsAsync([MpId]);
        var ownerPayer = payersForMp?.FirstOrDefault()?.Payer;
        if (ownerPayer != null)
        {
            MpPayerId    = ownerPayer.Id;
            MpCustomerId = ownerPayer.CustomerId;
        }
    }

    private async Task DiscoverFirstContract()
    {
        var (contracts, _) = await Settl.ContractClient.FindContractsAsync(commodityType: "Electricity");
        if (contracts?.Count > 0)
            FirstContractId = contracts[0].Id;
    }

    private async Task DiscoverFirstWaterContract()
    {
        var (contracts, _) = await Settl.ContractClient.FindContractsAsync(commodityType: "Water");
        if (contracts?.Count > 0)
            FirstWaterContractId = contracts[0].Id;
    }

    private static void Check(bool ok, string? error)
    {
        if (!ok) throw new InvalidOperationException($"Seeding the fake Settl tenant failed: {error}");
    }

    public Task DisposeAsync()
    {
        Settl.Dispose();
        return Task.CompletedTask;
    }
}
