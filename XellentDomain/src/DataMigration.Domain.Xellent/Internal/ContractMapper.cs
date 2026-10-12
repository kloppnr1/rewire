using DataMigration.Core;
using DataMigration.Domain.Customers;
using DataMigration.Domain.Products;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Maps <c>ExuContractTable</c> + <c>ExuContractPartTable</c> rows into the generic
/// <see cref="Contract"/> domain type. One <see cref="Contract"/> per <c>ExuContractTable</c>
/// row; one <see cref="MeteringPointAssignment"/> per <c>ExuContractPartTable</c> row.
///
/// <para>Contracts on merged (non-representative) accounts are attached to the
/// representative's <see cref="Customer.AccountNumber"/> via the <c>accountToRep</c>
/// map provided by <see cref="CustomerMapper"/>.</para>
///
/// <para>Known gap: <see cref="Product.Name"/> and <see cref="Product.Segment"/> have no
/// readily-available Xellent source in the current <c>XellentData</c> entity model
/// (no <c>VdoSalesJournalProduct</c> entity). For now <c>Code = Name = Productnum</c>
/// and <c>Segment = Both</c>; a future PR adds product-master lookup.</para>
/// </summary>
internal sealed class ContractMapper
{
    private static readonly DateTime EndDate = MigrationConstants.EndDateNull;

    private readonly XellentDbContext _context;
    private readonly IWarningCollector _warnings;
    private readonly MapperOptions _options;
    private readonly string _dataAreaId;

    public ContractMapper(
        XellentDbContext context,
        IWarningCollector warnings,
        MapperOptions options,
        string dataAreaId)
    {
        _context = context;
        _warnings = warnings;
        _options = options;
        _dataAreaId = dataAreaId;
    }

    /// <param name="restore">
    /// The contract-part restore for this load, or null when it is off. Passed in rather than injected:
    /// it is built after the mapper, once the run's contract numbers are known.
    /// </param>
    /// <param name="headerExemption">
    /// Lets contract headers the batch end-dated back in, by name. Null when the restore is off.
    /// </param>
    public async Task<IReadOnlyList<Contract>> MapAsync(
        string[]? accountNumbers,
        IReadOnlyDictionary<string, string> accountToRepresentative,
        ContractPartRestore? restore = null,
        ContractHeaderExemption? headerExemption = null,
        CancellationToken cancellationToken = default)
    {
        if (accountNumbers is { Length: 0 }) return [];

        restore ??= ContractPartRestore.Empty;
        var deliveryCategory = MapCommodity(_options.Commodity);
        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        var includeEndedCutoff = _options.IncludeEndedCutoff?.ToDateTime(TimeOnly.MinValue);
        // Parts only. The header predicate below keeps reading includeEndedCutoff — widening it would
        // pull genuinely terminated contracts into the run. See MapperOptions.IncludeEndedCutoffForParts.
        var partEndCutoff = _options.IncludeEndedCutoffForParts?.ToDateTime(TimeOnly.MinValue);

        // Header rows: one per ExuContractTable filtered to commodity + active/future.
        // Some Xellent contracts have empty top-level Deliverycategory but contain a part
        // matching the requested commodity (e.g. composite water+sewage contracts whose
        // top-level is blank but a Vand-part is active). Mirror SettlExtractionService's
        // OR-clause so those parent contracts surface alongside contracts where the top-
        // level cat matches directly.
        var contractRows = await (
            from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(accountNumbers))
            where contract.Dataareaid == _dataAreaId
                && (accountNumbers == null || accountNumbers.Contains(contract.Custaccount))
                && (contract.Deliverycategory == deliveryCategory
                    || _context.ExuContractPartTables.Any(cp =>
                        cp.Contractnum == contract.Contractnum
                        && cp.Dataareaid == contract.Dataareaid
                        && cp.Deliverycategory == deliveryCategory))
            select new
            {
                contract.Contractnum,
                contract.Custaccount,
                contract.Name,
                contract.Contractstartdate,
                contract.Billingplannum,
                contract.Custpaymcode,
                contract.Einvoiceeannum,
                contract.Einvoiceaccountcode,
                contract.Custpurchaseorder,
                contract.Custcontactpersonid
            }
        ).ToListAsync(cancellationToken);

        if (contractRows.Count == 0) return [];

        var contractNums = contractRows.Select(c => c.Contractnum).Distinct().ToArray();

        // Resolve contact person names (Custcontactpersonid → CONTACTPERSON.NAME) for
        // BuyerReferenceIdentifier on Payer + per-MP block-compare.
        var contactPersonIds = contractRows
            .Select(c => c.Custcontactpersonid)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();
        var contactPersonNameById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in contactPersonIds.Chunk(ChunkSize))
        {
            var page = await _context.ContactPersons
                .Where(cp => cp.Dataareaid == _dataAreaId && chunk.Contains(cp.Contactpersonid))
                .Select(cp => new { cp.Contactpersonid, cp.Name })
                .ToListAsync(cancellationToken);
            foreach (var cp in page)
                contactPersonNameById[cp.Contactpersonid] = cp.Name ?? string.Empty;
        }

        // Part rows differ by join path: electricity uses delpoints; all other commodities
        // (water, heat, EV charging) use installations (part.Instagreenum = ExuInstallation.Instnum).
        List<(string Contractnum, string Instagreenum, string Companyid, string Productnum, string Amrcode, DateTime Startdate, DateTime Enddate, string Meteringpoint)> partRows;

        if (_options.Commodity == Commodity.Electricity)
        {
            partRows = [];
            foreach (var chunk in contractNums.Chunk(ChunkSize))
            {
            var rows = await (
                from part in _context.ExuContractPartTables
                join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                    equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                    equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                where part.Dataareaid == _dataAreaId
                    && chunk.Contains(part.Contractnum)
                    && part.Deliverycategory == deliveryCategory
                    && delpoint.Deliverycategory == deliveryCategory
                    && (part.Enddate == EndDate || part.Enddate >= today
                        || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                    && (_options.ProductCodeFilter == null
                        || (_options.AdditionalAccounts != null && _options.AdditionalAccounts.Contains(delpoint.Meteringpoint))
                        || part.Productnum == _options.ProductCodeFilter)
                select new
                {
                    part.Contractnum,
                    part.Instagreenum,
                    part.Companyid,
                    part.Productnum,
                    part.Amrcode,
                    part.Startdate,
                    part.Enddate,
                    Meteringpoint = delpoint.Meteringpoint
                }
            ).ToListAsync(cancellationToken);
            partRows.AddRange(rows.Select(r => (r.Contractnum, r.Instagreenum, r.Companyid, r.Productnum, r.Amrcode, r.Startdate, r.Enddate, r.Meteringpoint)));
            }
        }
        else
        {
            partRows = [];
            foreach (var chunk in contractNums.Chunk(ChunkSize))
            {
            var rows = await (
                from part in _context.ExuContractPartTables
                join installation in _context.ExuInstallations on new { Instnum = part.Instagreenum, part.Dataareaid }
                    equals new { installation.Instnum, installation.Dataareaid }
                where part.Dataareaid == _dataAreaId
                    && chunk.Contains(part.Contractnum)
                    && part.Deliverycategory == deliveryCategory
                    && part.Startdate <= today
                    && (part.Enddate == EndDate || part.Enddate >= today
                        || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                    && (_options.ProductCodeFilter == null
                        || (_options.AdditionalAccounts != null && _options.AdditionalAccounts.Contains(part.Instagreenum))
                        || part.Productnum == _options.ProductCodeFilter)
                select new
                {
                    part.Contractnum,
                    part.Companyid,
                    part.Productnum,
                    part.Amrcode,
                    part.Startdate,
                    part.Enddate,
                    Instnum = part.Instagreenum
                }
            ).ToListAsync(cancellationToken);
            partRows.AddRange(rows.Select(r => (r.Contractnum, r.Instnum, r.Companyid, r.Productnum, r.Amrcode, r.Startdate, r.Enddate, $"V{r.Instnum}01")));
            }
        }

        // Restore, then re-filter. The SQL above ran with the widened part cutoff, so rows the batch
        // ended are still here; their real end date goes back on, the parts it deleted are added, and
        // the ordinary include-rule then runs again against the header cutoff. The re-filter is not
        // optional: where the batch overwrote an end date that was already in the past, the restored
        // date is genuinely expired and the part has to drop out again. It runs even when nothing was
        // restored, because the widened SQL cutoff let extra rows in either way.
        var contractNumSet = contractNums.ToHashSet(StringComparer.Ordinal);
        partRows = partRows
            .Select(p => restore.RestoredEndDate(PartKey(p, deliveryCategory)) is { } enddate
                ? (p.Contractnum, p.Instagreenum, p.Companyid, p.Productnum, p.Amrcode, p.Startdate, Enddate: enddate, p.Meteringpoint)
                : p)
            .Concat(restore.DeletedPartsFor(deliveryCategory)
                .Where(d => contractNumSet.Contains(d.Key.ContractNum) && PassesProductFilter(d))
                .Select(d => (
                    Contractnum: d.Key.ContractNum,
                    Instagreenum: d.Key.InstAgreeNum,
                    Companyid: d.Key.CompanyId,
                    Productnum: d.Key.ProductNum,
                    Amrcode: d.Key.AmrCode,
                    Startdate: d.Key.PartStartDate,
                    Enddate: d.EndDate,
                    Meteringpoint: d.MeteringPoint)))
            .Where(p => ContractPartInclusion.Included(
                _options.Commodity, p.Startdate, p.Enddate, today, includeEndedCutoff))
            .ToList();

        var partsByContract = partRows
            .GroupBy(p => p.Contractnum, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var contracts = new List<Contract>(contractRows.Count);
        foreach (var c in contractRows)
        {
            if (!accountToRepresentative.TryGetValue(c.Custaccount, out var representativeAccount))
                continue;

            var assignments = partsByContract.TryGetValue(c.Contractnum, out var parts)
                ? OrderAssignments(parts.Select(BuildAssignment), _options.ReferenceDate)
                : [];

            string planCode = c.Billingplannum ?? string.Empty;
            var billing = new BillingSchedule(BillingFrequencyMapper.Map(planCode), planCode);

            string? buyerReference = null;
            if (!string.IsNullOrEmpty(c.Custcontactpersonid)
                && contactPersonNameById.TryGetValue(c.Custcontactpersonid, out var rawName))
            {
                buyerReference = NullIfEmpty(rawName?.Trim());
            }

            var ean = NullIfEmpty(c.Einvoiceeannum?.Trim());
            var orderId = NullIfEmpty(c.Einvoiceaccountcode?.Trim());
            var agreementId = NullIfEmpty(c.Custpurchaseorder?.Trim());
            BuyerInvoicingReferences? buyerInvoicing =
                ean is null && orderId is null && agreementId is null && buyerReference is null
                    ? null
                    : new BuyerInvoicingReferences(ean, orderId, agreementId, buyerReference);

            contracts.Add(new Contract(
                Number: c.Contractnum,
                Description: c.Name ?? string.Empty,
                Start: DateOnly.FromDateTime(c.Contractstartdate),
                Commodity: _options.Commodity,
                CustomerNumber: representativeAccount,
                Billing: billing,
                MeteringPoints: assignments,
                PaymentTerm: NullIfEmpty(c.Custpaymcode),
                BuyerInvoicing: buyerInvoicing));
        }

        return contracts;
    }

    /// <summary>The log key for a live part row. The delivery category is the run's, not the row's.</summary>
    private static ContractPartLogKey PartKey(
        (string Contractnum, string Instagreenum, string Companyid, string Productnum, string Amrcode, DateTime Startdate, DateTime Enddate, string Meteringpoint) p,
        string deliveryCategory)
        => new(p.Contractnum, p.Instagreenum, deliveryCategory, p.Companyid, p.Productnum, p.Amrcode, p.Startdate);

    /// <summary>
    /// The same product filter the part queries push into SQL, applied to a synthesised part. The
    /// additional-accounts escape hatch keys on the metering point for electricity and on the
    /// installation number for the other commodities — as in the two queries above.
    /// </summary>
    private bool PassesProductFilter(RestoredContractPart part)
        => _options.ProductCodeFilter == null
            || (_options.AdditionalAccounts != null
                && _options.AdditionalAccounts.Contains(
                    _options.Commodity == Commodity.Electricity ? part.MeteringPoint : part.Key.InstAgreeNum))
            || part.Key.ProductNum == _options.ProductCodeFilter;

    /// <summary>
    /// Puts a contract's assignments in an order that always decides, and that answers "what is this
    /// metering point on right now" by reading the first row for the metering point.
    ///
    /// <para>One metering point can carry several parts at once — a base product plus a tariff or
    /// add-on — and with <see cref="MapperOptions.IncludeEndedCutoff"/> set it also carries the parts
    /// that ended inside the window. Both are deliberate (see
    /// <see cref="MeteringPointAssignment"/>), but until now the rows came back in whatever order SQL
    /// Server produced, so a consumer reading one product per metering point could get an expired
    /// part — and could get a different one on the next run. Measured on production account 100569
    /// with an include-ended window: <c>V Kvartal</c> (ended 2026-02-28) came out ahead of
    /// <c>V ErhKvt</c>, the part actually in force.</para>
    ///
    /// <para>The order is: metering point, then the parts in force at <paramref name="referenceDate"/>
    /// ahead of those that are not, then the newest start first, then the product code. The last step
    /// is what makes it total — without it, two parts that start on the same day (the
    /// <c>V ProdErhv</c>/<c>V ProdHand</c> pair, for one) fall back to row order again.</para>
    /// </summary>
    internal static List<MeteringPointAssignment> OrderAssignments(
        IEnumerable<MeteringPointAssignment> assignments, DateOnly referenceDate)
        => assignments
            .OrderBy(a => a.MeteringPointId, StringComparer.Ordinal)
            .ThenByDescending(a => InForceAt(a, referenceDate))
            .ThenByDescending(a => a.Start)
            .ThenBy(a => a.Product.Code, StringComparer.Ordinal)
            .ThenBy(a => a.AmrCode, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Whether an assignment covers <paramref name="referenceDate"/>. The open-ended sentinel
    /// (<see cref="MigrationConstants.EndDateNull"/>) means "no end", and a part that has not started
    /// yet is not in force — it is a future assignment, and it sorts behind the running one.
    /// </summary>
    private static bool InForceAt(MeteringPointAssignment assignment, DateOnly referenceDate)
        => assignment.Start <= referenceDate
            && (assignment.EndDate == DateOnly.FromDateTime(MigrationConstants.EndDateNull)
                || assignment.EndDate >= referenceDate);

    private MeteringPointAssignment BuildAssignment(
        (string Contractnum, string Instagreenum, string Companyid, string Productnum, string Amrcode, DateTime Startdate, DateTime Enddate, string Meteringpoint) p)
    {
        return new MeteringPointAssignment(
            MeteringPointId: p.Meteringpoint,
            Product: new Product(
                Code: p.Productnum,
                Name: p.Productnum,                       // degraded — no product-master in XellentData yet
                Commodity: _options.Commodity),
            Start: DateOnly.FromDateTime(p.Startdate),
            EndDate: DateOnly.FromDateTime(p.Enddate),
            AmrCode: p.Amrcode);
    }

    private static string MapCommodity(Commodity commodity) => commodity switch
    {
        Commodity.Electricity => MigrationConstants.DeliveryCategoryElectricity,
        Commodity.Water => MigrationConstants.DeliveryCategoryWater,
        Commodity.Heat => MigrationConstants.DeliveryCategoryHeating,
        Commodity.EvCharging => MigrationConstants.DeliveryCategoryCharge,
        _ => throw new ArgumentOutOfRangeException(nameof(commodity), commodity, "Unsupported commodity")
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Whether an installation-based contract part is included given the active date window.
    /// El-parts have no start-date cap (Settl holds future electricity contracts too).
    /// Installation-based parts (water/heat/drain) must be active on or before referenceDate —
    /// future parts have no pre-populated Settl MP to link to.
    ///
    /// <para>The rule itself lives in <see cref="ContractPartInclusion"/>, which the SQL predicates
    /// and the in-memory re-filter after a restore both go through.</para>
    /// </summary>
    internal static bool InstallationPartIncluded(
        DateTime startdate, DateTime enddate, DateTime today, DateTime? includeEndedCutoff)
        => ContractPartInclusion.Included(Commodity.Water, startdate, enddate, today, includeEndedCutoff);
}
