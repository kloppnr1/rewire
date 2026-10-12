using DataMigration.SettlApi.Clients;
using DataMigration.SettlApi.Models;

namespace DataMigration.SettlApi.Lookup;

/// <inheritdoc />
public sealed class SettlMeteringPointLookupService : ISettlMeteringPointLookupService
{
    private readonly ISettlCustomerClient _customerClient;
    private readonly ISettlMeteringPointClient _meteringPointClient;
    private readonly ISettlPayerClient _payerClient;

    public SettlMeteringPointLookupService(
        ISettlCustomerClient customerClient,
        ISettlMeteringPointClient meteringPointClient,
        ISettlPayerClient payerClient)
    {
        _customerClient = customerClient;
        _meteringPointClient = meteringPointClient;
        _payerClient = payerClient;
    }

    public async Task<IReadOnlyList<SettlMeteringPointView>> LookupByIdentificationsAsync(
        IReadOnlyList<SettlMpLookupRequest> requests,
        bool includeMissing,
        CancellationToken cancellationToken = default)
    {
        var (mbReverse, gaReverse) = await LoadGridReverseMapsAsync(cancellationToken);

        // Resolve identifications → billing infos (gives the Settl GUID).
        var resolved = new List<(SettlMpLookupRequest Req, BillingMeteringPointInfoDto? Info)>();
        foreach (var req in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (info, _) = await _meteringPointClient.SearchBillingByIdentificationAsync(req.MpIdentification, cancellationToken);
            resolved.Add((req, info));
        }

        var foundIds = resolved.Where(r => r.Info != null).Select(r => r.Info!.Id).Distinct().ToList();
        var payerInfoByMp = await LoadPayerInfoAsync(foundIds, cancellationToken);

        var customerByNumber = new Dictionary<string, CustomerInfoDto?>(StringComparer.Ordinal);
        var customerById = new Dictionary<Guid, CustomerResponse?>();
        var views = new List<SettlMeteringPointView>();

        foreach (var (req, info) in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (info == null)
            {
                if (includeMissing)
                    views.Add(BuildView(req.RepresentativeAccount, customer: null, payers: [],
                        found: false, master: null, billing: null, mbReverse, gaReverse, req.MpIdentification));
                continue;
            }

            var (master, billing) = await LoadMpAsync(info.Id, cancellationToken);
            payerInfoByMp.TryGetValue(info.Id, out var payerInfo);

            CustomerFields? customer = null;
            if (!string.IsNullOrEmpty(req.SettlCustomerNumber))
                customer = CustomerFields.From(await ResolveByNumberAsync(req.SettlCustomerNumber, customerByNumber, cancellationToken));
            else if (payerInfo?.CustomerId is { } customerId)
                customer = CustomerFields.From(await ResolveByIdAsync(customerId, customerById, cancellationToken));

            views.Add(BuildView(req.RepresentativeAccount, customer, payerInfo?.Payers ?? [],
                found: true, master, billing, mbReverse, gaReverse, req.MpIdentification, info.Id));
        }

        return views;
    }

    public async Task<SettlMeteringPointView?> LookupByIdentificationAsync(
        string meteringPointIdentification,
        CancellationToken cancellationToken = default)
    {
        var views = await LookupByIdentificationsAsync(
            [new SettlMpLookupRequest(meteringPointIdentification)], includeMissing: false, cancellationToken);
        return views.Count > 0 ? views[0] : null;
    }

    public async Task<IReadOnlyList<SettlMeteringPointView>> LookupByCustomerNumberAsync(
        string customerNumber,
        CancellationToken cancellationToken = default)
    {
        var (customer, _) = await _customerClient.SearchByCustomerNumberAsync(
            customerNumber, cancellationToken: cancellationToken);
        if (customer == null)
            return [];

        // Owner/top-level customers (number 900000–909999) hold no MPs directly — the MPs hang
        // under their subsidiaries. Resolve the whole organisation instead.
        if (IsOwnerCustomerNumber(customerNumber))
            return await LookupOrganisationAsync(customer.Id, cancellationToken);

        var (links, _) = await _customerClient.GetMeteringPointsAsync(customer.Id, cancellationToken);
        if (links == null || links.Count == 0)
            return [];

        var (mbReverse, gaReverse) = await LoadGridReverseMapsAsync(cancellationToken);
        var ids = links.Select(l => l.MeteringPointId).Distinct().ToList();
        var payerInfoByMp = await LoadPayerInfoAsync(ids, cancellationToken);
        var customerFields = CustomerFields.From(customer);
        var views = new List<SettlMeteringPointView>();

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (master, billing) = await LoadMpAsync(id, cancellationToken);
            payerInfoByMp.TryGetValue(id, out var payerInfo);
            views.Add(BuildView(customerNumber, customerFields, payerInfo?.Payers ?? [],
                found: master != null, master, billing, mbReverse, gaReverse,
                fallbackIdentification: "", id));
        }

        return views;
    }

    /// <summary>Owner/top-level customer numbers sit in the reserved 900000–909999 band.</summary>
    private static bool IsOwnerCustomerNumber(string customerNumber) =>
        int.TryParse(customerNumber, out var n) && n is >= 900000 and <= 909999;

    /// <summary>
    /// Resolves all metering points across an owner customer's organisation. Each MP carries the
    /// subsidiary customer that owns it, so customer columns reflect the subsidiary (not the owner).
    /// </summary>
    private async Task<IReadOnlyList<SettlMeteringPointView>> LookupOrganisationAsync(
        Guid ownerCustomerId, CancellationToken cancellationToken)
    {
        var (orgLinks, _) = await _customerClient.GetOrganisationMeteringPointsAsync(ownerCustomerId, cancellationToken);
        if (orgLinks == null || orgLinks.Count == 0)
            return [];

        var (mbReverse, gaReverse) = await LoadGridReverseMapsAsync(cancellationToken);
        var mpIds = orgLinks.Select(l => l.MeteringPointId).Distinct().ToList();
        var payerInfoByMp = await LoadPayerInfoAsync(mpIds, cancellationToken);
        var customerById = new Dictionary<Guid, CustomerResponse?>();
        var views = new List<SettlMeteringPointView>();

        foreach (var link in orgLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subsidiary = CustomerFields.From(await ResolveByIdAsync(link.CustomerId, customerById, cancellationToken));
            var (master, billing) = await LoadMpAsync(link.MeteringPointId, cancellationToken);
            payerInfoByMp.TryGetValue(link.MeteringPointId, out var payerInfo);
            views.Add(BuildView(subsidiary?.Number, subsidiary, payerInfo?.Payers ?? [],
                found: master != null, master, billing, mbReverse, gaReverse,
                fallbackIdentification: "", link.MeteringPointId));
        }

        return views;
    }

    private sealed class MpPayerInfo
    {
        public List<SettlPayerView> Payers { get; } = [];
        public Guid? CustomerId { get; set; }
    }

    private async Task<Dictionary<Guid, MpPayerInfo>> LoadPayerInfoAsync(
        IReadOnlyList<Guid> meteringPointIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, MpPayerInfo>();
        if (meteringPointIds.Count == 0)
            return map;

        var (entries, _) = await _payerClient.FindPayersForMeteringPointsAsync(meteringPointIds, null, cancellationToken);
        foreach (var entry in entries ?? [])
        {
            if (entry.Payer == null)
                continue;
            var payerView = MapPayer(entry.Payer);
            foreach (var mp in entry.MeteringPoints ?? [])
            {
                if (!map.TryGetValue(mp.MeteringPointId, out var info))
                {
                    info = new MpPayerInfo();
                    map[mp.MeteringPointId] = info;
                }
                info.Payers.Add(payerView);
                // Prefer the primary customer payer's CustomerId; fall back to the first seen.
                if (info.CustomerId == null || entry.Payer.IsPrimaryCustomerPayer)
                    info.CustomerId = entry.Payer.CustomerId;
            }
        }
        return map;
    }

    private static SettlPayerView MapPayer(PayerResponse payer) => new()
    {
        PayerIdentification = payer.PayerInformation?.PayerIdentifier,
        IsPrimaryCustomerPayer = payer.IsPrimaryCustomerPayer,
        Name = payer.PayerInformation?.PostalAddress?.Name1?.FullName,
        ElectronicNumber = payer.PayerBilling?.ElectronicNumber,
        InvoiceChannel = payer.PayerBilling?.InvoiceChannel,
        PaymentChannel = payer.PayerBilling?.PaymentChannel,
        Email = payer.PayerBilling?.Email,
    };

    private async Task<(MeteringPointCurrentDto? Master, BillingMeteringPointCurrentDto? Billing)> LoadMpAsync(
        Guid meteringPointId, CancellationToken cancellationToken)
    {
        var (master, _) = await _meteringPointClient.GetByIdAsync(meteringPointId, cancellationToken: cancellationToken);
        var (billing, _) = await _meteringPointClient.GetBillingByIdAsync(meteringPointId, cancellationToken: cancellationToken);
        // Water MPs exist in the commodity API, not market v3.
        if (master == null)
        {
            var (waterMp, _) = await _meteringPointClient.GetWaterByIdAsync(meteringPointId, cancellationToken);
            if (waterMp != null)
                master = WaterMpToCurrentDto(waterMp);
        }
        return (master, billing);
    }

    private static MeteringPointCurrentDto WaterMpToCurrentDto(MeteringPointInfoDto mp) => new()
    {
        Id = mp.Id,
        Identification = new MeteringPointCurrentIdentificationDto
        {
            MeteringPointIdentification = mp.MeteringPointIdentification,
        },
        MeterCharacteristics = new MeteringPointCurrentMeterCharacteristicsDto
        {
            MeteringPointType = mp.MeteringPointType,
            MeteringPointSubType = mp.MeteringPointSubType,
        },
        Grid = mp.MarketBalanceAreaId is { } mbAreaId
            ? new MeteringPointGridDto { MarketBalanceAreaId = mbAreaId }
            : null,
        Relation = mp.ParentMeteringPoint?.Id is { } parentId && parentId != Guid.Empty
            ? new MeteringPointRelationDto { ParentMeteringPointId = parentId }
            : null,
    };

    private async Task<CustomerInfoDto?> ResolveByNumberAsync(
        string customerNumber, Dictionary<string, CustomerInfoDto?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(customerNumber, out var cached))
            return cached;
        var (customer, _) = await _customerClient.SearchByCustomerNumberAsync(customerNumber, cancellationToken: cancellationToken);
        cache[customerNumber] = customer;
        return customer;
    }

    private async Task<CustomerResponse?> ResolveByIdAsync(
        Guid customerId, Dictionary<Guid, CustomerResponse?> cache, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(customerId, out var cached))
            return cached;
        var (customer, _) = await _customerClient.GetByIdAsync(customerId, cancellationToken);
        cache[customerId] = customer;
        return customer;
    }

    /// <summary>Flat customer fields, sourced from either the search DTO or the by-id response.</summary>
    private readonly record struct CustomerFields(
        Guid? Id, string? Number, string? Name, string? Cvr, string? Type, string? PostalCode, string? City, string? Email)
    {
        public static CustomerFields? From(CustomerInfoDto? c) => c == null
            ? null
            : new CustomerFields(c.Id, c.CustomerNumber, c.FullName, c.VatIdentificationNumber, c.CustomerType, c.PostalCode, c.City, c.Email);

        public static CustomerFields? From(CustomerResponse? c) => c == null
            ? null
            : new CustomerFields(
                c.Id, c.CustomerNumber,
                c.Address?.PostalAddress?.Name1?.FullName,
                c.VatIdentificationNumber, c.CustomerType,
                c.Address?.PostalAddress?.PhysicalAddress?.PostalCode,
                c.Address?.PostalAddress?.PhysicalAddress?.City,
                c.Address?.Email);
    }

    private static SettlMeteringPointView BuildView(
        string? representativeAccount,
        CustomerFields? customer,
        IReadOnlyList<SettlPayerView> payers,
        bool found,
        MeteringPointCurrentDto? master,
        BillingMeteringPointCurrentDto? billing,
        IReadOnlyDictionary<Guid, string> mbReverse,
        IReadOnlyDictionary<Guid, string> gaReverse,
        string fallbackIdentification,
        Guid? settlMeteringPointId = null) => new()
        {
            RepresentativeAccount = representativeAccount,
            SettlCustomerNumber = customer?.Number,
            SettlCustomerId = customer?.Id,
            CustomerName = customer?.Name,
            Cvr = customer?.Cvr,
            CustomerType = customer?.Type,
            CustomerPostalCode = customer?.PostalCode,
            CustomerCity = customer?.City,
            CustomerEmail = customer?.Email,

            MpIdentification = master?.Identification?.MeteringPointIdentification
                ?? (string.IsNullOrEmpty(fallbackIdentification) ? "" : fallbackIdentification),
            SettlMeteringPointId = settlMeteringPointId,
            FoundInSettl = found,
            MeteringPointType = master?.MeterCharacteristics?.MeteringPointType,
            MeteringPointSubType = master?.MeterCharacteristics?.MeteringPointSubType,
            GridMarketBalanceArea = ResolveGrid(master?.Grid?.MarketBalanceAreaId, mbReverse),
            GridArea = ResolveGrid(master?.Grid?.MeteringGridAreaId, gaReverse),
            DeliveryStart = master?.DeliveryStatus?.Start,
            IsChild = master?.Relation?.ParentMeteringPointId is { } parent && parent != Guid.Empty,
            Currency = billing?.BillingConfiguration?.Currency,
            JointInvoiceNumber = billing?.BillingConfiguration?.JointInvoiceNumber,
            ElectronicNumber = billing?.PaymentConfiguration?.ElectronicNumber,
            BillingConfiguration = MapBilling(billing?.BillingConfiguration),
            PaymentConfiguration = MapPayment(billing?.PaymentConfiguration),
            Payers = payers,
        };

    private static SettlMpBillingConfigView? MapBilling(BillingConfigurationDto? b) => b == null
        ? null
        : new SettlMpBillingConfigView
        {
            Start = b.Start,
            End = b.End,
            LifeTime = b.LifeTime,
            JointInvoice = b.JointInvoice,
            JointInvoiceNumber = b.JointInvoiceNumber,
            Currency = b.Currency,
            NextInvoiceManual = b.NextInvoiceManual,
            AutomaticApprovalOfDraft = b.AutomaticApprovalOfDraft,
        };

    private static SettlMpPaymentConfigView? MapPayment(MeteringPointPaymentConfigurationDto? p) => p == null
        ? null
        : new SettlMpPaymentConfigView
        {
            Start = p.Start,
            End = p.End,
            LifeTime = p.LifeTime,
            BillingType = p.BillingType,
            ElectronicNumber = p.ElectronicNumber,
            InvoiceCcEmail = p.InvoiceCcEmail,
            InvoiceCcElectronicNumber = p.InvoiceCcElectronicNumber,
            OrderNumber = p.OrderNumber,
            AgreementIdentifier = p.AgreementIdentifier,
            OrderIdentifier = p.OrderIdentifier,
            BookkeepingReference = p.BookkeepingReference,
            BuyerReference = p.BuyerReference,
        };

    private static string? ResolveGrid(Guid? id, IReadOnlyDictionary<Guid, string> reverse) =>
        id is { } g ? (reverse.TryGetValue(g, out var name) ? name : g.ToString("N")) : null;

    private async Task<(IReadOnlyDictionary<Guid, string> Mb, IReadOnlyDictionary<Guid, string> Ga)>
        LoadGridReverseMapsAsync(CancellationToken cancellationToken)
    {
        var mb = new Dictionary<Guid, string>();
        var ga = new Dictionary<Guid, string>();

        var (mbAreas, _) = await _meteringPointClient.FindMarketBalanceAreasAsync(cancellationToken);
        if (mbAreas != null)
            foreach (var a in mbAreas.Where(a => a.Name != null))
                mb[a.Id] = a.Name!;

        var (gaAreas, _) = await _meteringPointClient.FindMeteringGridAreasAsync(cancellationToken);
        if (gaAreas != null)
            foreach (var a in gaAreas.Where(a => a.MeteringGridAreaIdentification != null))
                ga[a.Id] = a.MeteringGridAreaIdentification!;

        return (mb, ga);
    }
}
