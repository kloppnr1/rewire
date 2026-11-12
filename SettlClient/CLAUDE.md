# SettlClient — Settl REST API client

This repo is the **only** Settl REST API client in the workspace. It publishes two
NuGet packages consumed by DataMigration, MigrationDashboard, TimeSeriesMigration and
CustomerSupport:

- `SettlClient.Api` (`DataMigration.SettlApi`) — REST client (customers,
  payers, metering points, contracts, settlement groups). HTTP plumbing in
  `Clients/SettlApiClientBase.cs`; one client class per entity under `Clients/`.
- `SettlClient` (`DataMigration.SettlClient`) — Domain→Settl migration
  orchestrator (`SettlClient`, translators under `Internal/`).

See the workspace CLAUDE.md (`source/repos`) for the full repo map, NuGet floating-ref
flow, production safety and verification rules. This file holds the traps; the full
route and body reference lives in the docs below.

## Read first

Read `docs/ai/start.md` before changing API routes, request models, migration translators
or public interfaces. It is the knowledge entry point and links to the source for each
claim. Put new contract knowledge there, not here.

## Working rules

- Commit to `develop`, never `master`. When the user is working in this repo, commit each finished change as soon as its gates are green; push only when she asks. Expert agents still do not commit.
- Code changes: `dotnet build SettlClient.slnx`, then
  `dotnet test src/Tests/DataMigration.SettlClient.Tests/DataMigration.SettlClient.Tests.csproj`.
  Docs-only changes need link and source checks, not a build.
- This repo is a library, not a CLI host. A consumer can only be verified after CI has
  published the package. Never pack locally to test cross-repo.
- Read versions from `Directory.Build.props` and the csproj package references, not from
  prose. Own packages are in the 0.7.x series; both XellentDomain references float on
  `0.8.*-*`.
- A method that compiles is not a verified runtime contract. Say so when a route or body
  has not been checked against Settl.

## Settl API versioning per entity — use the right version per operation

Versions belong to operations, not entities. Customer and Payer are on the Billing base
URL; the metering-point master is on the **Market** base URL (Billing has its own
`/api/v1/meteringPoints` for billing configuration).

| Entity | Create | Read / search / list | Update (core) |
|--------|--------|----------------------|---------------|
| Customer | `POST /api/v2/customers` | `GET /api/v2/customers/{id}` + `/api/v2/customers?...` | **`PUT /api/v3/customers/{id}`** |
| Payer | `POST /api/v1/Payers` | `GET /api/v1/Payers/{id}` | `PUT /api/v1/Payers/{id}` |
| MeteringPoint (master) | **`POST /api/v4/meteringPoints`** | `GET /api/v3/meteringPoints/{id}` | **Open — see below** |

**Metering-point master update is unresolved.** `UpdateAsync` and
`UpdateEstimatedAnnualVolumeAsync` still send a wrapped `PUT /api/v3/meteringPoints/{id}`.
A later live check (featuretest, 2026-09-09, recorded in `BlankDuplicateSecondConsumerNameAsync`)
found that route has GET only, and that method uses the bulk `PATCH /api/v3/meteringPoints`
instead. Settle which route Settl supports for the exact operation before adding or running
a metering-point mutation. Do not copy the PUT pattern.

**The metering-point trap:** the v3 create route is **gone** — `POST /api/v3/meteringPoints` answers
**404** (it briefly answered 405 while Settl was migrating). Create must go to v4 (`CreateV4Async`;
the old `CreateAsync` is still on the interface but dead), while read stays on v3. Verified against featuretest 2026-08-12: `POST /api/v4/meteringPoints`
returns 201 on an empty list, and `GET /api/v4/meteringPoints` is 404 — v4 has only POST and
`/{id}`. The v4 create body differs from v3
in exactly two places: the list is called `MeteringPointCreateObjectDtos` (not `MeteringPoints`), and
`InstallationAddress` is called `InstallationAddressCreateObject`. That address section carries Settl's
DAR switches: `EnrichAddress` (Settl derives municipality code, street code and DAR identifier from the
rest of the address) and `SkipAddressValidation` (create even when no DAR entry matches exactly).
Both are per-run settings on `SettlMigrationContext` — default enrich on, skip off. Which
environment validates addresses today is not known; check before relying on it. Water uses
its own create/search methods, not the v4 electricity body.

**The customer trap:** create/read/search are **v2**, but the core UPDATE is **v3**. A
read-modify-write update must read the v3 master (`GetRawByIdV3Async`) and PUT v3
(`UpdateV3RawAsync`) — the v2 read DTO is the wrong shape to echo back.

## Full-echo master-PUT pattern (core/master updates)

Several Settl PUT endpoints have **no partial update** — they replace the whole entity.
A partial or null-filled body silently drops links and untouched fields. The safe
pattern (verified non-destructive on featuretest):

1. `GetRaw…` the live entity as raw JSON.
2. Overwrite ONLY the target leaf/leaves (navigate case-insensitively — Settl accepts
   PascalCase echo of its own GET).
3. Strip `Uri`/`Links`, add a fresh `TransactionId`, echo the rest **verbatim**.
4. `PutRaw…` the same URL.

**Body shape asymmetry** (mirrors the create asymmetry):
- **Customer = WRAPPED** — `{ Customer: {<full v3 customer>}, TransactionId }`.
- **Payer = FLAT** — entity fields at top level (incl. `CustomerId` → link preserved)
  + `TransactionId`. A wrapped `{ Payer: ... }` body can destroy the customer link.
  For billing changes use `UpdatePayerBillingAsync`; the typed DTOs omit some billing
  fields, so a typed read/write loses data.
- **MeteringPoint** — the code sends `{ MeteringPoint: {...}, TransactionId }`, but the
  route itself is in doubt (see the open point above). Not a verified pattern.

Used by DataMigration's `reconcile-apply` core writers.

## Customer body: v2 ≡ v3 except one field (swagger-verified 2026-06-15)

The v2 (`UpdateCustomerWithHistoricalDataRequest`) and v3
(`UpdateCustomerWithHistoricalDataV3Request`) customer update bodies are **structurally
identical — 71 of 72 leaf paths byte-for-byte** (all Address/PostalAddress/
PhysicalAddress/Name/VAT/CrmData/CreditInfo/Owner/Notes paths, same nesting/casing).

**The single divergence:** `NationalIdentificationNumber` (CPR) is in the v2 body but
**NOT** in v3 — v3 moved it to a dedicated sub-resource:

- `PUT /api/v3/customers/{id}/national-identification-number`
- Flat body `{ TransactionId, NationalIdentificationNumber }`, requires a non-empty value
  (cannot clear), returns 204.
- Client method: `ISettlCustomerClient.UpdateNationalIdentificationNumberV3Async`.

It is also absent from the v3 GET response, so a wrapped-PUT echo cannot carry it — it
**must** be written through this sub-resource.

## Payer body: PUT vs GET divergence (swagger-verified 2026-06-15)

`PayerResponse` (GET) carries read-only fields the `UpdatePayerRequest` (PUT) body does
not define: `Id`, `Uri`, `Links[]`, `PayerBilling.OrganizationIdentifier`. The flat
full-echo strips `Uri`/`Links`; the PUT tolerates the rest. No core-leaf divergence.

## Identifier system — three types that must never be conflated

| Identifier | Source | Used for |
|-----------|--------|----------|
| **EAN** (13-digit, `ElectronicNumber`) | `ExuContractTable.Einvoiceeannum` | E-invoicing on Payer billing config. Nothing to do with metering points. |
| **Metering point identification** (18-digit) | `ExuDelpoint.Meteringpoint` | National MP id → Settl `MeteringPointIdentification`; lookup via `SearchByIdentificationAsync`. |
| **Settl GUID** | Settl-generated | Internal database id (`Id` on every DTO). |

An 18-digit number from Settl MP data is a metering point identification, **not** an EAN.

## Historical payer links and calendar dates

Payer link `Start` and `End` timestamps with `Z` or an explicit offset describe instants.
Convert them to Danish time before taking the calendar date, independently of the server's
local timezone. For example `2026-09-01T22:00:00Z` is `2026-09-02` in Denmark. Date-only
and zone-free timestamps retain their supplied calendar date. Payer-link ends are exclusive;
do not apply the contract-link end converter's minus-one-day adjustment to payer links.

The production `POST /api/v1/Payers/payersForMeteringPoints` endpoint returns an array and
ignores `Limit` and `Skip` (verified read-only 2026-10-01 with two metering points and
distinct payers, including `Limit=1` and `Skip=10000`). Do not infer that its shared request
DTO's pagination fields imply working pagination; a loop waiting for an empty page repeats
the same associations indefinitely.

## Reference

Swagger source for the contract facts above: `docs/settl-openapi/2026-06-15/`
(`Billing-Internal.json`, `Market-Internal.json`) — a point-in-time Settl OpenAPI snapshot.
It is not proof of today's endpoints.
