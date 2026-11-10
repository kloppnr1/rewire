> **✅ IMPLEMENTERET** — `559474e3`
>

# Plan: Pad-9 på Settl PayerIdentification (samme regel som CustomerNumber)

## Verifikation (start her)

### Trin 1 — Build + unit-tests
Kør:
```
dotnet build
dotnet test src/Tests/DataMigration.Migration.Tests
dotnet test src/Tests/DataMigration.OpsCli.Tests
```
Forventet: 0 errors. Eksisterende `CustomerNumberPolicyTests` grønne. Ny test `CreateRunner_pads_payer_identifier_in_loadResult_before_MigrateAsync` (production env, 5-cifret payer.Identifier → 6 cifre med '9'-præfiks; loadResult-kunde-AccountNumber tilsvarende paddet).

### Trin 2 — Xellent integration tests (golden)
Kør skill `xellent-integration-tests`. Forventet: golden-filer uændrede mod featuretest (env=null = no-op). Diff i golden = stop, spørg.

### Trin 3 — create dry-run mod featuretest (ingen ændring)
Kør:
```
dotnet run --project src/DataMigration.Migration -- create <profil> --env featuretest --dry-run
```
Forventet: output identisk med før. Featuretest paddes ikke.

### Trin 4 — create dry-run mod production (verificér payer-padding)
Kør:
```
dotnet run --project src/DataMigration.Migration -- create <profil> --env production --dry-run
```
Verificér i log: kunder med 5-cifrede Accountnums logger pad-warning **både** for CustomerNumber og for PayerIdentification (når Payer.Identifier også er kort). Ingen writes.

### Trin 5 — read-runner spot-check
Find 1-2 5-cifrede Accountnums hvor Payer.Identifier også er 5 cifre. Kør:
```
dotnet run --project src/DataMigration.OpsCli -- fix-payment-config --accounts <acct> --env production --dry-run
dotnet run --project src/DataMigration.OpsCli -- generate-seed --account <acct> --env production
```
Forventet: payer findes i Settl (lookup går på paddet 6-cifret form). Før fix: payer ikke fundet.

## Kontekst
`CustomerNumberPolicy.PadForEnv` padder allerede CustomerNumber til 6 cifre i production. Samme regel skal gælde Settl PayerIdentification — både i create-flow (så API-kaldet rammer med paddet form) og i OpsCli read-runners der laver lookup mod Settl. Hele logikken bliver i dette repo; `NewSettlement.SettlClient` røres ikke.

## Beslutninger (afklaret med bruger)
- Samme regel som `CustomerNumberPolicy.PadForEnv` — ingen ny policy-klasse, ingen ny længde.
- Create-flow: mutér `Customer.AccountNumber` og `Payer.Identifier` på Domain-objekterne i `loadResult` før `MigrateAsync`. Erstatter samtidig den nuværende halve løsning (pad på lokal `customerDtos`-liste der aldrig nåede API-kaldet).
- Read-runners: pad PayerIdentification umiddelbart før `SearchByPayerIdentificationAsync`.
- `ReportPayersRunner.cs:114` rører vi ikke — den bruger `payer.PayerIdentification` mod `SearchByCustomerNumberAsync` (Customer-lookup), og Settl returnerer allerede paddet form.

## Ændringer

### CreateRunner (`src/DataMigration.Migration.Library/Runners/CreateRunner.cs`)
- Nyt step **før** customerDtos bygges (linje ~273): foreach `c` i `loadResult.Customers`: pad `c.AccountNumber` via `CustomerNumberPolicy.PadForEnv(c.AccountNumber, options.Env, out wasPadded)` + log warning; foreach `p` i `c.Payers`: pad `p.Identifier` tilsvarende + log warning.
- Fjern den nuværende post-DTO-pad-løkke (linje 278-289) — overflødig nu hvor `customer.AccountNumber` allerede er paddet før `CustomerTranslator.Build`.
- `report.AccountGroupings` (linje 245) og `accountMpMapping` (linje 248) bygges af `extractionService` fra `loadResult` — verificér at de bruger Accountnum konsistent (alle stier rammer samme paddede værdi).

### FixPaymentConfigRunner (`src/DataMigration.OpsCli.Library/Runners/FixPaymentConfigRunner.cs`)
- Linje 148: erstat `payer.Identifier` med `CustomerNumberPolicy.PadForEnv(payer.Identifier, options.Env, out _)` i kaldet. Log warning hvis paddet (på linje med `account/payer.Identifier`-formattet log).

### GenerateSeedRunner (`src/DataMigration.OpsCli.Library/Runners/GenerateSeedRunner.cs`)
- Linje 320: pad `payerIdentification` via `CustomerNumberPolicy.PadForEnv(payerIdentification, env, out wasPadded)` før `SearchByPayerIdentificationAsync`. Log warning som CustomerNumber-paddingen lige ovenfor (linje 258-260).

### Tests
- Ny `CreateRunnerTests`-case: production env, en kunde med 5-cifret Accountnum + 5-cifret Payer.Identifier → efter `RunAsync` er `loadResult.Customers[i].AccountNumber` paddet til 6 cifre og `Payers[i].Identifier` ligeså.
- Ny `CustomerNumberPolicyTests`-case behøves ikke (samme funktion).

### CHANGELOG
- En række pr. command der får adfærdsændring: `create`, `fix-payment-config`, `generate-seed`. Nyeste øverst, dato 2026-05-29.

## Udenfor scope
- `ReportPayersRunner` (bruger CustomerNumber-lookup, ikke payer-lookup).
- SettlClient NuGet (al pad-logik bliver i DataMigration).
- Owner-padding (uændret — `CustomerOwnerResolver`/`OwnerClusterResolver` arbejder med Settl-genererede owner-numre).
