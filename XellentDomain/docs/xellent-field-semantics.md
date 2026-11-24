# Xellent field meanings and investigation details

Companion to the [model guide](xellent-model-guide.md) and
[column reference](xellent-schema-reference.md). Reviewed against local C# source
in XellentDomain, CustomerSupport, DataMigration, TimeSeriesMigration, and
MigrationDashboard on 2026-09-22. Code mappings, query behavior, comments, and
tests are identified below; none constitutes a fresh measurement of production
data. Raw codes with no verified label remain unknown.

## Contract parts, identifiers, and category paths

The domain rule is **one directly related metering point/delpoint per point-bearing
contract**. Parts describe products and periods for that point. Keep the contract
identifier when inspecting an account with several contracts. Child-point
relationships and payment coverage are separate from this direct relationship.

**Business meaning of `El-ekstern`:** the DataHub electricity supplier role
(*el-leverandør*), as clarified by the domain owner. Electricity is the commodity;
`El-ekstern` is a market-role classification stored in `DELIVERYCATEGORY`.
Consequently, delivery-category codes must not all be interpreted as utilities.
The mapper's use of `Commodity.Electricity` identifies the commodity for that
supplier-role scope; it does not make the raw code a separate commodity.

The live contract-part key includes `DATAAREAID`, `CONTRACTNUM`, `INSTAGREENUM`,
`DELIVERYCATEGORY`, `STARTDATE`, `COMPANYID`, `PRODUCTNUM`, `AMRCODE`,
`CONTRACTPARTSUBREFERENCETYPE`, and `CONTRACTPARTSUBREFERENCEID`. Grouping only by
contract or installation collapses distinct product/period rows. Source:
[`XellentDbContext`](../src/DataMigration.XellentData/XellentDbContext.cs).

DataMigration's
[`MeteringPointStrategyFactory`](../../DataMigration/src/DataMigration.Migration.Library/Services/MeteringPointStrategy/MeteringPointStrategyFactory.cs)
classifies `El-ekstern`/`El-Ekstern`, `El`, `FI`, `Tele`, `Supply`, and `Service`
as delpoint categories, using a case-insensitive set. XellentDomain's
[`ContractMapper`](../src/DataMigration.Domain.Xellent/Internal/ContractMapper.cs)
and [`MeteringPointMapper`](../src/DataMigration.Domain.Xellent/Internal/MeteringPointMapper.cs)
implement electricity through agreements/delpoints and water (`Vand`), heat
(`Varme`), and EV charging (`Charge`) through installations. These application
lists are not a complete database enumeration. `Charge_Abo` is a subscription
category accompanying `Charge` on a charger contract; see
[`MigrationConstants`](../src/DataMigration.Core/MigrationConstants.cs).

A blank contract-header `DELIVERYCATEGORY` is not enough to classify a contract
as having no point. `ContractMapper` explicitly permits a blank header with a
matching water part, including composite water/sewage contracts. Inspect parts
before classifying the header. Similarly, a main contract is identified by other
contracts referencing it; absence of a parent reference merely establishes that
the contract is not a sub-contract.

Installation-based domain objects receive a synthesized identifier
`V{Instnum}01` in `ContractMapper`. It is not a value read from
`EXU_DELPOINT.METERINGPOINT`. Retain the raw `INSTNUM` for Xellent lookup. Billing
`EINVOICEEANNUM`, national `METERINGPOINT`, local delpoint `NUM`, and installation
`INSTNUM` are separate identifier systems.

## Delpoint keys and related points

Use all five fields when joining a delpoint to its periods:
`DATAAREAID`, `DELIVERYCATEGORY`, `COMPANYID`, `ATTACHMENTNUM`, and `NUM`.
The period's equivalent of `NUM` is `DELPOINTNUM`. On meter trades and reading
extensions the equivalent of `ATTACHMENTNUM` is **`AGREEMENTNUM`**.
`MeteringPointMapper` explicitly warns that the reading-extension table's
separate `ATTACHMENT` column is not this relationship; that column is not mapped
in either current entity class.

The local delpoint number alone is not unique or selective. The
[`DelpointLookupKeyTests`](../src/Tests/DataMigration.Domain.Xellent.Tests/Internal/DelpointLookupKeyTests.cs)
include rows sharing a delpoint number across different attachments and verify
the full lookup scope for reading interval, annual consumption, and billing method.

`EXU_DELPOINT.DEL_PARENTDELPOINTNUM` indicates a parent link but is not a complete
five-field parent identity. The explicit relation table stores
`PRIMARYATTACHMENTNUM`/`PRIMARYDELPOINTNUM` and
`SECONDARYATTACHMENTNUM`/`SECONDARYDELPOINTNUM`, together with company, category,
data area, dates, `RELATIONTYPE`, and `ATTACHMENT`. Both endpoints are joined on
the complete delpoint key in
[`TimeSeriesExtractionService`](../../TimeSeriesMigration/src/TimeSeries.Migration/Services/TimeSeriesExtractionService.cs)
and CustomerSupport's
[`context`](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/XellentDatabaseModelContext.cs).
The code does not supply a complete meaning for every relation-type value.

## Dates, open periods, and version history

| Column family | Convention found in the code | Source |
|---|---|---|
| Contract/part end dates | `1900-01-01` means no end date. CustomerSupport's `Active()` checks only end date, so it also admits future starts; add the start predicate for an as-of question. | [Query extensions](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/XellentQueryExtensions.cs) |
| Meter type periods and meter trade end dates | The current-row queries use `1900-01-01`. This is not a uniqueness guarantee. | Query extensions; `MeteringPointMapper` |
| Billing-method period end date | CustomerSupport uses `2154-12-31` at midnight. XellentDomain instead selects the latest `STARTDATE <= reference date` per delpoint in its billing-method lookup. Preserve this difference when reproducing an application's answer. | Query extensions; `MeteringPointMapper` |
| Reading-resolution periods | XellentDomain's lookup selects the latest start per delpoint. TimeSeriesMigration retains the dated periods when selecting historical resolutions. | `MeteringPointMapper`; `TimeSeriesExtractionService` |
| `EMS_TIMESERIESVALUES.VERSIONTOEXCL` | Current version is exactly `2154-12-31 23:59:59`, not the midnight constant. Keep the version column when investigating corrections. | [TimeSeriesMapper](../src/DataMigration.Domain.Xellent/Internal/TimeSeriesMapper.cs); `TimeSeriesExtractionService` |
| `EMS_TIMESERIESVALUES.TIMEOFVALUE` | Treated as UTC. Local Danish calendar-day boundaries are converted to UTC before querying; daylight saving changes make a day differ from 24 hours. | `TimeSeriesExtractionService` |
| `MOBILEPAYAGREEMENTCANCELDATE` | Both null and `1900-01-01` are described as no cancellation in the existing findings. Do not infer cancellation from non-null alone. | [Existing findings](xellent-datamodel.md) |

There is no single "active" predicate for all Xellent tables. The precise
23:59:59 version sentinel cannot be replaced with `MigrationConstants.EndDateFuture`.
The UTC interpretation above is specific to `TIMEOFVALUE`; it does not establish
the time zone of every Xellent `datetime` column.

**CustomerSupport contract-list status:** `ContractService.GetContractsAsync`
labels a contract or part active when its end is the open sentinel or strictly
later than today; it does not check the start date. Its strict `>` differs from
the `>=` in `XellentQueryExtensions.Active()`. Thus a future agreement can show
as active, and an agreement ending today can be treated differently by the two
readers. These are application definitions, not a universal supply-state rule.
Source: [`ContractService`](../../CustomerSupport/src/CustomerSupport.Api/Services/ContractService.cs).

**Dated delivery report:** Dashboard's
[`XellentDeliveryOnDate`](../../MigrationDashboard/src/Dashboard.Api/Services/XellentDeliveryOnDate.cs)
checks both contract and part start/end dates on the requested day. Its point-type
selection is restricted to consumption/production points and uses the currently
open type period, so it does not reconstruct every historical point-type change.
When several candidate contracts remain for one point, it selects newest contract
start, then account and contract number. That is a deterministic report choice,
not proof that the source had one eligible contract or a rule resolving liability.

### Loader date scope

The domain classes can carry assignments with different start and end dates.
The current loader selects only a subset, as defined by
[`ContractPartInclusion`](../src/DataMigration.Domain.Xellent/Internal/ContractPartInclusion.cs)
and [`ContractMapper`](../src/DataMigration.Domain.Xellent/Internal/ContractMapper.cs):

- Electricity parts have no start-date cap, so future starts can be included.
- Water, heat, and EV charging parts require `STARTDATE <= ReferenceDate`.
- Parts qualify on end date when open-ended (`1900-01-01`), ending on or after
  the reference date, or ending on or after a configured `IncludeEndedCutoff`.
  Header selection also admits ended contracts within that configured window.
- Assignment ordering places parts in force at the reference date first within
  each point, followed by newest start date, product code, and AMR code. Several
  simultaneous products can still be valid; the first row is not the whole agreement.

Consequently, neither "all history is excluded" nor "all future assignments are
included" describes every loader configuration. Some older class comments describe
only the default snapshot intent; the predicates above describe current behavior.

## Readings, meters, and units

`EXU_READINGTABLE` is mapped only in CustomerSupport. Its modeled identity is
`DATAAREAID`, `INSTNUM`, `DELIVERYCATEGORY`, `METERNUM`, `COUNTERNUM`,
`READINGDATE`, and `COMPANYID`. Meter/counter changes can yield several rows on
the same installation/date. The model separately exposes `METERREADING` (register
value), `CONSUMPTION`, `CONSUMPTIONPERDAY`, `READINGMETHODNUM`, `READINGSTATUS`,
`REASONTOREADING`, and `QUANTITYCODEVALUE`.

CustomerSupport's
[`TimeSeriesService.GetReadingsTimeSeriesAsync`](../../CustomerSupport/src/CustomerSupport.Api/Services/TimeSeriesService.cs)
interprets `REASONTOREADING` as follows:

| Code | Interpretation in that application |
|---|---|
| 1 | Meter replacement |
| 2 | Ordinary reading; treated as annual when consumption is positive |
| 4 | Move-out |
| 6 | Move-in |
| 7 | Initial reading |

This service filters quantity codes containing `KWH` or starting with `M3`, and
derives display flags and accumulated consumption. Those are display choices;
they do not establish that no other registers, quantities, or codes exist. Keep
raw `QUANTITYCODEVALUE`, meter, and counter in investigations. Do not sum register
values across meters/counters or assume that an electricity output unit applies
to water and heating. The reviewed code provides no complete label set for
`READINGSTATUS`, `READINGMETHODNUM`, or `JOURNALTYPE`.

`EXU_METERJOURNALTRANS` records installation meter events, keyed by
`DATAAREAID`, `JOURNALID`, and `LINENUM`. `MeteringPointMapper` obtains a meter by
filtering `POSTED == 1` and taking the latest `TRANSDATE`, then highest `RECID`,
within the selected category and installation. That is the mapper's current-meter
choice; the raw event history retains unposted rows and earlier events.

`EXU_METERTABLETRADE` provides dated delpoint meters, `COUNTERCONSTANT`, and
`METERINGPOINTDHSUBTYPE`. The mapper uses the counter constant as the domain
meter's multiplication factor. Its installation branch supplies a default factor
of 1 and no reading interval; those defaults are not measurements from a water or
heat meter table.

### Reading displays and customer periods

`TimeSeriesService.GetReadingsTimeSeriesAsync` uses the earliest start and latest
end across the selected customer's installation parts as one query window, with
open ends replaced by today. It then groups readings by installation. It does
not intersect each reading with each individual contract-part interval. Disjoint
periods or different periods across installations can therefore admit readings
outside the customer's relevant interval; establish actual dated ownership before
using that chart as a complete customer-consumption allocation.

The same method derives display values and flags:

- “Start of delivery” includes an ordinary reading with zero consumption as well
  as move-in and initial readings. Only the explicit move-in reason sets “Move-in.”
- A first-reading heuristic is `CONSUMPTION == METERREADING` with a register value
  of at least 50,000. It changes accumulation/display behavior; it is not a complete
  definition of invalid readings across utilities.
- Later annual-reading values use accumulated consumption between annual readings,
  with resets for start-of-delivery and the first-reading heuristic. The first
  annual reading uses its own recorded consumption. Raw consumption can therefore
  differ from the displayed annual amount.
- The displayed annual estimate is positive `CONSUMPTIONPERDAY * 365`, rounded to
  one decimal. It is derived separately from the delpoint annual-estimate field.

Source: [`TimeSeriesService`](../../CustomerSupport/src/CustomerSupport.Api/Services/TimeSeriesService.cs).

`EXU_READINGTABLEEXT.EXPECTEDCONSUMPTIONPERYEAR` is an annual estimate, not the
measured interval consumption. The mapper chooses the latest `READINGDATE` with
`RECID` as tie-breaker for the full scoped delpoint key and uses positive values.
Source: [`MeteringPointMapper`](../src/DataMigration.Domain.Xellent/Internal/MeteringPointMapper.cs).

## Codes that must not be confused

| Source column | Values interpreted by current code | Evidence |
|---|---|---|
| `EMS_TIMESERIES.TIMERESOLUTION` | 0 None; 1 Quarter; 2 Hour; 3 Month; 4 Day; 5 Year; 6 Minute | [Xellent TimeResolution enum](../src/DataMigration.XellentData/Enums/TimeResolution.cs) |
| `EXU_TIMEREADINGSPERIOD.SERIESRESOLUTION` | 0 Hourly; 1 Quarter-hourly. Existing helpers default other values to hourly; that fallback does not define unknown raw values. | [ExtractionMappings](../src/DataMigration.Domain.Xellent/ExtractionMappings.cs) |
| `EMS_TIMESERIESVALUES.QUALITYOFVALUE` | 0 Machine-approved reading; other codes are not labeled by the current Xellent enum | [QualityOfValue](../src/DataMigration.XellentData/Enums/QualityOfValue.cs) |
| `EXU_BILLINGMETHODPERIOD.BILLINGMETHOD` | 1 and 3 map to Flex; 2 maps to NonProfiled | `ExtractionMappings.MapBillingMethod` |
| `EXU_METERTABLETRADE.METERINGPOINTDHSUBTYPE` | 1 Physical; 2 Virtual; 3 Calculated | `ExtractionMappings.MapMeteringPointSubTypeFromDhCode` |
| `EXU_METERPOINTSTATUSPERIOD.METERPOINTSTATUS` | 5 Created ("Oprettet"); other codes are not defined by this source | [DrainMeterState.OprettetStatus](../../MigrationDashboard/src/Dashboard.Api/Services/DrainMeterState.cs) |
| `EXU_AGREEMENTTABLE.EMS_AGREEMENTSTATUSEBIX` | 5 Delivery ended ("Leverance ophørt") | [Entity comment](../src/DataMigration.XellentData/Entities/ExuAgreementTable.cs) |
| `EXU_DELPOINTCONTACTINFOTABLE.CONTACTTYPE` | 4 is treated as the legal contact; do not assume a unique row per point/start date | `MeteringPointMapper.LoadDelpointContactPointsAsync` |

`METERPOINTSTATUS` and `EMS_AGREEMENTSTATUSEBIX` are different fields: code 5
has a different meaning in each. Likewise, hourly is 2 in `TIMERESOLUTION` and 0
in `SERIESRESOLUTION`.

[`ExtractionMappings`](../src/DataMigration.Domain.Xellent/ExtractionMappings.cs)
also maps raw meter-point type codes: `E17/Forb`/`FORB` consumption, `PROD`
production, `D05 M1` net production, `D06 M2` supply to grid, `D07 M3` consumption
from grid, `D08 Afregg` wholesale information, `D09 EP` own production, `D14 EV`
electrical heating, `D15` net consumption, `D17` other consumption, `D18` other
production, and `D19` effect. An E17-only filter is a consumption-only selection,
not the complete Xellent model.

## Time-series header selection

One metering point can have several `EMS_TIMESERIES` headers by company and
resolution. XellentDomain's short `TimeSeriesMapper` selects the first matching
header, while TimeSeriesMigration reads all relevant headers and their company
agreement/reading-resolution periods. The latter source explicitly handles
overlapping company histories. A first matching header is therefore not evidence
that the point has only one series.

Retain `DATAAREAID`, header `RECID`, `COMPANYID`, `DELIVERYCATEGORY`,
`METERINGPOINT`, `TIMERESOLUTION`, and `QUANTITYCODEVALUE` before selecting value
rows. Select the company, header/resolution, version, and time interval explicitly
before aggregating. CustomerSupport's `EmsTimeseries` class currently omits
`COMPANYID`, whereas XellentDomain maps it. Sources:
[`TimeSeriesMapper`](../src/DataMigration.Domain.Xellent/Internal/TimeSeriesMapper.cs),
[`TimeSeriesExtractionService`](../../TimeSeriesMigration/src/TimeSeries.Migration/Services/TimeSeriesExtractionService.cs),
and [column reference](xellent-schema-reference.md#ems_timeseries).

**End boundary difference:** `TimeSeriesMapper.LoadAsync` currently includes
`TIMEOFVALUE == toUtc` (`<=`). TimeSeriesMigration and the investigation example
use an exclusive upper bound (`<`). The domain `Period` type also describes a
half-open interval. Do not assume those implementations agree at the endpoint:
for adjacent investigation windows, use `fromUtc <= TIMEOFVALUE < toUtc`; when
reproducing the short mapper's exact output, account for its inclusive upper bound.

TimeSeriesMigration limits each series to the agreement periods for its company
and the applicable reading-resolution periods. Old company series can continue
to contain values beyond that company's relevant period; concatenating every
series would therefore misrepresent the history. When configured reading resolution
has no corresponding stored series, this extractor retains the stored resolution
and reports the mismatch; it does not create measured quarter-hour values from
hourly data. These are the extractor's interpretation rules.

DataMigration's
[`ReportMeterHistoryRunner`](../../DataMigration/src/DataMigration.OpsCli.Library/Runners/ReportMeterHistoryRunner.cs)
normally omits points with fewer than two distinct resolutions, unless
`IncludeSingleResolution` is set. It merges consecutive equal-resolution rows
and derives each end from the next resolution change, leaving the last open.
Absence from that report does not establish absence of a point or readings.

## Prices and tariff versions

There are two useful paths. CustomerSupport relates a delpoint charge assignment
to `EXU_PRICEELEMENTTABLE` by **`DATAAREAID`, `DELIVERYCATEGORY`,
`PARTYCHARGETYPEID`, `VALIDINMARKETFROM`**. XellentDomain's
[`SettlementBasisMapper`](../src/DataMigration.Domain.Xellent/Internal/SettlementBasisMapper.cs)
uses the price-check/check-data path and joins charge definitions on data area
and charge type identifier only. Its charge-definition entity omits the category
and market-version columns and is keyless. The latter query is evidence for that
application's behavior, not a proof that two columns uniquely identify every
charge definition. See CustomerSupport's
[`relationship #19`](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/XellentDatabaseModelContext.cs).

`EXU_DELPOINTPRICEELEMENTR25046` distinguishes `RELATIONVALIDFROM/TO` from
`VALIDINMARKETFROM/TOEXCL`. CustomerSupport's charge definition additionally maps
market validity and `STARTDATE/ENDDATE`. Preserve the separate time axes when
inspecting an assignment and its charge definition.

`EXU_PRICEELEMENTRATES` has `PRICE`, `PRICE2_`, ... `PRICE24_`; the C# class calls
the latter properties `Price2` ... `Price24`. `GetPriceForHour` defines slots
1–24, starting at 00:00–01:00. `SettlementBasisMapper.BuildHourlyRates` treats a
nonzero `PRICE` with all remaining slots zero as a flat rate. The mapper infers a
rate's end from the next `STARTDATE`; no end column is mapped on this entity.
That convention does not establish a general daylight-saving or sub-hour price rule.

`EXU_PRODUCTEXTENDTABLE` connects `PRODUCTNUM` to `PRODUCTTYPE` over dates;
the latter joins `INVENTTABLE.ITEMID`. The rate query then matches `RATETYPE` to
that product type. In this mapper, blank rate `PRODUCTNUM` selects generic energy
rates; the code comments identify nonblank product-specific rates as subscriptions
handled elsewhere. Keep these application predicates explicit.

`EXU_PRICEELEMENTTABLE.GRIDCOMPANYID` is mapped as a numeric GLN and formatted as
13 digits, with zero mapped to absent. It is not the short `COMPANYID` join key.
Sources: [`SettlementBasisMapper`](../src/DataMigration.Domain.Xellent/Internal/SettlementBasisMapper.cs)
and [rate entity](../src/DataMigration.XellentData/Entities/ExuPriceElementRates.cs).

### Product descriptions and billing schedules

`ContractMapper.BuildAssignment` currently sets both `Product.Code` and
`Product.Name` to the part's `PRODUCTNUM`. A domain product name is therefore
not evidence of a product-master description. The schema reference describes
available product/item fields separately.

The contract's `BILLINGPLANNUM` is retained as `BillingSchedule.PlanCode`.
[`BillingFrequencyMapper`](../src/DataMigration.Domain.Xellent/Internal/BillingFrequencyMapper.cs)
trims the value and classifies it as follows (these are application mappings):

| Source plan code | Domain frequency |
|---|---|
| `Måned.`, `Md+MdAC`, `MD+3MdAC` | Monthly |
| `Kvartal`, `KvartalAC` | Quarterly |
| `½Årlig`, `½År_Bagud` | SemiAnnually |
| `Årlig` | Annually |
| Other values, including null/empty | Other |

Retain the raw plan code: the frequency classification alone cannot explain
all on-account and settlement timing. Frequency belongs to the contract's billing
schedule; it does not define the reading interval or every subscription's frequency.

## Invoices, payments, and customer transactions

Keep the invoice recipient `CUSTINVOICEJOUR.INVOICEACCOUNT` separate from the
contract's `CUSTACCOUNT`. Both can be used to find a customer, but they represent
different references. `CUSTINVOICETRANS` contains posted invoice lines;
`CUSTINVOICELINE` belongs to the free text invoice header `CUSTINVOICETABLE`
through `PARENTRECID`. Both joins carry `DATAAREAID`.

The current domain enums interpret these invoice fields:

| Column | Codes | Source |
|---|---|---|
| `EXU_CUSTFREEINVOICESTATUS` | 0 None; 2 Invoiced; 3 Credited | [InvoiceStatus](../src/DataMigration.Domain/Customers/InvoiceStatus.cs) |
| `EXU_BILLINGCATEGORY` | 0 None; 1 Settlement; 2 Aconto; 3 SettlementAndAconto; 4 Final; 5 PureFinal | [BillingCategory](../src/DataMigration.Domain/Customers/BillingCategory.cs) |
| `EXU_PAYMENTMEDIA` | 0 None; 1 PBS; 2 FIK; 3 OIOXML; 4 MobilePay | [PaymentMedia](../src/DataMigration.Domain/Customers/PaymentMedia.cs) |

These are separate code sets. Payment details on `EXU_CONTRACTTABLE` also include
`PBSAGREEMENTNUM`, `PBSTRANSACTIONCODE`, `PBSTRANSDATE`,
`MOBILEPAYSUBSCRIPTIONAGRE20135`, and `MOBILEPAYAGREEMENTCANCELDATE`. A payment
agreement can sit on another category's contract or cover sub-contract points;
that does not alter the direct contract-to-point cardinality.

### Buyer references within a payer group

[`PayerMapper.BuildPayerReferences`](../src/DataMigration.Domain.Xellent/Internal/PayerMapper.cs)
groups by electronic invoice destination, then checks order, agreement, and buyer
references together. Among contracts matching that destination, if any reference
differs (including a mix of blank and nonblank), the payer retains its electronic
number but all three payer-level references are cleared. More specific values are
handled at point level by `XellentDomainMapper.ApplyMpBillingOverrides`.
Contracts with no matching EAN do not participate in this comparison. A payer
without an EAN receives no buyer-reference block. Thus a missing payer-level order
reference does not prove that its individual contracts have no order references.

**Unresolved code interpretation:** the entity comment labels PBS code 3 as
cancelled by PI, while the existing findings/report vocabulary labels it FI-card.
Definitions of active PBS also differ between readers. Preserve raw code and
identify the intended interpretation instead of claiming one universal meaning.
Sources: [contract entity](../src/DataMigration.XellentData/Entities/ExuContractTable.cs)
and [existing findings](xellent-datamodel.md).

[`CustomerTransactionService`](../../CustomerSupport/src/CustomerSupport.Api/Services/CustomerTransactionService.cs)
labels `CUSTTRANS.TRANSTYPE` 0 None, 1 Payment, 2 Settlement/clearing, 3 Interest,
4 Reminder, 5 Invoice, 6 Credit note, 7 Advance. `AMOUNTCUR` is paired with
`CURRENCYCODE`. Keep currency in any grouping; that service's running-balance
calculation does not establish that arbitrary accounts contain only one currency.

### Invoice lists, service icons, and account balances

[`InvoiceService`](../../CustomerSupport/src/CustomerSupport.Api/Services/InvoiceService.cs)
has different scopes for different actions:

| Action | Implemented selection or interpretation |
|---|---|
| Contract invoice list | Invoice account and exact contract match, with invoice date strictly later than 24 months before today. |
| Customer account invoice list | Invoice account match; this method has no date cutoff. |
| Contract-list service icons | Categories from parts starting on/before invoice date, without testing their end dates; fallback to header category. |
| Account-list service icons | Categories from parts in the main-contract group active on invoice date; fallback to group header categories. |
| Download age validation | Invoice date must fall within the configured maximum age, default five years. This is an eligibility check, not verification that a PDF exists. |

The icons are derived from agreement data, not posted invoice lines. Different
icons or list contents can reflect those selection rules. They do not alone prove
different billed services or missing invoice records. CustomerSupport exposes these
actions through [`CustomersController`](../../CustomerSupport/src/CustomerSupport.Api/Controllers/CustomersController.cs).

`CustomerTransactionService` calculates the account balance as the sum of fetched
transaction amounts and a running balance in transaction order. It applies no due-date
or invoice-allocation calculation and does not group the total by currency. The
displayed balance is therefore not, by that calculation alone, an overdue-debt
figure or proof that a particular invoice has been settled.

In SettlInvoicePdfUpload,
[`PdfResolver`](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfResolver.cs)
separately looks for a file for each invoice already in its input and marks the
file reference missing when no match is found. This is direct evidence that an
invoice record and PDF availability are separate facts.

## Customer lookup and identity validation

The [CustomerSupport enquiry review](customer-support-investigation-review.md)
maps every search input to its source fields and relationship paths. For example,
email search uses `CustTable.Email`; a broader contact enquiry may need party
communication values. Postal, installation, delpoint, and delpoint-contact DAR
identifiers describe different address roles. Search eligibility and result
grouping do not establish account existence, current supply or legal identity.

Customer mapping and validation also have different responsibilities:

| Value | Current mapping/check | Interpretation limit |
|---|---|---|
| Primary/secondary CVR | `CustomerMapper.NormalizeCvr` requires eight characters satisfying `char.IsDigit`; otherwise it warns and returns null. | No company-register validation. |
| Primary CPR | `IdentifierHelpers.ResolveCpr` prefers a nonempty primary value of length ten; otherwise accepts `PNR=` plus ten characters in the alternative number. | The implementation checks length, not digits or identity validity, despite its older “digits” comment. |
| Alternative number | `Exualternativecustomercode` identifies code 2 as CVR and code 6 as CPR; the CPR helper separately recognises the text prefix without checking the code. | Recognising the cause of a missing mapped identity does not repair or promote an alternative CVR. |
| Primary identity for creation | DataMigration's missing-identity filter requires either mapped primary CVR or CPR to be nonblank. | Secondary-person identity alone does not pass this gate. |
| Contact email | Prefer nonempty primary email, otherwise loaded communication fallback; trim and check a basic expression. Invalid selected email records an error and becomes null. | No retry of fallback after invalid primary; no mailbox verification. |

Sources: [CustomerMapper](../src/DataMigration.Domain.Xellent/Internal/CustomerMapper.cs),
[IdentifierHelpers](../src/DataMigration.Domain.Xellent/IdentifierHelpers.cs), and
the [DataMigration decision cases and call sites](datamigration-validation-review.md).
That review distinguishes warnings, collected errors, exclusions and exceptions,
and states the limited scope of the configurable placeholder-identity exception.

## Contacts, groups, documents, and sales journal

`CUSTTABLE.PARTYID` connects to party-address and communication relationships.
The communication path is `(DATAAREAID, PARTYID)` into
`DIRPARTYECOMMUNICATIONRELA2608`, then `(DATAAREAID, VALUESRECID)` into
`DIRECOMMUNICATIONADDRESS.(DATAAREAID, RECID)`. `CustomerMapper` selects email
type and orders by `PRIORITY`; it does not establish a meaning for every `STATUS`.
Source: [`CustomerMapper`](../src/DataMigration.Domain.Xellent/Internal/CustomerMapper.cs).

`CUSTTABLE.EXU_TECHNICGROUP4` joins `EXU_GROUPTABLE.GROUPNUM` with data area.
It is distinct from the installation/category attribute
`EXU_UTILITYINSTALLATION.TECHNICGROUP1`. Sources:
[`TechnicGroupClusterReader`](../../MigrationDashboard/src/Dashboard.Api/Services/TechnicGroupClusterReader.cs)
and [`CustomerSearchService`](../../CustomerSupport/src/CustomerSupport.Api/Services/CustomerSearchService.cs).
The mapped alternative-customer code records 6 CPR and 2 CVR, with 0 when no
alternative number is registered; this is metadata about identifier meaning,
not a reason to retrieve personal identifiers for an investigation.

Address joins can multiply rows. `ADDRESSZIPCODE` has a composite key including
country and county, so postcode alone does not identify one address row. The
mapper comments and
[`LegalAddressZipCollisionTests`](../src/Tests/DataMigration.Domain.Xellent.Tests/Internal/LegalAddressZipCollisionTests.cs)
cover this. Blank `ADDRESSID` values must not be used to resolve an arbitrary
matching `ADDRESS.EXU_ADDRESSID`. The mapper also documents multiple open legal
contact rows for one metering point; `STARTDATE` alone is not a reliable winner.

`DOCUREF` contains both party and referenced-record identifiers plus optional
document value/type links. CustomerSupport's
[`CustomerDocumentService`](../../CustomerSupport/src/CustomerSupport.Api/Services/CustomerDocumentService.cs)
looks up documents through `PARTYID`, left joins `VALUERECID = DOCUVALUE.RECID`,
and resolves `DOCUTYPE` using `TYPEID` and the requested data area. A document can
be a note without an attached file. That service does not establish a general
`REFTABLEID` label map or a company-safe rule for every referenced-record path;
those remain gaps rather than inferred invoice joins.

The document view groups rows by name, document type, and creation timestamp,
preferring a row with a file name. The notes view selects rows without an attachment
file name and excludes entries whose name contains the configured-in-code
self-service-login text.
Consequently, the visible lists are filtered/grouped projections, not raw record
counts or proof that every customer interaction has been displayed.

`VDOSALESJOURNALENTRY` is keyed by data area and sequence number; `FUNCTION_`
maps to the C# property `Function`. CustomerSupport's
[`SalesJournalService`](../../CustomerSupport/src/CustomerSupport.Api/Services/SalesJournalService.cs)
reads back an entry by a reference in `TECHNICTEXT1`.
`VDOSALESJOURNALPRODUCT` is keyed by data area and product ID and contains
`PRODUCTTYPE`/`ACTIVE`. The reviewed entity/context does not declare a relationship
between those two tables, or establish equality between sales-journal `PRODUCTID`
and contract-part `PRODUCTNUM`.

## Contract-part log is a change history

`EXU_CONTRACTPARTLOG.ERRORMSG` is a change-type description, despite its name.
The entity documents date changes, deletion, and one-time-charge text. Logged
`CONTRACTPARTSTARTDATE`/`CONTRACTPARTENDDATE` carry the state **before** the change.
`LOGDATETIME` dates the log event; `RECID` identifies the log row. `LOGEMPLID`
can be blank for unattended jobs. See the
[entity](../src/DataMigration.XellentData/Entities/ExuContractPartLog.cs) and
[`XellentContractPartQueries`](../src/DataMigration.Domain.Xellent/XellentContractPartQueries.cs).

The log omits the live part's two subreference fields. The query helper's
seven-field `ContractPartLogKey` is used within a data area; it is explicitly
documented as an incomplete general-purpose part identity. Do not join all log
rows to live parts on contract number alone or interpret every log row as a
deleted part.

## EF coverage differences and remaining gaps

| Model detail | Consequence for investigation |
|---|---|
| Agreement class attributes include `DELIVERYCATEGORY`, but both contexts' fluent `HasKey` omit it | The effective EF key is `(DATAAREAID, AGREEMENTNUM, COMPANYID)`. Preserve category in query scope; annotations alone do not describe the final EF configuration. |
| `EXU_READINGTABLEEXT` key is `RECID` in XellentDomain, `(DATAAREAID, RECID)` in CustomerSupport | Keep the data-area filter regardless of the EF key. |
| `EXU_PRICEELEMENTTABLE` and `INVENTTABLE` are keyless in XellentDomain, keyed in CustomerSupport | Keyless means read projection in that EF model, not duplicate data or a keyless physical database table. |
| `EMS_TIMESERIES.COMPANYID` is XellentDomain-only | A CustomerSupport entity cannot express this header filter without extending that model or using the shared context. |
| `EXU_PRICEELEMENTTABLE` has different column subsets in each model | Category/market validity fields are CS-only; description/GLN fields are XellentDomain-only. The union catalog is not one entity type. |
| C# string lengths and nullable declarations can differ | They are source-model evidence, not fresh SQL Server metadata. The catalog retains both models. |
| CustomerSupport configures invoice-line `LINENUM` with fluent `HasPrecision(28,12)` | The generated column list captures class annotations and keys; this additional fluent type facet is documented here. |

Sources: [shared context](../src/DataMigration.XellentData/XellentDbContext.cs),
[CustomerSupport context](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/XellentDatabaseModelContext.cs),
and the entity links in the [column reference](xellent-schema-reference.md).

The references cover the **53 distinct tables mapped by these two contexts**.
Physical indexes/constraints, unmapped tables/columns, unlisted status labels,
units on every counter, and the business rules for every category require
additional evidence. Application defaults and narrow query filters are not
substitutes for those definitions.
