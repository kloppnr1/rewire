# Adding water and heat invoices to an existing electricity customer

Reviewed on 2026-09-22 against XellentDomain, DataMigration, CustomerSupport,
and SettlInvoicePdfUpload source. The customer already has electricity invoice
history under a Settl customer number. Water and heat must extend that history
without losing, duplicating, or moving the existing invoices.

**Settl dependency:** The reviewed PDF integration uses one manifest per customer
at `hol/{SettlAccountNumber}/manifest.json`, with PDFs in that customer's `pdfs/`
folder. It uploads archived documents and metadata. The receiving Settl importer
is not in the reviewed code, so its duplicate handling, response to manifest
removals, and proof of successful invoice ingestion remain to be established.
Blob presence alone does not prove customer-visible availability in Settl. [I1]

For the surrounding customer, payer, and main/sub-contract relationships, see
[Water and heat after electricity](water-heat-after-electricity.md).

## 1. The electricity manifest causes all new invoices to be skipped

**Confirmed behavior:** In normal upload mode, `SelectNotUploadedAsync` checks
whether the customer's manifest exists. If it does, the entire customer is
skipped before resolving PDFs. `UploadAccountsAsync` repeats that account-level
existence check. Neither check compares the incoming invoice set with the old
manifest. The reported already-uploaded invoice count is taken from the incoming
account's invoice count, not from a comparison of invoice identities. [I1]

**Business consequence:** A customer with 20 existing electricity documents and
12 new water documents can be reported as already uploaded even though none of
the 12 water documents was checked individually.

**Required validation:** Compare document membership per customer. “Customer has
a manifest” establishes that some history was published, not that the arriving
water/heat history exists. Preserve separate counts for existing, genuinely new,
changed, and unresolved documents.

## 2. Re-upload replaces the inventory instead of merging it

**Confirmed behavior:** Re-upload reads existing manifests to obtain PDF names
to skip. It then writes a new manifest from the supplied `account.Invoices` and
overwrites the existing manifest. It does not union that list with the old list.
The manifest's `SourceAccounts` and `DeliveryCategories` also come from the new
input. Existing PDF blobs are not deleted by this method. [I1, I2]

**Business consequence:** Supplying only water/heat invoices removes electricity
entries from the published inventory. The binaries can remain in storage while
the new manifest no longer advertises them. The Settl importer's effect on already
ingested documents needs verification. Two independent water and heat snapshots
for the same customer can also overwrite each other's inventory.

**Required validation:** Build a complete intended inventory for that customer,
retaining approved electricity history plus genuinely new water/heat documents.
Account-level source memberships and categories must represent that combined
inventory too. Any removal, reassignment, or correction must be explained
explicitly. Re-upload is not, by itself, a safe append operation.

## 3. A combined invoice may already have moved with electricity

**Business case:** A main contract organises joint billing across electricity,
water and heat. A historical invoice/PDF may include several services, even
though it was selected during the electricity migration.

**Evidence and uncertainty:** `CUSTINVOICEJOUR` links to an invoice account and
contract. CustomerSupport's posted invoice lines expose `EXU_DELIVERYCATEGORY`
and `EXU_COMPANYID`. The account manifest has no per-invoice service category,
contract number, or line breakdown. An account's `DeliveryCategories` cannot
establish which services occur on each invoice. Actual mixed-service documents
must be established from invoice lines or authoritative billing evidence. [I3, I4]

**Required validation:** Compare the same source invoice identity across the
existing electricity inventory and proposed water/heat inventory. Keep one
canonical archived document for an overlapping invoice; do not create another
copy of the complete invoice for every service line or sub-contract. Preserve
its original total and tax. A shared main contract is a reason to inspect invoice
contents, not proof that every invoice in the family is mixed.

## 4. Invoice ownership differs from service ownership

**Business case:** A water sub-contract belongs to source account B, but the
historical invoice was issued to main account A. A and B may already map to the
same Settl customer, or to different customers. A changed representative account
can also make a previously uploaded electricity invoice appear to belong in a
new folder.

**Confirmed behavior:** `XellentInvoiceQueries` selects by `INVOICEACCOUNT` and
uses that value as the invoice's `AccountNumber`. The state calls it
`SourceAccount`. It is therefore invoice-recipient account evidence, not an
assertion about the service agreement's `CUSTACCOUNT`. The reviewed COS-driven
invoice enrichment assigns documents through the approved source-account-to-target
customer mapping. The manifest itself is stored by target customer number. [I3, I5]

**Required validation:** Retain invoice account, invoice contract, agreement
holder, main/sub family, approved target customer, and previous target placement
as separate evidence. Include billing accounts reached through the family, not
only accounts holding water/heat points. Flag one source invoice proposed under
two target customers or an existing invoice proposed under a new customer number.
Resolve entitlement from historical invoice evidence; today's point holder or
parent reference does not automatically receive the old invoice.

## 5. The existing invoice selection contains electricity-specific assumptions

**Confirmed behavior:** The reviewed `GenerateStateFromSettlRunner` invoice
enrichment applies `ForInvoiceScope`. That scope excludes invoices linked to an
explicit company other than `for`, and invoices with no matching contract. Blank
contract company is retained as a legacy case. It is not a general water/heat
category classifier. [I5, I6]

**Business consequence:** Reusing that selection unchanged can omit wanted
water/heat invoices, or treat a blank-company header as sufficient evidence even
when the invoice needs a different service classification. Removing the company
filter without replacing its business rule can pull unrelated documents into
the same customer history.

**Required validation:** Explain every included or excluded invoice using its
issuer/company, contract, invoice account and, where needed, line categories.
Keep a reason for missing-contract and ambiguous-category cases. Do not use
`BillingCategory` as the commodity: it distinguishes settlement, on-account and
final invoices, not electricity, water and heat.

## 6. Source identity, manifest identity and file identity do not match

**Confirmed behavior:** XellentDomain models the invoice-header key as
`(DATAAREAID, INVOICEID)`. `InvoiceStateComparer` compares
`(SourceAccount, InvoiceId)` within a state. The uploader fixes the data area to
`hol` and skips previously uploaded PDFs by filename. The target blob path
contains the target customer and filename, but no source account. [I1, I3, I7]

`PdfResolver` searches the source account folder, then `_unmatched`, and selects
the first filename containing the invoice ID. It does not establish an exact
filename token match, a unique candidate, or matching PDF content. [I8]

**Required validation:** Establish uniqueness of the source invoice key and
retain invoice-account provenance. For every intended target file, flag several
candidate PDFs, one filename used by different source invoices, or a file whose
content differs from the previously uploaded file of the same name. A filename
match can otherwise skip a genuinely new document, and two source folders can
collapse into one target filename after account merging. A changed filename can
also make an old invoice appear new. The actual incidence requires file/metadata
comparison; no such comparison was performed in this review.

## 7. An existing electricity PDF can lose its manifest link if it is absent locally

**Confirmed behavior:** Re-upload resolves every supplied invoice against local
PDF files before reading the old manifests. An unresolved invoice has its
`PdfFileName` set to null. The missing-PDF gate can stop the upload even if that
electricity PDF already exists in blob storage. If missing files are accepted,
the uploader still serializes the invoice entry with no filename; it does not
restore the old manifest's filename. [I1, I8, I9]

**Required validation:** Preserve a verified existing document reference when
the binary is already uploaded and the incoming source lacks a local copy.
Distinguish “missing locally,” “missing in blob,” and “missing from the target
invoice history.” A generic allowance for old missing PDFs does not establish
which condition applies and can turn an existing document link into a null link.

## 8. A correction or credit is overlooked because the invoice already exists

**Business case:** Since electricity migration, an invoice was credited, a PDF
was corrected, or a related credit document was posted on the shared account.
The water/heat addition finds the original invoice ID already present and treats
that as sufficient.

**Confirmed behavior:** Re-upload skips a PDF if its filename is in the old
manifest; it does not compare binary content. It rewrites metadata from the new
state, but `InvoiceStateComparer` compares placement and membership rather than
amounts, status, tax or PDF contents. The reviewed manifest does not contain an
original-invoice/credit-allocation relationship. [I1, I4, I7]

**Required validation:** Compare invoice status, amounts, tax, dates, and verified
document identity as well as membership. Retain credit documents as distinct
documents and establish their authoritative relationship to originals before
interpreting the combined financial history. Do not silently replace an original
invoice with a credit, or treat archived invoice totals as open receivables.

## 9. Electricity's history window or final invoice decides water's eligibility

**Confirmed behavior:** `InvoiceAccountFilter` groups the supplied invoices by
invoice account and examines its latest invoice. Default eligibility requires
that invoice to be Final or PureFinal. It does not partition by service; tied
invoice dates have no explicit secondary sort. The reviewed state enrichment
also applies a calendar-aligned history window derived from one customer-level
change-of-supplier date. [I5, I10]

**Business consequence:** Once several services are present in the input, a later
water on-account invoice can mask an electricity final invoice, or one final
invoice can incorrectly stand for the whole account. Recomputing the history
window using a different migration date can add or remove old electricity entries
when the intended change was only to add water/heat.

**Required validation:** Define service/document eligibility and historical
coverage independently from the customer's already-migrated flag. Retain the
existing approved electricity inventory as the comparison baseline, and explain
date-driven omissions separately from already-uploaded documents. A final invoice
for one service is not proof that every service on the account has ended.

## 10. The invoice inventory cannot by itself prove a complete financial migration

**Confirmed limitation:** The reviewed state/manifest transports invoice
metadata and PDF references. It does not establish remaining balances, partial
payment allocations, or which cross-service charges have already been settled.
`paymentMedia` is deliberately blanked in the outgoing blob manifest. [I1, I4]

**Required validation:** Define whether the requested result is accessible
historical invoices, financial balances, or both. Verify source inventory,
published manifest/PDFs, and actual Settl invoice availability separately. If
balances are also required, use an authoritative ledger/allocation source; neither
a PDF total nor the inherited electricity payment method supplies that answer.
See [development exercise D10](development-query-exercises.md#d10-what-is-the-exact-unpaid-amount-of-each-invoice-after-partial-payments).

## Concrete expected outcomes for the shared customer

The following are hypothetical review cases, not executed tests.

| Input situation | Correct expected result |
|---|---|
| 20 approved invoices already present; incoming selection has 22, of which 2 are the same existing mixed-service invoices | 40 distinct approved documents on the same customer, assuming no corrections/removals. The 2 overlapping documents are counted once. |
| Existing electricity manifest; incoming water-only list | Flag both normal-mode whole-customer skip and re-upload replacement risk. The water-only list is not a complete replacement manifest. |
| One mixed-service invoice reached through electricity main and water sub | One archived document with its full original amount; retain its service associations without duplicating the invoice. |
| Water agreement holder B; invoice account A; A already maps to the electricity customer | Evaluate the established invoice-account mapping. Do not move the invoice solely because B holds the water agreement. |
| Existing electricity file verified in blob, missing from the local PDF archive | Preserve its verified manifest reference; report local absence separately. Do not change it to a null filename as a side effect of adding water. |
| Same source invoice/customer but changed status, amount, or PDF content | Report a correction requiring evidence. A membership-only comparison is insufficient. |
| Same source invoice proposed on two customer numbers | Report ambiguous placement; neither current main/sub membership nor the latest representative resolves historical entitlement alone. |

For each target customer, compare the existing approved inventory with the full
intended inventory. A useful investigation output records source data area and
invoice ID, invoice account, source contract, service evidence, target customer,
previous placement, document reference, metadata differences, and an outcome such
as unchanged, added, corrected, excluded-with-reason, or unresolved. Some of these
fields are absent from the current manifest and must remain in supporting audit
evidence. Do not infer them from account-level categories.

## Source evidence

- **I1:** [BlobUploader](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/BlobUploader.cs): account-level skip, filename comparison, manifest replacement, blob namespace, and `BuildAccountManifestJson`.
- **I2:** [PdfExportFilter](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfExportFilter.cs): re-upload semantics.
- **I3:** [XellentInvoiceQueries](../src/DataMigration.Domain.Xellent/XellentInvoiceQueries.cs), [CustInvoiceJour](../src/DataMigration.XellentData/Entities/CustInvoiceJour.cs), and [CustomerSupport invoice lines](../../CustomerSupport/src/CustomerSupport.XellentDatabaseModel/Entities/CustInvoiceTrans.cs).
- **I4:** [DataMigration state](../../DataMigration/src/DataMigration.OpsCli.Library/Data/MigrationState.cs) and [PDF upload state](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/MigrationState.cs): account and invoice metadata fields.
- **I5:** [GenerateStateFromSettlRunner](../../DataMigration/src/DataMigration.OpsCli.Library/Runners/GenerateStateFromSettlRunner.cs): COS-driven invoice enrichment, source-account assignment, company scope and history window.
- **I6:** [ForInvoiceScope](../../DataMigration/src/DataMigration.OpsCli.Library/Services/ForInvoiceScope.cs).
- **I7:** [InvoiceStateComparer](../../DataMigration/src/DataMigration.OpsCli.Library/Services/InvoiceStateComparer.cs) and [existing comparison documentation](../../DataMigration/docs/invoice-state-comparison.md).
- **I8:** [PdfResolver](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfResolver.cs): source-folder search, substring matching, and clearing unresolved filenames.
- **I9:** [PdfExportRunner](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfExportRunner.cs) and [MissingPdfGate](../../SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/MissingPdfGate.cs): local resolution precedes the re-upload manifest read.
- **I10:** [InvoiceAccountFilter](../src/DataMigration.Domain/Customers/InvoiceAccountFilter.cs) and [CosStatePlanner](../../DataMigration/src/DataMigration.OpsCli.Library/Services/CosStatePlanner.cs).

This is a documentation/source review. No invoice data, PDFs, blob manifests, or
Settl import results were accessed, and no upload was performed.
