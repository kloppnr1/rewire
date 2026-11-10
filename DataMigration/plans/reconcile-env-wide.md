> **✅ IMPLEMENTERET** — `5cbe0df5`
>

# Plan: reconcile env-wide — generelt mapping pr. dataarea, ingen profil

> Status: PLAN GEMT — implementering ikke startet. Eksekvering: DataMigration først
> (backend + unit), så MigrationDashboard (env-only side). Commits løbende; develop→master
> er bruger-beslutning.

## Verifikation (start her)

### Trin 1 — build + unit tests
Kør:
```
dotnet build src/DataMigration.OpsCli.Library/DataMigration.OpsCli.Library.csproj
dotnet test src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj
```
Forventet: 0 build-errors; alle unit tests passed (inkl. nye tests for general-mapping-loader
+ MP-id→account-resolver).

### Trin 2 — CLI smoke (argument-wiring)
Kør:
```
dotnet run --project src/DataMigration.OpsCli -- reconcile --help
```
Forventet: exit 0; viser `--env`, `--output`, `--mapping` (override). INGEN `<profile>`-argument.

### Trin 3 — env-wide run mod featuretest (afgørende bevis)
Kør:
```
dotnet run --project src/DataMigration.OpsCli -- reconcile --env featuretest
```
Forventet:
- Stderr-log: `Settl parent InDelivery MPs (env): N` (paginering, ikke profil-scopet).
- `Distinct COS dates (Xellent loads): D`.
- Reconcile producerer CSV med `Category;RepresentativeAccount;Entity;EntityKey;Field;Intended;Actual;Kind`.
- Antal `MissingInXellent`-rækker ≈ 0 for korrekt-migrerede MP'er (ingen falsk drift fra
  manglende merge/owner-config — beviser at general-mapping dækker det profilerne gav).

### Trin 4 — spot-check (2-3 records vs kilde)
Verificér mod kildedata:
- Vælg 2 MP'er fra Settl InDelivery: bekræft at deres ejende account er fundet i Xellent ved
  refDate=COS-1 (MP-id→account-resolver virker).
- Vælg 1 account under en kendt `LegacyTg4Merge`-profil (PPA/Spar Nord): bekræft at den får
  SAMME repræsentant via general-mapping som den fik via create — dvs. INGEN falsk customer/
  payer-drift.

Read-only kommando (muterer ikke Settl) → ingen dry-run/hot-run-trin.

## Kontekst
Reconcile skal kunne køre **uden profil** — drevet udelukkende af et env/dataarea. Selektionen
kommer fra Settls parent-MP'er (ikke en profils account-liste); deres COS finder de kunder/
kontrakter i Xellent der skal sammenlignes. Profilens *øvrige* bidrag (kontrakt-maps, owner-
clusters, `LegacyTg4Merge`) flyttes til et **generelt mapping pr. dataarea**, så create-true kan
bygges uden at vide hvilken profil en account hører til.

## Tradeoff (afgjort — accepteret)
General-mapping bruges **kun til reconcile**; `create` forbliver profil-drevet. Konsekvens:
reconciles "create-true"-garanti afhænger nu af at general-mapping-filen matcher unionen af
profilernes config. Divergerer de, rapporterer reconcile falsk drift. Filen er derfor den
autoritative kilde der skal holdes i sync manuelt (ét sted pr. dataarea).

## Generelt mapping — form (afgjort)
- **Scope**: ét pr. env/dataarea (hol / HJH / Han).
- **Kilde**: én fil, vedligeholdt ét sted. Forslag: `profiles/reconcile/{dataarea}.json`
  (override via `--mapping <sti>`).
- **Indhold** (præcis det profilen leverer udover account-listen):
  - Kontrakt-resolution: `GroupNum` / `AccountNum` / `ProductNum` / `SettlementGroup`-maps.
  - Owner-clusters: `GroupOwners` / `AccountOwners`.
  - Merge-regel: `LegacyTg4Merge` (bool).
  - `DeliveryCategory` (default `El-ekstern` — parent-MP'er er delpoint-baserede).
- Ny type `ReconcileMappingOptions` + loader (mirror af de relevante `ProfileOptions`-felter,
  men uden `Accounts`).

## Ændringer — DataMigration

**Ny: `ReconcileMappingOptions` + `ReconcileMappingLoader`** (OpsCli.Library eller Migration.Library)
- Læser `profiles/reconcile/{dataarea}.json` → mapping-config.
- `CreateOptionsFactory` får en overload `BuildFromMapping(mapping, accounts, env, refDate, exemption, dryRun)`
  der bygger `CreateOptions` fra general-mapping + Settl-deriverede accounts (i stedet for profil).

**Ny: MP-id → account-resolver** (det manglende led)
- I dag er al extraction account-keyed; der er INGEN MP-id→account-vej.
- Forslag: ny query i `SettlMappingService` / XellentData: `ExuDelpoint.Meteringpoint ∈ {mp-ids}`
  → contract → `ExuContractTable.Accountnum`, scopet til dataarea + aktiv ved refDate. Returnér
  `Dictionary<mpId, account>`.
- Derefter `AccountRepresentativeResolver` + `XellentDomainMapper.LoadAsync(de accounts)` som i dag.

**`ReconcileRunner` — env-wide mode**
- Erstat trin 0 (profil-Xellent-enumeration) + trin 1 (per-MP `SearchByIdentificationAsync`) med:
  paginér `FindMeteringPointsAsync(limit, skip)` → behold `InDelivery=true` parent-MP'er (skip
  child via `ParentMeteringPoint.Id`) med COS fra `CurrentOrNextDeliveryPeriod.Start`.
- MP-id→account-resolver → distinkte accounts pr. COS-slice.
- Per COS: `MigrationDomainLoad.LoadAsync` med `CreateOptions` bygget fra general-mapping +
  slice-accounts → `CreateRunner` capture-mode → intended plans (uændret).
- Resten (union, diff, anomali, CSV) uændret. `OnProgress` flytter til paginerings-loopet.
- `ScanAsync(env, ct)` (drop `profileName`); behold strukturerede rækker til dashboardet.

**`src/DataMigration.OpsCli/Program.cs`**
- `reconcile`-kommando: drop `<profile>`-argument; behold `--env`, `--output`; tilføj
  `--mapping <sti>` (override default `profiles/reconcile/{dataarea}.json`).

**Tests** (`src/Tests/DataMigration.OpsCli.Tests/`)
- `ReconcileMappingLoader`: parser fil korrekt.
- MP-id→account-resolver: mock/integration (lille account-sæt).
- Bevar eksisterende `JsonValueDiff`/`DriftCategorizer`/`CosSlicer`-tests grønne.

**`CHANGELOG.md`** (repo-rod, samme commit — adfærdsdelta i Runners): ny række.

## Ændringer — MigrationDashboard (følge-op, eget repo)
- `ReconcileView`: **fjern profil-vælger**; behold kun env-vælger. Kør = `scan?env=`.
- `ReconcileScanService.StartScan(env)` (drop profile); endpoint `POST /api/reports/reconcile/scan?env=`.
- Resten (poll/result/DTO/tabel) uændret.

## Åbne punkter (afklar under implementering)
- **MP-id→account-query**: præcis join-vej (ExuDelpoint→ContractPart→Contract→Cust) + dataarea/
  refDate-filter skal verificeres mod `xel2012` (integration-test).
- **Performance**: full `FindMeteringPointsAsync`-paginering mod production kan være tung — log
  progress, evt. `--limit`/`--skip` til afgrænset kørsel.
- **General-mapping seed**: første `{dataarea}.json` bygges som union af eksisterende profilers
  mappings for den dataarea (engangs-arbejde, manuelt verificeret).
