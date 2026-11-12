> **✅ IMPLEMENTERET** — `0864b674`
>

# Plan: wire IgnoredGroups (merge + owner-clusters)

## Verifikation (start her)

### Trin 1 — pakke-side (UtilityData)
Kør:
```
cd C:\Users\sen.makj\source\repos\XellentDomain
dotnet build
dotnet test
```
Forventet: 0 errors; alle tests passed (inkl. ny resolver-test der beviser at en konto i ignoreret TG4 står alene i begge modes).

### Trin 2 — dette repo, owner-cluster-filter (uafhængigt af ny pakke)
Owner-cluster-filtret kompilerer mod nuværende pakke (IgnoredGroupNumbers findes allerede som parameter).
Kør:
```
dotnet test src/Tests/DataMigration.Migration.Tests/DataMigration.Migration.Tests.csproj --filter OwnerCluster
```
Forventet: ny test passerer — BuildOwnerClusters udelader ignorerede TG4 (ingen "cannot synthesize owner"-fejl for dem).

### Trin 3 — end-to-end merge (efter pakke-CI har publiceret preview)
Kør (dette repo):
```
dotnet nuget locals http-cache --clear
dotnet restore --force
RUN_INTEGRATION=1 dotnet test src/Tests/DataMigration.Xellent.IntegrationTests/...   (golden)
```
Verificér: en profil med IgnoredGroups sætter ikke de konti i en merge-gruppe; hver står som egen Settl-customer; ingen owner-cluster-fejl for de grupper.

### Spot-check (Xellent)
2-3 konti i fx "Medarbej" der i dag merger/klynger → efter ændring: standalone rep, ingen owner.

## Kontekst
`IgnoredGroups` er i dag dead config (ingen binding her; `MapperOptions.IgnoredGroupNumbers` deklareret men ukonsumeret i pakken). Wires så ignorerede TG4-grupper hverken merger eller danner owner-cluster.

## Ændringer

### Pakke (UtilityData) — merge + pakkens egen owner-cluster
- `AccountRepresentativeResolver.ResolveAsync`: nyt valgfrit `ignoredGroupNumbers`-param. Fingerprint-path: hent TG4, spring edge-tilføjelse over → standalone. Legacy-path: `KeyOf` returnerer accountnum når TG4 ignoreret.
- `CustomerMapper.MapAsync`: send `_options.IgnoredGroupNumbers` videre.
- `XellentDomainMapper.BuildOwnerClustersAsync`: udelad ignoreret TG4 (konsistens for loadResult.OwnerClusters).
- `Directory.Build.props`: VersionPrefix 0.8.5 → 0.8.6 (tjek feed). Commit develop → CI preview.

### Dette repo — plumbing + owner-cluster-filter
- `ExtractionOptions.IgnoredGroups` (string[]?) + `ReconcileMappingOptions.IgnoredGroups` (parity).
- `CreateOptions.IgnoredGroups`.
- `CreateOptionsFactory.Build` + `BuildFromMapping`: map IgnoredGroups.
- `MigrationDomainLoad.BuildMapperOptions`: sæt `IgnoredGroupNumbers`.
- `SettlMappingService.BuildOwnerClusters`: filtrér ignoreret TG4; CreateRunner sender `options.IgnoredGroups`.
- Efter pakke-CI: restore --force, build, test.

### Docs/changelog
- `docs/representation-principle.md`: "Ignored groups" er ikke længere legacy-only.
- `CHANGELOG.md`: række for adfærdsdelta i create.
