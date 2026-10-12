# NewSettlement.DataMigration

Tools for migrating customer master data from Xellent (Dynamics AX 2009) to Settl via the Settl REST API. Built around a per-customer `create` pipeline that runs either headless via CLI or interactively via a web dashboard.

## Payment-match rules

For reports supplied with a list of MP IDs, see the [complete MobilePay and PBS rules](https://dev.azure.com/verdo365/Verdo%20Agile%20Development/_git/NewSettlement.MigrationDashboard?path=/README.md&version=GBmaster&_a=preview) under **MP-list payment reports: current rules**. They cover private and business agreement selection, cross-commodity searches, the water/heat business error, matching to Settl E-numbers and report fields.

## What's here

### Web API

| Project | Description |
|---|---|
| [DataMigration.Api](src/DataMigration.Api/) | .NET 9 web API med Swagger UI (`/swagger`). Hosts `CreateRunner` in-process og eksponerer migrations-endpoints + CSV-download endpoints for `report-*` kommandoerne. Deployed as Windows Service `DataMigration.MigrationDashboard` on port 5050. Frontend lever i separat repo [`NewSettlement.MigrationDashboard`](https://verdo365.visualstudio.com/Verdo%20Agile%20Development/_git/NewSettlement.MigrationDashboard). |

### CLIs

| Project | Description |
|---|---|
| [DataMigration.Migration](src/DataMigration.Migration/) | `create` pipeline CLI. Runs customer → payer → MP → links against the Settl API for a profile |
| [DataMigration.OpsCli](src/DataMigration.OpsCli/) | Read-only ops CLI. Subcommands include `report`, `compare`, `payment-match`, `generate-seed`, `pull-state-sources`, and `generate-state-from-settl` |

### Shared libraries

| Project | Description |
|---|---|
| DataMigration.Core | Shared models, `MigrationConstants`, `WarningCollector` |
| DataMigration.Domain | Generic domain model (Customer, Contract, MeteringPoint, Product) — kilde-agnostisk repræsentation brugt af nye extraction-flow |
| DataMigration.Domain.Xellent | Xellent → `DataMigration.Domain` mapper (`XellentDomainMapper`, `AddressParser`) |
| DataMigration.XellentData | Xellent DB access (EF Core against AXDB50 / AXDB50_TEST) |
| DataMigration.SettlApi | Settl REST API client (customers, payers, metering points, contracts) |

## Quick start

```bash
# Run the full per-customer migration pipeline for a profile
dotnet run --project src/DataMigration.Migration -- create my-profile --env production

# Dry run first to verify extraction without hitting the create endpoints
dotnet run --project src/DataMigration.Migration -- create my-profile --dry-run --env featuretest

# Read-only scan: check Settl MPs for currency/EAN/joint/grid/type drift
dotnet run --project src/DataMigration.OpsCli -- report-meteringpoints --env production

# Generate HTML migration report from Xellent extraction (no API calls)
dotnet run --project src/DataMigration.OpsCli -- report my-profile --env production

# Start the API (http://localhost:5050) — frontend lever i NewSettlement.MigrationDashboard
dotnet run --project src/DataMigration.Api
```

See [src/DataMigration.OpsCli/README.md](src/DataMigration.OpsCli/README.md) for the ops command reference and profile schema.

## Build & test

```bash
dotnet build DataMigration.sln
dotnet test DataMigration.sln
```

Requires .NET 9.0 SDK. Linting policy (from `Directory.Build.props`): analyzers enabled, warnings treated as errors.

## Deployment

CI/CD via `azure-pipelines.yml`.

| Environment | Server | Default Xellent DB |
|---|---|---|
| Staging | `svdkra1vudv03` | `AXDB50_TEST` |
| Production | `SVDKRA1VBIFROST` | `AXDB50` |

- Push to `develop` → deploy Migration + OpsCli + Api to Staging
- Push to `master` → deploy Migration + OpsCli + Api to Production
- Manual runs can toggle individual services via pipeline parameters

Server targets: `C:\Services\DataMigration\Migration\`, `C:\Services\DataMigration\OpsCli\`, and `C:\Services\DataMigration\MigrationDashboard\` (Windows Service-navnet er bibeholdt for `DataMigration.Api`). External data (profiler, dashboard wave state) lever i `C:\Services\DataMigration\data\` og overlever redeploys.

## Profiles

JSON files in `src/profiles/` (gitignored by default — add to whitelist in `.gitignore` to share one). Example profiles like `exolak.json`, `pilot-production.json`, and the various `ppa-*.json` files are committed as reference.

## Related repos (repo decomposition)

This repo used to contain many more tools. Parts have been split out over time:

| Repo | What it owns |
|---|---|
| [NewSettlement.MigrationOps](https://verdo365.visualstudio.com/Verdo%20Agile%20Development/_git/NewSettlement.MigrationOps) | PDF invoice archive, migration tracking DB, Xellent notes CLI. Host of `DataMigration.Tracking` and all PDF/PdfIndex/PdfExport/XellentNotes/TrackingCli projects (moved out April 2026). OpsCli was moved out in the same split and moved **back** into this repo on 2026-04-15 — see `src/DataMigration.OpsCli/PORTED-FROM.md` |
| NewSettlement.CustomerSupport | Bifrost customer support web app. **Reads `migration-tracking.db` directly** from the MigrationOps deployment |
| NewSettlement.TimeSeriesMigration | Time series extraction + Settl posting (moved out 2026-04-13) |
| NewSettlement.XellentSettlement | Legacy settlement code (moved out earlier) |
| NewSettlement.UtilityOperations | ExportIsmobileRunner (moved out earlier) |
| NewSettlement.Tools | DataMigration.Analysis (moved out earlier) |

## Documentation

- [OpsCli command reference](src/DataMigration.OpsCli/README.md) — report, compare, generate-state, etc.
- [docs/settl-update-map.md](docs/settl-update-map.md) — Xellent↔Settl↔Repo lineage table grupperet pr. MP sub-section
- [docs/settl-api-integration.md](docs/settl-api-integration.md) — Settl API integration notes
- [docs/settl-vendor-request.md](docs/settl-vendor-request.md) — endpoint overview
- [docs/dst-handling.md](docs/dst-handling.md) — daylight savings transition handling for time series
- [docs/billing_public_api.json](docs/billing_public_api.json), [docs/market_public_api.json](docs/market_public_api.json), [docs/billing_internal_api.json](docs/billing_internal_api.json), [docs/market_internal_api.json](docs/market_internal_api.json) — Settl API specs
- [docs/settl-api-swagger.json](docs/settl-api-swagger.json) — Swagger for Settl API
