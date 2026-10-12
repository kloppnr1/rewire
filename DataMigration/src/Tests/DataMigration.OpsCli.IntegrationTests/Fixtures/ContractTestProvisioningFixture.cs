using DataMigration.SettlApi.Models;

namespace DataMigration.OpsCli.IntegrationTests.Fixtures;

/// <summary>
/// Provisions a dedicated set of Settl featuretest entities for API contract tests.
/// Entities are created idempotently on first use and never touched by migration runs.
/// No Xellent database access required — only Settl API.
///
/// <para>
/// <b>Dedicated entities</b> (created once, synthetic):<br/>
/// Customer: <see cref="CustomerNumber"/> (company, CVR <see cref="VatIdentificationNumber"/>).<br/>
/// Payer: linked to the customer via <see cref="PayerIdentifier"/>.<br/>
/// </para>
/// <para>
/// <b>Discovered entities</b> (first results from existing migration runs):<br/>
/// <see cref="MpId"/> / <see cref="MpNationalId"/> — first electricity billing MP found in featuretest.<br/>
/// <see cref="MpPayerId"/> / <see cref="MpCustomerId"/> — payer+customer from an existing migration run
/// that actually has MP links and billing config, needed for link/detail contract tests.<br/>
/// <see cref="FirstContractId"/> — first electricity Settl contract, for Contract→MP link tests.
/// </para>
/// </summary>
public class ContractTestProvisioningFixture : IAsyncLifetime
{
    // CustomerNumber in the 9XXXXX band — outside the real Xellent account range.
    public const string CustomerNumber          = "901001";
    public const string PayerIdentifier         = "901001-CT";
    public const string VatIdentificationNumber = "29658149"; // synthetic — not a real Danish CVR

    // Desired Settl GUIDs: passed on first creation so the IDs are stable.
    // If featuretest is reset, delete the entities; re-creation will use the same GUIDs.
    private static readonly Guid DesiredCustomerId = new("ca000001-0000-0000-0000-000000000001");
    private static readonly Guid DesiredPayerId    = new("cb000001-0000-0000-0000-000000000001");

    public SettlFeaturetestFixture Settl { get; } = new();

    // Dedicated entities (GUID = Desired* unless entity pre-existed with a different ID).
    public Guid CustomerId { get; private set; }
    public Guid PayerId    { get; private set; }

    // Discovered from existing migration runs — first electricity MP in featuretest.
    public Guid   MpId         { get; private set; }
    public string MpNationalId { get; private set; } = "";

    // Payer+customer from the migration run that owns MpId (has real links + billing config).
    public Guid MpPayerId    { get; private set; }
    public Guid MpCustomerId { get; private set; }

    // First electricity contract in featuretest (for Contract→MP link tests).
    public Guid FirstContractId { get; private set; }

    // First water contract in featuretest (for water association end-date tests).
    public Guid FirstWaterContractId { get; private set; }

    public async Task InitializeAsync()
    {
        await EnsureCustomer();
        await EnsurePayer();
        await DiscoverMp();
        await DiscoverFirstContract();
        await DiscoverFirstWaterContract();
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
        // We don't create a new electricity MP — it requires valid grid data only migration
        // runs supply. Discover the first available billing MP in featuretest instead.
        var (mps, _, mpErr) = await Settl.MeteringPointClient.FindBillingMeteringPointsAsync(limit: 1);
        if (mps == null || mps.Count == 0)
            throw new InvalidOperationException($"No billing MPs in featuretest: {mpErr}");

        MpId         = mps[0].Id;
        MpNationalId = mps[0].MeteringPointIdentification ?? "";

        // Discover the payer+customer that own this MP (from an existing migration run).
        // These have real MP links + billing config needed for link and detail contract tests.
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

    public Task DisposeAsync()
    {
        Settl.Dispose();
        return Task.CompletedTask;
    }
}
