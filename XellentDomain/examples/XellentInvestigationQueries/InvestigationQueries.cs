using DataMigration.Core;
using DataMigration.XellentData;
using Microsoft.EntityFrameworkCore;

namespace XellentInvestigationQueries;

/// <summary>
/// Read-only, source-scoped examples for investigating Xellent data.
/// The caller supplies a configured XellentDbContext and meaningful input criteria.
/// </summary>
public static class InvestigationQueries
{
    public sealed record SupplyPart(
        string Account, string Contract, string Company, string Category,
        string Installation, string Product, DateTime PartStart, DateTime PartEnd);

    /// <summary>
    /// All agreement parts in force at one installation on the requested day.
    /// Rows remain per part; the caller can inspect multiple products or conflicting contracts.
    /// </summary>
    public static IQueryable<SupplyPart> FindInstallationSupplyParts(
        XellentDbContext db, string dataAreaId, string installationNumber,
        string deliveryCategory, DateOnly day)
    {
        var onDay = day.ToDateTime(TimeOnly.MinValue);
        return (
            from contract in db.ExuContractTables.AsNoTracking()
            join part in db.ExuContractPartTables.AsNoTracking()
                on new { contract.Dataareaid, contract.Contractnum }
                equals new { part.Dataareaid, part.Contractnum }
            join installation in db.ExuInstallations.AsNoTracking()
                on new { part.Dataareaid, Instnum = part.Instagreenum }
                equals new { installation.Dataareaid, installation.Instnum }
            where contract.Dataareaid == dataAreaId
                && installation.Instnum == installationNumber
                && part.Deliverycategory == deliveryCategory
                && contract.Contractstartdate <= onDay
                && (contract.Contractenddate == MigrationConstants.EndDateNull
                    || contract.Contractenddate >= onDay)
                && part.Startdate <= onDay
                && (part.Enddate == MigrationConstants.EndDateNull || part.Enddate >= onDay)
            orderby contract.Custaccount, contract.Contractnum, part.Deliverycategory,
                part.Startdate, part.Recid
            select new SupplyPart(
                contract.Custaccount, contract.Contractnum, part.Companyid,
                part.Deliverycategory, installation.Instnum, part.Productnum,
                part.Startdate, part.Enddate)
        );
    }

    public sealed record PostedInvoiceHeader(
        string InvoiceId, DateTime InvoiceDate, string InvoiceAccount,
        string ContractNumber, string? AgreementHolder, string? Company,
        decimal AmountInAccountingCurrency);

    /// <summary>
    /// Posted invoice headers received by one source account within a half-open date window.
    /// The left join preserves invoices whose referenced agreement is absent.
    /// </summary>
    public static IQueryable<PostedInvoiceHeader> FindPostedInvoices(
        XellentDbContext db, string dataAreaId, string invoiceAccount,
        DateTime fromInclusive, DateTime toExclusive) =>
        (
            from invoice in db.CustInvoiceJours.AsNoTracking()
            join contract in db.ExuContractTables.AsNoTracking()
                on new { invoice.Dataareaid, Contractnum = invoice.ExuContractnum }
                equals new { contract.Dataareaid, contract.Contractnum }
                into contracts
            from contract in contracts.DefaultIfEmpty()
            where invoice.Dataareaid == dataAreaId
                && invoice.Invoiceaccount == invoiceAccount
                && invoice.Invoicedate >= fromInclusive
                && invoice.Invoicedate < toExclusive
            orderby invoice.Invoicedate, invoice.Invoiceid
            select new PostedInvoiceHeader(
                invoice.Invoiceid, invoice.Invoicedate, invoice.Invoiceaccount,
                invoice.ExuContractnum,
                contract == null ? null : contract.Custaccount,
                contract == null ? null : contract.Companyid,
                invoice.Invoiceamountmst)
        );

    public sealed record MeterEvent(
        string Installation, string Company, string Category,
        string Meter, DateTime EventTime, int JournalType, int Posted,
        string JournalId, decimal LineNumber);

    /// <summary>Meter journal events in a scoped installation and time window.</summary>
    public static IQueryable<MeterEvent> FindMeterEvents(
        XellentDbContext db, string dataAreaId, string companyId,
        string deliveryCategory, string installationNumber,
        DateTime fromInclusive, DateTime toExclusive) =>
        db.ExuMeterJournalTrans.AsNoTracking()
            .Where(row => row.Dataareaid == dataAreaId
                && row.Companyid == companyId
                && row.Deliverycategory == deliveryCategory
                && row.Installationnum == installationNumber
                && row.Transdate >= fromInclusive
                && row.Transdate < toExclusive)
            .OrderBy(row => row.Transdate)
            .ThenBy(row => row.Journalid)
            .ThenBy(row => row.Linenum)
            .Select(row => new MeterEvent(
                row.Installationnum, row.Companyid, row.Deliverycategory,
                row.Meternum, row.Transdate, row.Journaltype, row.Posted,
                row.Journalid, row.Linenum));

    public sealed record PointValue(
        long SeriesId, long ValueId, string MeteringPoint, string Company,
        string Category, string QuantityCode, int Resolution,
        DateTime TimeUtc, decimal Value, int Quality);

    /// <summary>
    /// Current-version observations for an explicitly selected company and resolution.
    /// UTC bounds must already represent the requested local business window.
    /// </summary>
    public static IQueryable<PointValue> FindCurrentPointValues(
        XellentDbContext db, string dataAreaId, string meteringPoint,
        string companyId, string deliveryCategory, int timeResolution,
        DateTime fromUtcInclusive, DateTime toUtcExclusive)
    {
        var currentVersion = new DateTime(2154, 12, 31, 23, 59, 59);
        return (
            from series in db.EmsTimeseries.AsNoTracking()
            join value in db.EmsTimeseriesValues.AsNoTracking()
                on new { series.Dataareaid, Timeseriesrefrecid = series.Recid }
                equals new { value.Dataareaid, value.Timeseriesrefrecid }
            where series.Dataareaid == dataAreaId
                && series.Meteringpoint == meteringPoint
                && series.Companyid == companyId
                && series.Deliverycategory == deliveryCategory
                && series.Timeresolution == timeResolution
                && value.Versiontoexcl == currentVersion
                && value.Timeofvalue >= fromUtcInclusive
                && value.Timeofvalue < toUtcExclusive
            orderby value.Timeofvalue, series.Recid, value.Recid
            select new PointValue(
                series.Recid, value.Recid, series.Meteringpoint,
                series.Companyid, series.Deliverycategory, series.Quantitycodevalue,
                series.Timeresolution, value.Timeofvalue, value.Value,
                value.Qualityofvalue)
        );
    }

    public sealed record PartChange(
        string Contract, string InstallationOrAgreement, string Company,
        string Category, string Product, DateTime PreviousStart,
        DateTime PreviousEnd, DateTime ChangedAt, string? Description);

    /// <summary>
    /// Recorded previous part states; this is evidence of edits, not a full
    /// reconstruction of the agreement at every historical instant.
    /// </summary>
    public static IQueryable<PartChange> FindPartChanges(
        XellentDbContext db, string dataAreaId, string contractNumber) =>
        db.ExuContractPartLogs.AsNoTracking()
            .Where(row => row.Dataareaid == dataAreaId
                && row.Contractnum == contractNumber)
            .OrderBy(row => row.Logdatetime)
            .ThenBy(row => row.Recid)
            .Select(row => new PartChange(
                row.Contractnum, row.Instagreenum, row.Companyid,
                row.Deliverycategory, row.Productnum,
                row.Contractpartstartdate, row.Contractpartenddate,
                row.Logdatetime, row.Errormsg));

    public sealed record PostalAddressCandidate(
        string Account, bool IsPrimary, DateTime RelationshipExpires);

    /// <summary>Accounts linked to a postal DAR address, including expired links.</summary>
    public static IQueryable<PostalAddressCandidate> FindPostalDarAccounts(
        XellentDbContext db, string dataAreaId, string darId) =>
        from customer in db.CustTables.AsNoTracking()
        join relationship in db.DirPartyAddressRelationships.AsNoTracking()
            on new { customer.Dataareaid, customer.Partyid }
            equals new { relationship.Dataareaid, relationship.Partyid }
        join addressLink in db.DirPartyAddressRelationshi1066s.AsNoTracking()
            on new { relationship.Dataareaid, Id = relationship.Recid }
            equals new { addressLink.Dataareaid,
                Id = addressLink.Partyaddressrelationshiprecid }
        join address in db.Addresses.AsNoTracking()
            on new { addressLink.Dataareaid, Id = addressLink.Addressrecid }
            equals new { address.Dataareaid, Id = address.Recid }
        where customer.Dataareaid == dataAreaId
            && address.Addrtableid == MigrationConstants.AddressTableId
            && address.ExuAddressid == darId
        select new PostalAddressCandidate(
            customer.Accountnum, relationship.Isprimary == 1,
            relationship.Validtodatetime);

    public sealed record ServiceAddressCandidate(
        string Account, string Contract, string Company, string Category,
        string SourcePointNumber);

    /// <summary>Installation-path agreements associated with one DAR address.</summary>
    public static IQueryable<ServiceAddressCandidate> FindInstallationDarAccounts(
        XellentDbContext db, string dataAreaId, string darId,
        string installationCategory) =>
        from contract in db.ExuContractTables.AsNoTracking()
        join part in db.ExuContractPartTables.AsNoTracking()
            on new { contract.Dataareaid, contract.Contractnum }
            equals new { part.Dataareaid, part.Contractnum }
        join installation in db.ExuInstallations.AsNoTracking()
            on new { part.Dataareaid, Instnum = part.Instagreenum }
            equals new { installation.Dataareaid, installation.Instnum }
        where contract.Dataareaid == dataAreaId
            && installation.Addressid == darId
            && part.Deliverycategory == installationCategory
        select new ServiceAddressCandidate(
            contract.Custaccount, contract.Contractnum,
            part.Companyid, part.Deliverycategory, installation.Instnum);

    /// <summary>Agreement/delpoint-path accounts associated with one DAR address.</summary>
    public static IQueryable<ServiceAddressCandidate> FindDelpointDarAccounts(
        XellentDbContext db, string dataAreaId, string darId,
        string delpointCategory) =>
        from contract in db.ExuContractTables.AsNoTracking()
        join part in db.ExuContractPartTables.AsNoTracking()
            on new { contract.Dataareaid, contract.Contractnum }
            equals new { part.Dataareaid, part.Contractnum }
        join agreement in db.ExuAgreementTables.AsNoTracking()
            on new { part.Dataareaid, part.Companyid,
                part.Deliverycategory, Agreementnum = part.Instagreenum }
            equals new { agreement.Dataareaid, agreement.Companyid,
                agreement.Deliverycategory, agreement.Agreementnum }
        join point in db.ExuDelpoints.AsNoTracking()
            on new { agreement.Dataareaid, agreement.Companyid,
                agreement.Deliverycategory,
                Attachmentnum = agreement.Agreementnum }
            equals new { point.Dataareaid, point.Companyid,
                point.Deliverycategory, point.Attachmentnum }
        where contract.Dataareaid == dataAreaId
            && point.Addressid == darId
            && part.Deliverycategory == delpointCategory
        select new ServiceAddressCandidate(
            contract.Custaccount, contract.Contractnum,
            point.Companyid, point.Deliverycategory, point.Meteringpoint);

    public sealed record PointContactAddressCandidate(
        string Account, string Contract, string MeteringPoint,
        string Company, string Category, int ContactType,
        DateTime Start, DateTime End);

    /// <summary>Dated delpoint-contact address records with the supplied DAR ID.</summary>
    public static IQueryable<PointContactAddressCandidate> FindPointContactDarAccounts(
        XellentDbContext db, string dataAreaId, string darId,
        string delpointCategory) =>
        from contract in db.ExuContractTables.AsNoTracking()
        join part in db.ExuContractPartTables.AsNoTracking()
            on new { contract.Dataareaid, contract.Contractnum }
            equals new { part.Dataareaid, part.Contractnum }
        join agreement in db.ExuAgreementTables.AsNoTracking()
            on new { part.Dataareaid, part.Companyid,
                part.Deliverycategory, Agreementnum = part.Instagreenum }
            equals new { agreement.Dataareaid, agreement.Companyid,
                agreement.Deliverycategory, agreement.Agreementnum }
        join point in db.ExuDelpoints.AsNoTracking()
            on new { agreement.Dataareaid, agreement.Companyid,
                agreement.Deliverycategory,
                Attachmentnum = agreement.Agreementnum }
            equals new { point.Dataareaid, point.Companyid,
                point.Deliverycategory, point.Attachmentnum }
        join contact in db.ExuDelpointContactInfoTables.AsNoTracking()
            on new { point.Dataareaid, point.Companyid,
                point.Deliverycategory, point.Meteringpoint }
            equals new { contact.Dataareaid, contact.Companyid,
                contact.Deliverycategory, contact.Meteringpoint }
        where contract.Dataareaid == dataAreaId
            && contact.Addressid == darId
            && part.Deliverycategory == delpointCategory
        select new PointContactAddressCandidate(
            contract.Custaccount, contract.Contractnum,
            point.Meteringpoint, point.Companyid, point.Deliverycategory,
            contact.Contacttype, contact.Startdate, contact.Enddate);

    public sealed record ReadingResolutionPeriod(
        string MeteringPoint, string Company, string Category,
        string AttachmentNumber, string DelpointNumber,
        DateTime Start, DateTime End, int SeriesResolution);

    /// <summary>All recorded resolution periods for a scoped delpoint.</summary>
    public static IQueryable<ReadingResolutionPeriod> FindReadingResolutionHistory(
        XellentDbContext db, string dataAreaId, string meteringPoint,
        string companyId, string deliveryCategory) =>
        from point in db.ExuDelpoints.AsNoTracking()
        join period in db.ExuTimereadingsPeriods.AsNoTracking()
            on new { point.Dataareaid, point.Companyid,
                point.Deliverycategory, point.Attachmentnum,
                Delpointnum = point.Num }
            equals new { period.Dataareaid, period.Companyid,
                period.Deliverycategory, period.Attachmentnum,
                period.Delpointnum }
        where point.Dataareaid == dataAreaId
            && point.Meteringpoint == meteringPoint
            && point.Companyid == companyId
            && point.Deliverycategory == deliveryCategory
        orderby period.Startdate, point.Attachmentnum, point.Num
        select new ReadingResolutionPeriod(
            point.Meteringpoint, point.Companyid, point.Deliverycategory,
            point.Attachmentnum, point.Num,
            period.Startdate, period.Enddate, period.Seriesresolution);

    public sealed record DelpointMeterAssignment(
        string MeteringPoint, string Company, string Category,
        string AttachmentNumber, string DelpointNumber,
        string MeterNumber, DateTime Start, DateTime End,
        decimal CounterConstant, int DhSubType);

    /// <summary>Meter assignments overlapping a requested interval.</summary>
    public static IQueryable<DelpointMeterAssignment> FindDelpointMeters(
        XellentDbContext db, string dataAreaId, string meteringPoint,
        string companyId, string deliveryCategory,
        DateTime fromInclusive, DateTime toExclusive) =>
        from point in db.ExuDelpoints.AsNoTracking()
        join meter in db.ExuMeterTableTrades.AsNoTracking()
            on new { point.Dataareaid, point.Companyid,
                point.Deliverycategory,
                Agreementnum = point.Attachmentnum,
                Delpointnum = point.Num }
            equals new { meter.Dataareaid, meter.Companyid,
                meter.Deliverycategory, meter.Agreementnum,
                meter.Delpointnum }
        where point.Dataareaid == dataAreaId
            && point.Meteringpoint == meteringPoint
            && point.Companyid == companyId
            && point.Deliverycategory == deliveryCategory
            && meter.Startdate < toExclusive
            && (meter.Enddate == MigrationConstants.EndDateNull
                || meter.Enddate >= fromInclusive)
        orderby meter.Startdate, meter.Recid
        select new DelpointMeterAssignment(
            point.Meteringpoint, point.Companyid, point.Deliverycategory,
            point.Attachmentnum, point.Num, meter.Meternum,
            meter.Startdate, meter.Enddate,
            meter.Counterconstant, meter.MeteringPointDhSubType);
}
