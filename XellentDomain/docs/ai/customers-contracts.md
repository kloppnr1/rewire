# Customers and contracts

Use for customer enquiries, contract families, and continuity of customer identity.
Labels are defined in [Start here](start.md).

## What a match establishes

**Rule:** A search result is a candidate association. Establish the role and period
before saying the account was supplied or owes an invoice. Customer postal address,
installation/service address, point contact, and invoice address serve different
purposes. One DAR match can reach several accounts through different routes.

**Code:** Shared `XellentDbContext` and CustomerSupport's
`XellentDatabaseModelContext` are separate EF models. Use the context that maps
the needed table. CustomerSupport's actual search predicates are evidence for
screen behavior; they are not a universal definition of customer identity.

For a specific search input, open the relevant row in
[CustomerSupport enquiry review](../customer-support-investigation-review.md#search-inputs-as-clues-to-the-model).
For an address enquiry, [exercise D7](../development-query-exercises.md#d7-which-accounts-are-associated-with-this-dar-address-and-in-what-role)
preserves postal, installation, delpoint, and contact routes and their date evidence.

## Follow the relationship being asked about

| Relationship | Source join and interpretation |
|---|---|
| Customer → contract | `CUSTTABLE.(DATAAREAID, ACCOUNTNUM)` → `EXU_CONTRACTTABLE.(DATAAREAID, CUSTACCOUNT)` |
| Contract → parts | `(DATAAREAID, CONTRACTNUM)`; retain product, company, category, AMR and dates |
| Sub → main contract | `(DATAAREAID, REFERENCETOMAINCONTRACT)` → main `(DATAAREAID, CONTRACTNUM)` |
| Contract → point | Follow each part's category path in [Supply and readings](supply-readings.md#joins-and-identifiers) |

**Rule:** Several product parts on one contract do not imply several points.
Several contracts can concern the same point over time. An empty parent reference
means no parent is named; other contracts referencing this one establish its role
as a main. An umbrella may lack a directly supplied point.

**Code:** Water parts can sit under blank-category headers. The installation join
does not impose equality between header company and part company. Adding that
condition changes which rows qualify. Preserve the part's company for later joins.

For a family enquiry, retain the main, siblings across **all categories**, and
their holders and dates. Ended electricity contracts may explain an existing Settl
relationship. The [family query](../water-heat-after-electricity.md#source-query-find-the-whole-family-around-water-and-heat)
includes them. It covers direct main/sub links; missing parents or further parent
references need investigation before calling the family complete.

## Existing Settl customer continuity

**Code:** Customer mapping can reselect the lowest representative among accounts
surviving its contract/address filters. A water population can therefore yield a
different representative from the earlier electricity population. The creation
flow looks up the resulting customer number, including a formatting fallback;
that does not establish continuity with an arbitrary earlier representative.

Compare source account → approved representative → actual target customer GUID
across both populations. Main/sub membership, common CVR, postal match, and
organisational ownership each provide different evidence. Do not silently merge
accounts or give historical invoices to today's point holder.

**Open:** The correct owner of a conflicting relationship requires dated source
evidence and the existing target relation. A successful lookup alone is insufficient.
CustomerSupport retrieval and DataMigration acceptance are separate questions:
the [validation review](../datamigration-validation-review.md#identity-and-contact-cases)
distinguishes normalisation, warning, exclusion, and exception.

## Evidence when more detail is needed

- [Source joins and cardinality](../xellent-model-guide.md#reading-the-model).
- [CustomerMapper](../../src/DataMigration.Domain.Xellent/Internal/CustomerMapper.cs), representative reselection and main-contract joins.
- [SettlClient](../../../NewSettlement.SettlClient/src/DataMigration.SettlClient/SettlClient.cs), existing customer lookup.
- Payment inheritance or joint invoicing: [Billing and Settl](billing-settl.md).
- Historical invoice placement: [Invoices](invoices.md).
