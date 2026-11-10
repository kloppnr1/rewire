# Billing and Settl

Use for payer routing, payment inheritance, invoice grouping, and adding services
to existing Settl customers. Labels are defined in [Start here](start.md).

## Business relationships to preserve

**Rule:** Keep organisational owner, agreement holder, payer, invoice recipient,
and joint-invoice family distinct. Main/sub-contract links can cross categories;
each point-bearing agreement still directly covers its own single point. Establish
the complete dated family using [Customers and contracts](customers-contracts.md)
when the question depends on siblings or an earlier electricity migration.

## Current Xellent → Settl behavior

These are **Code** observations, not universal utility rules or proven Settl
acceptance constraints. They require comparison with the actual target state.

| Behavior | What the investigation must establish |
|---|---|
| Joint groups with fewer than two points in the loaded population are removed | One electricity point plus one water point can look like a singleton in each extract. Inspect the whole family, including already migrated electricity. |
| Default payer plus extra payers for differing invoice EANs; extra IDs numbered in encounter order within the loaded contracts | The same `C-2` can mean different recipients in separate electricity/water extracts. Compare customer GUID, destination, channels, primary status and references before reusing the payer. |
| Existing customers/payers are reused by identifier without refreshing their details | “Already exists” does not prove the projected meaning agrees. A shared-payer change affects its existing electricity points too. |
| Uniform invoice reference fields go on the payer; divergence sends contract-specific references to points | Same EAN does not establish same order/buyer references. Check the complete intended payer group; each commodity slice can appear uniform by itself. |
| Water/heat points must be externally pre-populated; the live flow searches them | A constructed DTO does not prove their meter/address/billing fields were written. Point billing-configuration writes in this flow are gated to electricity. |
| Billing-plan codes map to customer-level settlement-group memberships | Adding a water group requires understanding how its schedule applies alongside electricity. Frequency alone loses detail; preserve raw `BILLINGPLANNUM`. |

Sources: [PayerMapper](../../src/DataMigration.Domain.Xellent/Internal/PayerMapper.cs),
[MeteringPointMapper](../../src/DataMigration.Domain.Xellent/Internal/MeteringPointMapper.cs),
[XellentDomainMapper](../../src/DataMigration.Domain.Xellent/XellentDomainMapper.cs), and
[SettlClient](../../../NewSettlement.SettlClient/src/DataMigration.SettlClient/SettlClient.cs).

## Payment evidence

**Code:** Billing-channel selection reads across categories and follows referenced
main contracts. An ended main can still supply payment evidence for an active sub.
MobilePay resolution selects among candidates; a deterministic winner does not
establish that only one agreement exists. The current `MobilePayResolver.IsActive`
requires a nonblank ID and cancellation date **equal to `1900-01-01`**; null does
not pass that implementation. Older notes describing null differently are not
evidence of current resolver behavior.

Payment channel `BS`/`MobilePay` does not establish actual payment-agreement
coverage. The existing MobilePay comparison starts from electricity points; its
success is insufficient evidence for water/heat coverage. Check the agreement
identity, source contract, cancellation evidence and covered points/group.

## Open questions and deeper evidence

**Open:** Cross-company/group eligibility, acceptance of multiple primary payers,
and correct scope of shared schedules require verified target rules. Do not
assert rejection or acceptance from the mapping alone. Settl configuration has
effective dates; inspect relevant versions when a proposed change could affect
earlier electricity billing or a scheduled future change.

Open only the matching numbered scenario in
[Water/heat after electricity](../water-heat-after-electricity.md) for detailed
evidence on payer conflicts, historical routing, product periods, or ownership.
[Migration validation](../datamigration-validation-review.md) documents distinct
warning/error/exclusion outcomes. For invoice archive work, continue with
[Invoices](invoices.md); payer correctness does not prove invoice ownership or upload completeness.

## PBS transaction code 3

Code 3 means **Aftale afmeldt af PI**, not FI-Kort. The shared PBS selector
treats it as terminated, like codes 4 and 5: excluded by default, or included
with `includeTerminated` only when `PBSTRANSDATE` is after the COS date
(or when no COS date was supplied). `ModifiedDateTime` is not this date.
This meaning was confirmed by the user on 2026-10-09.

The default PBS selection accepts only active transaction codes 1, 2 and 6.
Unknown codes are excluded even with `includeTerminated`; that option applies
only to known terminated codes 3, 4 and 5.
