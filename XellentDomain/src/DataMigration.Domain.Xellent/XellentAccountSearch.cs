using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;
using static DataMigration.Core.MigrationConstants;

namespace DataMigration.Domain.Xellent;

/// <summary>
/// Searches Xellent for accounts matching criteria. Returns matched accounts with the
/// CustTable columns + active-contract metadata callers typically need. Mirrors the
/// query layer formerly maintained in NewSettlement.CustomerSupport/CustomerSearchService.
///
/// <para>Direct class (no interface) per <c>XellentDomainMapper</c> convention. For one-call
/// usage with internal DbContext lifecycle, see <see cref="XellentAccountSearchLoader"/>.</para>
/// </summary>
public class XellentAccountSearch
{
    /// <summary>
    /// Collation override used for text searches to avoid Danish "aa" = "å" collation
    /// breaking partial LIKE matches (e.g. "Vesterga%" not matching "Vestergaard").
    /// Applied to Name, Namealias, Street, City, full-address and delpoint/installation
    /// address LIKE comparisons.
    /// </summary>
    private const string TextSearchCollation = "Latin1_General_CI_AS";

    private readonly XellentDbContext _context;
    private readonly IWarningCollector _warnings;
    private readonly XellentAccountSearchOptions _options;

    public XellentAccountSearch(
        XellentDbContext context,
        IWarningCollector warnings,
        XellentAccountSearchOptions options)
    {
        _context = context;
        _warnings = warnings;
        _options = options;
    }

    public async Task<XellentAccountSearchResult> SearchAsync(
        XellentAccountSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        _context.Database.SetCommandTimeout(_options.CommandTimeoutSeconds);

        var today = criteria.AsOfDate;
        var dataAreas = criteria.DataAreas;
        var preFilteredAccounts = criteria.PreFilteredAccounts;

        var streetRelevancePattern = criteria.StreetRelevancePattern ?? "";
        var darIdForRelevance = criteria.DarIdForRelevance ?? "";
        var needsRelevanceScoring =
            !string.IsNullOrEmpty(streetRelevancePattern) || !string.IsNullOrEmpty(darIdForRelevance);

        IQueryable<JoinedRow> query = needsRelevanceScoring
            ? BuildQueryWithRelevance(dataAreas, preFilteredAccounts, today, streetRelevancePattern, darIdForRelevance)
            : BuildQuery(dataAreas, preFilteredAccounts, today);

        // Field-specific filters — skipped when caller pre-filtered (already applied upstream)
        if (preFilteredAccounts == null)
        {
            query = ApplyFieldFilters(query, criteria);
            query = ApplyStreetFilter(query, criteria.Street);
            query = ApplyCityFilter(query, criteria.City);
            query = ApplyZipCodeFilter(query, criteria.ZipCode);
            query = ApplyUniversalTextFilter(query, criteria.UniversalText);
            query = ApplyDarIdStrictFilter(query, criteria.DarIdStrict);
            query = ApplyContractExistenceFilter(query, today, criteria.IncludeInactive);
        }

        // Total before Take — used by callers to surface "X of Y" indicators when
        // results are capped. Only worth the extra round-trip when capped, but the
        // optimization isn't worth the API complexity for the first iteration.
        var totalCount = await query.CountAsync(cancellationToken);

        var matches = await query
            .OrderByDescending(x => x.DarIdRelevance)
            .ThenByDescending(x => x.StreetRelevance)
            .ThenBy(x => x.Customer.Name)
            .ThenBy(x => x.Customer.Accountnum)
            .Take(criteria.MaxResults)
            .Select(x => new XellentAccountMatch(
                x.Customer.Dataareaid,
                x.Customer.Accountnum,
                x.Customer.Name,
                x.Customer.Namealias,
                x.Address.Street,
                x.Address.City,
                x.Address.Zipcode,
                x.Address.Countryregionid,
                x.Customer.Phone,
                x.Customer.Cellularphone,
                x.Customer.Email,
                x.Customer.Identificationnumber,
                x.Customer.Companyregnum,
                x.Customer.ExuCvrDisponent1,
                x.Customer.ExuCprDisponent1,
                x.Customer.ExuTechnicgroup4,
                // WHERE guarantees contract existence; recompute only when caller wants
                // to distinguish Active vs Inactive (IncludeInactive=true path)
                criteria.IncludeInactive
                    ? _context.ExuContractTables.Any(c =>
                        c.Custaccount == x.Customer.Accountnum &&
                        c.Dataareaid == x.Customer.Dataareaid &&
                        (c.Contractenddate == EndDateNull ||
                         (c.Contractenddate > today && c.Contractstartdate <= today)))
                    : true,
                (from c in _context.ExuContractTables
                 join cp in _context.ExuContractPartTables
                     on new { c.Contractnum, c.Dataareaid }
                     equals new { cp.Contractnum, cp.Dataareaid }
                 where c.Custaccount == x.Customer.Accountnum
                     && c.Dataareaid == x.Customer.Dataareaid
                     && c.Contractenddate != EndDateNull
                     && cp.Deliverycategory == DeliveryCategoryElectricity
                 select (DateTime?)c.Contractenddate).Max(),
                x.StreetRelevance,
                x.DarIdRelevance))
            .ToListAsync(cancellationToken);

        return new XellentAccountSearchResult(matches, totalCount);
    }

    /// <summary>Base query: CustTable → DirParty → Address join, scoped to data areas + pre-filtered accounts.</summary>
    private IQueryable<JoinedRow> BuildQuery(
        IReadOnlyList<string>? dataAreas,
        IReadOnlyList<string>? preFilteredAccounts,
        DateTime today)
    {
        return from cust in _context.CustTables
               where (dataAreas == null || dataAreas.Contains(cust.Dataareaid))
                   && (preFilteredAccounts == null || preFilteredAccounts.Contains(cust.Accountnum))
               join partyAddr in _context.DirPartyAddressRelationships
                   on new { cust.Partyid, cust.Dataareaid }
                   equals new { partyAddr.Partyid, partyAddr.Dataareaid }
               join addrLink in _context.DirPartyAddressRelationshi1066s
                   on new { Recid = partyAddr.Recid, partyAddr.Dataareaid }
                   equals new { Recid = addrLink.Partyaddressrelationshiprecid, addrLink.Dataareaid }
               join addr in _context.Addresses
                   on new { Recid = addrLink.Addressrecid, addrLink.Dataareaid }
                   equals new { addr.Recid, addr.Dataareaid }
               where addr.Addrtableid == AddressTableId
                   && partyAddr.Validtodatetime >= today
               select new JoinedRow
               {
                   Customer = cust,
                   Address = addr,
                   StreetRelevance = 0,
                   DarIdRelevance = 0
               };
    }

    /// <summary>Base query with relevance subqueries inlined (only when caller supplied relevance patterns).</summary>
    private IQueryable<JoinedRow> BuildQueryWithRelevance(
        IReadOnlyList<string>? dataAreas,
        IReadOnlyList<string>? preFilteredAccounts,
        DateTime today,
        string streetRelevancePattern,
        string darIdForRelevance)
    {
        return from cust in _context.CustTables
               where (dataAreas == null || dataAreas.Contains(cust.Dataareaid))
                   && (preFilteredAccounts == null || preFilteredAccounts.Contains(cust.Accountnum))
               join partyAddr in _context.DirPartyAddressRelationships
                   on new { cust.Partyid, cust.Dataareaid }
                   equals new { partyAddr.Partyid, partyAddr.Dataareaid }
               join addrLink in _context.DirPartyAddressRelationshi1066s
                   on new { Recid = partyAddr.Recid, partyAddr.Dataareaid }
                   equals new { Recid = addrLink.Partyaddressrelationshiprecid, addrLink.Dataareaid }
               join addr in _context.Addresses
                   on new { Recid = addrLink.Addressrecid, addrLink.Dataareaid }
                   equals new { addr.Recid, addr.Dataareaid }
               where addr.Addrtableid == AddressTableId
                   && partyAddr.Validtodatetime >= today
               let streetRelevance = string.IsNullOrEmpty(streetRelevancePattern) ? 0 :
                   (EF.Functions.Like(addr.FullAddress, streetRelevancePattern) ? 1 : 0)
               let darIdRelevance = string.IsNullOrEmpty(darIdForRelevance) ? 0 :
                   ((addr.ExuAddressid == darIdForRelevance
                     || (from contract in _context.ExuContractTables
                         join part in _context.ExuContractPartTables
                             on new { contract.Contractnum, contract.Dataareaid }
                             equals new { part.Contractnum, part.Dataareaid }
                         join inst in _context.ExuInstallations
                             on new { Instnum = part.Instagreenum, part.Dataareaid }
                             equals new { inst.Instnum, inst.Dataareaid }
                         where contract.Custaccount == cust.Accountnum
                             && contract.Dataareaid == cust.Dataareaid
                             && inst.Addressid == darIdForRelevance
                         select 1).Any()) ? 1 : 0)
               select new JoinedRow
               {
                   Customer = cust,
                   Address = addr,
                   StreetRelevance = streetRelevance,
                   DarIdRelevance = darIdRelevance
               };
    }

    private IQueryable<JoinedRow> ApplyFieldFilters(
        IQueryable<JoinedRow> query,
        XellentAccountSearchCriteria criteria)
    {
        if (criteria.Name is { Patterns.Count: > 0 } name)
        {
            foreach (var pattern in name.Patterns)
            {
                query = query.Where(x =>
                    EF.Functions.Like(EF.Functions.Collate(x.Customer.Name, TextSearchCollation), pattern) ||
                    EF.Functions.Like(EF.Functions.Collate(x.Customer.Namealias, TextSearchCollation), pattern));
            }
        }

        if (!string.IsNullOrWhiteSpace(criteria.AccountNumber))
        {
            var account = criteria.AccountNumber.Trim();
            query = query.Where(x => x.Customer.Accountnum == account);
        }

        if (!string.IsNullOrWhiteSpace(criteria.Email))
        {
            var email = criteria.Email.Trim();
            query = query.Where(x => x.Customer.Email == email);
        }

        if (!string.IsNullOrWhiteSpace(criteria.PhonePattern))
        {
            var phonePattern = criteria.PhonePattern;
            query = query.Where(x =>
                EF.Functions.Like(x.Customer.Phone, phonePattern) ||
                EF.Functions.Like(x.Customer.Cellularphone, phonePattern));
        }

        if (!string.IsNullOrWhiteSpace(criteria.IdentifierPattern))
        {
            var idPattern = criteria.IdentifierPattern;
            query = query.Where(x =>
                EF.Functions.Like(x.Customer.ExuCprDisponent1, idPattern) ||
                EF.Functions.Like(x.Customer.ExuCprDisponent2, idPattern) ||
                EF.Functions.Like(x.Customer.ExuCvrDisponent1, idPattern) ||
                EF.Functions.Like(x.Customer.ExuCvrDisponent2, idPattern));
        }

        if (!string.IsNullOrWhiteSpace(criteria.InvoiceNumberPadded))
        {
            var invoicePadded = criteria.InvoiceNumberPadded;
            query = query.Where(x => _context.CustInvoiceJours.Any(inv =>
                inv.Invoiceid == invoicePadded &&
                inv.Invoiceaccount == x.Customer.Accountnum &&
                inv.Dataareaid == x.Customer.Dataareaid));
        }

        return query;
    }

    /// <summary>
    /// Street/city/zip filter with up to 4 alternative LIKE patterns. Mirrors the
    /// variation-count branching in Bifrost's CustomerSearchService (0/1/2/3+ variants).
    /// </summary>
    private IQueryable<JoinedRow> ApplyStreetFilter(IQueryable<JoinedRow> query, StreetCriteria? street)
    {
        if (street is null || street.Patterns.Count == 0)
            return query;

        var patterns = street.Patterns;
        var p0 = patterns[0];
        var p1 = patterns.Count > 1 ? patterns[1] : null;
        var p2 = patterns.Count > 2 ? patterns[2] : null;
        var p3 = patterns.Count > 3 ? patterns[3] : null;
        var alsoSearchCityAndZip = street.AlsoSearchCityAndZip;

        // The OR-chain checks: x.Address.FullAddress (and variants), optionally City+Zipcode,
        // plus contract → agreement → delpoint addresses and contract → installation addresses.
        return query.Where(x =>
            EF.Functions.Like(EF.Functions.Collate(x.Address.FullAddress, TextSearchCollation), p0) ||
            (p1 != null && EF.Functions.Like(EF.Functions.Collate(x.Address.FullAddress, TextSearchCollation), p1)) ||
            (p2 != null && EF.Functions.Like(EF.Functions.Collate(x.Address.FullAddress, TextSearchCollation), p2)) ||
            (p3 != null && EF.Functions.Like(EF.Functions.Collate(x.Address.FullAddress, TextSearchCollation), p3)) ||
            (alsoSearchCityAndZip && EF.Functions.Like(EF.Functions.Collate(x.Address.City, TextSearchCollation), p0)) ||
            (alsoSearchCityAndZip && EF.Functions.Like(x.Address.Zipcode, p0)) ||
            (from contract in _context.ExuContractTables
             join part in _context.ExuContractPartTables
                 on new { contract.Contractnum, contract.Dataareaid }
                 equals new { part.Contractnum, part.Dataareaid }
             join agreement in _context.ExuAgreementTables
                 on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                 equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
             join dp in _context.ExuDelpoints
                 on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                 equals new { Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
             where contract.Custaccount == x.Customer.Accountnum
                 && contract.Dataareaid == x.Customer.Dataareaid
                 && (EF.Functions.Like(EF.Functions.Collate(dp.Addressstreet, TextSearchCollation), p0)
                     || (alsoSearchCityAndZip && EF.Functions.Like(dp.Zipcodeid, p0)))
             select 1).Any() ||
            (from contract in _context.ExuContractTables
             join part in _context.ExuContractPartTables
                 on new { contract.Contractnum, contract.Dataareaid }
                 equals new { part.Contractnum, part.Dataareaid }
             join inst in _context.ExuInstallations
                 on new { Instnum = part.Instagreenum, part.Dataareaid }
                 equals new { inst.Instnum, inst.Dataareaid }
             where contract.Custaccount == x.Customer.Accountnum
                 && contract.Dataareaid == x.Customer.Dataareaid
                 && EF.Functions.Like(EF.Functions.Collate(inst.Instaddressformatted, TextSearchCollation), p0)
             select 1).Any());
    }

    private IQueryable<JoinedRow> ApplyCityFilter(IQueryable<JoinedRow> query, CityCriteria? city)
    {
        if (city is null || city.Patterns.Count == 0)
            return query;

        // Manual OR-chain — IEnumerable.Any cannot be translated against SQL Server
        // compatibility 110 (it would require JSON-array support added in 130).
        var p0 = city.Patterns[0];
        var p1 = city.Patterns.Count > 1 ? city.Patterns[1] : null;
        var p2 = city.Patterns.Count > 2 ? city.Patterns[2] : null;
        var p3 = city.Patterns.Count > 3 ? city.Patterns[3] : null;
        return query.Where(x =>
            EF.Functions.Like(EF.Functions.Collate(x.Address.City, TextSearchCollation), p0) ||
            (p1 != null && EF.Functions.Like(EF.Functions.Collate(x.Address.City, TextSearchCollation), p1)) ||
            (p2 != null && EF.Functions.Like(EF.Functions.Collate(x.Address.City, TextSearchCollation), p2)) ||
            (p3 != null && EF.Functions.Like(EF.Functions.Collate(x.Address.City, TextSearchCollation), p3)));
    }

    private IQueryable<JoinedRow> ApplyZipCodeFilter(IQueryable<JoinedRow> query, ZipCodeCriteria? zip)
    {
        if (zip is null || string.IsNullOrEmpty(zip.Pattern))
            return query;

        var pattern = zip.Pattern;
        return query.Where(x =>
            EF.Functions.Like(x.Address.Zipcode, pattern) ||
            (from contract in _context.ExuContractTables
             join part in _context.ExuContractPartTables
                 on new { contract.Contractnum, contract.Dataareaid }
                 equals new { part.Contractnum, part.Dataareaid }
             join agreement in _context.ExuAgreementTables
                 on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                 equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
             join dp in _context.ExuDelpoints
                 on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                 equals new { Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
             where contract.Custaccount == x.Customer.Accountnum
                 && contract.Dataareaid == x.Customer.Dataareaid
                 && EF.Functions.Like(dp.Zipcodeid, pattern)
             select 1).Any());
    }

    /// <summary>
    /// DAR id strict filter: restricts matches to accounts whose customer-address, delpoint-address,
    /// delpoint-contact-info-address OR installation-address equals the given DAR id. Used by callers
    /// when DAR id is the only address criterion (otherwise prefer <c>DarIdForRelevance</c>).
    /// </summary>
    private IQueryable<JoinedRow> ApplyDarIdStrictFilter(IQueryable<JoinedRow> query, string? darIdStrict)
    {
        if (string.IsNullOrWhiteSpace(darIdStrict))
            return query;

        var id = darIdStrict;
        return query.Where(x =>
            x.Address.ExuAddressid == id
            ||
            (from contract in _context.ExuContractTables
             join part in _context.ExuContractPartTables
                 on new { contract.Contractnum, contract.Dataareaid }
                 equals new { part.Contractnum, part.Dataareaid }
             join agreement in _context.ExuAgreementTables
                 on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                 equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
             join dp in _context.ExuDelpoints
                 on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                 equals new { Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
             where contract.Custaccount == x.Customer.Accountnum
                 && contract.Dataareaid == x.Customer.Dataareaid
                 && dp.Addressid == id
             select 1).Any()
            ||
            (from contract in _context.ExuContractTables
             join part in _context.ExuContractPartTables
                 on new { contract.Contractnum, contract.Dataareaid }
                 equals new { part.Contractnum, part.Dataareaid }
             join agreement in _context.ExuAgreementTables
                 on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                 equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
             join dp in _context.ExuDelpoints
                 on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                 equals new { Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
             join ci in _context.ExuDelpointContactInfoTables
                 on new { dp.Meteringpoint, dp.Deliverycategory, dp.Companyid, dp.Dataareaid }
                 equals new { ci.Meteringpoint, ci.Deliverycategory, ci.Companyid, ci.Dataareaid }
             where contract.Custaccount == x.Customer.Accountnum
                 && contract.Dataareaid == x.Customer.Dataareaid
                 && ci.Addressid == id
             select 1).Any()
            ||
            (from contract in _context.ExuContractTables
             join part in _context.ExuContractPartTables
                 on new { contract.Contractnum, contract.Dataareaid }
                 equals new { part.Contractnum, part.Dataareaid }
             join inst in _context.ExuInstallations
                 on new { Instnum = part.Instagreenum, part.Dataareaid }
                 equals new { inst.Instnum, inst.Dataareaid }
             where contract.Custaccount == x.Customer.Accountnum
                 && contract.Dataareaid == x.Customer.Dataareaid
                 && inst.Addressid == id
             select 1).Any());
    }

    /// <summary>
    /// Universal-text filter: single-string search whose semantics depend on <see cref="UniversalTextMode"/>.
    /// Mirrors the legacy universal-query path in Bifrost CustomerSearchService.
    /// </summary>
    private IQueryable<JoinedRow> ApplyUniversalTextFilter(IQueryable<JoinedRow> query, UniversalTextCriteria? universal)
    {
        if (universal is null || string.IsNullOrEmpty(universal.Query))
            return query;

        var q = universal.Query;
        switch (universal.Mode)
        {
            case UniversalTextMode.Uuid:
                return query.Where(x =>
                    (from contract in _context.ExuContractTables
                     join part in _context.ExuContractPartTables
                         on new { contract.Contractnum, contract.Dataareaid }
                         equals new { part.Contractnum, part.Dataareaid }
                     join agreement in _context.ExuAgreementTables
                         on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                         equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                     join dp in _context.ExuDelpoints
                         on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                         equals new { Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
                     where contract.Custaccount == x.Customer.Accountnum
                         && contract.Dataareaid == x.Customer.Dataareaid
                         && dp.Addressid == q
                     select 1).Any()
                    ||
                    (from contract in _context.ExuContractTables
                     join part in _context.ExuContractPartTables
                         on new { contract.Contractnum, contract.Dataareaid }
                         equals new { part.Contractnum, part.Dataareaid }
                     join agreement in _context.ExuAgreementTables
                         on new { Agreementnum = part.Instagreenum, part.Dataareaid, part.Companyid }
                         equals new { agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                     join dp in _context.ExuDelpoints
                         on new { Attachmentnum = agreement.Agreementnum, agreement.Dataareaid, agreement.Companyid }
                         equals new { Attachmentnum = dp.Attachmentnum, dp.Dataareaid, dp.Companyid }
                     join ci in _context.ExuDelpointContactInfoTables
                         on new { dp.Meteringpoint, dp.Deliverycategory, dp.Companyid, dp.Dataareaid }
                         equals new { ci.Meteringpoint, ci.Deliverycategory, ci.Companyid, ci.Dataareaid }
                     where contract.Custaccount == x.Customer.Accountnum
                         && contract.Dataareaid == x.Customer.Dataareaid
                         && ci.Addressid == q
                     select 1).Any());

            case UniversalTextMode.NumericExact6:
                return query.Where(x => x.Customer.Accountnum == q);

            case UniversalTextMode.NumericMixed:
                var phoneNumPattern = $"%{q}%";
                return query.Where(x =>
                    x.Customer.Accountnum == q ||
                    x.Customer.ExuCprDisponent1 == q ||
                    x.Customer.ExuCprDisponent2 == q ||
                    x.Customer.ExuCvrDisponent1 == q ||
                    x.Customer.ExuCvrDisponent2 == q ||
                    EF.Functions.Like(x.Customer.Phone, phoneNumPattern) ||
                    EF.Functions.Like(x.Customer.Cellularphone, phoneNumPattern));

            case UniversalTextMode.FreeText:
                var freeTextPattern = $"%{q}%";
                return query.Where(x =>
                    EF.Functions.Like(EF.Functions.Collate(x.Customer.Name, TextSearchCollation), freeTextPattern) ||
                    EF.Functions.Like(EF.Functions.Collate(x.Customer.Namealias, TextSearchCollation), freeTextPattern) ||
                    EF.Functions.Like(x.Customer.Accountnum, freeTextPattern) ||
                    EF.Functions.Like(x.Customer.Phone, freeTextPattern) ||
                    EF.Functions.Like(x.Customer.Cellularphone, freeTextPattern) ||
                    EF.Functions.Like(x.Customer.Email, freeTextPattern) ||
                    EF.Functions.Like(EF.Functions.Collate(x.Address.FullAddress, TextSearchCollation), freeTextPattern) ||
                    EF.Functions.Like(EF.Functions.Collate(x.Address.City, TextSearchCollation), freeTextPattern) ||
                    EF.Functions.Like(x.Address.Zipcode, freeTextPattern));

            default:
                return query;
        }
    }

    private IQueryable<JoinedRow> ApplyContractExistenceFilter(
        IQueryable<JoinedRow> query,
        DateTime today,
        bool includeInactive)
    {
        if (!includeInactive)
        {
            return query.Where(x => _context.ExuContractTables.Any(c =>
                c.Custaccount == x.Customer.Accountnum &&
                c.Dataareaid == x.Customer.Dataareaid &&
                (c.Contractenddate == EndDateNull ||
                 (c.Contractenddate > today && c.Contractstartdate <= today))));
        }

        // Exclude Pending (must have at least one contract)
        return query.Where(x => _context.ExuContractTables.Any(c =>
            c.Custaccount == x.Customer.Accountnum &&
            c.Dataareaid == x.Customer.Dataareaid));
    }

    /// <summary>
    /// Anonymous-projection holder for the joined CustTable + Address + relevance scores.
    /// Defined as a private nested class (not anonymous type) so the queries compose
    /// across method boundaries without losing the type.
    /// </summary>
    private sealed class JoinedRow
    {
        public XellentData.Entities.CustTable Customer { get; init; } = null!;
        public XellentData.Entities.Address Address { get; init; } = null!;
        public int StreetRelevance { get; init; }
        public int DarIdRelevance { get; init; }
    }
}
