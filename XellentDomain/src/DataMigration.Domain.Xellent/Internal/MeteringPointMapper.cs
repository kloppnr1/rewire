using DataMigration.Core;
using DataMigration.Domain.Common;
using DataMigration.Domain.Customers;
using DataMigration.Domain.MeteringPoints;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Domain.Xellent.Internal.XellentQueryLimits;

namespace DataMigration.Domain.Xellent.Internal;

/// <summary>
/// Maps Xellent metering-point data into the generic <see cref="MeteringPoint"/> domain type.
/// Strategy split is driven by <see cref="MapperOptions.Commodity"/>:
/// electricity uses the delpoint chain (<c>ContractPart → Agreement → Delpoint</c>),
/// other commodities (water/heat/charge) use the installation chain
/// (<c>ContractPart → ExuInstallation</c>) and synthesize a <c>"V{Instnum}01"</c> identifier.
///
/// <para>Known gaps (deferred to future work):</para>
/// <list type="bullet">
///   <item><see cref="Meter.ReadingInterval"/> always null — needs <c>ExuTimereadingsPeriod</c> lookup.</item>
///   <item><see cref="ElectricityMeteringPoint.Kind"/> — always <c>Consumption</c>; production via <c>PROD</c> MeterTypeCode path not yet verified.</item>
///   <item>DAR identifier on installation MPs uses <c>ExuInstallation.Addressid</c>; on delpoint MPs uses the legal
///         contact's <c>Addressid</c> when present, else <c>ExuDelpoint.Addressid</c>.</item>
/// </list>
/// </summary>
internal sealed class MeteringPointMapper
{
    private static readonly DateTime EndDate = MigrationConstants.EndDateNull;

    /// <summary>
    /// Default parent meter type code allow-list = E17 (consumption) ∪ E18 (production)
    /// expanded to raw Xellent codes ("E17/Forb", "FORB", "PROD"). Filtering on these in
    /// the main join excludes children (D05/D06/.../D19) directly in SQL — no post-hoc
    /// rela25505 lookup needed.
    /// </summary>
    private static readonly string[] ParentMeterCodes =
        ExtractionMappings.ExpandDataHubCodesToXellent(["E17", "E18"])!;

    private readonly XellentDbContext _context;
    private readonly IWarningCollector _warnings;
    private readonly MapperOptions _options;
    private readonly string _dataAreaId;

    public MeteringPointMapper(
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
    public Task<MeteringPointMapResult> MapAsync(
        string[] accountNumbers,
        ContractPartRestore? restore = null,
        ContractHeaderExemption? headerExemption = null,
        CancellationToken cancellationToken = default)
    {
        if (accountNumbers.Length == 0)
            return Task.FromResult(new MeteringPointMapResult(Array.Empty<MeteringPoint>(), Array.Empty<JointInvoiceGroup>()));

        return _options.Commodity == Commodity.Electricity
            ? MapElectricityAsync(accountNumbers, restore ?? ContractPartRestore.Empty, headerExemption, cancellationToken)
            : MapInstallationAsync(accountNumbers, restore ?? ContractPartRestore.Empty, headerExemption, cancellationToken);
    }

    private async Task<MeteringPointMapResult> MapElectricityAsync(
        string[] accountNumbers,
        ContractPartRestore restore,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        var deliveryCategory = MigrationConstants.DeliveryCategoryElectricity;
        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        var includeEndedCutoff = _options.IncludeEndedCutoff?.ToDateTime(TimeOnly.MinValue);
        // Parts only — the contract header predicate below keeps the un-widened cutoff.
        var partEndCutoff = _options.IncludeEndedCutoffForParts?.ToDateTime(TimeOnly.MinValue);

        // Chunked on accounts, which is safe here: every cross-row derivation — joint-invoice
        // groups, umbrella contracts — runs after the full result is accumulated, so no chunk
        // boundary can cut through one.
        var rowsFor = (string[] chunk) =>
            from contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(chunk))
            join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { part.Contractnum, part.Dataareaid }
            join agreement in _context.ExuAgreementTables on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
            join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
            join street in _context.ExuStreets on new { delpoint.Streetnum, delpoint.Countynum, delpoint.Dataareaid }
                equals new { street.Streetnum, street.Countynum, street.Dataareaid } into streetJoin
            from street in streetJoin.DefaultIfEmpty()
            join zip in _context.AddressZipCodes on new { Zipcode = delpoint.Zipcodeid, delpoint.Dataareaid }
                equals new { zip.Zipcode, zip.Dataareaid } into zipJoin
            from zip in zipJoin.DefaultIfEmpty()
            join meter in _context.ExuMeterTableTrades on new { Delpointnum = delpoint.Num, Agreementnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Deliverycategory, delpoint.Companyid }
                equals new { meter.Delpointnum, meter.Agreementnum, meter.Dataareaid, meter.Deliverycategory, meter.Companyid } into meterJoin
            from meter in meterJoin.Where(m => m.Enddate == EndDate).DefaultIfEmpty()
            join meterType in _context.EgsMeterpointTypecodeperiods on new { Delpointnum = delpoint.Num, Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid, delpoint.Deliverycategory }
                equals new { meterType.Delpointnum, meterType.Attachmentnum, meterType.Dataareaid, meterType.Companyid, meterType.Deliverycategory }
            where contract.Dataareaid == _dataAreaId
                && chunk.Contains(contract.Custaccount)
                && contract.Deliverycategory == deliveryCategory
                && delpoint.Deliverycategory == deliveryCategory
                && meterType.Enddate == EndDate
                && ParentMeterCodes.Contains(meterType.Meterpointtypecode)
                && (part.Enddate == EndDate || part.Enddate >= today
                    || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                && (_options.ProductCodeFilter == null
                    || (_options.AdditionalAccounts != null && _options.AdditionalAccounts.Contains(delpoint.Meteringpoint))
                    || part.Productnum == _options.ProductCodeFilter)
            select new
            {
                MeteringPoint = delpoint.Meteringpoint,
                ContractMain = contract.Referencetomaincontract,
                ContractNum = contract.Contractnum,
                AgreementStartdate = agreement.Startdate,
                Instbillingname = agreement.Instbillingname,
                Delpoint = delpoint,
                StreetName = street != null ? street.Streetname : null,
                CityName = zip != null ? zip.City : null,
                ZipCountryCode = zip != null ? zip.Countryregionid : null,
                MeterNum = meter != null ? meter.Meternum : null,
                MeterCounter = meter != null ? meter.Counterconstant : (decimal?)null,
                MeterDhSubType = meter != null ? meter.MeteringPointDhSubType : (int?)null,
                MeterTypeCode = meterType.Meterpointtypecode,
                // Carried so the part-level date rule can be re-applied in memory after a restore.
                PartInstagreenum = part.Instagreenum,
                PartCompanyid = part.Companyid,
                PartProductnum = part.Productnum,
                PartAmrcode = part.Amrcode,
                PartStartdate = part.Startdate,
                PartEnddate = part.Enddate
            };

        var chunks = accountNumbers.Chunk(ChunkSize).ToList();
        var rows = await rowsFor(chunks[0]).ToListAsync(cancellationToken);
        for (var i = 1; i < chunks.Count; i++)
            rows.AddRange(await rowsFor(chunks[i]).ToListAsync(cancellationToken));

        // The query ran with the widened part cutoff, so parts the batch ended are still here. Put
        // their real end date back, then re-apply the ordinary part rule against the header cutoff —
        // a part whose restored end date is genuinely in the past drops out again. The rows themselves
        // are left alone: nothing downstream reads the part end date, only this filter does.
        // End date only: this query caps no start date (unlike the contract mapper's installation
        // branch), so the re-filter must not either.
        rows = rows
            .Where(r => ContractPartInclusion.EndDateIncluded(
                restore.RestoredEndDate(new ContractPartLogKey(
                    r.ContractNum, r.PartInstagreenum, deliveryCategory, r.PartCompanyid,
                    r.PartProductnum, r.PartAmrcode, r.PartStartdate)) ?? r.PartEnddate,
                today,
                includeEndedCutoff))
            .ToList();

        // Parts the batch deleted cannot be reached from EXU_CONTRACTPARTTABLE at all, so their
        // metering points are fetched on their own — agreement → delivery point, with the same street,
        // zip, meter and meter-type joins and the same parent-meter filter as above — and appended in
        // the same shape before the dedup below. ~2.100 rows worst case.
        var restoredParts = restore.IsEmpty
            ? new List<RestoredContractPart>()
            : restore.DeletedPartsFor(deliveryCategory)
                .Where(p => ContractPartInclusion.EndDateIncluded(p.EndDate, today, includeEndedCutoff))
                .Where(p => _options.ProductCodeFilter == null
                    || (_options.AdditionalAccounts != null && _options.AdditionalAccounts.Contains(p.MeteringPoint))
                    || p.Key.ProductNum == _options.ProductCodeFilter)
                .ToList();

        if (restoredParts.Count > 0)
        {
            var accountSet = accountNumbers.ToHashSet(StringComparer.Ordinal);
            var restoredContractNums = restoredParts.Select(p => p.Key.ContractNum).Distinct(StringComparer.Ordinal).ToArray();

            var contractById = new Dictionary<string, (string? Main, string Num)>(StringComparer.Ordinal);
            foreach (var chunk in restoredContractNums.Chunk(ChunkSize))
            {
                var page = await ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForContracts(chunk))
                    .Where(c => c.Dataareaid == _dataAreaId
                        && chunk.Contains(c.Contractnum)
                        && c.Deliverycategory == deliveryCategory)
                    .Select(c => new { c.Contractnum, c.Custaccount, c.Referencetomaincontract })
                    .ToListAsync(cancellationToken);
                foreach (var c in page.Where(c => accountSet.Contains(c.Custaccount)))
                    contractById[c.Contractnum] = (c.Referencetomaincontract, c.Contractnum);
            }

            var restoredAgreements = restoredParts.Select(p => p.Key.InstAgreeNum).Distinct(StringComparer.Ordinal).ToArray();
            var delpointRows = new List<(string Agreementnum, string Companyid, string Meteringpoint, DateTime AgreementStartdate,
                string? Instbillingname, DataMigration.XellentData.Entities.ExuDelpoint Delpoint, string? StreetName, string? CityName,
                string? ZipCountryCode, string? MeterNum, decimal? MeterCounter, int? MeterDhSubType, string MeterTypeCode)>();
            foreach (var chunk in restoredAgreements.Chunk(ChunkSize))
            {
                var page = await (
                    from agreement in _context.ExuAgreementTables
                    join delpoint in _context.ExuDelpoints on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                        equals new { Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid }
                    join street in _context.ExuStreets on new { delpoint.Streetnum, delpoint.Countynum, delpoint.Dataareaid }
                        equals new { street.Streetnum, street.Countynum, street.Dataareaid } into streetJoin
                    from street in streetJoin.DefaultIfEmpty()
                    join zip in _context.AddressZipCodes on new { Zipcode = delpoint.Zipcodeid, delpoint.Dataareaid }
                        equals new { zip.Zipcode, zip.Dataareaid } into zipJoin
                    from zip in zipJoin.DefaultIfEmpty()
                    join meter in _context.ExuMeterTableTrades on new { Delpointnum = delpoint.Num, Agreementnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Deliverycategory, delpoint.Companyid }
                        equals new { meter.Delpointnum, meter.Agreementnum, meter.Dataareaid, meter.Deliverycategory, meter.Companyid } into meterJoin
                    from meter in meterJoin.Where(m => m.Enddate == EndDate).DefaultIfEmpty()
                    join meterType in _context.EgsMeterpointTypecodeperiods on new { Delpointnum = delpoint.Num, Attachmentnum = delpoint.Attachmentnum, delpoint.Dataareaid, delpoint.Companyid, delpoint.Deliverycategory }
                        equals new { meterType.Delpointnum, meterType.Attachmentnum, meterType.Dataareaid, meterType.Companyid, meterType.Deliverycategory }
                    where agreement.Dataareaid == _dataAreaId
                        && chunk.Contains(agreement.Agreementnum)
                        && agreement.Deliverycategory == deliveryCategory
                        && delpoint.Deliverycategory == deliveryCategory
                        && meterType.Enddate == EndDate
                        && ParentMeterCodes.Contains(meterType.Meterpointtypecode)
                    select new
                    {
                        agreement.Agreementnum,
                        agreement.Companyid,
                        delpoint.Meteringpoint,
                        AgreementStartdate = agreement.Startdate,
                        agreement.Instbillingname,
                        Delpoint = delpoint,
                        StreetName = street != null ? street.Streetname : null,
                        CityName = zip != null ? zip.City : null,
                        ZipCountryCode = zip != null ? zip.Countryregionid : null,
                        MeterNum = meter != null ? meter.Meternum : null,
                        MeterCounter = meter != null ? meter.Counterconstant : (decimal?)null,
                        MeterDhSubType = meter != null ? meter.MeteringPointDhSubType : (int?)null,
                        MeterTypeCode = meterType.Meterpointtypecode
                    }
                ).ToListAsync(cancellationToken);

                delpointRows.AddRange(page.Select(p => (p.Agreementnum, p.Companyid, p.Meteringpoint, p.AgreementStartdate,
                    (string?)p.Instbillingname, p.Delpoint, (string?)p.StreetName, (string?)p.CityName,
                    (string?)p.ZipCountryCode, (string?)p.MeterNum, p.MeterCounter, p.MeterDhSubType, p.MeterTypeCode)));
            }

            // Company id folded to one casing on both sides — Xellent spells the same company both
            // 'for' and 'For', and this pairs a logged part with a live delivery point.
            var delpointsByAgreement = delpointRows.ToLookup(d => (ContractPartRestore.CompanyKey(d.Companyid), d.Agreementnum));

            foreach (var part in restoredParts)
            {
                if (!contractById.TryGetValue(part.Key.ContractNum, out var contract)) continue;

                foreach (var d in delpointsByAgreement[(ContractPartRestore.CompanyKey(part.Key.CompanyId), part.Key.InstAgreeNum)]
                             .Where(d => string.Equals(d.Meteringpoint, part.MeteringPoint, StringComparison.Ordinal)))
                {
                    // Same anonymous shape as the main query — the compiler unifies the two, so a
                    // change made to one and not the other stops compiling right here.
                    rows.Add(new
                    {
                        MeteringPoint = d.Meteringpoint,
                        ContractMain = contract.Main,
                        ContractNum = contract.Num,
                        AgreementStartdate = d.AgreementStartdate,
                        Instbillingname = d.Instbillingname,
                        Delpoint = d.Delpoint,
                        StreetName = d.StreetName,
                        CityName = d.CityName,
                        ZipCountryCode = d.ZipCountryCode,
                        MeterNum = d.MeterNum,
                        MeterCounter = d.MeterCounter,
                        MeterDhSubType = d.MeterDhSubType,
                        MeterTypeCode = d.MeterTypeCode,
                        PartInstagreenum = part.Key.InstAgreeNum,
                        PartCompanyid = part.Key.CompanyId,
                        PartProductnum = part.Key.ProductNum,
                        PartAmrcode = part.Key.AmrCode,
                        PartStartdate = part.Key.PartStartDate,
                        PartEnddate = part.EndDate
                    });
                }
            }
        }

        if (rows.Count == 0)
            return new MeteringPointMapResult(Array.Empty<MeteringPoint>(), Array.Empty<JointInvoiceGroup>());

        // ADDRESSZIPCODE's key is (Dataareaid, Countryregionid, Zipcode, County), so the same zip
        // number genuinely recurs across countries — confirmed for dataarea 'hol': 12 zips, every
        // one of them paired with a DK row (e.g. zip 2610 is both "Rødovre, DK" and "Mesnali, NO").
        // The zip join above is on Zipcode+Dataareaid alone, so a collision fans this row out into
        // one candidate per city before anything downstream runs. Delpoint delivery addresses are
        // always Danish (AddressMapper.ResolveDelpointAddress hardcodes CountryCode), so there is
        // no per-row hint to weigh candidates against — collapse straight to the DK candidate.
        rows = rows
            .GroupBy(r => new
            {
                r.MeteringPoint, r.ContractMain, r.ContractNum, r.AgreementStartdate, r.Instbillingname,
                DelpointNum = r.Delpoint.Num, r.Delpoint.Attachmentnum, r.Delpoint.Companyid,
                r.StreetName, r.MeterNum, r.MeterCounter, r.MeterDhSubType, r.MeterTypeCode
            })
            .Select(g =>
            {
                if (g.Count() == 1) return g.First();
                var dk = g.FirstOrDefault(x => string.Equals(x.ZipCountryCode, "DK", StringComparison.OrdinalIgnoreCase));
                if (dk != null) return dk;
                var fallback = g.OrderBy(x => x.ZipCountryCode, StringComparer.Ordinal).First();
                _warnings.AddWarning(
                    $"MP {fallback.MeteringPoint}: delivery-address zip {fallback.Delpoint.Zipcodeid} collides " +
                    "across countries in ADDRESSZIPCODE and none of the candidates is DK — picked " +
                    $"'{fallback.CityName}' ({fallback.ZipCountryCode}) arbitrarily [Xellent: ADDRESSZIPCODE]");
                return fallback;
            })
            .ToList();

        // Determine joint-invoice group: from contract.Referencetomaincontract, OR from
        // contract.Contractnum if any sibling contract references it as master.
        //
        // Primary scope: El-ekstern contracts that appear in rows.
        // Umbrella scope: contracts referenced as main that are NOT in rows (they are not
        //   El-ekstern themselves), but have a blank delivery category in Xellent — these are
        //   valid joint-group anchors (common pattern in Xellent for cross-contract joint invoicing).
        // Excluded: referenced contracts with a specific non-El-ekstern, non-blank delivery
        //   category (e.g. Vand/Varme) — a child El-ekstern contract must not inherit such a
        //   contract's joint-invoice group id.
        var contractNumsInScope = rows.Select(r => r.ContractNum).ToHashSet(StringComparer.Ordinal);

        // Find references that point outside El-ekstern scope and check if they are valid umbrella anchors.
        var referencedOutOfScope = rows
            .Where(r => !string.IsNullOrEmpty(r.ContractMain) && !contractNumsInScope.Contains(r.ContractMain!))
            .Select(r => r.ContractMain!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var umbrellaContracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in referencedOutOfScope.Chunk(ChunkSize))
        {
            var found = await _context.ExuContractTables
                .Where(c => chunk.Contains(c.Contractnum) && c.Dataareaid == _dataAreaId
                            && (c.Deliverycategory == null || c.Deliverycategory == ""))
                .Select(c => c.Contractnum)
                .ToListAsync(cancellationToken);
            umbrellaContracts.UnionWith(found);
        }

        bool IsInScope(string contractNum) =>
            contractNumsInScope.Contains(contractNum) || umbrellaContracts.Contains(contractNum);

        var allMainRefs = rows
            .Where(r => !string.IsNullOrEmpty(r.ContractMain) && IsInScope(r.ContractMain!))
            .Select(r => r.ContractMain!)
            .ToHashSet(StringComparer.Ordinal);

        // Group to one row per metering-point (deterministic pick: first by MP id, then by ContractNum).
        // Children are excluded by the EgsMeterpointTypecodeperiod parent-filter in the main join.
        var byMp = rows
            .GroupBy(r => r.MeteringPoint, StringComparer.Ordinal)
            .Select(g => g.OrderBy(r => r.ContractNum, StringComparer.Ordinal).First())
            .ToList();

        // The three lookups below — reading interval, expected annual volume, billing method — all
        // hang off the same key shape: DATAAREAID + DELIVERYCATEGORY + attachment + COMPANYID +
        // delpoint number, in that order, which is also the clustered key of all three tables.
        // Filtering on the delpoint number alone is worth nothing: that column holds four distinct
        // values in the whole table (the literal 'Målepunkt' plus 1/2/3), so the optimizer has no
        // choice but to scan — ~5,8 mio. rows read from EXU_READINGTABLEEXT to return 54. Passing
        // the whole key turns that scan into a seek. Measured on one batch: the loose predicate cost
        // 104.497 logical reads on EXU_READINGTABLEEXT, 4.850 on EXU_TIMEREADINGSPERIOD and 4.928 on
        // EXU_BILLINGMETHODPERIOD; a query naming the exact key tuples cost 384, 269 and 265.
        // The code below sits between the two: it pushes three separate IN lists (delpoint numbers,
        // attachment numbers, company ids), so it matches the cross product of a chunk's values —
        // the same seek, but reading a bounded superset of the real keys, because the lists are
        // combined rather than paired. DELIVERYCATEGORY is not optional — it is the second key
        // column, and leaving it out is measurably slower than the loose predicate was.
        //
        // The values do not change. A full outer join under both predicates returned zero differing
        // rows on all three tables; the grouping by the fuller key further down was already throwing
        // the collisions away, so the extra rows were fetched and never used. The filter is what
        // makes the index usable, nothing else.
        //
        // Chunked on the whole key tuple, so a group never straddles two queries.
        var lookupChunks = byMp
            .Select(r => (Delpointnum: r.Delpoint.Num, r.Delpoint.Attachmentnum, r.Delpoint.Companyid))
            .Distinct()
            .Chunk(DelpointKeyChunkSize)
            .Select(c => (
                Delpoints: c.Select(k => k.Delpointnum).Distinct().ToArray(),
                Attachments: c.Select(k => k.Attachmentnum).Distinct().ToArray(),
                Companies: c.Select(k => k.Companyid).Distinct().ToArray()))
            .ToList();

        // Reading-interval lookup: ExuTimereadingsPeriod.Seriesresolution per (Delpointnum, Attachmentnum).
        // Seriesresolution == 1 → QuarterHourly (15 min), else Hourly (1 h).
        var trpRows = new List<(string Delpointnum, string Attachmentnum, string Companyid, DateTime Startdate, int Seriesresolution)>();
        foreach (var keys in lookupChunks)
        {
            var page = await _context.ExuTimereadingsPeriods
                .Where(t => t.Dataareaid == _dataAreaId
                    && t.Deliverycategory == deliveryCategory
                    && keys.Attachments.Contains(t.Attachmentnum)
                    && keys.Companies.Contains(t.Companyid)
                    && keys.Delpoints.Contains(t.Delpointnum))
                .Select(t => new { t.Delpointnum, t.Attachmentnum, t.Companyid, t.Startdate, t.Seriesresolution })
                .ToListAsync(cancellationToken);
            trpRows.AddRange(page.Select(t => (t.Delpointnum, t.Attachmentnum, t.Companyid, t.Startdate, t.Seriesresolution)));
        }
        var seriesResolutionByDelpoint = trpRows
            .GroupBy(t => (t.Delpointnum, t.Attachmentnum, t.Companyid))
            .ToDictionary(g => g.Key, g => (int?)g.OrderByDescending(t => t.Startdate).First().Seriesresolution);

        // EstimatedAnnualVolume lookup: latest ExuReadingTableExt.Expectedconsumptionperyear
        // per (Delpointnum, Agreementnum, Companyid). Only included when > 0.
        // Grouping stays correct per chunk: the group key is the same tuple the chunks partition on,
        // so no group is split across two queries. Note that the attachment column here is
        // AGREEMENTNUM — it carries the delivery point's Attachmentnum. The table also has a column
        // named ATTACHMENT; it is a different thing and matches nothing.
        //
        // Off unless the caller asks via MapperOptions.IncludeEstimatedAnnualVolume — and the only one
        // of the three delpoint-keyed lookups here that may be skipped at all. The market overwrites
        // the value in Settl once delivery activates, so migrating it buys nothing; that reasoning
        // applies to this lookup and no other. The billing-method read below and the reading-interval
        // read above must stay unconditional: settlement method falls back to the meter type code
        // when the billing-method row is missing, so dropping that query yields a different value
        // rather than an empty one. See the flag's documentation.
        var latestReadings = new List<(string Delpointnum, string Agreementnum, string Companyid, decimal Expectedconsumptionperyear)>();
        if (_options.IncludeEstimatedAnnualVolume)
        {
            foreach (var keys in lookupChunks)
            {
                var page = await _context.ExuReadingTableExts
                    .Where(r => r.Dataareaid == _dataAreaId
                        && r.Deliverycategory == deliveryCategory
                        && keys.Attachments.Contains(r.Agreementnum)
                        && keys.Companies.Contains(r.Companyid)
                        && keys.Delpoints.Contains(r.Delpointnum))
                    .GroupBy(r => new { r.Delpointnum, r.Agreementnum, r.Companyid })
                    .Select(g => g.OrderByDescending(r => r.Readingdate)
                                  .ThenByDescending(r => r.Recid)
                                  .Select(r => new { r.Delpointnum, r.Agreementnum, r.Companyid, r.Expectedconsumptionperyear })
                                  .First())
                    .ToListAsync(cancellationToken);
                latestReadings.AddRange(page.Select(r => (r.Delpointnum, r.Agreementnum, r.Companyid, r.Expectedconsumptionperyear)));
            }
        }
        var expectedKwhByDelpoint = latestReadings
            .Where(r => r.Expectedconsumptionperyear > 0)
            .ToDictionary(
                r => (r.Delpointnum, r.Agreementnum, r.Companyid),
                r => (double?)r.Expectedconsumptionperyear);

        var billingMethodRows = new List<(string Delpointnum, string Attachmentnum, string Companyid, DateTime Startdate, int Billingmethod)>();
        foreach (var keys in lookupChunks)
        {
            var page = await _context.ExuBillingMethodPeriods
                .Where(b => b.Dataareaid == _dataAreaId
                    && b.Deliverycategory == deliveryCategory
                    && keys.Attachments.Contains(b.Attachmentnum)
                    && keys.Companies.Contains(b.Companyid)
                    && keys.Delpoints.Contains(b.Delpointnum)
                    && b.Startdate <= today)
                .Select(b => new { b.Delpointnum, b.Attachmentnum, b.Companyid, b.Startdate, b.Billingmethod })
                .ToListAsync(cancellationToken);
            billingMethodRows.AddRange(page.Select(b => (b.Delpointnum, b.Attachmentnum, b.Companyid, b.Startdate, b.Billingmethod)));
        }
        var billingMethodByDelpoint = billingMethodRows
            .GroupBy(b => (b.Delpointnum, b.Attachmentnum, b.Companyid))
            .ToDictionary(g => g.Key, g => (int?)g.OrderByDescending(b => b.Startdate).First().Billingmethod);

        var contactsByMp = await LoadDelpointContactPointsAsync(
            byMp.Select(r => r.MeteringPoint).ToArray(), deliveryCategory, cancellationToken);

        var meteringPoints = byMp
            .OrderBy(r => r.MeteringPoint, StringComparer.Ordinal)
            .Select(r =>
        {
            var legalContact = contactsByMp.GetValueOrDefault(r.MeteringPoint)
                ?.FirstOrDefault(cp => cp.Type == ContactPointType.Legal);
            var legalDarId = legalContact?.Address.Dar?.Identifier.ToString();

            var address = AddressMapper.ResolveDelpointAddress(
                new DelpointAddressInput(
                    StreetName: r.StreetName,
                    HouseNumberStart: r.Delpoint.Housenumstart ?? 0,
                    HouseLetterStart: r.Delpoint.Houseletterstart,
                    Floor: r.Delpoint.Floor,
                    Apartment: r.Delpoint.Apartment,
                    Zipcode: r.Delpoint.Zipcodeid,
                    City: r.CityName,
                    StreetCode: r.Delpoint.Streetnum,
                    CitySubDivision: r.Delpoint.Addrlocation,
                    MunicipalityCode: r.Delpoint.Countynum,
                    DarIdentifier: legalDarId ?? r.Delpoint.Addressid),
                _warnings,
                $"MP {r.MeteringPoint}");

            var seriesResolution = seriesResolutionByDelpoint.GetValueOrDefault(
                (r.Delpoint.Num, r.Delpoint.Attachmentnum, r.Delpoint.Companyid));
            var readingInterval = seriesResolution == 1 ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(1);
            var readingOccurrence = readingInterval.TotalMinutes == 15
                ? MeterReadingOccurrence.QuarterHourly
                : MeterReadingOccurrence.Hourly;
            var meter = !string.IsNullOrEmpty(r.MeterNum)
                ? new Meter(r.MeterNum, r.MeterCounter ?? 1m, ReadingInterval: readingInterval)
                {
                    Decimals = 0,
                    DigitsOfPrecision = 0,
                    ReadingOccurrence = readingOccurrence,
                    MeteringPointSubType = r.MeterDhSubType is { } dhSubType
                        ? ExtractionMappings.MapMeteringPointSubTypeFromDhCode(dhSubType)
                        : null
                }
                : null;

            var jointGroupId = !string.IsNullOrEmpty(r.ContractMain) && IsInScope(r.ContractMain!)
                ? r.ContractMain
                : allMainRefs.Contains(r.ContractNum) ? r.ContractNum : null;

            var billingMethod = billingMethodByDelpoint.GetValueOrDefault(
                (r.Delpoint.Num, r.Delpoint.Attachmentnum, r.Delpoint.Companyid));
            var grid = new GridConnection(
                PriceArea: MapPriceArea(r.Delpoint.Powerexchangearea),
                GridAreaCode: ExtractionMappings.MapGridArea(r.Delpoint.EmsNetarea ?? string.Empty),
                SettlementMethod: billingMethod.HasValue
                    ? ExtractionMappings.MapBillingMethod(billingMethod.Value)
                    : ExtractionMappings.MapSettlementMethodKind(r.MeterTypeCode),
                Kind: ExtractionMappings.MapMeteringPointKind(r.MeterTypeCode),
                AdditionalInfo: null);
            var contacts = contactsByMp.GetValueOrDefault(r.MeteringPoint) ?? (IReadOnlyList<ContactPoint>)Array.Empty<ContactPoint>();

            var expectedKwh = expectedKwhByDelpoint.GetValueOrDefault(
                (r.Delpoint.Num, r.Delpoint.Attachmentnum, r.Delpoint.Companyid));
            return new MeteringPoint(
                Identification: r.MeteringPoint,
                InstallationAddress: address,
                Commodity: Commodity.Electricity,
                Meter: meter,
                ContactPoints: contacts,
                JointInvoiceGroupId: jointGroupId,
                ConnectionStatus: ConnectionStatus.Connected,
                InDelivery: false,
                Grid: grid,
                Start: DateOnly.FromDateTime(r.AgreementStartdate),
                EstimatedAnnualVolumeKwh: expectedKwh,
                BillingName: NullIfEmpty(r.Instbillingname));
        }).ToList();

        var jointMeteringPoints = DemoteSingletonJointGroups(meteringPoints);
        return new MeteringPointMapResult(jointMeteringPoints, BuildJointInvoiceGroups(jointMeteringPoints));
    }

    private async Task<MeteringPointMapResult> MapInstallationAsync(
        string[] accountNumbers,
        ContractPartRestore restore,
        ContractHeaderExemption? headerExemption,
        CancellationToken cancellationToken)
    {
        var deliveryCategory = _options.Commodity switch
        {
            Commodity.Water => MigrationConstants.DeliveryCategoryWater,
            Commodity.Heat => MigrationConstants.DeliveryCategoryHeating,
            Commodity.EvCharging => MigrationConstants.DeliveryCategoryCharge,
            _ => throw new InvalidOperationException($"Unsupported installation commodity: {_options.Commodity}")
        };
        var today = _options.ReferenceDate.ToDateTime(TimeOnly.MinValue);
        var includeEndedCutoff = _options.IncludeEndedCutoff?.ToDateTime(TimeOnly.MinValue);
        // Parts only — the contract header predicate below keeps the un-widened cutoff.
        var partEndCutoff = _options.IncludeEndedCutoffForParts?.ToDateTime(TimeOnly.MinValue);

        // Same reasoning as the electricity branch: chunked on accounts, with every cross-row
        // derivation running after the full result is accumulated.
        var rowsFor = (string[] chunk) =>
            from cust in _context.CustTables
            join contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForAccounts(chunk))
                on new { cust.Accountnum, cust.Dataareaid }
                equals new { Accountnum = contract.Custaccount, contract.Dataareaid }
            join part in _context.ExuContractPartTables on new { contract.Contractnum, contract.Dataareaid }
                equals new { part.Contractnum, part.Dataareaid }
            join installation in _context.ExuInstallations on new { Instnum = part.Instagreenum, part.Dataareaid }
                equals new { installation.Instnum, installation.Dataareaid }
            join partyAddr in _context.DirPartyAddressRelationships
                on new { cust.Partyid, cust.Dataareaid }
                equals new { partyAddr.Partyid, partyAddr.Dataareaid } into partyJoin
            from partyAddr in partyJoin.Where(p => p.Isprimary == 1).DefaultIfEmpty()
            join addrLink in _context.DirPartyAddressRelationshi1066s
                on new { Recid = partyAddr != null ? partyAddr.Recid : 0L, partyAddr!.Dataareaid }
                equals new { Recid = addrLink.Partyaddressrelationshiprecid, addrLink.Dataareaid } into linkJoin
            from addrLink in linkJoin.DefaultIfEmpty()
            join addr in _context.Addresses
                on new { Recid = addrLink != null ? addrLink.Addressrecid : 0L, addrLink!.Dataareaid }
                equals new { addr.Recid, addr.Dataareaid } into addrJoin
            from addr in addrJoin.Where(a => a.Addrtableid == MigrationConstants.AddressTableId).DefaultIfEmpty()
            where cust.Dataareaid == _dataAreaId
                && chunk.Contains(cust.Accountnum)
                && part.Deliverycategory == deliveryCategory
                && (part.Enddate == EndDate || part.Enddate >= today
                    || (partEndCutoff != null && part.Enddate >= partEndCutoff))
                && (_options.ProductCodeFilter == null
                    || (_options.AdditionalAccounts != null && _options.AdditionalAccounts.Contains(part.Instagreenum))
                    || part.Productnum == _options.ProductCodeFilter)
            select new
            {
                ContractMain = contract.Referencetomaincontract,
                ContractNum = contract.Contractnum,
                PartStartdate = part.Startdate,
                Installation = installation,
                // Carried so the part-level date rule can be re-applied in memory after a restore.
                PartInstagreenum = part.Instagreenum,
                PartCompanyid = part.Companyid,
                PartProductnum = part.Productnum,
                PartAmrcode = part.Amrcode,
                PartEnddate = part.Enddate,
                CustomerStreet = addr != null ? addr.Street : null,
                CustomerZipcode = addr != null ? addr.Zipcode : null,
                CustomerCity = addr != null ? addr.City : null,
                CustomerCountry = addr != null ? addr.Countryregionid : null
            };

        var chunks = accountNumbers.Chunk(ChunkSize).ToList();
        var rows = await rowsFor(chunks[0]).ToListAsync(cancellationToken);
        for (var i = 1; i < chunks.Count; i++)
            rows.AddRange(await rowsFor(chunks[i]).ToListAsync(cancellationToken));

        // Restore, then re-filter — same rule as the electricity branch, end date only: this query
        // caps no start date either.
        rows = rows
            .Where(r => ContractPartInclusion.EndDateIncluded(
                restore.RestoredEndDate(new ContractPartLogKey(
                    r.ContractNum, r.PartInstagreenum, deliveryCategory, r.PartCompanyid,
                    r.PartProductnum, r.PartAmrcode, r.PartStartdate)) ?? r.PartEnddate,
                today,
                includeEndedCutoff))
            .ToList();

        // Deleted installation-based parts: contract → installation, with the customer address the
        // main query resolves, appended in the same shape. None were seen in the 2026-08-31 incident
        // (every deleted part there was electricity), but a contract link with no metering point
        // behind it would be worse than an unused path.
        var restoredParts = restore.IsEmpty
            ? new List<RestoredContractPart>()
            : restore.DeletedPartsFor(deliveryCategory)
                .Where(p => ContractPartInclusion.EndDateIncluded(p.EndDate, today, includeEndedCutoff))
                .Where(p => _options.ProductCodeFilter == null
                    || (_options.AdditionalAccounts != null && _options.AdditionalAccounts.Contains(p.Key.InstAgreeNum))
                    || p.Key.ProductNum == _options.ProductCodeFilter)
                .ToList();

        if (restoredParts.Count > 0)
        {
            var accountSet = accountNumbers.ToHashSet(StringComparer.Ordinal);
            var restoredContractNums = restoredParts.Select(p => p.Key.ContractNum).Distinct(StringComparer.Ordinal).ToArray();

            var contextByContract = new Dictionary<string, (string? Main, string? Street, string? Zipcode, string? City, string? Country)>(StringComparer.Ordinal);
            foreach (var chunk in restoredContractNums.Chunk(ChunkSize))
            {
                var page = await (
                    from cust in _context.CustTables
                    join contract in ContractHeaderScope.InDateWindow(_context, today, includeEndedCutoff, headerExemption?.ForContracts(chunk))
                        on new { cust.Accountnum, cust.Dataareaid }
                        equals new { Accountnum = contract.Custaccount, contract.Dataareaid }
                    join partyAddr in _context.DirPartyAddressRelationships
                        on new { cust.Partyid, cust.Dataareaid }
                        equals new { partyAddr.Partyid, partyAddr.Dataareaid } into partyJoin
                    from partyAddr in partyJoin.Where(p => p.Isprimary == 1).DefaultIfEmpty()
                    join addrLink in _context.DirPartyAddressRelationshi1066s
                        on new { Recid = partyAddr != null ? partyAddr.Recid : 0L, partyAddr!.Dataareaid }
                        equals new { Recid = addrLink.Partyaddressrelationshiprecid, addrLink.Dataareaid } into linkJoin
                    from addrLink in linkJoin.DefaultIfEmpty()
                    join addr in _context.Addresses
                        on new { Recid = addrLink != null ? addrLink.Addressrecid : 0L, addrLink!.Dataareaid }
                        equals new { addr.Recid, addr.Dataareaid } into addrJoin
                    from addr in addrJoin.Where(a => a.Addrtableid == MigrationConstants.AddressTableId).DefaultIfEmpty()
                    where cust.Dataareaid == _dataAreaId
                        && chunk.Contains(contract.Contractnum)
                    select new
                    {
                        contract.Contractnum,
                        cust.Accountnum,
                        ContractMain = contract.Referencetomaincontract,
                        CustomerStreet = addr != null ? addr.Street : null,
                        CustomerZipcode = addr != null ? addr.Zipcode : null,
                        CustomerCity = addr != null ? addr.City : null,
                        CustomerCountry = addr != null ? addr.Countryregionid : null
                    }
                ).ToListAsync(cancellationToken);

                foreach (var c in page.Where(c => accountSet.Contains(c.Accountnum)))
                    contextByContract[c.Contractnum] =
                        (c.ContractMain, c.CustomerStreet, c.CustomerZipcode, c.CustomerCity, c.CustomerCountry);
            }

            var restoredInstNums = restoredParts.Select(p => p.Key.InstAgreeNum).Distinct(StringComparer.Ordinal).ToArray();
            var installationByNum = new Dictionary<string, DataMigration.XellentData.Entities.ExuInstallation>(StringComparer.Ordinal);
            foreach (var chunk in restoredInstNums.Chunk(ChunkSize))
            {
                var page = await _context.ExuInstallations
                    .Where(i => i.Dataareaid == _dataAreaId && chunk.Contains(i.Instnum))
                    .ToListAsync(cancellationToken);
                foreach (var installation in page)
                    installationByNum[installation.Instnum] = installation;
            }

            foreach (var part in restoredParts)
            {
                if (!contextByContract.TryGetValue(part.Key.ContractNum, out var ctx)) continue;
                if (!installationByNum.TryGetValue(part.Key.InstAgreeNum, out var installation)) continue;

                // Same anonymous shape as the main query — drift stops compiling here.
                rows.Add(new
                {
                    ContractMain = ctx.Main,
                    ContractNum = part.Key.ContractNum,
                    PartStartdate = part.Key.PartStartDate,
                    Installation = installation,
                    PartInstagreenum = part.Key.InstAgreeNum,
                    PartCompanyid = part.Key.CompanyId,
                    PartProductnum = part.Key.ProductNum,
                    PartAmrcode = part.Key.AmrCode,
                    PartEnddate = part.EndDate,
                    CustomerStreet = ctx.Street,
                    CustomerZipcode = ctx.Zipcode,
                    CustomerCity = ctx.City,
                    CustomerCountry = ctx.Country
                });
            }
        }

        if (rows.Count == 0)
            return new MeteringPointMapResult(Array.Empty<MeteringPoint>(), Array.Empty<JointInvoiceGroup>());

        var instNums = rows.Select(r => r.Installation.Instnum).Distinct().ToList();
        // Grouped per chunk: the group key is the installation number the chunks partition on.
        var meterByInstallation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in instNums.Chunk(ChunkSize))
        {
            var page = await (
                from mj in _context.ExuMeterJournalTrans
                where mj.Dataareaid == _dataAreaId
                    && mj.Deliverycategory == deliveryCategory
                    && mj.Posted == 1
                    && chunk.Contains(mj.Installationnum)
                group mj by mj.Installationnum into g
                select new
                {
                    Installationnum = g.Key,
                    Meternum = g.OrderByDescending(m => m.Transdate).ThenByDescending(m => m.Recid).First().Meternum
                }
            ).ToListAsync(cancellationToken);
            foreach (var x in page)
                meterByInstallation[x.Installationnum] = x.Meternum;
        }

        var allMainRefs = rows
            .Where(r => !string.IsNullOrEmpty(r.ContractMain))
            .Select(r => r.ContractMain!)
            .ToHashSet(StringComparer.Ordinal);

        var meteringPoints = rows
            .GroupBy(r => r.Installation.Instnum, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Installation.Instnum, StringComparer.Ordinal)
            .Select(r =>
            {
                var address = AddressMapper.ResolveInstallationAddress(
                    new InstallationAddressInput(
                        CustomerStreet: r.CustomerStreet,
                        Zipcode: r.CustomerZipcode,
                        City: r.CustomerCity,
                        CountryCode: !string.IsNullOrEmpty(r.CustomerCountry) ? r.CustomerCountry : null,
                        DarIdentifier: r.Installation.Addressid),
                    _warnings,
                    $"MP V{r.Installation.Instnum}01");

                var meterNum = meterByInstallation.GetValueOrDefault(r.Installation.Instnum);
                var meter = !string.IsNullOrEmpty(meterNum)
                    ? new Meter(meterNum, MultiplicationFactor: 1m, ReadingInterval: null)
                    {
                        Decimals = 1,
                        DigitsOfPrecision = 1,
                        ReadingOccurrence = MeterReadingOccurrence.Hourly
                    }
                    : null;

                var jointGroupId = !string.IsNullOrEmpty(r.ContractMain)
                    ? r.ContractMain
                    : allMainRefs.Contains(r.ContractNum) ? r.ContractNum : null;

                return new MeteringPoint(
                    Identification: $"V{r.Installation.Instnum}01",
                    InstallationAddress: address,
                    Commodity: _options.Commodity,
                    Meter: meter,
                    ContactPoints: Array.Empty<ContactPoint>(),
                    JointInvoiceGroupId: jointGroupId,
                    ConnectionStatus: ConnectionStatus.Connected,
                    InDelivery: false,
                    Grid: null,
                    Start: DateOnly.FromDateTime(r.PartStartdate));
            })
            .ToList();

        var jointMeteringPoints = DemoteSingletonJointGroups(meteringPoints);
        return new MeteringPointMapResult(jointMeteringPoints, BuildJointInvoiceGroups(jointMeteringPoints));
    }

    /// <summary>
    /// Demotes single-member joint groups back to solo. A <see cref="MeteringPoint.JointInvoiceGroupId"/>
    /// is only meaningful when ≥2 migrated MPs share it — a joint invoice means several MPs on one
    /// invoice. The group-id is assigned whenever a contract references a main contract, but that main
    /// may be an old multi-commodity (vand/varme) umbrella whose only migrated leg is this single
    /// electricity MP. Such a group has one member, and billing must NOT mark it JointInvoice=true.
    /// Nulling the id here keeps the billing flag (derived directly from the id in SettlClient) aligned
    /// with the ≥2-member rule already applied to contract links — without it, single-MP cases surface
    /// as false "should be joint" drift in reconcile.
    /// </summary>
    internal static IReadOnlyList<MeteringPoint> DemoteSingletonJointGroups(IReadOnlyList<MeteringPoint> meteringPoints)
    {
        var sizeById = meteringPoints
            .Where(mp => !string.IsNullOrEmpty(mp.JointInvoiceGroupId))
            .GroupBy(mp => mp.JointInvoiceGroupId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return meteringPoints
            .Select(mp => !string.IsNullOrEmpty(mp.JointInvoiceGroupId)
                          && sizeById.TryGetValue(mp.JointInvoiceGroupId!, out var members) && members < 2
                ? mp with { JointInvoiceGroupId = null }
                : mp)
            .ToList();
    }

    /// <summary>
    /// Groups MPs by their <see cref="MeteringPoint.JointInvoiceGroupId"/> — one
    /// <see cref="JointInvoiceGroup"/> per non-null group-id. Solo MPs (group-id null)
    /// are excluded from the resulting list. Run after <see cref="DemoteSingletonJointGroups"/>,
    /// so every resulting group has ≥2 members.
    /// </summary>
    private static IReadOnlyList<JointInvoiceGroup> BuildJointInvoiceGroups(IReadOnlyList<MeteringPoint> meteringPoints)
    {
        return meteringPoints
            .Where(mp => !string.IsNullOrEmpty(mp.JointInvoiceGroupId))
            .GroupBy(mp => mp.JointInvoiceGroupId!, StringComparer.Ordinal)
            .Select(g => new JointInvoiceGroup(
                Id: g.Key,
                MasterContractNumber: g.Key,
                MemberMeteringPointIds: g.Select(mp => mp.Identification).OrderBy(x => x, StringComparer.Ordinal).ToList()))
            .OrderBy(grp => grp.Id, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<ContactPoint>>> LoadDelpointContactPointsAsync(
        string[] meteringPointIds,
        string deliveryCategory,
        CancellationToken cancellationToken)
    {
        if (meteringPointIds.Length == 0)
            return new Dictionary<string, IReadOnlyList<ContactPoint>>(StringComparer.Ordinal);

        // Grouped per chunk rather than across the whole result: the chunks partition the metering
        // point ids and the query filters on Meteringpoint, so every row for a given MP lands in
        // the same chunk. A whole-commodity run has tens of thousands of ids here.
        var result = new Dictionary<string, IReadOnlyList<ContactPoint>>(StringComparer.Ordinal);
        foreach (var chunkIds in meteringPointIds.Chunk(ChunkSize))
        {
        var data = await (
            from contactInfo in _context.ExuDelpointContactInfoTables
            join zip in _context.AddressZipCodes on new { Zipcode = contactInfo.Zipcode, contactInfo.Dataareaid }
                equals new { zip.Zipcode, zip.Dataareaid } into zipJoin
            from zip in zipJoin.DefaultIfEmpty()
            join ownAddr in _context.Addresses on new { Addressid = contactInfo.Addressid, contactInfo.Dataareaid }
                equals new { Addressid = ownAddr.ExuAddressid, ownAddr.Dataareaid } into ownAddrJoin
            from ownAddr in ownAddrJoin.DefaultIfEmpty()
            where contactInfo.Dataareaid == _dataAreaId
                && contactInfo.Deliverycategory == deliveryCategory
                && contactInfo.Enddate == EndDate
                && contactInfo.Contacttype == 4
                && chunkIds.Contains(contactInfo.Meteringpoint)
            select new
            {
                contactInfo.Meteringpoint,
                contactInfo.Startdate,
                // Monotonic and unique per contact row — the last tie-break in every pick below,
                // which is what makes those picks total rather than dependent on SQL row order.
                contactInfo.Recid,
                contactInfo.Name1,
                contactInfo.Name2,
                contactInfo.Streetname,
                contactInfo.Housenumber,
                contactInfo.Floor,
                contactInfo.Apartment,
                contactInfo.Zipcode,
                CityName = zip != null ? zip.City : null,
                ZipCountryCode = zip != null ? zip.Countryregionid : null,
                contactInfo.Addressid,
                // A blank Addressid must never resolve a country from ownAddr — dbo.ADDRESS carries
                // ~140k rows with EXU_ADDRESSID = '' across every country in production, so the join
                // above fans out against all of them on a blank key. Without this guard,
                // the row pick below would arbitrarily take one of those rows' countries, silently
                // overriding the correct ZipCountryCode fallback. Same rule as
                // CustomerMapper.FetchLegalAddressFallbacksAsync's contactInfo.Addressid != null/""
                // filter, applied here to the derived country instead of the outer row: dropping the
                // row would lose name/phone/email too.
                //
                // This query does NOT return at most one Contacttype=4 row per metering point — an
                // earlier comment here claimed it did, and the pick below was built on that. Measured
                // on AXDB50_TEST 'hol': 2.537 metering points have two or more open Contacttype=4
                // rows sharing one Startdate, and 21 of those pairs differ in NAME2 alone.
                OwnCountryCode = (!string.IsNullOrEmpty(contactInfo.Addressid) && ownAddr != null)
                    ? ownAddr.Countryregionid
                    : null,
                contactInfo.Phone,
                contactInfo.Mobile,
                contactInfo.Email
            }
        ).ToListAsync(cancellationToken);

        foreach (var kv in data
            .GroupBy(x => x.Meteringpoint, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    // Same zip-collision exposure as CustomerMapper.FetchLegalAddressFallbacksAsync:
                    // ADDRESSZIPCODE's key is (Dataareaid, Countryregionid, Zipcode, County), so one
                    // zip number can fan out into candidate rows for different countries. Collapse
                    // those candidates per underlying contact row (same street/address, same
                    // Startdate) before picking the contact row to use, so a collision never leaks
                    // into the Startdate tie-break below.
                    //
                    // The key below does not include NAME2, so two genuinely different contact rows
                    // that share an address and a start date but carry different branch names land
                    // in the same group here. PickContactRow decides between them — never the order
                    // the rows came back in.
                    var candidates = g
                        .GroupBy(x => new { x.Startdate, x.Streetname, x.Housenumber, x.Floor, x.Apartment, x.Zipcode, x.Addressid, x.OwnCountryCode })
                        .Select(cg =>
                        {
                            var zipRows = cg.ToList();
                            if (zipRows.Count == 1) return zipRows[0];

                            var distinctCountries = zipRows
                                .Select(x => x.ZipCountryCode)
                                .Where(c => !string.IsNullOrEmpty(c))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count();
                            if (distinctCountries <= 1)
                                return PickContactRow(zipRows, x => x.Name2, x => x.Recid);

                            // Genuine zip collision: prefer the candidate whose ADDRESSZIPCODE
                            // country matches this contact's own resolved country (from its
                            // Addressid). Otherwise deterministic pick + warning — the migration
                            // should not hard-fail over one ambiguous zip, but the pick is a guess.
                            var ownCountry = cg.Key.OwnCountryCode;
                            var matches = zipRows
                                .Where(x => !string.IsNullOrEmpty(ownCountry)
                                    && string.Equals(x.ZipCountryCode, ownCountry, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (matches.Count > 0)
                                return PickContactRow(matches, x => x.Name2, x => x.Recid);

                            // Lowest country code decides which zip row, then the same total rule
                            // decides which contact row inside it.
                            var lowestCountry = zipRows
                                .Select(x => x.ZipCountryCode)
                                .OrderBy(c => c, StringComparer.Ordinal)
                                .First();
                            var fallback = PickContactRow(
                                zipRows.Where(x => string.Equals(x.ZipCountryCode, lowestCountry, StringComparison.Ordinal)),
                                x => x.Name2,
                                x => x.Recid);
                            _warnings.AddWarning(
                                $"MP {g.Key}: legal-contact zip {fallback.Zipcode} collides across countries " +
                                $"in ADDRESSZIPCODE ({string.Join("/", zipRows.Select(x => x.ZipCountryCode).Distinct(StringComparer.OrdinalIgnoreCase))}) " +
                                $"and no own-address country hint matched — picked '{fallback.CityName}' " +
                                $"({fallback.ZipCountryCode}) arbitrarily [Xellent: ADDRESSZIPCODE]");
                            return fallback;
                        })
                        .ToList();

                    // Newest start date wins, as before. Where several candidates share it — the
                    // common case, 2.537 metering points in 'hol' — the branch name decides, and
                    // Recid settles whatever is left. Total, so no comparison ends in a tie and
                    // hands the choice back to whatever order SQL Server returned.
                    var c = candidates
                        .OrderByDescending(x => x.Startdate)
                        .ThenByDescending(x => HasBranchName(x.Name2))
                        .ThenBy(x => x.Recid)
                        .First();
                    var lines = new List<CommunicationLine>();
                    if (!string.IsNullOrEmpty(c.Phone)) lines.Add(new CommunicationLine("Phone", c.Phone));
                    if (!string.IsNullOrEmpty(c.Mobile)) lines.Add(new CommunicationLine("Mobile", c.Mobile));
                    if (!string.IsNullOrEmpty(c.Email)) lines.Add(new CommunicationLine("Email", c.Email));
                    var communicationLines = lines.Count > 0 ? lines : null;
                    var primaryName = c.Name1 ?? string.Empty;
                    var secondaryName = NullIfEmpty(c.Name2);

                    // Country precedence: the contact's own resolved Address row (OwnCountryCode)
                    // is authoritative when it exists — same rule as the account-scoped fallback
                    // fix in CustomerMapper. When it doesn't (Addressid unresolved), the
                    // ADDRESSZIPCODE row picked above is still a real signal; falling straight
                    // through to AddressMapper's DK module default is the last resort.
                    var effectiveCountry = !string.IsNullOrEmpty(c.OwnCountryCode)
                        ? c.OwnCountryCode
                        : (!string.IsNullOrEmpty(c.ZipCountryCode) ? c.ZipCountryCode : null);

                    var address = AddressMapper.ResolveCustomerAddress(
                        new CustomerAddressInput(
                            Street: $"{c.Streetname} {c.Housenumber}".Trim(),
                            Zipcode: c.Zipcode,
                            City: c.CityName,
                            CountryCode: effectiveCountry,
                            DarIdentifier: c.Addressid,
                            LegalFallback: null,
                            Floor: c.Floor,
                            Apartment: c.Apartment),
                        _warnings,
                        $"MP {c.Meteringpoint} contact");

                    // Migrations-policy "aldrig egne data på teknisk": læs kun Contacttype=4 (Legal)
                    // fra Xellent og syntesér TechnicalAddress som identisk kopi.
                    return (IReadOnlyList<ContactPoint>)new List<ContactPoint>
                    {
                        new ContactPoint(
                            Type: ContactPointType.Legal,
                            PrimaryName: primaryName,
                            SecondaryName: secondaryName,
                            Address: address,
                            CommunicationLines: communicationLines),
                        new ContactPoint(
                            Type: ContactPointType.Technical,
                            PrimaryName: primaryName,
                            SecondaryName: secondaryName,
                            Address: address,
                            CommunicationLines: communicationLines)
                    };
                },
                StringComparer.Ordinal))
            result[kv.Key] = kv.Value;
        }

        return result;
    }

    /// <summary>
    /// Picks one contact row out of several the surrounding grouping cannot tell apart, by a rule
    /// that always decides: a populated NAME2 (the branch name — "Sparekassen Ry Afdeling" and
    /// the like) beats a blank one, and RECID settles the rest.
    ///
    /// <para>Both steps matter. The first is the data decision: a branch name is real information
    /// about who the correspondent is, and silently dropping it is the worse of the two outcomes.
    /// The second is what makes the ordering total — without it, rows that tie on NAME2 fall back
    /// to whatever order SQL Server returned, which is not stable between runs of the same build
    /// (measured: five metering points flipped in both directions across two runs).</para>
    /// </summary>
    private static T PickContactRow<T>(IEnumerable<T> rows, Func<T, string?> name2, Func<T, long> recid) =>
        rows.OrderByDescending(r => HasBranchName(name2(r)))
            .ThenBy(r => recid(r))
            .First();

    private static bool HasBranchName(string? name2) => !string.IsNullOrWhiteSpace(name2);

    private static PriceArea MapPriceArea(string? exchangeArea)
    {
        if (string.IsNullOrEmpty(exchangeArea)) return PriceArea.Dk1;
        return exchangeArea.Trim().ToUpperInvariant() switch
        {
            "DK VEST" or "DK1" or "VEST" or "JYLLAND" or "JYLLAND/FYN" => PriceArea.Dk1,
            "DK ØST" or "DK OST" or "DK2" or "ØST" or "SJÆLLAND" => PriceArea.Dk2,
            _ => PriceArea.Dk1
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}

internal sealed record MeteringPointMapResult(
    IReadOnlyList<MeteringPoint> MeteringPoints,
    IReadOnlyList<JointInvoiceGroup> JointInvoiceGroups);

