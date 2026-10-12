using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Lookup;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlClient.Tests;

public class SettlMeteringPointLookupServiceTests
{
    private static readonly Guid MbId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CustomerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MpId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OwnerId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static SettlMeteringPointLookupService Build() =>
        new(new FakeCustomerClient(), new FakeMeteringPointClient(), new FakePayerClient());

    [Fact]
    public async Task LookupByIdentifications_found_with_customer_and_payers()
    {
        var requests = new[] { new SettlMpLookupRequest("571313100000000001", "100200", "100200") };

        var views = await Build().LookupByIdentificationsAsync(requests, includeMissing: false);

        var v = Assert.Single(views);
        Assert.True(v.FoundInSettl);
        Assert.Equal("100200", v.SettlCustomerNumber);
        Assert.Equal(CustomerId, v.SettlCustomerId);
        Assert.Equal("Acme A/S", v.CustomerName);
        Assert.Equal("36665030", v.Cvr);
        Assert.Equal("Randers", v.CustomerCity);
        Assert.Equal(MpId, v.SettlMeteringPointId);
        Assert.Equal("E17", v.MeteringPointType);
        Assert.Equal("DK1", v.GridMarketBalanceArea);
        Assert.Equal("DKK", v.Currency);
        Assert.NotNull(v.BillingConfiguration);
        Assert.True(v.BillingConfiguration!.JointInvoice);
        Assert.True(v.BillingConfiguration.NextInvoiceManual);
        Assert.Equal("DKK", v.BillingConfiguration.Currency);
        Assert.Equal(new DateOnly(2024, 1, 1), v.BillingConfiguration.Start);
        Assert.NotNull(v.PaymentConfiguration);
        Assert.Equal("5790000000001", v.PaymentConfiguration!.ElectronicNumber);
        Assert.Equal("Invoice", v.PaymentConfiguration.BillingType);
        Assert.Equal("cc@example.com", v.PaymentConfiguration.InvoiceCcEmail);
        Assert.Equal("BR-42", v.PaymentConfiguration.BuyerReference);
        var payer = Assert.Single(v.Payers);
        Assert.Equal("100200", payer.PayerIdentification);
        Assert.True(payer.IsPrimaryCustomerPayer);
        Assert.Equal("5790000000001", payer.ElectronicNumber);
    }

    [Fact]
    public async Task LookupByIdentifications_missing_included_when_flag_set()
    {
        var requests = new[] { new SettlMpLookupRequest("missing", "100200", "100200") };

        var views = await Build().LookupByIdentificationsAsync(requests, includeMissing: true);

        var v = Assert.Single(views);
        Assert.False(v.FoundInSettl);
        Assert.Equal("missing", v.MpIdentification);
        Assert.Null(v.SettlMeteringPointId);
        Assert.Empty(v.Payers);
    }

    [Fact]
    public async Task LookupByIdentifications_missing_omitted_by_default()
    {
        var requests = new[] { new SettlMpLookupRequest("missing", "100200", "100200") };

        var views = await Build().LookupByIdentificationsAsync(requests, includeMissing: false);

        Assert.Empty(views);
    }

    [Fact]
    public async Task LookupByIdentification_resolves_owning_customer_via_payer()
    {
        // No customer number supplied — customer must be resolved through the payer relation.
        var view = await Build().LookupByIdentificationAsync("571313100000000001");

        Assert.NotNull(view);
        Assert.True(view!.FoundInSettl);
        Assert.Equal(CustomerId, view.SettlCustomerId);
        Assert.Equal("100200", view.SettlCustomerNumber);
        Assert.Equal("Acme A/S", view.CustomerName);
        Assert.Equal("8900", view.CustomerPostalCode);
        Assert.Equal("E17", view.MeteringPointType);
        Assert.Single(view.Payers);
    }

    [Fact]
    public async Task LookupByIdentification_missing_returns_null()
    {
        var view = await Build().LookupByIdentificationAsync("missing");
        Assert.Null(view);
    }

    [Fact]
    public async Task LookupByCustomerNumber_returns_view_per_linked_mp_with_customer_and_payers()
    {
        var views = await Build().LookupByCustomerNumberAsync("100200");

        var v = Assert.Single(views);
        Assert.True(v.FoundInSettl);
        Assert.Equal("100200", v.SettlCustomerNumber);
        Assert.Equal("Acme A/S", v.CustomerName);
        Assert.Equal(MpId, v.SettlMeteringPointId);
        Assert.Equal("571313100000000001", v.MpIdentification);
        Assert.Single(v.Payers);
    }

    [Fact]
    public async Task LookupByCustomerNumber_unknown_customer_returns_empty()
    {
        var views = await Build().LookupByCustomerNumberAsync("999999");
        Assert.Empty(views);
    }

    [Fact]
    public async Task LookupByCustomerNumber_owner_resolves_organisation_metering_points()
    {
        // 900089 is an owner (900000–909999) — MPs come from the organisation, owned by a subsidiary.
        var views = await Build().LookupByCustomerNumberAsync("900089");

        var v = Assert.Single(views);
        Assert.True(v.FoundInSettl);
        Assert.Equal(MpId, v.SettlMeteringPointId);
        // Customer columns reflect the subsidiary, not the owner.
        Assert.Equal("100200", v.SettlCustomerNumber);
        Assert.Equal("Acme A/S", v.CustomerName);
    }

    // === Fakes ===

    private sealed class FakeCustomerClient : ISettlCustomerClient
    {
        public Task<(CustomerInfoDto? Customer, string? Error)> SearchByCustomerNumberAsync(
            string customerNumber, string countryCode = "DK", CancellationToken cancellationToken = default)
        {
            CustomerInfoDto? dto = customerNumber switch
            {
                "100200" => new CustomerInfoDto
                {
                    Id = CustomerId,
                    CustomerNumber = "100200",
                    CustomerType = "Company",
                    VatIdentificationNumber = "36665030",
                    FullName = "Acme A/S",
                    Email = "acme@example.com",
                    PostalCode = "8900",
                    City = "Randers",
                },
                "900089" => new CustomerInfoDto { Id = OwnerId, CustomerNumber = "900089", CustomerType = "Company", FullName = "Kdgr_Acme" },
                _ => null,
            };
            return Task.FromResult<(CustomerInfoDto?, string?)>((dto, null));
        }

        public Task<(CustomerResponse? Customer, string? Error)> GetByIdAsync(Guid customerId, CancellationToken cancellationToken = default)
        {
            var dto = customerId == CustomerId
                ? new CustomerResponse
                {
                    Id = CustomerId,
                    CustomerNumber = "100200",
                    CustomerType = "Company",
                    VatIdentificationNumber = "36665030",
                    Address = new CustomerAddressDto
                    {
                        Email = "acme@example.com",
                        PostalAddress = new PostalAddressDto
                        {
                            Name1 = new ConsumerNameDto { FullName = "Acme A/S" },
                            PhysicalAddress = new PhysicalAddressDto { PostalCode = "8900", City = "Randers" },
                        },
                    },
                }
                : null;
            return Task.FromResult<(CustomerResponse?, string?)>((dto, null));
        }

        public Task<(string? Json, string? Error)> GetRawByIdV3Async(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateV3RawAsync(Guid customerId, string updateRequestJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateNationalIdentificationNumberV3Async(Guid customerId, string nationalIdentificationNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<(List<CustomerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(
            Guid customerId, CancellationToken cancellationToken = default)
        {
            var items = customerId == CustomerId
                ? new List<CustomerMeteringPointDto> { new() { Id = Guid.NewGuid(), MeteringPointId = MpId, CommodityType = "Electricity" } }
                : new List<CustomerMeteringPointDto>();
            return Task.FromResult<(List<CustomerMeteringPointDto>?, string?)>((items, null));
        }

        public Task<(List<CustomerOrganisationMeteringPointDto>? Items, string? Error)> GetOrganisationMeteringPointsAsync(Guid customerId, CancellationToken cancellationToken = default)
        {
            var items = customerId == OwnerId
                ? new List<CustomerOrganisationMeteringPointDto> { new() { Id = Guid.NewGuid(), MeteringPointId = MpId, CustomerId = CustomerId } }
                : new List<CustomerOrganisationMeteringPointDto>();
            return Task.FromResult<(List<CustomerOrganisationMeteringPointDto>?, string?)>((items, null));
        }

        public Task<(bool Success, string? Error)> CreateAsync(CreateCustomersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid customerId, AddCustomerMeteringPointsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> FindCustomersAsync(int limit = 1000, int skip = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerInfoDto> Items, int TotalCount, string? Error)> SearchCustomersAsync(string? fullText = null, string? name = null, string? vatIdentificationNumber = null, int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(CustomerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid customerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<CustomerSegmentDto>? Segments, string? Error)> FindCustomerSegmentsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(SearchCustomerPriceListResponse? Result, string? Error)> SearchPriceListsAsync(Guid customerId, SearchCustomerPriceListRequest? request = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<ExtraInvoiceLineDto> Items, int TotalCount, string? Error)> GetExtraInvoiceLinesAsync(Guid customerId, int limit = 50, int skip = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePayerClient : ISettlPayerClient
    {
        public Task<(List<PayerForMeteringPointsDto>? Items, string? Error)> FindPayersForMeteringPointsAsync(
            IReadOnlyList<Guid> meteringPointIds, DateTime? searchDate = null, CancellationToken cancellationToken = default)
        {
            var items = new List<PayerForMeteringPointsDto>();
            if (meteringPointIds.Contains(MpId))
            {
                items.Add(new PayerForMeteringPointsDto
                {
                    Payer = new PayerResponse
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = CustomerId,
                        IsPrimaryCustomerPayer = true,
                        PayerInformation = new PayerInformationDto
                        {
                            PayerIdentifier = "100200",
                            PostalAddress = new PostalAddressDto { Name1 = new ConsumerNameDto { FullName = "Acme A/S" } },
                        },
                        PayerBilling = new PayerBillingDto
                        {
                            ElectronicNumber = "5790000000001",
                            InvoiceChannel = "PDF",
                            PaymentChannel = "FIK",
                            Email = "acme@example.com",
                        },
                    },
                    MeteringPoints = [new PayerMeteringPointDto { MeteringPointId = MpId }],
                });
            }
            return Task.FromResult<(List<PayerForMeteringPointsDto>?, string?)>((items, null));
        }

        public Task<(bool Success, string? Error)> CreateAsync(CreatePayersRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(PayerInfoDto? Payer, string? Error)> SearchByPayerIdentificationAsync(string payerIdentification, string countryCode = "DK", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> AddMeteringPointsAsync(Guid payerId, AddPayerMeteringPointsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(PayerResponse? Payer, string? Error)> GetByIdAsync(Guid payerId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(PayerHistoryResponse? History, string? Error)> GetHistoryAsync(Guid payerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<PayerMeteringPointDto>? Items, string? Error)> GetMeteringPointsAsync(Guid payerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string? Json, string? Error)> GetRawByIdAsync(Guid payerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdatePayerBillingAsync(Guid payerId, IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> billingPatches, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateRawAsync(Guid payerId, string updateRequestJson, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> DeleteAsync(Guid payerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<PayerInfoDto> Items, int TotalCount, string? Error)> FindPayersAsync(int limit = 100, int skip = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<PayerInfoDto>? Items, string? Error)> FindPayersByCustomerAsync(Guid customerId, string countryCode = "DK", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeMeteringPointClient : ISettlMeteringPointClient
    {
        public Task<(BillingMeteringPointInfoDto? MeteringPoint, string? Error)> SearchBillingByIdentificationAsync(
            string meteringPointIdentification, CancellationToken cancellationToken = default)
        {
            var dto = meteringPointIdentification == "missing"
                ? null
                : new BillingMeteringPointInfoDto { Id = MpId, MeteringPointIdentification = meteringPointIdentification };
            return Task.FromResult<(BillingMeteringPointInfoDto?, string?)>((dto, null));
        }

        public Task<(MeteringPointCurrentDto? MeteringPoint, string? Error)> GetByIdAsync(
            Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
        {
            var dto = new MeteringPointCurrentDto
            {
                Id = meteringPointId,
                Identification = new MeteringPointCurrentIdentificationDto { MeteringPointIdentification = "571313100000000001" },
                MeterCharacteristics = new MeteringPointCurrentMeterCharacteristicsDto { MeteringPointType = "E17", MeteringPointSubType = "D01" },
                Grid = new MeteringPointGridDto { MarketBalanceAreaId = MbId, MeteringGridAreaId = GaId },
                DeliveryStatus = new MeteringPointCurrentSectionDto { Start = new DateTime(2024, 1, 1) },
                Relation = new MeteringPointRelationDto { ParentMeteringPointId = null },
            };
            return Task.FromResult<(MeteringPointCurrentDto?, string?)>((dto, null));
        }

        public Task<(BillingMeteringPointCurrentDto? MeteringPoint, string? Error)> GetBillingByIdAsync(
            Guid billingMeteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default)
        {
            var dto = new BillingMeteringPointCurrentDto
            {
                Id = billingMeteringPointId,
                BillingConfiguration = new BillingConfigurationDto
                {
                    Currency = "DKK",
                    JointInvoiceNumber = "7",
                    JointInvoice = true,
                    NextInvoiceManual = true,
                    AutomaticApprovalOfDraft = false,
                    Start = new DateOnly(2024, 1, 1),
                },
                PaymentConfiguration = new MeteringPointPaymentConfigurationDto
                {
                    ElectronicNumber = "5790000000001",
                    BillingType = "Invoice",
                    InvoiceCcEmail = "cc@example.com",
                    BuyerReference = "BR-42",
                },
            };
            return Task.FromResult<(BillingMeteringPointCurrentDto?, string?)>((dto, null));
        }

        public Task<(List<MarketBalanceAreaInfoDto>? Items, string? Error)> FindMarketBalanceAreasAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<(List<MarketBalanceAreaInfoDto>?, string?)>((new List<MarketBalanceAreaInfoDto> { new() { Id = MbId, Name = "DK1" } }, null));

        public Task<(List<MeteringGridAreaInfoDto>? Items, string? Error)> FindMeteringGridAreasAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<(List<MeteringGridAreaInfoDto>?, string?)>((new List<MeteringGridAreaInfoDto> { new() { Id = GaId, MeteringGridAreaIdentification = "344" } }, null));

        public void SetDeliveryCategory(string deliveryCategory) { }
        public Task<(bool Success, string? Error)> CreateAsync(CreateMeteringPointsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> CreateV4Async(CreateMeteringPointsV4Request request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> CreateWaterAsync(CreateWaterMeteringPointRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> SearchWaterByIdentificationAsync(string meteringPointIdentification, CancellationToken cancellationToken = default) =>
            Task.FromResult<(MeteringPointInfoDto?, string?)>((null, null));
        public Task<(MeteringPointInfoDto? MeteringPoint, string? Error)> GetWaterByIdAsync(Guid meteringPointId, CancellationToken cancellationToken = default) =>
            Task.FromResult<(MeteringPointInfoDto?, string?)>((null, null));
        public Task<(bool Success, string? Error)> UpdateBillingAsync(Guid billingMeteringPointId, UpdateBillingMeteringPointRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(GetDeliveryStatusesResponse? DeliveryStatuses, string? Error)> GetDeliveryStatusesAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string? Json, string? Error)> GetDeliveryStatusesRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(string? Json, string? Error)> GetByIdRawAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateAsync(Guid meteringPointId, MeteringPointDto dto, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(BillingMeteringPointHistoryResponse? History, string? Error)> GetBillingHistoryAsync(Guid billingMeteringPointId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(MeteringPointV3HistoryResponse? History, string? Error)> GetMarketHistoryAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(MeteringPointV4Dto? MeteringPoint, string? Error)> GetByIdV4Async(Guid meteringPointId, DateTime? effectiveDate = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<BillingMeteringPointInfoDto> Items, int TotalCount, string? Error)> FindBillingMeteringPointsAsync(int limit = 100, int skip = 0, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<MeteringPointInfoDto> Items, int TotalCount, string? Error)> FindMeteringPointsAsync(int limit = 100, int skip = 0, bool doCount = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(List<MeteringPointInfoDto> Items, string? Error)> FindWaterMeteringPointsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateAnnualConsumptionsAsync(Guid meteringPointId, double annualVolumeKwh, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> UpdateEstimatedAnnualVolumeAsync(Guid meteringPointId, double annualVolumeKwh, DateOnly effectiveStart, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> AddExemptionsAsync(Guid billingMeteringPointId, AddMeteringPointExemptionsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<(bool Success, string? Error)> BlankDuplicateSecondConsumerNameAsync(Guid meteringPointId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
