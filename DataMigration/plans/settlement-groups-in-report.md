> **✅ IMPLEMENTERET** — `d3cdab03`
>

# Settlement groups synlige i create-rapporten (offline + dry-run + hot)

## Verifikation (start her)

### Hvad der beviser at det virker

Kør `create` i alle tre modes mod en profil med `SettlementGroupMap` (fx PPA):

**Trin 1 — offline (ingen Settl):**
```
dotnet run --project src/DataMigration.Migration -- create --profile ppa --offline --report-json out-offline.json
```
Forventet: rapporten har `CustomerSettlementGroups` udfyldt (accountnum → gruppenavn(e)),
opløst lokalt. `SettlementGroupsAdded/SkippedExisting/Failed` = 0 (ingen Settl).
Ingen "not found"-errors.

**Trin 2 — dry-run mod featuretest (read-only Settl):**
```
dotnet run --project src/DataMigration.Migration -- create --profile ppa --dry-run --env featuretest --report-json out-dryrun.json
```
Forventet: `CustomerSettlementGroups` udfyldt (samme som offline), OG
`SettlementGroupsAdded` = antal "would add", `SettlementGroupsSkippedExisting` =
antal allerede-medlemmer, `SettlementGroupsFailed` = antal grupper ikke fundet i Settl.
Counts != 0 hvis profilen har kunder uden for grupperne.

**Trin 3 — spot-check:** vælg 2-3 kunder fra `CustomerSettlementGroups` i out-dryrun.json
og verificér gruppenavnet mod profilens `settlementGroupMap` (planCode → gruppe) og
kundens kontrakters `PlanCode`.

**Trin 4 — hot-run mod featuretest:**
```
dotnet run --project src/DataMigration.Migration -- create --profile ppa --env featuretest --report-json out-hot.json
```
Forventet: `SettlementGroupsAdded` matcher dry-run'ens would-add; kør igen → anden kørsel
viser samme antal under `SkippedExisting` (idempotent).

### Forventet output / succeskriterium
- Alle tre report-json'er har `CustomerSettlementGroups` populeret (også offline).
- Tællerne er != 0 i dry-run/hot (ikke længere altid 0).
- Dashboard-rapporten viser pr. kunde: forventet gruppe + (online) status.

## Kontekst
Settlement-group-assignment (kunde → gruppe via `AddCustomersAsync`) er i dag usynlig i
rapporten: `MpSettlementGroups` er MP-keyet og ikke renderet, og tællerne
`SettlementGroupsAdded/Skipped/Failed` fødes aldrig. Gør assignmentet synligt pr. kunde,
i alle modes.

## Ændringer

### Repo: NewSettlement.SettlClient (NuGet-producer — cross-repo)
- `AssignSettlementGroupsAsync`: returnér counts (added/wouldAdd, skippedExisting, failed)
  i stedet for `void`. Dry-run tæller "would add" som added.
- `SettlMigrationResult`: tilføj felter `SettlementGroupsAdded`, `SettlementGroupsSkippedExisting`,
  `SettlementGroupsFailed` (additivt, default 0).
- `MigrateAsync`: tildel counts fra `AssignSettlementGroupsAsync` til result-record.
- Bump `VersionPrefix` i `Directory.Build.props`. Commit til develop → vent på CI-preview.

### Repo: NewSettlement.DataMigration (her)
- `dotnet restore --force` → træk ny SettlClient-preview (floating ref).
- `CreateRunner` aggregering (linje ~675-702): `report.SettlementGroupsAdded += result.SettlementGroupsAdded`
  m.fl.
- Ny report-felt `CustomerSettlementGroups` (`Dictionary<string accountNumber, List<string>>`),
  forventet gruppe(r) pr. kunde — opløst lokalt FØR offline-droppet (samme kilde som
  `MpSettlementGroups`: `SettlementGroupMap` + kontrakternes `PlanCode`). Virker offline.
- Udvid `SettlementGroupExpectationResolver` med customer-keyet resolution (behold MP-keyet
  for nu — afklar om `MpSettlementGroups` skal udfases separat).
- Populér `report.CustomerSettlementGroups` ved siden af `MpSettlementGroups` (linje ~493).
- Unit test for customer-keyet resolver (first-wins ved flere planer, samme som MP-varianten).
- CHANGELOG.md-række (adfærdsændring i CreateRunner).

### Repo: NewSettlement.MigrationDashboard (cross-repo — separat follow-up)
- `dotnet restore --force` → ny SettlClient + DataMigration.Migration-preview.
- FromReport-mapping: medtag `CustomerSettlementGroups` + de tre tællere.
- React: render pr. kunde forventet gruppe; online berig med status/counts; offline kun forventet.

## Rækkefølge (cross-repo NuGet-flow)
1. SettlClient: result-felter + counts → commit develop → CI preview.
2. DataMigration: restore → CreateRunner-counts + CustomerSettlementGroups + test → commit.
3. MigrationDashboard: restore → rendering → commit.

## Afklaret
- Granularitet: pr. kunde → gruppe.
- Modes: forventet offline (lokalt), fuld status online (dry-run + hot).
- Tællere: ja, fødes fra MigrateResult.
