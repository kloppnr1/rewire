# TimeSeries.Xellent.IntegrationTests

Live-DB regressions-suite for `TimeSeriesExtractionService` + `TimeZoneNormalizationService`.
Kører mod **AXDB50_TEST** på `xel2012` og låser den Settl-POST-payload der ville lande på
`/v1/time-series/{guid}/points` bit-identisk via committede JSON-golden-files.

## Hvorfor et separat projekt?

- CI-agenten har ingen netværksadgang til `xel2012` → projektet er bevidst **ikke i
  `NewSettlement.TimeSeriesMigration.slnx`** og ignoreres dermed af `dotnet build` /
  `dotnet test` i Azure-pipelinen.
- Skal kun køres lokalt af udviklere på VPN.

## Forudsætninger

- Windows-maskine med Kerberos/Integrated Security-adgang til `xel2012`
- `testsettings.json` i denne folder (er gitignored). Format:
  ```json
  {
    "ConnectionStrings": {
      "TestConnection": "Server=xel2012;Database=AXDB50_TEST;Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=True"
    }
  }
  ```
  Se `testsettings.example.json`.

## Kørsel

```powershell
dotnet test src/Tests/TimeSeries.Xellent.IntegrationTests/
```

Første kørsel: ingen golden-files — tests fejler og skriver proposed baselines til
`Goldens/dst/<scenario>/new/`. Review dem, flyt til `Goldens/dst/<scenario>/`, committ,
kør igen.

## Workflow

### Normal regression-kørsel
Alle tests skal passere uden baseline-ændringer. Fejl betyder enten:
1. Live Xellent-data er ændret (sjældent på TEST-DB, men muligt)
2. Extraction- eller normalisering-koden har fået en utilsigtet adfærdsændring (ofte)

Ved fejl: `git status` i `Goldens/` — fejl-beskeden har lagt en `new/` fil du kan diff'e
mod den committede version.

### Bevidst ændring i extraction / normalisering
1. Ret koden i `TimeSeries.Migration`
2. Kør testen lokalt → fejler med baseline-mismatch
3. Review `Goldens/dst/<scenario>/new/*.json` mod den committede version — sikre dig at
   delta KUN er det du forventede
4. Promote: flyt `Goldens/dst/<scenario>/new/*.json` til `Goldens/dst/<scenario>/`
5. Commit kode + opdaterede goldens sammen

## Hvad bliver der testet?

- MP `571313115242231955` (kunde `405013` i `HOL`), `El-ekstern`
- Spring forward: `2024-03-31` (02:00 CET er ugyldig → carry-over til 03:00 CEST)
- Fall back: `2024-10-27` (02:00 forekommer to gange → første forekomst tolkes som CEST)

Hver test bygger den eksakte `SettlTimeSeriesRequestDto` som `TimeSeriesRunner.BuildTimeSeriesRequest`
ville bygge, serialiserer til wire-JSON, og sammenligner mod committed golden via
`JsonGoldenAsserter`. `TransactionId`, `RegistrationTime` og `Revision` er fastlåste i testen
så goldens er deterministiske.
