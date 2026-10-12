> **✅ IMPLEMENTERET** — `272c8179`
>

## Verifikation (start her)

### Trin 1 — build
Kør:
```powershell
dotnet build src/DataMigration.OpsCli/DataMigration.OpsCli.csproj
```
Forventet: `Build succeeded. 0 Error(s)`.

### Trin 2 — kør mod featuretest med en fan-out-profil
Kør:
```powershell
cd src/DataMigration.OpsCli
dotnet run -- report-payer-diff match --env featuretest
```
Forventet:
- Ny kolonne `MeteringPointIdsByPayer` i CSV-headeren
- For rep-rækker med payer-fan-out: feltet udfyldt som `427549=[571...,571...] | 427549-1=[571...]`
- For member-/UNCHANGED-rækker uden payers: feltet tomt

### Trin 3 — spot check
Kør:
```powershell
Get-Content report-payer-diff-*.csv | Select-String "\-1=\[" | Select-Object -First 3
```
Verificér mod kildedata: MP-id'erne under `{acc}-1` hører til de kontrakter hvis EAN adskiller sig fra default-payeren (jf. `Payer.ContractNumbers`).

## Kontekst
`report-payer-diff` viser payer fan-out men ikke hvilke metering points der følger med hver payer — gør `RELINK_MPS_BY_CONTRACT_EAN`-action uhåndterbar. MP-id-per-account findes allerede via `SettlMappingService.ExtractAccountMeteringPointMappingAsync`; her korreleres den per payer på `ContractNum`.

## Ændringer
- `ReportPayerDiffRunner`:
  - Konstruér `SettlMappingService(loadResult, warnings, refDate, "El-ekstern")` og kald `ExtractAccountMeteringPointMappingAsync(accounts)`
  - Byg `rep → (ContractNum → [MeteringPointId])`
  - For hver rep-payer: join `Payer.ContractNumbers` → MP-ids; formatér `{payerId}=[{mp,mp}]` joinet med ` | `
  - Ny CSV-kolonne `MeteringPointIdsByPayer` efter `PayerIdentifiers`
- Ingen Settl-kald, ren Xellent — read-only mod AXDB50/AXDB50_TEST