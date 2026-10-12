> **✅ IMPLEMENTERET** — `7e7b6079`
>

## Verifikation (start her)

### Trin 1 — build
Kør:
```powershell
dotnet build src/DataMigration.OpsCli/DataMigration.OpsCli.csproj
```
Forventet: `Build succeeded. 0 Error(s)`.

### Trin 2 — kør mod featuretest med `match`-profilen
Kør:
```powershell
cd src/DataMigration.OpsCli
dotnet run -- report-payer-diff match --env featuretest
```
Forventet:
- Stderr siger `Customers: N, Payers before: N, Payers after: M (M > N)`
- En fil `report-payer-diff-YYYYMMDD-HHmmss.csv` skrives
- For Gribskov (352639) + Køreteknikken-merge-gruppen (427549) findes `ADDED`-rækker (multi-EAN fan-out kendt fra commit `b9496a81`)

### Trin 3 — spot check
Kør:
```powershell
Get-Content report-payer-diff-*.csv | Select-Object -First 5
Get-Content report-payer-diff-*.csv | Select-String "ADDED" | Measure-Object
```
Forventet: ≥1 ADDED-række i `match`-profilen.

## Kontekst
Commit `b9496a81` (2026-05-19) ændrede payer-logik: tidligere 1 payer pr. kunde, nu 1 default + N extras pr. unikt kontrakt-EAN. Behov for CSV som viser hvor diffen rammer pr. profil.

## Ændringer
- Ny `ReportPayerDiffRunner` i `src/DataMigration.OpsCli.Library/Runners/`
  - Loader Domain via `XellentDomainMapper.LoadAsync(profile.Accounts)`
  - Per customer: emit 1 række pr. `customer.Payers`
  - Status = `SAME` hvis `IsPrimary=true`, ellers `ADDED`
- CSV-kolonner (semikolon-separeret, UTF-8 + BOM):
  `AccountNumber;CustomerName;PayerIdentifier;IsPrimary;ElectronicNumber;ContractNumbers;Status`
- Ny `report-payer-diff <profile>`-kommando i `src/DataMigration.OpsCli/Program.cs`
  - Args: profile (positional), `--env`, `--data-area`, `--reference-date`, `--legacy-tg4-merge`
  - Output: `report-payer-diff-{timestamp}.csv` i cwd
- Ingen Settl-kald (rent Xellent + Domain) → ingen mutation, kører read-only mod AXDB50/AXDB50_TEST