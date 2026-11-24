# Start here: Xellent domain questions

This is a routing page for customer support, data investigation, development,
and migration questions across the workspace. All source categories remain in
scope. Read this page and **one relevant topic first**. Follow another link only
when the question needs that relationship, an exact field, or source verification.
The long reviews are supporting evidence; they are not required startup reading.

## Rules shared by every investigation

- A point-bearing Xellent contract directly relates to **one distinct point**.
  Several parts can describe products/periods on that point. A main/sub family
  groups separate contracts; an umbrella can have no point.
- Keep source account, agreement holder, payer, invoice recipient, and current
  point holder distinct. A shared address or parent does not equate those roles.
- Billing EAN (`EINVOICEEANNUM`), national point ID (`METERINGPOINT`), installation
  number (`INSTNUM`), and Settl GUID are different identifiers.
- Keep data area, company/category where required, and the date being asked
  about. `El-ekstern` means the DataHub electricity supplier role. A blank
  contract-header category does not establish absence of supply.
- Distinguish **Rule** (business meaning), **Code** (observed implementation),
  and **Open** (evidence or a decision is missing). Mapped EF keys do not prove
  SQL constraints. Source review does not establish live data quality.

## Choose the smallest useful topic

| Question | Read first |
|---|---|
| Change a package or public API; verify code; identify SQL names or shared constants | [Development and package ownership](development.md) |
| Find a customer; identify agreement holder; follow main/sub relationships; compare representatives | [Customers and contracts](customers-contracts.md) |
| Establish dated supply; select meters/readings; handle units, time zones or replacement | [Supply and readings](supply-readings.md) |
| Explain payer, payment, billing schedule or existing Settl electricity relationships | [Billing and Settl](billing-settl.md) |
| Find invoices; determine historical recipient; extend an existing customer archive | [Invoices](invoices.md) |

For a topic beyond these summaries, search the headings in the
[business model](../utility-business-model.md) or
[field reference](../xellent-field-semantics.md), then read the relevant section.
For code, inspect only the needed table in the [schema reference](../xellent-schema-reference.md)
and the linked query implementation. The [query exercises](../development-query-exercises.md)
identify sample code and its verification limits.

## Working alone or with other agents

State the business question, identifiers/date scope, and intended output first.
When delegating, send that scope and this entry point; each agent selects its own
topic. Return the answer, evidence, uncertainty, and any dependency on another
topic. The coordinator checks customer identity, family membership, payer routing,
and invoice ownership agree wherever the answers meet. A concern outside one
agent's remit must be reported as a dependency, not silently assumed resolved.

Read further when an answer needs it; stop when it is supported. Apply workspace
rules for production access and personal data. The summaries were prepared on
2026-09-23; verify relevant current C# before changing behavior or executing a query.
