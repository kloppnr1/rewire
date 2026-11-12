using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Engros;
using DataMigration.XellentData;
using DataMigration.XellentData.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Maps Xellent rate / billing-history data into a domain <see cref="SettlementBasis"/>
/// for a single metering point in a date range. Mirrors the query shapes used by
/// <c>XellentSettlement.Api/Services/CorrectionService.cs</c> but produces
/// generic Domain.Engros types instead of Settl-specific DTOs.
/// </summary>
internal sealed class SettlementBasisMapper
{
    private const int TariffChargeTypeCode = 3;
    private static readonly DateTime EndDateNull = MigrationConstants.EndDateNull;

    private readonly XellentDbContext _context;
    private readonly string _dataAreaId;

    public SettlementBasisMapper(XellentDbContext context, string dataAreaId)
    {
        _context = context;
        _dataAreaId = dataAreaId;
    }

    /// <summary>
    /// Loads <see cref="SettlementBasis"/> for the given electricity metering point and date range.
    /// Returns null when no flex-billing-history exists for the MP.
    /// </summary>
    public async Task<SettlementBasis?> MapAsync(
        string meteringPointId,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken = default)
    {
        const string deliveryCategory = MigrationConstants.DeliveryCategoryElectricity;

        var hours = await LoadConsumptionHoursAsync(meteringPointId, deliveryCategory, periodStart, periodEnd, cancellationToken);
        if (hours.Count == 0)
            return null;

        var gridTariffs = await LoadGridTariffComponentsAsync(meteringPointId, deliveryCategory, periodStart, periodEnd, cancellationToken);
        var productMargins = await LoadProductMarginComponentsAsync(meteringPointId, deliveryCategory, periodStart, periodEnd, cancellationToken);

        var components = gridTariffs.Concat(productMargins).ToList();

        return new SettlementBasis(
            MeteringPointId: meteringPointId,
            Period: new Period(DateOnly.FromDateTime(periodStart), DateOnly.FromDateTime(periodEnd)),
            Hours: hours,
            Components: components);
    }

    private async Task<IReadOnlyList<ConsumptionHour>> LoadConsumptionHoursAsync(
        string meteringPointId,
        string deliveryCategory,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken)
    {
        var lines = await (
            from h in _context.ExuFlexBhistoryTables
            join l in _context.ExuFlexBhistoryLines
                on new { h.Dataareaid, h.Histkeynumber }
                equals new { l.Dataareaid, l.Histkeynumber }
            where h.Dataareaid == _dataAreaId
                && h.Meteringpoint == meteringPointId
                && h.Deliverycategory == deliveryCategory
                && l.Datetime24hour >= periodStart
                && l.Datetime24hour <= periodEnd
            orderby l.Datetime24hour
            select new { l.Datetime24hour, l.Timevalue, l.Powerexchangeprice, l.Calculatedprice }
        ).ToListAsync(cancellationToken);

        return lines
            .Select(l => new ConsumptionHour(
                Hour: DateTime.SpecifyKind(l.Datetime24hour, DateTimeKind.Utc),
                Kwh: l.Timevalue,
                SpotPrice: l.Powerexchangeprice,
                EnergyRate: l.Calculatedprice))
            .ToList();
    }

    private async Task<IReadOnlyList<PriceComponent>> LoadGridTariffComponentsAsync(
        string meteringPointId,
        string deliveryCategory,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken)
    {
        // Active tariff assignments for this MP (deduped by PartyChargeTypeId — multiple PriceElementCheck-rows
        // can reference same charge-type via different RecIds).
        var assignments = await (
            from pec in _context.ExuPriceElementChecks
            join pecd in _context.ExuPriceElementCheckData
                on new { pec.Dataareaid, RefRecid = pec.Recid }
                equals new { pecd.Dataareaid, RefRecid = pecd.Priceelementcheckrefrecid }
            join pet in _context.ExuPriceElementTables
                on new { pecd.Dataareaid, pecd.Partychargetypeid }
                equals new { pet.Dataareaid, pet.Partychargetypeid }
            where pec.Dataareaid == _dataAreaId
                && pec.Meteringpointid == meteringPointId
                && pec.Deliverycategory == deliveryCategory
                && pet.Chargetypecode == TariffChargeTypeCode
                && pecd.Startdate <= periodEnd
                && (pecd.Enddate == EndDateNull || pecd.Enddate >= periodStart)
            select new
            {
                pet.Partychargetypeid,
                pet.Description,
                pet.Gridcompanyid
            }
        ).ToListAsync(cancellationToken);

        var distinctAssignments = assignments
            .GroupBy(a => a.Partychargetypeid, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (distinctAssignments.Count == 0)
            return Array.Empty<PriceComponent>();

        var components = new List<PriceComponent>();
        foreach (var assignment in distinctAssignments)
        {
            // All rate-rows for this charge-type, ordered by StartDate. Each rate is valid
            // from its StartDate until the next rate's StartDate (or open-ended for the last).
            var rates = await _context.ExuPriceElementRates
                .Where(r => r.Dataareaid == _dataAreaId
                         && r.Partychargetypeid == assignment.Partychargetypeid
                         && r.Startdate <= periodEnd)
                .OrderBy(r => r.Startdate)
                .ToListAsync(cancellationToken);

            if (rates.Count == 0) continue;

            for (int i = 0; i < rates.Count; i++)
            {
                var rate = rates[i];
                var nextStart = i + 1 < rates.Count ? rates[i + 1].Startdate : (DateTime?)null;

                // Skip if this rate ends before the period starts.
                if (nextStart.HasValue && nextStart.Value <= periodStart) continue;

                var validityFrom = DateOnly.FromDateTime(rate.Startdate);
                var validityTo = nextStart.HasValue ? DateOnly.FromDateTime(nextStart.Value) : (DateOnly?)null;

                components.Add(new PriceComponent(
                    Kind: PriceComponentKind.GridTariff,
                    Code: assignment.Partychargetypeid,
                    Description: assignment.Description,
                    OwnerGln: FormatGln(assignment.Gridcompanyid),
                    Rates: BuildHourlyRates(rate),
                    Validity: new Period(validityFrom, validityTo)));
            }
        }

        return components;
    }

    private async Task<IReadOnlyList<PriceComponent>> LoadProductMarginComponentsAsync(
        string meteringPointId,
        string deliveryCategory,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken)
    {
        // MP → Delpoint → Agreement → ContractPart → ProductExtend → InventTable
        // Filtered to product types that use rate-table pricing (ItemType=2, UseRateFromFlexPricing=0).
        var productTypes = await (
            from dp in _context.ExuDelpoints
            join agr in _context.ExuAgreementTables
                on new { dp.Dataareaid, Agreementnum = dp.Attachmentnum }
                equals new { agr.Dataareaid, agr.Agreementnum }
            join cp in _context.ExuContractPartTables
                on new { agr.Dataareaid, Instagreenum = agr.Agreementnum }
                equals new { cp.Dataareaid, cp.Instagreenum }
            join pe in _context.ExuProductExtendTables
                on new { cp.Dataareaid, cp.Productnum }
                equals new { pe.Dataareaid, pe.Productnum }
            join inv in _context.InventTables
                on new { pe.Dataareaid, Itemid = pe.Producttype }
                equals new { inv.Dataareaid, inv.Itemid }
            where dp.Dataareaid == _dataAreaId
                && dp.Meteringpoint == meteringPointId
                && dp.Deliverycategory == deliveryCategory
                && cp.Startdate <= periodEnd
                && (cp.Enddate >= periodStart || cp.Enddate == EndDateNull)
                && pe.Startdate <= periodEnd
                && (pe.Enddate >= periodStart || pe.Enddate == EndDateNull)
                && inv.Itemtype == 2
                && inv.ExuUserateFromFlexPricing == 0
            select new { cp.Productnum, pe.Producttype }
        ).Distinct().ToListAsync(cancellationToken);

        if (productTypes.Count == 0)
            return Array.Empty<PriceComponent>();

        var components = new List<PriceComponent>();
        foreach (var pt in productTypes)
        {
            // Rates from EXU_RATETABLE for this producttype. PRODUCTNUM='' = generic per-kWh rate
            // (product-specific rates with PRODUCTNUM != '' are subscriptions, handled elsewhere).
            var rates = await _context.ExuRateTables
                .Where(r => r.Dataareaid == _dataAreaId
                         && r.Ratetype == pt.Producttype
                         && r.Deliverycategory == deliveryCategory
                         && r.Productnum == ""
                         && r.Startdate <= periodEnd)
                .OrderBy(r => r.Startdate)
                .ToListAsync(cancellationToken);

            for (int i = 0; i < rates.Count; i++)
            {
                var rate = rates[i];
                if (rate.Rate == 0) continue;
                var nextStart = i + 1 < rates.Count ? rates[i + 1].Startdate : (DateTime?)null;
                if (nextStart.HasValue && nextStart.Value <= periodStart) continue;

                var validityFrom = DateOnly.FromDateTime(rate.Startdate);
                var validityTo = nextStart.HasValue ? DateOnly.FromDateTime(nextStart.Value) : (DateOnly?)null;

                components.Add(new PriceComponent(
                    Kind: PriceComponentKind.ProductMargin,
                    Code: pt.Productnum,
                    Description: $"Product margin ({pt.Producttype})",
                    OwnerGln: null,
                    Rates: HourlyRates.Flat(rate.Rate),
                    Validity: new Period(validityFrom, validityTo)));
            }
        }

        return components;
    }

    /// <summary>Constructs <see cref="HourlyRates"/> from the 24 PRICE/PRICE2_/.../PRICE24_ columns.</summary>
    internal static HourlyRates BuildHourlyRates(ExuPriceElementRates rate)
    {
        var arr = new[]
        {
            rate.Price,   rate.Price2,  rate.Price3,  rate.Price4,  rate.Price5,  rate.Price6,
            rate.Price7,  rate.Price8,  rate.Price9,  rate.Price10, rate.Price11, rate.Price12,
            rate.Price13, rate.Price14, rate.Price15, rate.Price16, rate.Price17, rate.Price18,
            rate.Price19, rate.Price20, rate.Price21, rate.Price22, rate.Price23, rate.Price24
        };
        // If all hourly slots are zero but PRICE has a value, treat as flat (some Xellent rate-rows
        // populate only PRICE for flat-rate elements).
        if (arr.Skip(1).All(p => p == 0m) && arr[0] != 0m)
            return HourlyRates.Flat(arr[0]);
        return new HourlyRates(arr);
    }

    /// <summary>Formats Xellent's numeric GRIDCOMPANYID as a 13-digit GLN string. Returns null for zero.</summary>
    internal static string? FormatGln(decimal gridCompanyId)
    {
        if (gridCompanyId == 0m) return null;
        return ((long)gridCompanyId).ToString("D13");
    }
}
