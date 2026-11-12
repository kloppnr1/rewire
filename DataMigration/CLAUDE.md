# Project Memory

## Representation principle (read first — applies to anything touching accounts)

Customers with same **CVR + Fingerprint** merge into one Settl customer.
Lowest Accountnum wins and becomes the representative; its Accountnum is the
only one written to Settl as `CustomerNumber`. Other group members vanish
from Settl — their MPs and payers hang under the representative.

Fingerprint = normalised
`Name|AdditionalName|Street|Zip|City|Email` (NFD + accent-fold +
lowercase + strip whitespace/punctuation incl. NBSP, '/', '.', ',', '-',
'&', quotes, parens). Address fields come from the **primary
CustTable-address** (`DirPartyAddressRelationship.Isprimary=1`), NOT from
the legal contact — even though the create-pipeline otherwise treats legal
contact as truth for customer/payer addresses. Email and EAN were both
removed from the fingerprint 2026-05-20, then Email was re-introduced
2026-06-04: a differing contact email again splits same-CVR accounts into
separate Settl customers (Email participates in both merge-edges — see
source note below). EAN stays out — contract-level EAN variation
legitimately appears under one legal entity and shouldn't split merge-groups.

Legacy `(CVR + TechnicGroup4)` merge-rule preserved for profiles that set
`Extraction.LegacyTg4Merge=true` (PPA, Spar Nord — branches that must
merge despite varying addresses). The `ValidateCvrGroupings` validator
runs only in legacy-mode.

`OwnerCluster` is a separate construct (not the merge rule): ≥2
representatives sharing the same TG4 are wrapped under a synthetic
owner-customer. CVR can vary across cluster members. Lives one level above
representatives.

Source: `FingerprintCalculator.cs` + `AccountRepresentativeResolver.cs` in
NuGet package `XellentDomain.Domain.Xellent` (repo
`XellentDomain`) — no longer in this repo. The resolver is a
union-find over two edges: street-fingerprint (`S|Cvr|fp`) OR legal-contact
DAR id (`D|Cvr|nameKey|darId`); same-CVR accounts merge on either. Email is
folded into the fingerprint on both edges.

**Consequence for any code that takes Xellent Accountnums and looks them up
in Settl (report-*, compare, generate-state):** translate Accountnum →
representative via `AccountRepresentativeResolver` (the package type)
BEFORE calling Settl. `GetEffectiveGroupKey` is now a runtime lookup over
`_mergeKeyByAccount` (not a pure function — requires
`BuildMergeKeyMapAsync` to have run first), so external/ops code MUST use
`AccountRepresentativeResolver` instead. Searching Settl with a
non-representative Accountnum returns null — silent data loss, not an
error. Forårsagede tidligere et false-negative i en daværende fix-runner
(2026-04-15). Full detail: `docs/representation-principle.md`.

**Scale limit on the resolver:** one `AccountRepresentativeResolver.ResolveAsync`
call above ~30.000 accounts kills the run in seconds with *"the query processor
ran out of internal resources and could not produce a query plan"* — the account
list is inlined as literals (compat level 110, no `OPENJSON`). It is plan
compilation, not a timeout. A larger caller must portion on **whole CVR classes**,
never raw account chunks. `export-settlement-config`, `report-meter-info` (via
`XellentDomainLoader`) and `generate-state-from-settl` (COS path) are guarded;
`--flat-reference-date` is not. **This will hit Vand and Varme too — and it is NOT
`MeteringPointAccountResolver.BatchSize`, which was blamed first and is fine.**
Measured ceiling + guard rules: `docs/representative-resolver-scale-limit.md`.

## Planning
Plans live in `plans/` at repo root. Keep them concise — bullet points only, no code snippets or lengthy explanations.

## Git
Commit workflow: After a code change is built successfully (or tested/verified), ask to commit immediately. Do not batch multiple fixes into one commit unless I request it.

**Adfærdshistorik for create + fix-* commands:** Når en commit ændrer adfærd i `src/DataMigration.Migration/` eller `src/DataMigration.OpsCli/Runners/`, tilføj en række til tabellen i `CHANGELOG.md` (repo-rod) i **samme commit**. Rene refactors uden adfærdsdelta, test-only og doc-only commits springer over. Format: `| YYYY-MM-DD | \`command\` (eller \`a\` + \`b\`) | kort overskrift | \`sha\` |`. Nyeste række øverst. Commit-SHA kan tilføjes som follow-up amend hvis nødvendigt.

## Identifier-system — tre typer der IKKE må forveksles

| Identifikator | Eksempel | Kilde | Bruges til |
|---------------|----------|-------|------------|
| **EAN** (`ElectronicNumber`/`Einvoiceeannum`) | 13-cifret | `ExuContractTable.Einvoiceeannum` | E-fakturering (OioXml). Sidder på Payer billing-config. **Intet med metering points at gøre.** |
| **Metering point identification** | 571313179104952730 (18-cifret) | `ExuDelpoint.Meteringpoint` | Nationalt MP-ID. Migreres til Settl som `MeteringPointIdentification`. Bruges til Settl-lookup via `SearchByIdentificationAsync()`. |
| **Settl GUID** | UUID-format | `MeteringPointDto.Id` | Settl's interne database-ID. Genereres ved oprettelse. Gemmes i tracking DB som `MigratedContract.MeteringPointId`. |

**Regel:** Et 18-cifret tal fra Settl MP-data er et metering point identification, IKKE et EAN. EAN optræder kun i billing/payer-kontekst (`PaymentConfiguration.ElectronicNumber`).

## Migration flow (Xellent → Settl)

### Creation sequence (`create` command → CreateRunner → SettlClient.MigrateAsync)

**Extraction:** `XellentDomainMapper.LoadAsync` → `XellentLoadResult` (customers + MPs).
`SettlExtractionService` beholdes til support-queries: `BuildMergeKeyMapAsync`,
`ExtractAccountGroupingsAsync`, `ExtractAccountMeteringPointMappingAsync`,
`ExtractMpOrderIdentifiersAsync`, `ExtractMpActiveContractTemplatesAsync`.
`ContractNameResolver` løser contract-navne → Settl contract-GUIDs før per-kunde-loop.

**Per customer (`SettlClient.MigrateAsync`)**, i denne rækkefølge:

1. **Customer** — `CustomerTranslator.Build` → `SearchByCustomerNumberAsync` → create hvis ny → Settl Guid
2. **Payer** — `PayerTranslator.Build` → `SearchByPayerIdentificationAsync` → create hvis ny → Guid
3. **Metering Point(s)** — `MeteringPointTranslator.Build` → electricity: search-or-create + 5x retry; water/heat: search-only (pre-populated eksternt)
4. **Billing config** — `UpdateBillingAsync`: Currency=DKK + JointInvoice + use-case-2 OrderIdentifier (electricity only)
5. **Links** — Customer↔MP, Payer↔MP, Contract↔MP (additive, skip eksisterende; contract-links keyed by Settl contract Guid)

### Dual API for metering points

MPs lever i to parallelle Settl API'er. Brug den rigtige:

| API | Formål | Felter | Case |
|-----|--------|--------|------|
| Market v3 (`/api/v3/meteringPoints`) | Master data | Identification, Address, Consumer, Grid, Meter, JointInvoiceNumber | Create + update master data |
| Billing v1 (`/api/v1/meteringPoints`) | Billing config | BillingConfiguration (Currency, JointInvoice), PaymentConfiguration (ElectronicNumber, BillingType) | `create`-step 4; `fix-settlement-groups` |

### MP extraction strategies (DeliveryCategory bestemmer)

| DeliveryCategory | Strategy | Xellent join path | Settl API |
|-----------------|----------|-------------------|-----------|
| El-ekstern, El, FI, Tele, Supply, Service | **Delpoint-based** | Contract→ContractPart→Agreement→Delpoint | Market v3 (electricity) |
| Vand, Varme, Kloak, Charge, Charge_Abo | **Installation-based** | Contract→ContractPart→Installation | Commodities/Tenant v1 (water) |

Kilde: `MeteringPointStrategyFactory.DelpointCategories`. Installation-based MPs har ingen Grid/SettlementMethod og bruger kundens postadresse.

### Settl mutations sker kun via `create`

Tidligere `fix-*` runners er fjernet (commit `3f946ac3`, 2026). Drift detekteres nu read-only via `report-meteringpoints` + `report-payers`; rettelser sker ved at køre `create` igen, ikke via separat fix-runner. `docs/settl-update-map.md` bevares som lineage-tabel (Xellent↔Settl↔repo) selvom fix-runners er væk.

## migration-state.json — unified invoice+tracking JSON

**Canonical DTO:** `src/DataMigration.OpsCli/Data/MigrationState.cs` (`MigrationStateDocument`, `MigratedAccount`, `InvoiceManifestItem`). This repo is source of truth. Byte-identical forks (namespace only differs) live in `SettlInvoicePdfUpload` and `CustomerSupport` — changes here must propagate to both.

**Production path:** `C:\Services\DataMigration\OpsCli\data\migration-state.json`

**Workflow:** `generate-state` → producerer tracking + invoice JSON for én eller flere profiler. `generate-state-from-settl` → Settl-driven shape (én entry pr. kunde i Settl, ingen Xellent, overskriver altid). `migration-tracking.db` is dead and must not be used.

## What's in this repo

`DataMigration.Migration` (the `create` pipeline CLI) og `DataMigration.OpsCli` (read-only ops). Sibling repos:

- `MigrationDashboard` — React frontend + `Dashboard.Api` (port-5050 web API; hoster CreateRunner + report-CSV-endpoints + reconcile-scan). Flyttet ud 2026, commit `747f1325`.
- `CustomerSupport` — legacy Bifrost customer support web app
- `MigrationOps` — PDF archiving + tracking + notes (PdfIndexCli/Service, PdfExportCli, XellentNotesCli, TrackingCli)
- `TimeSeriesMigration` — time series extraction + Settl posting

**Web API:** flyttet ud af dette repo. `DataMigration.Api` blev slettet 2026-05-13; port-5050-web-API'et (CreateRunner in-process + `report-*` CSV-download-endpoints) lever nu som `Dashboard.Api` i `MigrationDashboard` (sammen med React-frontenden) og deploys derfra.

**CLIs:**
- `DataMigration.Migration` — `create` command (customer → payer → MP → links) + CreateRunner + SettlExtractionService. **Eneste komponent der muterer Settl.**
- `DataMigration.OpsCli` — read-only ops + Excel-export: `report`, `report-meteringpoints`, `report-payers`, `report-drift` (unified "forkert-i-Settl"-scan: aggregerer report-meteringpoints + report-payers + payer fan-out drift), `report-meter-info`, `report-delivery-start`, `compare`, `payment-match` (`--type pbs|mb`), `generate-seed`, `generate-state`, `generate-state-from-settl`, `extract-domain` (smoke-test af `XellentDomainMapper`), `export-eav-excel`, `export-exemption-excel`, `fix-settlement-groups` (eneste mutation-runner: tilføjer MPs til eksisterende Settl Settlement Groups). Deployed til `C:\Services\DataMigration\OpsCli\`. Settl-MP-søgning (på kundenummer / identification) lever nu i dashboardet (`Dashboard.Api` → `SettlMeteringPointLookupService` fra NuGet-pakken) — den gamle `report-profile-mps`-kommando er fjernet.

**Libraries (lokale projekter):**
- `DataMigration.Core`           - Shared models and helpers (WarningCollector, MigrationConstants)
- `DataMigration.Domain`         - Generic source-agnostic domain model (`Customer`, `Contract`, `MeteringPoint`, `Product`, `OwnerCluster`, `CustomerSnapshot`, `CustomerBatch`)
- `DataMigration.Domain.Xellent` - Xellent → Domain loader (`XellentDomainMapper`, `MapperOptions`, customer-validators under `Validation/`)
- `DataMigration.XellentData`    - Xellent database access (EF Core, Dynamics AX 2009)

**Libraries (folder `SettlClient`):**
- `SettlClient.Api` (`DataMigration.SettlApi`) - Settl REST API client (customers, payers, MPs, contracts)
- `SettlClient` (`DataMigration.SettlClient`)  - Domain → Settl migration orchestrator (`SettlClient`, `ISettlClient`, translators under `Internal/`: `CustomerTranslator`, `PayerTranslator`, `MeteringPointTranslator`)

**Public API rule (Domain.Xellent):** consumers `using DataMigration.Domain.Customers;` for output-typer (`CustomerSnapshot`, `CustomerBatch`, `OwnerCluster`). Kun `XellentDomainMapper` + `MapperOptions` + validators eksponeres fra `Domain.Xellent`-namespace selv. Ingen Xellent-specifikke typer i return-shapes — gør Domain genbrugelig fra fremtidige loaders. Implementation-mappers (`CustomerMapper`, `ContractMapper`, `MeteringPointMapper`) er `internal sealed` i `Domain.Xellent.Internal`.

## Reference data (loaded on demand via skills)

- Xellent products and billing plans → `.claude/skills/xellent-products/`
- Dashboard start/stop/status → `dashboard-ops`, which lives in the **MigrationDashboard** repo
  (`MigrationDashboard/.claude/skills/dashboard-ops/SKILL.md`) — read it as a file,
  or let `migrationdashboard-expert` do it. Any dashboard process on this machine is a leftover
  from a Claude session; stop it rather than working around it.
- Settl API queries → `.claude/skills/settl-api-query/`
- Post-migration fix authoring + routing → `.claude/skills/post-migration-fix/` (consults `docs/settl-update-map.md`)

## Test gates

- **Xellent live-DB regression tests** → `.claude/skills/xellent-integration-tests/`
  Run proactively after any change to extraction-layer code (SettlExtractionService,
  XellentDomainMapper, SettlClient, CreateRunner, helpers under `Services/Extraction/`
  or translators in NuGet package `SettlClient`). Project lives at
  `src/Tests/DataMigration.Xellent.IntegrationTests/` and is **deliberately not in the
  sln** — CI has no access to `xel2012`.
- **OpsCli live-run integration tests** — `src/Tests/DataMigration.OpsCli.IntegrationTests/`
  (also **deliberately not in sln**). Verifies report/compare/state-runners mod
  AXDB50_TEST + Settl featuretest. Run after changes to any runner i
  `src/DataMigration.OpsCli/Runners/`. See project README for kontekst.

## Database connectivity rule

ALL internal databases (xel2012/AXDB50, Bifrost servers, etc.) require corporate VPN.
On the FIRST DB query failure with "named pipe", "network-related", "netværksstien",
"could not open a connection" or "not found or was not accessible" — STOP, tell the
user the VPN is likely down, and wait. Never retry on a loop.
