> **✅ IMPLEMENTERET** — `aba88763`
>

# Plan: Offline dry-run (skip alle Settl API-kald)

## Verifikation (start her)

### Hvad der beviser at det virker
Kør (efter SettlClient-pakke er restored i DataMigration):
```
# El-profil offline — skal være hurtig og lave NUL Settl-kald
dotnet run --project src/DataMigration.Migration -- create <el-profil> --offline

# Vand/varme-profil offline — vand-MPs skal vises som would-create, ikke fejle
dotnet run --project src/DataMigration.Migration -- create <vand-profil> --offline
```

### Forventet output / succeskriterium
- Log-linje øverst: `OFFLINE MODE: all Settl API calls skipped`
- `Dry Run: true` (offline tvinger dry-run)
- Alle kunder/payers/MPs: `[dry-run] Would create ...` (ingen `already exists`)
- Vand-profil: ingen `must be pre-populated`-fejl; vand-MPs = would-create
- 0 collector-errors fra contract-resolution / settlement-group preflight
- Exit code 0
- Mærkbart hurtigere end samme profil uden `--offline` (ingen netværks-round-trips)
- Sammenlign: `create <profil> --dry-run` (uden offline) skal stadig vise `already exists` for eksisterende entiteter (uændret adfærd)

## Kontekst
Dry-run laver i dag mange Settl read-kald (per-kunde customer/payer/MP-søgninger + link-GETs + contract-resolution + settlement preflight). En offline-mode springer ALLE Settl-kald over for hurtig extraction/payload-smoke-test; alt rapporteres som would-create.

## Repo-afhængighed (bekræft ledighed før start)
1. **NewSettlement.SettlClient** (NuGet-producer) — pull develop (1 commit bag), lille ændring, bump, push, CI-vent.
2. **NewSettlement.DataMigration** (dette repo) — hovedændringen.
3. **NewSettlement.MigrationDashboard** (valgfri, primær kørsels-sti) — én-linje wiring af flag.

## Ændringer

### SettlClient (NuGet — minimal)
- `SettlMigrationContext`: tilføj `bool Offline = false` (default false → ingen adfærdsændring for eksisterende callere).
- `SettlClient.MigrateAsync` water/heat-branch: når MP ikke findes **og** `context.Offline` → rapportér would-create (mpsCreated++, syntetisk guid) i stedet for `AddError`. El + normal dry-run uændret (normal dry-run skal stadig flagge manglende vand-MP).
- Bump `VersionPrefix` i `Directory.Build.props`. Commit develop, push, vent på CI-preview.

### DataMigration (dette repo)
- `CreateOptions`: tilføj `bool Offline`.
- `CreateRunner.RunAsync` — når `options.Offline`:
  - tving `DryRun = true`, log offline-banner (før "Dry Run"-log).
  - swap injicerede klienter → `Mock*Client` (eksisterende stubs) → al Settl-traffik bliver no-op; owner-resolvere/allocator bliver dermed også offline.
  - skip `ContractNameResolver` (tomt resultat) — ellers AddError per contract.
  - skip settlement-group preflight + sæt `SettlementGroupMap = null` i context — ellers AddError per gruppe.
  - sæt `context.Offline = true` (driver water-branch i SettlClient).
- `Program.cs` (create): tilføj `--offline` Option, send til `CreateOptionsFactory.Build`/CreateOptions.
- `CreateOptionsFactory`: propagér `offline`-parameter.
- Restore ny SettlClient-pakke (`dotnet restore --force`), build, test.
- CHANGELOG.md: ny række (adfærdsændring i Migration).

### MigrationDashboard (valgfri, separat session/godkendelse)
- Sæt `options.Offline = true` på dry-run-stien (CreateRunner-host i Dashboard.Api) — fx ny query-param/toggle.

## Verifikations-trin (build-time, ingen mutationer)
- SettlClient: `dotnet build` + `dotnet test` → 0 fejl.
- DataMigration: `dotnet build` + unit tests (`RUN_INTEGRATION=1` da extraction/SettlClient-sti berøres).
- CLI smoke: `create <profil> --offline` som ovenfor.
