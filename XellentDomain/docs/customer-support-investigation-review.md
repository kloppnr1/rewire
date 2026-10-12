# CustomerSupport enquiries: using the model to find the evidence

Reviewed on 2026-09-22 against the local C# implementations and the existing model
documents. This is **task 1: data retrieval**, independent of the
[DataMigration validation review](datamigration-validation-review.md).

The question is whether an investigator can use the documented model to obtain
the data needed for the enquiries behind CustomerSupport's search options.
Reproducing the application's result page, ranking, grouping, or exact match set
is not the acceptance criterion.

This was a manual documentation and source review. Cases are hypothetical; no
customer data was queried, no application tests were run, and no independent or
blind AI evaluation was performed. Expected answers below were checked against
source. They are also reusable questions for a later documents-only evaluation.

## What a sufficient answer must establish

For each enquiry, identify the business object, mapped fields, relationship path,
source scope, relevant dates, and ambiguity that must be retained. Finding a
candidate account does not establish the caller's identity, current supply, or
responsibility for payment.

Use `(Dataareaid, Accountnum)` for a source account. Preserve company and delivery
category on joins that require them. Keep all categories in scope unless the
enquiry explicitly restricts them. In particular, `El-ekstern` describes the
DataHub electricity supplier role; it is not a separate utility.

## Questions and source-checked answers

Initial coverage describes the documents **before this review's additions**:
**Supported** means the route and essential distinction were already explicit;
**Partial** means the tables were present but an enquiry-specific detail was
missing; **Gap** means the required search interpretation was absent. These are
author assessments, not measured model accuracy.

| Case | Customer-service question | How to use the model correctly | Initial coverage; evidence |
|---|---|---|---|
| S01 | I have the customer's account number. Where do I start? | Read `CustTables` using both `Dataareaid` and `Accountnum`. Then follow `ExuContractTables.Custaccount`. Keep the raw Xellent account separate from any merged or formatted Settl customer number. | Supported; E1, E2 |
| S02 | I only remember part of the customer's name, or their trading name. | Search `CustTable.Name` and `Namealias`. For several words, decide whether all words must match; CustomerSupport requires each word somewhere in either field. Keep candidate accounts distinct even if their names coincide. | Partial; E1, E2 |
| S03 | A caller gives an email address. Which accounts record it? | Search `CustTable.Email`. Distinguish exact address from fragment search. If the enquiry concerns all recorded contact channels, also inspect the party communication relationship; CustomerSupport's dedicated email search does not include that fallback source. | Partial; E1–E3 |
| S04 | A caller gives only part of a phone number. | Search both `Phone` and `Cellularphone`; state whether formatting was normalised. The reviewed search uses substring matching without a phone-format normalisation step. A shared number can identify several candidates. | Partial; E1, E2 |
| S05 | The caller quotes an identifier belonging to the second named person. Can we find the account? | Search both primary and secondary CPR/CVR fields, preserving which field matched. The secondary identifier is an association to the account; it does not become the primary customer's identifier. | Partial; E1, E2 |
| S06 | An identifier is stored as an alternative customer number. Will the ordinary identifier search find it? | Inspect `Exualternativecustomernumber` together with `Exualternativecustomercode`. The dedicated search checks the four CPR/CVR fields, not this alternative field. Code 2 denotes CVR and code 6 CPR; do not infer type from length alone. | Partial; E2, E3 |
| S07 | I have an invoice number. Which account was invoiced? | Find `CustInvoiceJour.Invoiceid` in the chosen data area and follow `Invoiceaccount` to `CustTable.Accountnum`. Source search left-pads the supplied invoice number to 20 characters. Preserve `ExuContractnum` separately to inspect the associated agreement and its customer. | Partial; E1, E2 |
| S08 | The invoice recipient and the service customer appear different. Which one should the enquiry show? | Show both roles: invoice account from the invoice header, agreement holder from `ExuContractTable.Custaccount`, joined through invoice `ExuContractnum` and data area. Neither relationship should silently overwrite the other. | Supported; E2, E4 |
| S09 | The customer gives a correspondence address. How do we find the account? | Follow customer `Partyid` → party-address relationship → address link → `Address`. Use `FullAddress`, `City`, or `Zipcode` as appropriate; retain relationship expiry and primary flag. Multiple address rows can yield the same account. | Partial; E2 |
| S10 | The customer gives the supplied premises, which differ from their correspondence address. | Search installation `Instaddressformatted` through contract → part → installation, and delpoint `Addressstreet` through contract → part → agreement → delpoint. Label the address role. Choosing only the customer's postal address misses these routes. | Supported; E2, E4 |
| S11 | I only have a city or postcode. Can I find customers supplied there? | First specify correspondence location versus supply location. Postal city is `Address.City`; postal postcode is `Address.Zipcode`; the delpoint search also uses `Zipcodeid`. CustomerSupport's city filter only checks the postal-address path, and its postcode filter does not directly search the installation path. For installation location use its mapped address fields. | Partial; E1, E2, E4 |
| S12 | I have a DAR address identifier. Which relationships could it identify? | Inspect customer `Address.ExuAddressid`, installation `Addressid`, delpoint `Addressid`, and dated delpoint contact-info `Addressid`. Keep contact type and dates when interpreting the last route. A DAR match establishes an address association, not which customer is supplied today. | Partial; E2, E4 |
| S13 | I know both the name and the address. Should either be enough? | For a combined enquiry, intersect account candidates from the two criteria; use alternatives within a criterion only where intended. Keep a DAR identifier as a strict condition when the question requires that exact address. In the application, DAR accompanied by address text can instead affect ranking. | Gap; E1, E2 |
| S14 | We supplied this address several years ago. Who held the agreement then? | Retrieve matching installation/delpoint relationships, then evaluate each candidate agreement and applicable part against the requested period. Do not apply today's search eligibility as a history rule. The search address subqueries themselves do not restrict those agreements/parts to the enquiry date. | Supported for dated attribution; E2, E4 |
| S15 | An exact account is absent from the search screen. Does it no longer exist? | Query `CustTable` directly first. CustomerSupport requires a qualifying postal-address join and a qualifying contract even for an otherwise matching account. Including inactive accounts still requires at least one contract. A search absence does not establish database absence. | Gap; E1, E2 |
| S16 | The search describes the account as active. Does that prove it receives service today? | Inspect agreement and part start/end dates for the relevant point. The search's contract predicate accepts the open-end sentinel without checking its start date; a future agreement can qualify. Search eligibility cannot establish current delivery by itself. | Partial; E2, E4 |
| S17 | The screen lists additional accounts under the result. Did each match the caller's details? | Inspect primary-match markers and grouping separately. CustomerSupport can add accounts through technical-group/CVR associations. This is a display association, not proof that each row matched the enquiry or that all rows are one customer or payer. | Partial; E1 |
| S18 | A search reports a total. Can I use it as the number of distinct affected customers? | Build a scoped investigation query and deduplicate by the required business identity. Search totals can count joined address rows; some routes limit candidates before counting. Group counts, account counts, and expanded rows describe different things. | Gap; E1, E2 |
| S19 | The caller quotes a metering-point identifier or an invoice-recipient identifier. Can both be treated as account numbers? | Keep identifier type explicit: national point identifier is `ExuDelpoint.Meteringpoint`; invoice-recipient EAN is contract `Einvoiceeannum`; local point number is `ExuDelpoint.Num`. Follow the appropriate relationship back to agreements/accounts. General numeric search is not a universal identifier resolver. | Supported; E1, E2, E4 |
| S20 | One agreement produces several rows while tracing the supplied point. Does it cover several points? | Deduplicate part/product/period rows by the full point identity. One point-bearing contract directly relates to one distinct point/delpoint. Two distinct direct points are an exception to investigate after checking scope and joins. Technical child-point relationships are separate. | Supported; E4 and domain-owner clarification |

## Search inputs as clues to the model

This inventory covers the actual request fields so enquiries are not limited to
the most familiar search box. Application matching rules are useful evidence;
an investigation should choose predicates appropriate to its business question.

| Input | Current implementation and investigation implication |
|---|---|
| General `Query` | Six digits means exact account only. Other all-digit input tries exact account/primary-secondary CPR/CVR or partial phone/mobile. UUID input searches delpoint and delpoint-contact DAR paths. Other text searches account/name/alias/phone/mobile/email and postal full address/city/postcode. It does not search every address role or every identifier type. |
| `Name` | Words combined with AND; each word may occur in name or alias. |
| `AccountNumber` | Exact trimmed source account. |
| `Identifier` | Partial match across primary and secondary CPR/CVR. |
| `Email` | Exact trimmed value in the customer table. |
| `Phone` | Partial match across phone/mobile. |
| `InvoiceNumber` | Exact match after trimming and left-padding with spaces to length 20. |
| `Street` | Postal full address plus installation and delpoint paths. Postal text has additional formatting variants; without DAR it also tries city/postcode. |
| `City` | Postal city; a multiword city can also match its first word. |
| `ZipCode` | Postal or delpoint postcode, partial by default. |
| `DarId` | Strict across four address paths when no street/city/postcode is supplied; otherwise a relevance hint. General UUID search has narrower coverage. |
| `DataAreas` | Source scope, defaulted by API configuration. Do not assume account numbers are globally unique. |
| `IncludeInactive` | Broadens contract eligibility to any contract; it does not include accounts with no contract. |
| `MaxResults` | Limits retrieval. It is not a business rule or proof of completeness. |

Specific criteria are combined with AND and take precedence over general text.
Text matching depends on SQL collation and wildcard handling. Several fields use
`*` conversion to SQL patterns; general free text has different handling. If exact
application equivalence is ever needed, use the implementation linked below.

## EF investigation examples

These fragments use the existing `XellentDbContext db` and `CancellationToken ct`
with `using Microsoft.EntityFrameworkCore;`. Inputs such as `dataAreaId`,
`nameFragment`, and `invoiceNumber` are parameters supplied by the investigator.
They project identifiers for follow-up and are source-reviewed examples, not
executed queries. No migration loader snapshot is needed for these raw lookups.

### Find candidate accounts by a name fragment

```csharp
var accounts = await db.CustTables.AsNoTracking()
    .Where(c => c.Dataareaid == dataAreaId
        && (c.Name.Contains(nameFragment) || c.Namealias.Contains(nameFragment)))
    .Select(c => new { c.Dataareaid, c.Accountnum })
    .ToListAsync(ct);
```

This is a single-fragment enquiry. Several required words should add predicates
to the same query. Add agreements after identifying the candidates; an account
lookup need not discard an account merely because its postal address is missing.

### Trace an invoice to its recipient and agreement

```csharp
var sourceInvoiceId = invoiceNumber.Trim().PadLeft(20);
var invoices = await db.CustInvoiceJours.AsNoTracking()
    .Where(i => i.Dataareaid == dataAreaId && i.Invoiceid == sourceInvoiceId)
    .Select(i => new { i.Dataareaid, i.Invoiceid, i.Invoiceaccount, i.ExuContractnum })
    .ToListAsync(ct);

var contractNumbers = invoices.Select(i => i.ExuContractnum).Distinct().ToArray();
var agreementHolders = await db.ExuContractTables.AsNoTracking()
    .Where(c => c.Dataareaid == dataAreaId && contractNumbers.Contains(c.Contractnum))
    .Select(c => new { c.Dataareaid, c.Contractnum, c.Custaccount,
        c.Contractstartdate, c.Contractenddate })
    .ToListAsync(ct);
```

Keep the invoice rows even if an agreement is absent. Compare `Invoiceaccount`
with the agreement's `Custaccount` as separate roles. This enquiry deliberately
has no invoice-age filter.

### Trace a postal DAR identifier without hiding relationship history

```csharp
var postalLinks = await (
    from c in db.CustTables.AsNoTracking()
    join r in db.DirPartyAddressRelationships
        on new { c.Dataareaid, c.Partyid } equals new { r.Dataareaid, r.Partyid }
    join l in db.DirPartyAddressRelationshi1066s
        on new { r.Dataareaid, Id = r.Recid }
        equals new { l.Dataareaid, Id = l.Partyaddressrelationshiprecid }
    join a in db.Addresses
        on new { l.Dataareaid, Id = l.Addressrecid }
        equals new { a.Dataareaid, Id = a.Recid }
    where c.Dataareaid == dataAreaId && a.ExuAddressid == darId
        && a.Addrtableid == DataMigration.Core.MigrationConstants.AddressTableId
    select new { c.Dataareaid, c.Accountnum, r.Isprimary, r.Validtodatetime }
).ToListAsync(ct);
```

This covers the postal role only. See the [relationship guide](xellent-model-guide.md#relationships-grounded-in-c)
and its EF examples for installation and agreement/delpoint traversal. The mapped
party-address relationship exposes an expiry, but no valid-from property; do not
claim a complete historical postal-address interval from this projection.

## Findings and documentation changes

The existing guides supported the main account/agreement/point relationships,
invoice-versus-supply roles, category scope, and dated attribution. They were less
complete for contact-field selection, alternative identifiers, address-role search,
and the reasons search results can omit or expand records. This document adds the
missing enquiry routes and the field reference now links to them.

The acceptance criterion for these 20 questions is a correct retrieval plan with
its interpretation limits. It is not agreement with a particular search screen's
page. A subsequent evaluation should supply the documents without the source code,
ask these questions, and check object, fields, joins, scope, dates, and ambiguity
against the answers above.

## Source evidence

- **E1:** CustomerSupport [customer service](../../NewSettlement.CustomerSupport/src/CustomerSupport.Api/Services/CustomerSearchService.cs), [request](../../NewSettlement.CustomerSupport/src/CustomerSupport.Api/Models/CustomerSearchRequest.cs), [controller](../../NewSettlement.CustomerSupport/src/CustomerSupport.Api/Controllers/CustomersController.cs), and [UI service](../../NewSettlement.CustomerSupport/src/CustomerSupport.Web/services/customerService.ts). Executable predicates take precedence over older request-property comments.
- **E2:** Shared [account search](../src/DataMigration.Domain.Xellent/XellentAccountSearch.cs), [criteria](../src/DataMigration.Domain.Xellent/XellentAccountSearchCriteria.cs), [customer entity](../src/DataMigration.XellentData/Entities/CustTable.cs), and [EF context](../src/DataMigration.XellentData/XellentDbContext.cs).
- **E3:** Shared [customer mapper](../src/DataMigration.Domain.Xellent/Internal/CustomerMapper.cs) and [identifier helper](../src/DataMigration.Domain.Xellent/IdentifierHelpers.cs); see also [identity fields](xellent-field-semantics.md#customer-lookup-and-identity-validation).
- **E4:** [Model guide](xellent-model-guide.md), [field semantics](xellent-field-semantics.md), and [business model](utility-business-model.md), including their C# relationship evidence.
