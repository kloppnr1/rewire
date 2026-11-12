# SettlClient knowledge entry point

## Ownership and evidence

This repo publishes `SettlClient.Api` (REST clients, namespace
`DataMigration.SettlApi`) and `SettlClient` (domain-to-Settl migration,
namespace `DataMigration.SettlClient`). See the [API project](../../src/DataMigration.SettlApi/DataMigration.SettlApi.csproj),
[orchestrator project](../../src/DataMigration.SettlClient/DataMigration.SettlClient.csproj),
and [orchestrator](../../src/DataMigration.SettlClient/SettlClient.cs).
Change reusable API contracts here rather than rebuilding them in consumers.

**Code:** the routes and bodies below describe the current implementation.
**Rule:** preserve unrelated data, use direct entity lookups, and follow the
[workspace safety and package flow](../../../AGENTS.md).
**Open:** runtime API support was not probed during this documentation migration.
The [Billing](../settl-openapi/2026-06-15/Billing-Internal.json) and
[Market](../settl-openapi/2026-06-15/Market-Internal.json) OpenAPI files are historical
snapshots, not proof of today's endpoints. The [data object model](../settl-data-object-model.md)
is useful background, but also relies on that snapshot.

## Routes and body shapes

Versions belong to operations, not entire entities. Billing and Market have different
base URLs; [SettlApiOptions](../../src/DataMigration.SettlApi/Configuration/SettlApiOptions.cs)
also supports Market URL overrides by delivery category.

| Operation | Current client route / body |
|---|---|
| Customer create | Billing `POST /api/v2/customers`; `{ TransactionId, Customers: [...] }` |
| Customer read/search/list | Billing v2; `FindCustomersAsync(limit, skip)` supports pagination |
| Customer master update | Billing `PUT /api/v3/customers/{id}`; raw `{ Customer: {...}, TransactionId }`, paired with v3 raw GET |
| Payer create | Billing `POST /api/v1/Payers`; `{ TransactionId, Payers: [...] }` |
| Payer read/search/list/update | Billing v1; raw update fields are flat at the top level alongside `TransactionId` |
| Electricity metering-point create | Market `POST /api/v4/meteringPoints`; `{ TransactionId, MeteringPointCreateObjectDtos: [...] }` |
| Market metering-point read | v3 search/list/detail; `GetByIdV4Async` also reads v4 detail |
| Billing metering-point read/update | `GET /api/v1/meteringPoints/{id}`; `PUT /api/v1/meteringPoints/{id}/meteringPoint` with billing/payment configuration request |

Route sources: [customer client](../../src/DataMigration.SettlApi/Clients/SettlCustomerClient.cs),
[payer client](../../src/DataMigration.SettlApi/Clients/SettlPayerClient.cs), and
[metering-point client](../../src/DataMigration.SettlApi/Clients/SettlMeteringPointClient.cs).
Create/request sources: [customer models](../../src/DataMigration.SettlApi/Models/CustomerModels.cs),
[payer models](../../src/DataMigration.SettlApi/Models/PayerModels.cs), and
[metering-point models](../../src/DataMigration.SettlApi/Models/MeteringPointModels.cs).
Do not assume one JSON casing policy: the [HTTP base client](../../src/DataMigration.SettlApi/Clients/SettlApiClientBase.cs)
has separate PascalCase and camelCase options and omits typed null properties.

The orchestrator uses `CreateV4Async` for electricity. The older v3 `CreateAsync`
remains in the interface but is documented there as an obsolete route.
V4 uses `InstallationAddressCreateObject` rather than `InstallationAddress`;
its address flags default to enrichment on and validation skipping off in
[SettlMigrationContext](../../src/DataMigration.SettlClient/SettlMigrationContext.cs).
Claims about which environment currently validates addresses need fresh evidence.
Water creation/search use the separate commodity API, not the electricity v4 body.

## Updates that preserve existing data

- Customer master changes use `GetRawByIdV3Async` and `UpdateV3RawAsync`.
  Preserve the full raw entity, change only intended fields, remove transport-only
  `Uri`/`Links`, and wrap it with a fresh transaction ID. A v2 typed read is not the
  v3 update body. See the [customer interface](../../src/DataMigration.SettlApi/Clients/ISettlCustomerClient.cs).
- The customer's national identification number is a separate v3 subresource:
  `PUT /api/v3/customers/{id}/national-identification-number`, flat
  `{ TransactionId, NationalIdentificationNumber }`. The interface documents a
  non-empty value requirement; the wrapped master update cannot carry this field.
- Prefer `UpdatePayerBillingAsync` for payer billing changes. It reads raw JSON,
  changes selected billing leaves, strips `Uri`/`Links`, sets `PayerInformation=null`,
  and adds a fresh transaction ID. It maps `OrganizationIdentifier` to
  `VatIdentificationNumber` for PUT. Keep top-level `CustomerId`: a wrapped
  `{ Payer: ... }` body can destroy the customer link. Typed DTOs omit some billing
  fields, so a typed read/write can lose data. See the [payer interface](../../src/DataMigration.SettlApi/Clients/ISettlPayerClient.cs).

**Open: metering-point master PUT support is inconsistent in the repo evidence.**
`UpdateAsync` and `UpdateEstimatedAnnualVolumeAsync` still send wrapped PUT bodies
to Market v3 `/{id}`. However, `BlankDuplicateSecondConsumerNameAsync` records a
later observation that this route has no PUT/PATCH and uses bulk
`PATCH /api/v3/meteringPoints` instead. Its interface comment still describes PUT.
Do not copy the older universal full-PUT guidance or treat these methods as runtime
verified merely because they compile. Establish the supported route for the exact
operation before adding or running a mutation.

The duplicate-consumer cleanup sends `MeteringPointPatches` with `ConsumerPatch`,
preserves `Consumer.Start` as `PatchTime`, and sets `MarketUpdate=false`. Name fields
are cleared with null; the national identification number uses an empty string.
This is operation-specific behaviour, covered by the
[billing/consumer client tests](../../src/Tests/DataMigration.SettlClient.Tests/SettlMeteringPointClientBillingTests.cs).
`EstimatedAnnualVolume` and `HistoricAnnualConsumption` are different fields:
`UpdateAnnualConsumptionsAsync` is not a substitute for an estimated-volume update.

## Identifiers, packages, and verification

EAN (`ElectronicNumber`, normally 13 digits) belongs to invoicing/billing.
Metering-point identification (normally 18 digits for electricity) is used by
`SearchByIdentificationAsync`. The entity `Id` is a GUID; do not interchange these.
Customer, payer, metering point, and contract have direct clients. Relation methods
are for questions about the relation itself.

Read versions from [Directory.Build.props](../../Directory.Build.props) and package
references, not stale prose. At this migration the prefix is `0.7.52`, local suffix
is `dev`, and both XellentDomain references float on `0.8.*-*`.
No CI pipeline packs or publishes these libraries; other folders reference them
with `ProjectReference`.

For code changes run `dotnet build SettlClient.slnx`, then
`dotnet test src/Tests/DataMigration.SettlClient.Tests/DataMigration.SettlClient.Tests.csproj`.
This repo is a library producer, not a CLI host; verify consumer behaviour after CI
has published a producer change. Documentation-only changes need link/source checks,
not application builds. Report any unverified runtime contract explicitly.

### Historical dates and package publication

[DateOnlyUtcConverter](../../src/DataMigration.SettlApi/Models/DateOnlyUtcConverter.cs)
reads zoned timestamps as an instant and then a Danish calendar day. Dates without
an offset retain their calendar day. The UTC year-9999 open-end marker maps to
`DateOnly.MaxValue`, including nullable payer ends and exclusive contract ends.
Do not parse zoned values through the host's local time: on Danish Windows the
open-end marker overflows before a sentinel check can run. The actual payer HTTP
response path is covered by [payer client tests](../../src/Tests/DataMigration.SettlClient.Tests/SettlPayerClientTests.cs).

**Release evidence (2026-10-05):** master `f0f9f18` contained the historical-date
fix in package `0.7.51-preview.31867`, but the later develop build `31963` from
`22037d7` republished older behavior as `0.7.51-preview.31963`. Floating consumers
selected that later package. Version `0.7.52` carries the master fix into develop;
it outranks subsequent older `0.7.51` builds. Keep the canonical publishing branch
current with released fixes before publishing the same version series again.
