> **✅ IMPLEMENTERET** — `037c7dcf`
>

# Plan: `report-drift` — unified "forkert-i-Settl" rapport (v1)

## Verifikation (start her)

### Trin 1 — build + unit tests
```
dotnet build src/DataMigration.OpsCli.sln
dotnet test src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj
```
Forventet: 0 build-errors; alle unit tests passed.

### Trin 2 — CLI smoke (argument-wiring)
```
dotnet run --project src/DataMigration.OpsCli -- report-drift --help
```
Forventet: exit 0; viser `--env`, `--show-all`, `--verbose`, `--reference-date`,
`--profile`, `--no-fan-out`.

### Trin 3 — consistency-run mod featuretest (afgørende bevis)
```
dotnet run --project src/DataMigration.OpsCli -- report-meteringpoints --env featuretest
dotnet run --project src/DataMigration.OpsCli -- report-payers --env featuretest
dotnet run --project src/DataMigration.OpsCli -- report-drift --env featuretest
```
Forventet:
- `report-drift`-CSV'ens MP- + Payer-intrinsiske NeedsUpdate-rækker = EXAKT summen
  fra de to enkelt-scans (samme check-kilde).
- Fan-out-rækker er NYE (findes ikke i de gamle scans). Verificér 2-3 mod Xellent
  manuelt (kunde med kendt antal unikke kontrakt-EANs).
- Stderr-summary: `MP: N | Payer: M | Fan-out: F | total drift: N+M+F`.

Read-only kommando (ingen mutation) → ingen dry-run/hot-run-trin.

## Kontekst
Ét sted at se alt der er migreret forkert i Settl, i stedet for at køre flere
report-kommandoer og stitche CSV'er. Scope: KUN "forkert-i-Settl" (drift på det
migrerede), IKKE "mangler-i-Settl". De to ægte Settl-drevne scans deler allerede
kerne-skema (`EntityType, Identification, SettlId, Status, Årsager`) +
`NeedsUpdate`/`OK`-vokabular. Derudover bæres ÉT koncept fra `report-payer-diff`
over: payer fan-out forventning (1 default + N pr. unik kontrakt-EAN).

## Arkitektur (clean: én check-kilde)
- **`DriftRow`** (ny, OpsCli.Library/Runners): EntityType, Identification, SettlId,
  Status, Årsager, Detalje. Fælles række-shape.
- **`ReportMeteringPointsRunner`** + **`ReportPayersRunner`**: tilføj
  `ScanDriftAsync(...)` der returnerer strukturerede `DriftRow`-lister. Deres
  eksisterende detalje-CSV bevares uændret (projektion fra samme `reasons`/status —
  RunAsync og ScanDriftAsync deler intern scan-metode). Ingen ny constructor-dep på
  payers-runneren (fan-out lever IKKE her — kun i aggregatoren).
- **`ReportDriftRunner`** (ny): kører MP-scan + payer-scan + fan-out-check, samler
  DriftRows, filtrerer til Status≠OK (default), emitterer unified CSV (kerne +
  `Detalje`-blob). `--show-all` bevarer OK-rækker.

## Payer fan-out drift (CVR-scopet — profil-løst, korrekt)
CVR er merge-nøglens partition (`{CVR}|{Fingerprint}`) → members deler altid rep'ens
CVR. Derfor scopes kandidat-load til CVR, ikke hele dataarea'et:
1. Settl-payers → distinkte `CustomerId` → `CustomerNumber` = migrerede rep-accounts
   (genbrug payer-scanets allerede-loadede payers + customer-resolution).
2. `CustTable WHERE Accountnum ∈ rep-accounts` → deres CVR'er (`ExuCvrDisponent1`).
3. `CustTable WHERE ExuCvrDisponent1 ∈ de CVR'er` → kandidat-accounts (superset af
   alle members; kun relevante CVR'er).
4. `AccountRepresentativeResolver.ResolveAsync` + `XellentDomainMapper.LoadAsync(kandidater)`
   → `loadResult.Customers[].Payers` = forventet fan-out pr. rep (verificeret kode).
5. Gruppér Settl-payers pr. CustomerId → faktisk antal + `{account}-{n}`-suffixer;
   sammenlign med forventet. Flag NeedsUpdate: manglende payers (under-migreret),
   ekstra payers, forkerte suffixer. Ekstra reps fra same-CVR-naboer (ikke i Settl)
   ignoreres.
- Reference-date: default i dag, override via `--reference-date`. (Drift = Settl-snapshot
  vs Xellent nu; kontrakter endt siden migration er legitim drift.) `--profile`/`--accounts`
  kan valgfrit narrowe; `--no-fan-out` slår checket fra.
- Genbrug (NuGet `NewSettlement.XellentDomain.*` + lokal Migration.Library):
  `AccountRepresentativeResolver` (`DataMigration.Domain.Xellent`), `XellentDomainMapper`,
  `SettlMappingService` (`DataMigration.Migration.Services`). Mønster fra
  `ReportPayerDiffRunner.cs`.

## Filer
- `src/DataMigration.OpsCli.Library/Runners/DriftRow.cs` (ny)
- `src/DataMigration.OpsCli.Library/Runners/ReportDriftRunner.cs` (ny)
- `src/DataMigration.OpsCli.Library/Runners/ReportMeteringPointsRunner.cs` (+ScanDriftAsync)
- `src/DataMigration.OpsCli.Library/Runners/ReportPayersRunner.cs` (+ScanDriftAsync → DriftRows + payers + rep-map)
- `src/DataMigration.OpsCli/Program.cs` (registrér `report-drift`)
- `src/Tests/DataMigration.OpsCli.Tests/Runners/ReportDriftRunnerTests.cs` (ny, mock-clients)
- `CHANGELOG.md` (ny række — adfærd i Runners/)
- `CLAUDE.md` + project-knowledge skill (tilføj `report-drift` til OpsCli-kommandoliste)

## Detalje-kolonne
Semikolon-blob `key=value` (grep-venligt): MP → grid/type/EAV/joint-mismatch;
Payer → customerId/format; Fan-out → expected=N actual=M missing/extra suffixer.