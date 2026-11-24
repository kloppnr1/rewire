# Align MeteringPointMapper filters with ContractMapper

## Verifikation (start her)

### 1. Unit tests i XellentDomain
Kør:
```
dotnet test src/Tests/DataMigration.Domain.Xellent.Tests
```
Forventet: alle eksisterende tests passerer (0 failed).

### 2. Lokal pack + brug i DataMigration
Kør (XellentDomain repo):
```
dotnet pack src/DataMigration.Domain.Xellent -c Release -o ../local-temp /p:Version=0.6.1-preview-local
```
Verificér i DataMigration: bump `XellentDomain.Domain.Xellent` til `0.6.1-preview-local` (kun lokalt — commit IKKE).

### 3. Xellent integration tests (DataMigration repo)
Kør:
```
dotnet test src/Tests/DataMigration.Xellent.IntegrationTests
```
Forventet: golden-JSON for electricity-profiler kan ændre sig (færre MPs i loadResult.MeteringPoints). Diff vises — promovér IKKE uden eksplicit bruger-tilladelse.

### 4. Hot-spot verifikation: customer 104804
Kør (DataMigration repo, samme profil som tidligere):
```
dotnet run --project src/DataMigration.Migration -- create --profile <profil> --selected-accounts 104804 --dry-run
```
Forventet output i summary:
```
Metering Points: 1 extracted, 1 created, 0 existing, 0 failed
```
Tidligere: 3 extracted, 1 created.

## Kontekst
`MeteringPointMapper.MapElectricityAsync` og `MapInstallationAsync` ekstraherer flere MPs end `ContractMapper` tildeler — fordi de ikke anvender `ProductCodeFilter`. Resulterer i "spøgelses-MPs" i `loadResult.MeteringPoints` som aldrig migreres.

## Ændringer

- `Internal/MeteringPointMapper.cs::MapElectricityAsync`: tilføj samme `ProductCodeFilter`-klausul som `ContractMapper` har i electricity-part-joinet — gated på `delpoint.Meteringpoint` (parallelt med `ContractMapper`s gating på samme felt for el-grenen).
- `Internal/MeteringPointMapper.cs::MapInstallationAsync`: tilføj samme `ProductCodeFilter`-klausul — gated på `part.Instagreenum` (parallelt med `ContractMapper`s gating på samme felt for installation-grenen).
- Begge steder skal `AdditionalAccounts`-override også med, så filteret er præcist samme tre-leddet OR som i `ContractMapper`.
- Bump `Directory.Build.props` version til `0.6.1-preview` (release-bump skubbes til ADO-pipeline efter merge).
