# NewSettlement.SettlClient — Settl REST API client

This repo is the **only** Settl REST API client in the workspace. It publishes two
NuGet packages consumed by DataMigration, MigrationDashboard, TimeSeriesMigration and
CustomerSupport:

- `NewSettlement.SettlClient.Api` (`DataMigration.SettlApi`) — REST client (customers,
  payers, metering points, contracts, settlement groups). HTTP plumbing in
  `Clients/SettlApiClientBase.cs`; one client class per entity under `Clients/`.
- `NewSettlement.SettlClient` (`DataMigration.SettlClient`) — Domain→Settl migration
  orchestrator (`SettlClient`, translators under `Internal/`).

See the workspace CLAUDE.md (`source/repos`) for the full repo map, NuGet floating-ref
flow, branch standard (develop→master), and verification rules. This file holds the
**Settl API contract knowledge** that is this repo's responsibility to keep accurate.

## Settl API versioning per entity — use the right version per operation

The Settl Billing API is **not** uniformly versioned. Each entity mixes versions:

| Entity | Create | Read / search / list | Update (core) |
|--------|--------|----------------------|---------------|
| Customer | `POST /api/v2/customers` | `GET /api/v2/customers/{id}` + `/api/v2/customers?...` | **`PUT /api/v3/customers/{id}`** |
| Payer | `POST /api/v1/Payers` | `GET /api/v1/Payers/{id}` | `PUT /api/v1/Payers/{id}` |
| MeteringPoint (master) | **`POST /api/v4/meteringPoints`** | `GET /api/v3/meteringPoints/{id}` | `PUT /api/v3/meteringPoints/{id}` |

**The metering-point trap:** the v3 create route is **gone** — `POST /api/v3/meteringPoints` answers
**404** (it briefly answered 405 while Settl was migrating). Create must go to v4, while read and
master-update stay on v3. Verified against featuretest 2026-08-12: `POST /api/v4/meteringPoints`
returns 201 on an empty list, and `GET /api/v4/meteringPoints` is 404 — v4 has only POST and
`/{id}`. The v4 create body differs from v3
in exactly two places: the list is called `MeteringPointCreateObjectDtos` (not `MeteringPoints`), and
`InstallationAddress` is called `InstallationAddressCreateObject`. That address section carries Settl's
DAR switches: `EnrichAddress` (Settl derives municipality code, street code and DAR identifier from the
rest of the address) and `SkipAddressValidation` (create even when no DAR entry matches exactly).
Validation is enabled on featuretest only, not in production. Both are per-run settings on
`SettlMigrationContext` — default enrich on, skip off.

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
  + `TransactionId`.
- **MeteringPoint = WRAPPED** — `{ MeteringPoint: {...}, TransactionId }`.

Used by DataMigration's `reconcile-apply` core writers and `UpdateEstimatedAnnualVolumeAsync`.

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
