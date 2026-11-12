# Development exercises against the Xellent model

Reviewed on 2026-09-22. These are business requests phrased as small development
tasks: retrieve specific source data under explicit conditions. I used the
[business model](utility-business-model.md), [table and join guide](xellent-model-guide.md),
[field semantics](xellent-field-semantics.md), and [schema reference](xellent-schema-reference.md)
to construct the queries, then compared them with the mapped C# entities and
relevant application readers. Eleven query methods in
[InvestigationQueries.cs](../examples/XellentInvestigationQueries/InvestigationQueries.cs)
compile against `XellentDbContext` and generate SQL through EF Core configured
for SQL Server compatibility level 110. D6 uses CustomerSupport's separate EF
model and was checked against that context and its entity source; it was not
compiled or translated in this repository. D10 identifies a question these
mapped fields cannot yet answer precisely. The SQL generation check opens no
database connection.

**Where `InvestigationQueries` comes from:** I added it under
[`examples/XellentInvestigationQueries`](../examples/XellentInvestigationQueries/InvestigationQueries.cs)
for this review. It is sample code, not an existing XellentDomain library or
published API. Its methods contain the actual LINQ queries over the existing
`XellentDbContext` and return `IQueryable<T>`; the calls below execute those
queries with `ToListAsync`. The sample
[`DanishTimeWindows`](../examples/XellentInvestigationQueries/DanishTimeWindows.cs)
helper is new for the same reason. The runnable
[`Program.cs`](../examples/XellentInvestigationQueries/Program.cs) only asks EF
to generate SQL and checks fixed daylight-saving examples.

The requests use hypothetical identifiers and dates. They have not been run
against production data. Successful compilation and SQL generation establish
query construction, not correctness of live results, query plans, or source data.

## D1. Who was supplied with water or heat at this installation on a date?

**Business request:** Given a source installation number, AX data area, and
calendar date, return the customer account, agreement, product, company and
service category in force that day. Support may need to explain an occupancy
change or a product period on a water or heat installation.

```csharp
var waterParts = await InvestigationQueries.FindInstallationSupplyParts(
    db, dataAreaId, installationNumber,
    MigrationConstants.DeliveryCategoryWater, day).ToListAsync(ct);

var heatParts = await InvestigationQueries.FindInstallationSupplyParts(
    db, dataAreaId, installationNumber,
    MigrationConstants.DeliveryCategoryHeating, day).ToListAsync(ct);
```

The implementation joins contract → part by data area and contract number,
then part → installation by data area and installation number. It retains the
**part's** `Companyid` in the result rather than requiring it to match the
contract header's company field. Requiring that equality could exclude
installation paths admitted by the loader. The query checks both contract
and part start/end dates; `1900-01-01` is the open-end
sentinel for these fields. It retains one row per product/part. A point-bearing
contract directly relates to one point, but several parts can describe that
point; do not count these rows as distinct points or silently select the first.
The caller explicitly selects an installation-based category. Another category
may require the agreement/delpoint path instead. [Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs)
and [source mapping](../src/DataMigration.Domain.Xellent/Internal/MeteringPointMapper.cs).

**Assessment:** Constructable from the existing relationship and date guidance.
The exercise makes the category branch and two simultaneous date predicates
concrete. For a general historical report, retain contradictory candidate
contracts rather than adopting an application's tie-break as business truth.

## D2. Which posted invoices went to an account last quarter, and whose agreements do they cite?

**Business request:** For a specified invoice account and half-open calendar
window, list posted invoice headers, agreement numbers, agreement holders and
accounting-currency amounts. A billed recipient can differ from the agreement
holder, and a missing agreement should remain visible for investigation.

```csharp
var invoiceHeaders = await InvestigationQueries.FindPostedInvoices(
    db, dataAreaId, invoiceAccount, fromInclusive, toExclusive)
    .ToListAsync(ct);
```

The implementation selects `CUSTINVOICEJOUR` by `(DATAAREAID, INVOICEACCOUNT)`
and joins the contract through `(DATAAREAID, EXU_CONTRACTNUM)` with a left join.
The result preserves `Invoiceaccount` and `Custaccount` as separate roles.
`INVOICEAMOUNTMST` is the source's accounting-currency amount, not an assertion
about an individual line or payment status. This query deliberately has no
CustomerSupport screen's 24-month cutoff. [Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs)
and [invoice semantics](xellent-field-semantics.md#invoices-payments-and-customer-transactions).

**Assessment:** Constructable for invoice **headers** in `XellentDbContext`.
The guide identifies the required different context for posted **lines**; D6
checks that path. Absence of a PDF or contract screen row does not exclude a
posted invoice header.

## D3. What meter evidence exists around a suspected water-meter replacement?

**Business request:** For a given installation, company, water service category
and short time window, retrieve dated meter journal events. The support worker
also needs readings marked as replacements to compare register values before
and after the event.

```csharp
var events = await InvestigationQueries.FindMeterEvents(
    db, dataAreaId, companyId, MigrationConstants.DeliveryCategoryWater,
    installationNumber, fromInclusive, toExclusive).ToListAsync(ct);
```

The compiled query returns `Meternum`, `Transdate`, `Journaltype`, `Posted`,
`Journalid` and `Linenum`, scoped by data area, company, category and
installation. It includes posted and unposted rows so the caller can examine
both. The current-meter choice elsewhere filters posted rows and takes the
latest date; that choice would hide earlier events.

Replacement **readings** live in CustomerSupport's `ExuReadingTables`, which
`XellentDbContext` does not expose. This additional query uses that context:

```csharp
var replacementReadings = await supportContext.ExuReadingTables.AsNoTracking()
    .Where(r => r.Dataareaid == dataAreaId
        && r.Companyid == companyId
        && r.Deliverycategory == MigrationConstants.DeliveryCategoryWater
        && r.Instnum == installationNumber
        && r.Readingdate >= fromInclusive
        && r.Readingdate < toExclusive
        && r.Reasontoreading == 1)
    .OrderBy(r => r.Readingdate)
    .Select(r => new { r.Instnum, r.Meternum, r.Counternum,
        r.Readingdate, r.Meterreading, r.Consumption,
        r.Quantitycodevalue, r.Reasontoreading })
    .ToListAsync(ct);
```

CustomerSupport interprets reading reason 1 as replacement; the reviewed model
does not provide a complete label set for meter-journal `JOURNALTYPE`. Keep the
raw meter and counter identities. A register difference across replacement is
not automatically consumption. [Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs),
[reading entity](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/ExuReadingTable.cs),
and [reading semantics](xellent-field-semantics.md#readings-meters-and-units).

**Assessment:** The model correctly directs the developer to two EF contexts
and prevents inventing a journal-code meaning. The CustomerSupport query is
source-checked but has not been compiled in this example project.

## D4. Which stored observations exist for a DataHub electricity point on a Danish day?

**Business request:** Investigate a missing quarter-hour interval for one
national metering-point identifier, source company and local calendar day.
Return the current-version observations, series identity, quality and quantity
code. The source category `El-ekstern` denotes the electricity-supplier market
role; it is not a separate commodity.

```csharp
var (fromUtc, toUtc) = DanishTimeWindows.ForDay(day);

var values = await InvestigationQueries.FindCurrentPointValues(
    db, dataAreaId, meteringPointIdentification, companyId,
    MigrationConstants.DeliveryCategoryElectricity,
    timeResolution: 1, fromUtcInclusive: fromUtc,
    toUtcExclusive: toUtc).ToListAsync(ct);
```

`EMS_TIMESERIES.TIMERESOLUTION = 1` is quarter-hourly. The implementation
joins values by `(DATAAREAID, header RECID)`, restricts the selected company,
category and resolution, selects `VERSIONTOEXCL = 2154-12-31 23:59:59`, and
uses `[fromUtc, toUtc)`. It returns header and value IDs to expose multiple
series instead of silently aggregating them. Local-day duration can vary at
daylight-saving transitions. For attribution to a customer or a company over a
long history, also intersect the relevant dated agreement and reading-resolution
periods. This query only establishes what is **stored** for the supplied scope.
[Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs),
[field semantics](xellent-field-semantics.md#time-series-header-selection), and
[historical extraction](../../TimeSeriesMigration/src/TimeSeries.Migration/Services/TimeSeriesExtractionService.cs).

Convert **both local midnights** to UTC. Adding 24 hours to the first UTC
boundary would select the wrong part of a transition day. The runnable example
checks these two Danish calendar days:

| Local day | Inclusive UTC start | Exclusive UTC end | Duration | Quarter-hour timestamps if the series is complete |
|---|---|---|---:|---:|
| 2025-03-30, clocks forward | 2025-03-29 23:00Z | 2025-03-30 22:00Z | 23 hours | 92 |
| 2025-10-26, clocks back | 2025-10-25 22:00Z | 2025-10-26 23:00Z | 25 hours | 100 |

On the autumn day, local **02:30 occurs twice**. Keep the offset when
displaying the source UTC timestamp:

```csharp
var first = DanishTimeWindows.ToDanishTime(
    new DateTime(2025, 10, 26, 0, 30, 0, DateTimeKind.Utc));
var second = DanishTimeWindows.ToDanishTime(
    new DateTime(2025, 10, 26, 1, 30, 0, DateTimeKind.Utc));
// first:  2025-10-26 02:30 +02:00
// second: 2025-10-26 02:30 +01:00
```

On the spring day, local 02:00–02:59 does not occur. A missing local 02:15
or 02:30 then is not automatically a gap in the stored observations. The 92/100
figures describe uninterrupted 15-minute timestamps inside those UTC windows;
they are not evidence that this particular point is complete or that a given
timestamp denotes a period start rather than an end.

**Assessment:** Constructable, provided the task specifies company, resolution,
version and interval convention. A point identifier alone is insufficient to
choose one header.

## D5. Did a product part disappear from this agreement?

**Business request:** A support worker can no longer see a service product on
one contract. Retrieve the recorded part changes for that contract so the
previous dates and product codes can be examined.

```csharp
var changes = await InvestigationQueries.FindPartChanges(
    db, dataAreaId, contractNumber).ToListAsync(ct);
```

The query retains part `Instagreenum`, category, company, product, dates before
the change, description and log time. `ERRORMSG` is a change description in this
table; it does not necessarily mean an error. A date-change log row does not by
itself mean deletion, and the log alone does not reconstruct a full agreement
snapshot. Join or compare current parts when answering what is live now.
[Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs)
and [log semantics](xellent-field-semantics.md#contract-part-log-is-a-change-history).

**Assessment:** Constructable as an evidence query. The documents correctly
limit the conclusion that may be drawn from those rows.

## D6. Which posted lines actually billed heat to an invoice account?

**Business request:** For an invoice account and date window, list posted heat
invoice lines with their invoice and agreement references. This answers what
was billed more directly than service icons on an invoice list.

`CUSTINVOICETRANS` is mapped in CustomerSupport's
`XellentDatabaseModelContext`. The code below was checked against its entity
properties and relationship configuration; it is **not** part of the compiled
shared-context example.

```csharp
var billedHeatLines = await (
    from invoice in supportContext.CustInvoiceJours.AsNoTracking()
    join line in supportContext.CustInvoiceTranses.AsNoTracking()
        on new { invoice.Dataareaid, invoice.Invoiceid }
        equals new { line.Dataareaid, line.Invoiceid }
    where invoice.Dataareaid == dataAreaId
        && invoice.Invoiceaccount == invoiceAccount
        && invoice.Invoicedate >= fromInclusive
        && invoice.Invoicedate < toExclusive
        && line.ExuDeliverycategory == MigrationConstants.DeliveryCategoryHeating
    orderby invoice.Invoicedate, invoice.Invoiceid, line.Linenum, line.Recid
    select new { invoice.Invoiceid, invoice.Invoiceaccount,
        invoice.ExuContractnum, invoice.Invoicedate,
        line.Recid, line.Itemid, line.ExuDeliverycategory,
        line.ExuCompanyid, line.Qty, line.Salesunit,
        line.Lineamount, line.Currencycode }
).ToListAsync(ct);
```

The `(DATAAREAID, INVOICEID)` join matches the [context relationship](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/XellentDatabaseModelContext.cs).
The line category is a recorded field. A line's amount and currency should be
kept together; this selection alone does not calculate an unpaid balance or
prove the service period. [Posted line entity](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceTrans.cs)
and [invoice semantics](xellent-field-semantics.md#invoices-payments-and-customer-transactions).

**Assessment:** The documentation identifies the correct model and join. The
shared `XellentDbContext` cannot compile this query because it has no posted-line
set; the CustomerSupport model is needed.

## D7. Which accounts are associated with this DAR address, and in what role?

**Business request:** A support worker receives an exact DAR address ID and
needs candidate accounts linked through a postal address, a water/heat
installation, an electricity delpoint, or a delpoint contact record. The worker
must see which route matched before interpreting responsibility for service.

```csharp
var postal = await InvestigationQueries.FindPostalDarAccounts(
    db, dataAreaId, darId).ToListAsync(ct);
var heatInstallations = await InvestigationQueries.FindInstallationDarAccounts(
    db, dataAreaId, darId, MigrationConstants.DeliveryCategoryHeating)
    .ToListAsync(ct);
var electricityPoints = await InvestigationQueries.FindDelpointDarAccounts(
    db, dataAreaId, darId, MigrationConstants.DeliveryCategoryElectricity)
    .ToListAsync(ct);
var pointContacts = await InvestigationQueries.FindPointContactDarAccounts(
    db, dataAreaId, darId, MigrationConstants.DeliveryCategoryElectricity)
    .ToListAsync(ct);
```

The four queries retain the route: customer postal `ADDRESS.EXU_ADDRESSID`,
installation `ADDRESSID`, delpoint `ADDRESSID`, and dated delpoint-contact
`ADDRESSID`. Postal traversal uses party-address relationship and link rows,
with `ADDRTABLEID` scoped to the customer address type. Service traversal uses
contract → part → installation or agreement → delpoint. Point contact traversal
also carries contact type and period dates. More installation or delpoint
categories can be queried by passing their category explicitly; do not treat
`El-ekstern` as a utility name.

These are **candidate associations**, including historical parts and expired
postal links. A duplicated account can have multiple roles or multiple product
parts. Retain route, contract, company, category, and dates before deciding who
was supplied on a particular day. The postal relationship has an expiry field
but no mapped valid-from field. [Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs),
[address relationships](xellent-model-guide.md#relationships-grounded-in-c),
and [CustomerSupport enquiry review](customer-support-investigation-review.md).

**Assessment:** Constructable across all four address roles. The docs already
named them; the exercise exposed why an exact address match alone cannot serve
as an active-supply predicate.

## D8. When did a point change from hourly to quarter-hourly readings?

**Business request:** An analyst investigates why a point's older series is
hourly while recent observations are quarter-hourly. Retrieve the complete
configured reading-resolution history for one source company and category.

```csharp
var resolutionPeriods = await InvestigationQueries.FindReadingResolutionHistory(
    db, dataAreaId, meteringPointIdentification, companyId,
    MigrationConstants.DeliveryCategoryElectricity).ToListAsync(ct);
```

The query joins `EXU_DELPOINT` to `EXU_TIMEREADINGSPERIOD` using the full
five-field delpoint identity: data area, category, company, attachment, and
delpoint number. It returns all start/end dates and the raw `SERIESRESOLUTION`.
Here 0 means hourly and 1 means quarter-hourly. The separate
`EMS_TIMESERIES.TIMERESOLUTION` uses different numbers, so compare meaning,
not raw integer equality. Check for overlaps or gaps before deriving a single
effective period. A configured quarter-hour period does not prove quarter-hour
observations exist. [Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs),
[period entity](../src/DataMigration.XellentData/Entities/ExuTimereadingsPeriod.cs),
and [time-series semantics](xellent-field-semantics.md#time-series-header-selection).

**Assessment:** Constructable. The complete key and two resolution code sets
are essential to a correct answer.

## D9. Which physical meters were assigned to this electricity point during the period?

**Business request:** Support wants the dated meter assignments for one point
and company in an interval, including meter number and counter constant. This
helps investigate a suspected meter replacement without treating it as a
customer or agreement change.

```csharp
var meters = await InvestigationQueries.FindDelpointMeters(
    db, dataAreaId, meteringPointIdentification, companyId,
    MigrationConstants.DeliveryCategoryElectricity,
    fromInclusive, toExclusive).ToListAsync(ct);
```

The query joins the delpoint's `(DATAAREAID, DELIVERYCATEGORY, COMPANYID,
ATTACHMENTNUM, NUM)` to meter trade's corresponding `AGREEMENTNUM` and
`DELPOINTNUM`. It returns every assignment overlapping the requested interval,
including an open-end sentinel. It preserves the meter's start/end and
`COUNTERCONSTANT`; several rows may overlap or cover different history. A
changed meter number is replacement evidence, but the query alone does not
show every reading or establish that a customer moved. [Implementation](../examples/XellentInvestigationQueries/InvestigationQueries.cs),
[meter trade entity](../src/DataMigration.XellentData/Entities/ExuMeterTableTrade.cs),
and [equipment semantics](xellent-field-semantics.md#readings-meters-and-units).

**Assessment:** Constructable. Joining on `DELPOINTNUM` alone would be unsafe;
the five-part identity and date overlap are required.

## D10. What is the exact unpaid amount of each invoice after partial payments?

**Business request:** Collections needs the remaining amount per invoice on a
specified day, taking partial payments, credit notes and settlements into
account. An invoice issue date, due date or account balance alone is insufficient.

**Result:** Do not write a query that equates an invoice header amount with an
outstanding amount, or allocates an account-level transaction sum to individual
invoices. CustomerSupport maps `CUSTTRANS` with account, amount, currency,
transaction type, invoice text, closed date and last-settlement date, but the
reviewed models and service do not establish the complete settlement/allocation
relationship or residual open amount per invoice. The account-balance service
simply sums account transactions. The next development step is to identify the
authoritative open-transaction or settlement-allocation source and its date
semantics, then extend the model before claiming this answer.
[Transaction entity](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustTrans.cs),
[account-balance reader](../../CustomerSupport/src/CustomerSupport.Api/Services/CustomerTransactionService.cs),
and [invoice/payment semantics](xellent-field-semantics.md#invoices-payments-and-customer-transactions).

**Assessment:** Insufficient evidence for exact invoice-level unpaid amounts.
Correct use of the model here is to state the missing relationship and avoid a
plausible but wrong balance calculation.

## What this check established

| Request | Constructed result | Main condition that mattered |
|---|---|---|
| D1 Dated installation supply | Compiled and EF SQL generated | Both agreement and part dates; part company retained without imposing a header-company equality |
| D2 Posted invoice headers | Compiled and EF SQL generated | Invoice recipient remains separate from agreement holder; missing agreement preserved |
| D3 Meter journal events | Compiled and EF SQL generated | Company/category/installation scope; raw event codes retained |
| D4 Point observations | Compiled and EF SQL generated | Header company/resolution, value version, Danish-day UTC bounds |
| D5 Part change history | Compiled and EF SQL generated | Logged previous state and change time, not presumed deletion |
| D6 Posted invoice lines | Source-checked; not compiled here | Requires CustomerSupport's mapped line entity and context |
| D7 DAR address candidates | Four compiled queries; EF SQL generated | Postal, installation, delpoint, and point-contact roles remain distinct |
| D8 Reading resolution history | Compiled and EF SQL generated | Full delpoint key; period code distinct from series-header code |
| D9 Dated delpoint meters | Compiled and EF SQL generated | Full delpoint key, company, category, and overlap window |
| D10 Exact unpaid amount per invoice | No reliable query from reviewed mappings | Requires authoritative residual/settlement allocation evidence |

The shared `XellentDbContext` does not map `EXU_READINGTABLE` and
`CUSTINVOICETRANS`; the model directs developers to CustomerSupport's EF
context for those tables. The reviewed models also lack a proven route to
invoice-level remaining balances. No production query or data-result
comparison was performed.
