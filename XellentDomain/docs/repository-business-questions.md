# Business questions derived from repository usages

Reviewed on 2026-09-22. This round starts with what the applications actually do:
customer-service enquiries, invoice lists, account statements, reading displays,
dated delivery reports, meter-history reports, and invoice-file lookup. The
questions below were derived from those usages, then checked against the business
and data-model documents.

Repos read: **CustomerSupport, DataMigration, MigrationDashboard,
TimeSeriesMigration, and SettlInvoicePdfUpload**, with XellentDomain as the shared
model reference. Only documentation in XellentDomain was changed. This was a source
review, not a production-data check or a blind evaluation by a separate AI.

## Customer service and billing questions

| # | Business question prompted by an actual usage | Answer from the reviewed implementation | Usage and initial documentation coverage |
|---|---|---|---|
| R01 | The customer screen says an agreement is active. Does that mean we are supplying it today? | The label alone does not establish that. The contract screen checks the end date but not the start, so it can label a future agreement active. It also treats an agreement ending today differently from another shared active-agreement helper. | **E1.** The documents warned about date rules, but omitted this actual screen's rule and its difference on the end date. |
| R02 | The agreement remains open, but its service part ended. Should the point still appear as supplied on the selected date? | The dated delivery report requires both the agreement and the applicable part to be in force. An open agreement alone is insufficient. | **E6.** The business document covered dated relationships; the report's combined condition needed to be made explicit. |
| R03 | Two agreements appear to cover the same point on the same day. Has the delivery report resolved which one is correct? | It selects the newest agreement start and uses account/agreement order to break ties. That gives a repeatable result; it does not establish that the other candidate was invalid. Investigate the underlying overlap. | **E6.** The report's conflict selection was missing. |
| R04 | Why can an older invoice appear under the customer but disappear when looking at one contract? | The contract list is restricted to the exact invoice account and contract and the last 24 months. The account invoice list has no date cutoff in that method. A missing row can reflect view scope. | **E2.** This distinction was missing. |
| R05 | The same invoice shows different service icons in two views. Does that prove different services were billed? | No. Both views derive icons from agreement parts, using different group/date rules. Neither establishes billed lines from the icons. Consult the invoice and its line items. | **E2.** The difference between derived icons and billed evidence was missing. |
| R06 | We cannot retrieve the invoice PDF. Does that mean the invoice was never issued? | No. Invoice records, download eligibility, and file availability are separate. The download check has an age limit; the PDF resolver can receive an invoice record and find no corresponding file. | **E2, E9.** The documents described invoices and documents separately, but did not explain this operational distinction. |
| R07 | Is the account balance shown to customer service the amount overdue, or proof that a particular invoice is unpaid? | Neither follows from this calculation alone. The service sums transaction amounts; it does not calculate overdue amounts from due dates or settle individual invoices through allocations. Currency must also be considered. | **E3.** The general distinction was covered; the actual balance calculation and its limits were missing. |

## Consumption and service-history questions

| # | Business question prompted by an actual usage | Answer from the reviewed implementation | Usage and initial documentation coverage |
|---|---|---|---|
| R08 | Why can the annual consumption shown in customer service differ from the consumption on the latest reading? | The reading display can accumulate consumption between annual readings, including intermediate meter events. It applies resets and special handling for the first annual reading and suspected initial readings. The displayed annual value need not equal one source reading's consumption. | **E4.** Derived display values were mentioned, but this explanation was missing. |
| R09 | Is the annual estimate shown beside a water or heat reading a measurement of the last 12 months? | No. In this view it is calculated from a positive daily-consumption rate multiplied by 365 and rounded. It is an extrapolation, separate from measured history and the separate annual-estimate source used elsewhere. | **E4.** Estimates were covered generally; the origin of this particular estimate was missing. |
| R10 | The reading view marks “start of delivery.” Does that prove the customer moved in on that day? | No. The marker can also come from an ordinary reading with zero consumption or an initial reading. The explicit move-in marker has its own recorded reason. | **E4.** Reading-reason meanings were covered; the broader display marker was missing. |
| R11 | A customer occupied an installation during two separate periods. Does the chart exclude the gap between them? | The current reading query uses one span from the earliest start to the latest end across the customer's selected parts. It does not intersect each reading with each separate occupancy period. Check the actual intervals before attributing every displayed quantity to that customer. | **E4.** The business rule required dated attribution, but this application's wider query window was undocumented. |
| R12 | Why must we retain different measurement periods instead of treating a point's current reading frequency as its entire history? | The extraction and history report retain changes between hourly and quarter-hour readings over time. Extraction also limits a company's series to its relevant agreement periods. Current configuration alone does not describe all historical values. | **E7, E8.** Historical resolution and company periods were already covered in the field reference; the business explanation was strengthened. |
| R13 | The configuration says quarter-hour readings, but only hourly values exist. Can we treat them as measured quarter-hour values? | No. The extractor keeps the resolution of the stored series and reports the mismatch. It does not manufacture measured quarter-hour observations. | **E7.** This fallback and its meaning were missing. |
| R14 | A point is absent from the meter-history report. Does it have no readings or meter history? | That conclusion is unsupported. By default this report omits points with only one distinct reading resolution. It describes resolution changes rather than every point or physical meter replacement. | **E8.** The report's default selection was missing. |

## Customer records and grouping questions

| # | Business question prompted by an actual usage | Answer from the reviewed implementation | Usage and initial documentation coverage |
|---|---|---|---|
| R15 | A customer note has no attached file. Is it an incomplete document? | Not necessarily. The customer-service application deliberately presents records without an attachment as notes. Read the note content and document type before assuming a missing file. | **E5.** Already explicit in field semantics; now included in the business explanation. |
| R16 | The document view shows fewer entries than the underlying records. Does that prove records were lost? | No. The view groups entries by name, type, and creation time, preferring a file-bearing row. The notes view also filters certain entries. Compare the view's scope before interpreting its count. | **E5.** The grouping/filtering behavior was missing. |
| R17 | A point belongs to a different customer now. Does that make the current customer the owner of its old invoice/account history? | No. The historical account-assignment logic explicitly treats the current point holder as insufficient evidence of who held the relationship years earlier. Use the historical accounts and dated relationships. | **E10.** Already supported by the business model's historical-attribution rule; this usage provides additional evidence. |
| R18 | Several accounts appear under one organisational group. Does that establish that they are one legal customer or share one payer? | No. The group reader groups by organisational membership; that grouping does not establish legal identity or shared payment arrangements. The shared domain explicitly allows members with different legal company identifiers. | **E11.** Already covered by the distinction between organisational, customer, and payer relationships. |

## Usage evidence

These links identify the actual readers and transformations that prompted the
questions. Application choices remain application choices; a label, filter, or
tie-break does not become a universal utility business rule.

| Evidence | Repo usage inspected |
|---|---|
| E1 | CustomerSupport [`ContractService.GetContractsAsync`](../../CustomerSupport/src/CustomerSupport.Api/Services/ContractService.cs), compared with [`XellentQueryExtensions.Active`](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/XellentQueryExtensions.cs). The former uses a strict end-date comparison; the latter includes an end date equal to today. |
| E2 | CustomerSupport [`InvoiceService`](../../CustomerSupport/src/CustomerSupport.Api/Services/InvoiceService.cs): `GetInvoicesByContractAsync`, `GetInvoicesByAccountAsync`, and `ValidateInvoiceForDownloadAsync`. Contract icons retain categories from parts started by invoice date without an end-date check; account icons use active parts in the main-contract group, with header fallbacks. Download age is configurable and defaults to five years. |
| E3 | CustomerSupport [`CustomerTransactionService.GetTransactionsAsync`](../../CustomerSupport/src/CustomerSupport.Api/Services/CustomerTransactionService.cs): sums fetched transaction amounts and builds a running balance; no due-date or allocation calculation. |
| E4 | CustomerSupport [`TimeSeriesService.GetReadingsTimeSeriesAsync`](../../CustomerSupport/src/CustomerSupport.Api/Services/TimeSeriesService.cs): selects a combined date span, groups by installation, derives display flags and accumulated consumption, and extrapolates annual estimates. |
| E5 | CustomerSupport [`CustomerDocumentService`](../../CustomerSupport/src/CustomerSupport.Api/Services/CustomerDocumentService.cs): `GetDocumentsAsync` and `GetNotesAsync` use party-linked records, optional file links, grouping, and note filters. |
| E6 | MigrationDashboard [`XellentDeliveryOnDate`](../../MigrationDashboard/src/Dashboard.Api/Services/XellentDeliveryOnDate.cs): `Delivering` checks agreement and part periods; `Fold` selects one candidate per point. Its point-type selection is restricted and uses the open type period, so this report is not a complete reconstruction of all historical types. |
| E7 | TimeSeriesMigration [`TimeSeriesExtractionService`](../../TimeSeriesMigration/src/TimeSeries.Migration/Services/TimeSeriesExtractionService.cs): `ExtractSingleMeteringPointAsync`, company agreement periods, reading-resolution periods, and `WrongResolutionAsync`. |
| E8 | DataMigration [`ReportMeterHistoryRunner`](../../DataMigration/src/DataMigration.OpsCli.Library/Runners/ReportMeterHistoryRunner.cs): report selection and `BuildResolutionPeriods`. The report is wired into the [`report-meter-history` command](../../DataMigration/src/DataMigration.OpsCli/Program.cs). |
| E9 | SettlInvoicePdfUpload [`PdfResolver.ResolvePdfPaths`](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfResolver.cs), called by [`PdfExportRunner`](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfExportRunner.cs): file resolution is performed for existing invoice inputs and can yield a missing file reference. |
| E10 | DataMigration [`HistoricalSourceOwnership.Assign`](../../DataMigration/src/DataMigration.OpsCli.Library/Services/HistoricalSourceOwnership.cs): the current point customer is retained as context but does not prove historical ownership. Only that business distinction is used here. |
| E11 | MigrationDashboard [`TechnicGroupClusterReader`](../../MigrationDashboard/src/Dashboard.Api/Services/TechnicGroupClusterReader.cs), used by [`OwnerClusterLookup`](../../MigrationDashboard/src/Dashboard.Api/Services/OwnerClusterLookup.cs); shared [`OwnerCluster`](../src/DataMigration.Domain/Customers/OwnerCluster.cs) explains membership across legal entities. |

The customer-service usages E1–E5 are called from the corresponding enquiries in
[`CustomersController`](../../CustomerSupport/src/CustomerSupport.Api/Controllers/CustomersController.cs).
The dated delivery reader is called by Dashboard's
[`DrainSupplierChangeService`](../../MigrationDashboard/src/Dashboard.Api/Services/DrainSupplierChangeService.cs).
These call sites establish that the inspected readers participate in application
workflows; no workflows were executed during this review.

## Documentation changes and limits

Added [business evidence in everyday work](utility-business-model.md#10-business-evidence-in-everyday-work)
to the generic model. It explains dated supply, derived reading displays, separate
occupancy periods, invoice evidence, balances, and document views in business terms.

Added the exact source-specific behavior to the field reference:

- [Dates and status](xellent-field-semantics.md#dates-open-periods-and-version-history).
- [Reading displays and customer periods](xellent-field-semantics.md#reading-displays-and-customer-periods).
- [Time-series periods and report coverage](xellent-field-semantics.md#time-series-header-selection).
- [Invoice lists and balances](xellent-field-semantics.md#invoice-lists-service-icons-and-account-balances).
- [Documents and notes](xellent-field-semantics.md#contacts-groups-documents-and-sales-journal).

The review found differences and limits in existing application behavior, especially
the contract-screen end-date rule, the reading chart's combined occupancy window,
and invoice icon derivation. Those implementations were not changed. The documents
now expose them so that investigations can distinguish what the screen displays
from what the underlying business evidence establishes.
