# Invoices

Use for invoice retrieval, historical ownership, and adding documents where
electricity invoices already exist. Labels are defined in [Start here](start.md).

## Find the invoice and establish its owner

**Code:** `CUSTINVOICEJOUR.INVOICEACCOUNT` is the invoice recipient account.
`EXU_CONTRACTNUM` links to the invoice's contract, whose `CUSTACCOUNT` is the
agreement holder. Join with `DATAAREAID`. Shared `XellentInvoiceQueries` selects
by invoice account and carries it as `Invoice.AccountNumber`; the migration state
calls it `SourceAccount`. Keep those roles when comparing to a target customer.

The shared context maps invoice headers. Posted lines (`CUSTINVOICETRANS`) need
CustomerSupport's context and join by `(DATAAREAID, INVOICEID)`. Line category and
company can establish service coverage. Account-level `DeliveryCategories` cannot
classify an individual invoice; `BillingCategory` means settlement/on-account/final,
not commodity. See [D2/D6 retrieval examples](../development-query-exercises.md).

**Rule:** A main/sub family can explain who was invoiced for a service, but does
not automatically give today's point holder the old invoice. Trace historical
invoice account, contract and approved source-to-target customer mapping. If one
document includes several services, archive that document once with its original
total; retain the service associations as evidence. Confirm actual mixed content
from lines, rather than inferring it solely from family membership.

## Existing electricity archive: critical Settl dependencies

The reviewed integration publishes one manifest at
`hol/{SettlAccountNumber}/manifest.json` and PDFs under that customer's `pdfs/`.

| Code behavior | Required interpretation |
|---|---|
| Normal upload skips an entire customer when its manifest exists | It has not checked that new water/heat invoices are present. |
| Re-upload skips known PDF filenames and **replaces** the manifest from incoming invoices | Prepare a full intended inventory retaining approved electricity entries; water-only input is not a safe replacement. Source-account/category lists also come from the new input. |
| Local PDF resolution precedes reading the existing re-upload manifest | A PDF present in blob but missing locally can block the run or lose its filename in the rewritten manifest if missing files are accepted. Preserve verified existing references. |
| Matching uses the first filename containing the invoice ID; uploaded files are recognized by name | Exact invoice identity, unique candidate, and content equality are separate checks. Flag collisions and ambiguous matches. |
| Invoice-state comparison checks membership/placement | It does not establish unchanged status, amounts, tax, or PDF contents. Corrections need separate evidence. |

**Open:** The receiving Settl importer was not reviewed. Blob upload is not proof
of target invoice availability; its treatment of removals, repeated invoices and
corrections needs verification. The uploader does not delete old PDF blobs when
it replaces a manifest.

## Selection and validation boundaries

**Code:** Current invoice-state enrichment has FOR-company scope; explicit other
companies and missing contracts can be excluded. Default account eligibility
depends on the latest supplied invoice being final, and historical coverage uses
a customer-level date. Those electricity-oriented rules do not establish complete
water/heat selection. Retain an inclusion/exclusion reason for each candidate.

Example: 20 approved existing documents plus 22 incoming, with 2 overlapping
mixed-service invoices, means 40 distinct documents if no corrections/removals
are intended. Preserve placement on the established customer and investigate
any proposed move. **Open:** Exact unpaid balances cannot be derived from invoice
totals or PDFs; authoritative payment/settlement allocations are still needed.

## Evidence when more detail is needed

- [Invoice query](../../src/DataMigration.Domain.Xellent/XellentInvoiceQueries.cs).
- [BlobUploader](../../../NewSettlement.SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/BlobUploader.cs) and [PdfResolver](../../../NewSettlement.SettlInvoicePdfUpload/src/DataMigration.PdfExportCli.Library/PdfResolver.cs).
- [Ten invoice scenarios](../invoice-history-after-electricity.md): read the relevant numbered case and its source links.
- Customer/family mapping disputed: [Customers and contracts](customers-contracts.md). Shared payer/reference changes: [Billing and Settl](billing-settl.md).
