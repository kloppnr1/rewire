# Adding water and heat when electricity is already in Settl

Source review: 2026-09-22. The important unit of investigation is the customer's
complete agreement and billing relationships, including electricity already in
Settl. A water or heat extract alone cannot establish that those relationships
remain correct. The scenarios below are hypothetical; the implementation
behaviors are grounded in the linked C# sources. No live customer data was read.

A main/sub-contract relationship can connect separate electricity, water and
heat agreements. **Each point-bearing contract still directly covers one point.**
The parent relationship supplies grouping or inherited billing context; it does
not make the main contract the direct service agreement for every child's point.
A main contract can also be an umbrella without a point of its own.

**Settl dependency:** This document evaluates the current Xellent → Settl
integration. Source anomalies and general business questions are paired with
the specific Settl mapping behavior that can expose them. Where the reviewed
code does not establish a target constraint, the scenario identifies the missing
check instead of asserting that Settl must accept or reject the data.

## 1. Joint invoicing disappears when each commodity is considered separately

**Business case:** A main contract and its sub-contract together represent one
electricity point and one water installation. Electricity is already in Settl.
The customer expects the established billing arrangement to carry across when
water is added. The reverse direction, water main with electricity sub, must also
be considered, as must a blank-category umbrella with both as children.

**Source behavior:** `MeteringPointMapper` derives the joint group from
`REFERENCETOMAINCONTRACT` and demotes a group with fewer than two points in the
loaded population. `SettlMappingService` uses those resulting groups to set
`IsJoined`. A one-electricity/one-water family can therefore lose its group in
both independent commodity loads. This is a consequence of the population used
for counting, even when the source relationships are correct. [S1, S2]

**Correct validation:** Reconstruct the whole family across categories and
compare every member's source contract, point, intended payer, and actual target
joint-invoice configuration. Check the already migrated electricity member as
well as the arriving water/heat member. A shared parent identifies a candidate
group; it does not prove that Settl permits joint invoicing across their supplier
companies, customers, payers, or schedules. Those constraints need an explicit
business mapping. Do not merge service agreements to manufacture a group.

## 2. A payer identifier is reused for a different invoice recipient

**Business case:** In the electricity migration, fictional customer `C` has a
default payer `C-1` and an extra payer `C-2` for invoice destination E. In the
water migration, the same default and a different extra destination W produce
`C-1` and `C-2` again. Another case is electricity's single payer `C` becoming
water's `C-1` plus `C-2`.

**Source behavior:** `PayerMapper` numbers extra payers in first-occurrence order
over the contracts supplied to it. `XellentDomainMapper` supplies the contracts
loaded for that commodity. `SettlClient.MigrateAsync` finds an existing payer by
identifier and reuses its GUID; that branch does not compare its customer or
billing destination with the intended payer. It also does not update the existing
payer's billing details. The legacy identifier fallback handles number formatting,
not a changed meaning of suffix `-2`. [S3, S4, S5]

**Correct validation:** For every planned payer, compare the actual target
customer GUID, electronic-invoice destination, payment/invoice channels, primary
status, and applicable reference fields. Report both “same identifier, different
meaning” and “same intended payer, different identifier.” Preserve the established
electricity payer's identity; assign or reuse a payer based on the agreed billing
arrangement. Do not fix the suffix conflict by overwriting an electricity payer.

## 3. The same customer resolves to a new representative account

**Business case:** Related source accounts were represented by account A when
electricity migrated. The water population retains B but excludes A because its
relevant contract or address row is absent from that population. The new extract
can then propose a different target customer number for the same merge group.

**Source behavior:** `CustomerMapper` reselects the lowest representative among
members surviving its contract/address filters. Settl creation searches the
resulting customer number, with a legacy formatting fallback. It does not resolve
an arbitrary new representative to the previous electricity customer's GUID.
[S5, S6]

**Correct validation:** Compare each source-account-to-representative mapping
with the established electricity customer mapping and current target ownership.
Flag a source account mapped to different target customers, and a planned new
customer whose intended group already exists. Do not infer that the same parent
contract, CVR, or address alone authorizes merging customers. Main/sub contracts
with different agreement holders require an explicit responsibility check.

## 4. A main contract has ended, but its children still need its billing context

**Business case:** An electricity main contract has ended in Xellent after its
service moved to Settl, while a water sub-contract remains. Alternatively, an
already migrated electricity sub-contract takes its payment arrangement from a
water or heat main. An active-contract-only search can make the parent or the
electricity sibling disappear from the investigation.

**Source behavior:** Billing-channel lookup deliberately spans categories and
follows the referenced main contract. MobilePay resolution also inspects referenced
mains, including ended ones, and can see several candidate agreements. Main
contracts win over subs, then contract number breaks ties; one selected candidate
does not prove that there was only one. Some other customer fields use a
category-filtered main-contract join, so inheritance is field-specific. [S6]

**Correct validation:** Include ended source parents and electricity siblings
as relationship evidence, retaining their dates. Compare the selected payment
agreement's source contract, cancellation state, competing candidates, and actual
target payer. An ended service contract alone does not establish that its payment
agreement ended. Flag missing parents, self-references, longer parent chains,
cycles, and differing holders; do not silently replace them with a default payer.

## 5. Existing customer and payer records are accepted without semantic comparison

**Business case:** A customer changed their payment channel or invoice recipient
after electricity migrated. Water is successfully linked to the old target payer,
but that payer still contains the earlier settings. Conversely, changing a shared
payer to meet water's needs could change the way electricity is billed.

**Source behavior:** The create flow reuses existing customer and payer GUIDs
without refreshing those records. Customer and payer links are added when the
point GUID is absent; an existing link is skipped by point GUID without comparing
its dates. The flow does not establish that no conflicting link exists under
another customer/payer. [S5]

**Correct validation:** Compare intended and actual shared fields and the dated
customer/payer-to-point relationships. Resolve whether a difference is a common
customer change, a service-specific override, or a genuinely separate payer.
Successful lookup or linking alone is insufficient evidence of correct billing.
Any proposed change to a shared payer must account for its electricity points.

## 6. Water/heat points are found, but their billing setup is still incomplete

**Business case:** The water installation exists in Settl and is linked to the
correct customer, so the migration appears successful. Its joint-invoice or
invoice-reference configuration does not match the electricity side of the family.

**Source behavior:** The current live `SettlClient.MigrateAsync` water/heat branch
searches for externally pre-populated points; it does not create them. Its
`EnsureBillingConfigAsync` call is gated by `mp.Grid != null`, the electricity
path. Building a water/heat DTO with billing and meter fields therefore does not
prove those fields were written by this flow. An offline plan also cannot prove
that a real target point exists or has the required configuration. [S5]

**Correct validation:** Inspect the actual pre-populated water/heat point,
including commodity, meter, address, and billing settings, and compare it with
the complete family. Establish the supported way to apply any missing water/heat
configuration before treating customer/contract links as a complete result.

## 7. A water billing schedule is added to the shared electricity customer

**Business case:** Electricity and water have different settlement or on-account
billing rhythms. Adding a water group to the existing customer may have a wider
effect than adding a schedule to only the new water agreement.

**Source behavior:** Billing-plan codes map to settlement groups, and membership
is attached to the **customer**. The migration adds mapped groups and skips
memberships already present; this is not a per-point schedule assignment. [S5, S7]

**Correct validation:** Compare all existing and intended group memberships and
the target schedules they apply. Establish how the destination limits each
schedule to the relevant commodity/products. Several memberships are not by
themselves an error, but the reviewed mapping alone does not prove that electricity
cannot be scheduled again or receive an unintended billing rhythm.

## 8. Historical invoices and migration status are attributed too broadly

**Business case:** A customer is already recorded as migrated for electricity.
That status is mistaken for water/heat completion, or historical charges on a main
invoice are reassigned to the newly migrated water contract because it is in the
same family.

**Evidence:** The migration-state model retains delivery categories and source
accounts per target account, while posted invoice recipients and agreement holders
are distinct references. Those fields do not make every service or historical
invoice on an account equivalent. [S8, S9]

**Correct validation:** Establish coverage per point, contract, and category.
Retain each invoice's source account, contract, category, dates, and recipient.
Check invoice lines when an invoice spans services. Main/sub grouping does not
transfer old electricity debt to water, prove an invoice is unpaid, or establish
that an invoice has already been migrated. Exact outstanding balances still need
the settlement/allocation evidence identified in development exercise D10.

The detailed [invoice-history review](invoice-history-after-electricity.md)
adds ten cases for electricity documents already published under the same customer
number. In particular, normal upload skips customers with an existing manifest,
while re-upload replaces the manifest from the supplied inventory rather than
merging old electricity entries with new water/heat entries.

## 9. The invoice reaches the right recipient with the wrong order reference

**Business case:** A municipality receives electricity and water invoices at the
same electronic-invoice EAN, but requires separate order or buyer references.
Electricity's reference is already stored on a shared Settl payer. Every contract
in the water extract has the water reference, so that extract also treats its
reference as suitable for the payer.

**Settl dependency and evidence:** `PayerMapper` checks whether references agree
across the contracts supplied to it. If they agree, they go on the payer;
otherwise `ApplyMpBillingOverrides` supplies contract-specific references on
points. An electricity-only or water-only population can each appear internally
consistent even when the combined family disagrees. Existing payer records are
reused without an update, and the water/heat path does not write point payment
overrides. [S3, S4, S5]

**Correct validation:** Compare order, agreement, and buyer references across
the whole intended payer group and against the actual target payer and point
overrides. Report where the effective water reference would be inherited from
electricity. A matching EAN establishes the invoice destination; it does not
establish that all invoice references can be shared. Confirm the supported
water/heat override representation before choosing one common payer.

## 10. Automatic payment appears configured but does not cover the new services

**Business case:** Electricity is collected through an existing payment
agreement. Water inherits `BS` or `MobilePay` as its payment channel, and is
assumed to join the same collection arrangement. Its point or joint-invoice
number may never have been connected to the actual agreement.

**Settl dependency and evidence:** `PayerTranslator` populates a payment-channel
code, but its create DTO does not carry the source PBS agreement number or
MobilePay subscription ID. Separate reporting compares source agreements with
Settl payment-agreement records. `MobilePaySettlMatchService` explicitly scopes
its target population to electricity points in delivery; a clean result from
that reader does not establish water/heat payment coverage. PBS reporting can
match either a point or a joint-invoice number, so changing the family grouping
can change the applicable matching route. [S10, S11, S12]

**Correct validation:** Retain the source payment-agreement provenance, actual
Settl agreement identity, and the points/group it covers. Check each arriving
water/heat member against that scope and retain the existing electricity member.
Count distinct agreement IDs, not copies of the same ID on several main/sub
contracts. Distinct competing IDs need a decision even if the resolver selects
one deterministically. A channel setting or a match on one electricity point is
insufficient evidence of complete family coverage. [S16]

## 11. Adding a second service creates another primary payer

**Business case:** Electricity already uses payer `C` as the primary payer.
Adding water introduces an extra invoice destination, so the new payer projection
contains `C-1` as primary and `C-2` as secondary. The old primary still exists.

**Settl dependency and evidence:** `PayerMapper` makes the default payer primary
within the supplied set. `PayerTranslator` carries that flag into the target
create request. The existing-payer path does not reconcile primary flags across
all payers already belonging to the customer. The reviewed code does not establish
whether Settl rejects a second primary, replaces the previous choice, or accepts
both. [S3, S5, S10]

**Correct validation:** Compare primary status across the complete existing and
planned payer population for that target customer. Flag a new primary when one
already exists, and determine the destination's rule before treating the creation
as safe. Check which payer electricity continues to use; do not rely on whichever
payer the UI happens to display first.

## 12. Historical payer changes are flattened into the default payer

**Business case:** A water installation has an older contract billed to one
recipient and a newer contract billed to another. Both periods are intentionally
included for investigation. The customer also has an established electricity
payer, which becomes the default in the new projection.

**Settl dependency and evidence:** `ResolvePayerRouting` collects payer IDs from
all loaded contract assignments for a point without comparing their periods. If
more than one payer remains, it routes the point to the default and emits a
warning. `ApplyMpBillingOverrides` independently chooses the first encountered
contract for a point when there are several. Consequently, payer routing and
invoice-reference selection can use different fallback rules. [S4, S5]

**Correct validation:** Build dated point-to-payer expectations from the source
contracts. Distinguish successive legitimate payers from simultaneous conflicting
responsibility. Require the actual target links and references to agree for each
relevant period. Treat a default-routing warning as an unresolved business
assignment, rather than accepting a link merely because the default payer exists.
This case requires multiple contracts/periods on one point, not multiple points
on one contract.

## 13. Several source product periods become one target contract link

**Business case:** A heating installation has two separate periods of the same
product, or concurrent components that map to one target contract. All source
parts were extracted, but the target contains only a single link.

**Settl dependency and evidence:** `SettlClient` groups planned links by
`(MeteringPointId, ContractId)`. It takes the latest start, the latest end (or open
end if any row is open), and combines joint-invoice flags. Both the captured plan
and live path use that reduction. For fictional January–March and September–open
periods mapping to the same target contract, the result starts in September;
the earlier period is not separately represented. [S5]

**Correct validation:** Compare source product/period rows with the complete
target contract-link representation and target product configuration. If only a
current snapshot is intended, record that history is outside that representation.
If history is required, a single successful link does not satisfy it. If several
concurrent charge components intentionally map to one target contract, establish
that its configured products include every component; link count alone cannot
prove billing equivalence.

## 14. A shared payer change applies to the wrong historical period

**Business case:** Water has an older source contract start than the customer's
electricity relationship in Settl. Someone copies water's proposed payer payload
into an update, carrying that old start date. Alternatively, the target already
has a future billing change that a current-only comparison misses.

**Settl dependency and evidence:** `PayerTranslator` uses the earliest loaded
customer-contract start for new payer information and billing. Settl's payer
models document `Start` as a version boundary: a different start creates another
historical version. Point billing/payment payloads likewise carry a start and
`UntilNextHistoricalEntry` lifetime. These dates control validity; they are not
ordinary fields to overwrite while reconciling content. The current create flow
does not update an existing payer, so this is specifically a risk when resolving
the differences identified in scenarios 5 and 9. [S5, S10, S13]

**Correct validation:** Compare the target history at the dates relevant to
electricity and water/heat, including already scheduled versions. Establish the
intended effective period of any shared-field change. Preserve the meaning of
earlier electricity invoices and future billing instructions. Do not use the
earliest water contract date as an automatic effective date for a shared-payer
correction.

## 15. Organisational ownership is mistaken for shared payment responsibility

**Business case:** Several properties belong to a housing organisation. Some
electricity customers already sit under a Settl owner. The arriving water
accounts also have main/sub-contract links, but those links need not describe
the same grouping or identify the organisation as the payer for every property.

**Settl dependency and evidence:** `OwnerClusterResolver` builds synthetic owner
customers from source owner clusters and resolves existing owners through selected
member accounts. This is separate from `JointInvoiceGroupId`, contract-to-payer
routing, and source main/sub links. A fresh standalone run checks the cluster's
lowest member for an existing owner; a batched run can check several members.
Different member populations can therefore affect whether an established owner
is found. Reused customer records are not refreshed by the create branch. [S5, S14]

**Correct validation:** Compare four relations separately: organisational owner,
agreement holder, payer, and joint-invoice membership. Detect an existing owner
on electricity members that is absent from the water-selected members, or several
target owners claimed by one proposed cluster. Do not derive financial liability
from organisational membership or convert every main/sub family into a target
owner hierarchy.

## 16. The family changed after electricity was migrated

**Business case:** Electricity was linked to main contract M1 when it migrated.
The source water sub-contract now references M2 following a regrouping. Rebuilding
the family from today's Xellent rows can attach water to M2 while electricity
remains in M1, or tempt a correction that changes historical billing membership.

**Settl dependency and evidence:** The reviewed contract-header mapping exposes
one current `REFERENCETOMAINCONTRACT`; it does not expose a separately dated
history of that relationship. Point mapping uses that reference as the group ID.
Settl's current and historical billing configuration must therefore be compared
with the intended source family, rather than assuming today's parent reconstructs
the relationship at electricity migration. [S1, S13, S15]

**Correct validation:** Flag a disagreement between an existing electricity
point's group and the current source family. Establish whether it is an intentional
regrouping, stale source data, a missing previous parent, or a mapping error.
Keep the existing target group and the proposed group visible together. The
mapped header alone cannot establish when the source parent changed; obtain
authoritative history or a business decision before assigning an effective date.

## Source query: find the whole family around water and heat

The following read-only SQL uses `@DataAreaId`. It starts from water/heat parts,
then includes their direct main contracts and siblings of **every category**.
There is deliberately no active-date filter: migrated electricity can be ended
in Xellent. It returns identifiers, dates, and category codes, without personal
fields. The query is source-reviewed, but has not been executed against Xellent.

```sql
WITH Seeds AS (
    SELECT DISTINCT c.DATAAREAID,
           COALESCE(NULLIF(LTRIM(RTRIM(c.REFERENCETOMAINCONTRACT)), ''),
                    c.CONTRACTNUM) AS MainContractNum
    FROM dbo.EXU_CONTRACTTABLE AS c
    WHERE c.DATAAREAID = @DataAreaId
      AND EXISTS (
          SELECT 1 FROM dbo.EXU_CONTRACTPARTTABLE AS p
          WHERE p.DATAAREAID = c.DATAAREAID AND p.CONTRACTNUM = c.CONTRACTNUM
            AND p.DELIVERYCATEGORY IN ('Vand', 'Varme'))
), PartCategories AS (
    SELECT DISTINCT p.DATAAREAID, p.CONTRACTNUM, p.DELIVERYCATEGORY
    FROM dbo.EXU_CONTRACTPARTTABLE AS p
    WHERE p.DATAAREAID = @DataAreaId
)
SELECT s.DATAAREAID, s.MainContractNum,
       CASE WHEN parent.CONTRACTNUM IS NULL THEN 1 ELSE 0 END AS MissingMain,
       parent.REFERENCETOMAINCONTRACT AS MainParentReference,
       parent.CUSTACCOUNT AS MainAccount,
       member.CONTRACTNUM, member.CUSTACCOUNT, member.COMPANYID,
       CASE WHEN member.CONTRACTNUM = s.MainContractNum THEN 'MainOrStandalone'
            ELSE 'Sub' END AS FamilyRole,
       member.DELIVERYCATEGORY AS HeaderCategory,
       pc.DELIVERYCATEGORY AS PartCategory,
       member.CONTRACTSTARTDATE, member.CONTRACTENDDATE, member.BILLINGPLANNUM
FROM Seeds AS s
LEFT JOIN dbo.EXU_CONTRACTTABLE AS parent
  ON parent.DATAAREAID = s.DATAAREAID AND parent.CONTRACTNUM = s.MainContractNum
JOIN dbo.EXU_CONTRACTTABLE AS member
  ON member.DATAAREAID = s.DATAAREAID
 AND (member.CONTRACTNUM = s.MainContractNum
      OR member.REFERENCETOMAINCONTRACT = s.MainContractNum)
LEFT JOIN PartCategories AS pc
  ON pc.DATAAREAID = member.DATAAREAID AND pc.CONTRACTNUM = member.CONTRACTNUM
ORDER BY s.MainContractNum, member.CONTRACTNUM, pc.DELIVERYCATEGORY;
```

This is a direct main/sub-family query, not a recursive hierarchy resolver.
`MissingMain = 1` or a populated `MainParentReference` requires inspection before
assuming the family is complete. A contract can produce several category rows;
do not count them as separate contracts or points. Follow each part's category
path to identify its point, then use the existing source-to-target account mapping
and Settl's current relationships to establish which electricity members already
exist. A source category or source end date alone does not establish target state.

## Evidence to retain for each investigated family

| Relationship | Evidence needed to establish a correct result |
|---|---|
| Source family | Data area, main/sub contract IDs, all part categories, source account IDs, company and periods; missing or chained parents remain visible. |
| Customer continuity | Source account → previous electricity representative → existing target customer GUID → proposed water/heat customer GUID; unexplained changes are flagged. |
| Payer continuity | Intended payer meaning and contract membership → existing payer GUID/customer → planned payer identifier and routing; both number changes and number reuse are checked. |
| Service continuity | One distinct point per point-bearing source contract; electricity and new water/heat ownership and link periods remain attributable separately. |
| Billing continuity | Actual group number, payer, references, channels and settlement memberships across the complete family; changes to electricity are explicit business decisions. |
| Completeness | Every intended member has its target point and relationships, or a stated unresolved discrepancy. Existence counts alone do not establish correctness. |

## Source references

- **S1:** [MeteringPointMapper](../src/DataMigration.Domain.Xellent/Internal/MeteringPointMapper.cs), installation/electricity group derivation and `DemoteSingletonJointGroups`.
- **S2:** [SettlMappingService.MeteringPoints](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/SettlMappingService.MeteringPoints.cs), `ExtractAccountMeteringPointMappingAsync`.
- **S3:** [PayerMapper](../src/DataMigration.Domain.Xellent/Internal/PayerMapper.cs), `Build` and invoice-reference handling.
- **S4:** [XellentDomainMapper](../src/DataMigration.Domain.Xellent/XellentDomainMapper.cs), contract loading and `PayerMapper.Build`.
- **S5:** [SettlClient](../../NewSettlement.SettlClient/src/DataMigration.SettlClient/SettlClient.cs), `MigrateAsync` customer/payer reuse, water/heat lookup, billing configuration, relationship checks, `ResolvePayerRouting`, and `AssignSettlementGroupsAsync`.
- **S6:** [CustomerMapper](../src/DataMigration.Domain.Xellent/Internal/CustomerMapper.cs), representative reselection, `FetchBillingChannelInfoAsync`, and `FetchMobilePayAsync`.
- **S7:** [SettlementGroupExpectationResolver](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/SettlementGroupExpectationResolver.cs), customer-scoped expected memberships.
- **S8:** [MigrationState](../../NewSettlement.DataMigration/src/DataMigration.OpsCli.Library/Data/MigrationState.cs), `MigratedAccount.DeliveryCategories` and `SourceAccounts`.
- **S9:** [Invoice and payment semantics](xellent-field-semantics.md#invoices-payments-and-customer-transactions) and [development exercise D10](development-query-exercises.md#d10-what-is-the-exact-unpaid-amount-of-each-invoice-after-partial-payments).
- **S10:** [PayerTranslator](../../NewSettlement.SettlClient/src/DataMigration.SettlClient/Internal/PayerTranslator.cs), primary flag, billing channels, reference fields, and earliest-contract start.
- **S11:** [MobilePaySettlMatchService](../../NewSettlement.DataMigration/src/DataMigration.OpsCli.Library/Reporting/MobilePaySettlMatchService.cs), electricity-scoped target population and separate payment-agreement comparison.
- **S12:** [PbsMatchService](../../NewSettlement.DataMigration/src/DataMigration.OpsCli.Library/Reporting/PbsMatchService.cs), point and joint-invoice agreement matching.
- **S13:** [PayerModels](../../NewSettlement.SettlClient/src/DataMigration.SettlApi/Models/PayerModels.cs) and [MeteringPointModels](../../NewSettlement.SettlClient/src/DataMigration.SettlApi/Models/MeteringPointModels.cs), effective dates, histories, and billing/payment configuration versions.
- **S14:** [OwnerClusterResolver](../../NewSettlement.DataMigration/src/DataMigration.Migration.Library/Services/OwnerClusterResolver.cs), `ResolveOwnerAsync` and `IdempotencyAnchors`.
- **S15:** [ExuContractTable](../src/DataMigration.XellentData/Entities/ExuContractTable.cs), current main-contract reference.
- **S16:** [MobilePayResolver](../src/DataMigration.Domain.Xellent/Internal/MobilePayResolver.cs), `CandidateCount` counts distinct agreement IDs, with main/sub provenance retained.
