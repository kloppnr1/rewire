> **✅ IMPLEMENTERET** — `ukendt (CHANGELOG 2026-05-27)`
>

# Plan: Env-afhængig padding af Settl CustomerNumber (pad med '9')

## Verifikation (start her)

### Trin 1 — Build (begge repos)
Kør (SettlClient først, derefter DataMigration efter NuGet-restore):
```
# i NewSettlement.SettlClient
dotnet build
dotnet test src/Tests/DataMigration.SettlClient.Tests
# i NewSettlement.DataMigration (efter consumer-restore mod ny preview)
dotnet build
dotnet test src/DataMigration.Migration.Library.Tests   # hvis findes
```
Forventet: 0 errors. Nye unit-tests for formatter passerer (se nedenfor).

### Trin 2 — Formatter unit-test (succeskriterium, eksakt)
Ny test verificerer:
- env `production`, `"57923"` → `"957923"`; `"100029"` → `"100029"` (uændret, allerede 6)
- env `production-vhel`, `"5792"` → `"95792"`; `"57923"` → `"57923"` (uændret, allerede 5)
- env `featuretest` / `production-hev` → input returneres uændret (ingen padding)
- env `production`, `"1000291"` (7 cifre, > 6) → **kaster** (hard fail)
- env `production-vhel`, `"100029"` (6 cifre, > 5) → **kaster** (hard fail)
- ikke-numerisk input i normaliseret env → **kaster**

### Trin 3 — create dry-run mod featuretest (ingen adfærdsændring her)
Kør:
```
# i NewSettlement.DataMigration
dotnet run --project src/DataMigration.Migration -- create <profil> --env featuretest --dry-run
```
Forventet: identisk output som før (featuretest paddes ikke).

### Trin 4 — create dry-run mod production (verificér padding)
Kør:
```
dotnet run --project src/DataMigration.Migration -- create <profil> --env production --dry-run
```
Verificér i log: en kunde med 5-cifret Xellent-Accountnum vises/oprettes nu med 6-cifret `957...` CustomerNumber **og en warning logges** ("padded til ..."); 6-cifrede er uændrede og giver ingen warning. Ingen writes (dry-run).

### Trin 5 — spot-check (2-3 records)
Find 2-3 kendte 5-cifrede production-Accountnums i Xellent, og bekræft at dry-run-outputtet (og en efterfølgende `report`/`compare --env production`) slår dem op i Settl med det paddede `9`-præfiks.

## Kontekst
For `production` har Xellent både 5- og 6-cifrede kundenumre; Settl `production` kræver præcis 6 cifre og `production-vhel` præcis 5. Kortere numre venstre-paddes med cifferet `9` til mål-længden. `featuretest` og `production-hev` røres ikke. Numre længere end mål-længden (eller ikke-numeriske) → hard fail.

## Beslutninger (afklaret med bruger)
- Pad-tegn: `'9'` (ikke `'0'`).
- Mål-længder: `production`=6, `production-vhel`=5. `featuretest`/`production-hev`/øvrige = ingen normalisering.
- For lange / ikke-numeriske numre i normaliseret env → **hard fail** (kast, stop kørsel).
- Når et nummer faktisk paddes (length < mål) → **warning** logges (uændrede numre giver ingen warning).
- Create-padding lægges i producer-pakken `NewSettlement.SettlClient` (ren adskillelse rå AccountNumber vs Settl CustomerNumber). OpsCli read-runners paddes in-repo.
- Gælder alt der rører Settl: create + read-runners der slår op på Accountnum.

## Ændringer

### Fase A — `NewSettlement.SettlClient` (producer-pakke, separat repo)
*(Kræver at SettlClient-repoet er ledigt; bekræftes før start.)*
- Ny intern helper `CustomerNumberFormatter.Pad(string accountNumber, int? targetLength, out bool wasPadded)`: når `targetLength` er sat → kræv all-digits og `length <= targetLength` (ellers kast), returnér `PadLeft(targetLength, '9')` og sæt `wasPadded=true` hvis input var kortere; når `null` → returnér uændret, `wasPadded=false`.
- Når `wasPadded` → log warning ("CustomerNumber {raw} padded til {paddet} for env"). I pakken: via `_logLine` + tilføj til `warnings`-listen der returneres fra `MigrateAsync`.
- `SettlMigrationContext`: nyt felt `int? CustomerNumberLength = null`.
- `CustomerTranslator.Build`: ny optional param `int? customerNumberLength = null`; sætter `CustomerNumber = CustomerNumberFormatter.Pad(customer.AccountNumber, customerNumberLength)`. Default null = uændret (in-repo report/owner-kald forbliver rå).
- `SettlClient.cs`:
  - linje ~95: `CustomerTranslator.Build(customer, context.CustomerNumberLength)`.
  - linje ~105-106: søg på `customerDto.CustomerNumber` (paddet) i stedet for `customer.AccountNumber`.
  - Owner-lookup (linje 98) forbliver på rå `customer.AccountNumber` (matcher rå nøgler i `OwnerByCustomerNumber`).
  - Payer-fallback (linje 154) røres IKKE (kun kundenummer i scope).
- Bump `Directory.Build.props` `<VersionPrefix>` 0.7.0 → 0.7.1 (tjek feed: sæt højere end eksisterende previews). Commit develop → vent på CI-preview.

### Fase B — `NewSettlement.DataMigration` (consumer)
- Ny `CustomerNumberPolicy` i `DataMigration.Migration.Library` (nåbar fra både Migration + OpsCli.Library):
  - `int? LengthForEnv(string env)` → production=6, production-vhel=5, ellers null.
  - `string Pad(string accountNumber, int? targetLength, out bool wasPadded)` (samme regel som pakkens; bevidst lille duplikat — dokumentér at de to skal holdes ens).
- **Warning ved pad** (read-runners + generate-state): når `wasPadded`, log warning via runnerens eksisterende `WarningCollector`/log-linje.
- **Create**: `CreateOptions` + nyt felt (env eller `CustomerNumberLength`); `Program.cs` (Migration) udleder fra `settlEnv` og sætter det; `CreateRunner` sætter `context.CustomerNumberLength` (linje ~342). In-repo `customerDtos` (linje 268) bygges uden length → forbliver rå (bevarer grouping/owner-joins).
- **OpsCli read-runners** — pad account/CustomerNumber lige før `SearchByCustomerNumberAsync`, env-styret:
  - `CompareRunner.cs:80` (`customer.CustomerNumber`)
  - `ReportMeterInfoRunner.cs:69` (`accountNum`)
  - `GenerateSeedRunner.cs:254` (`account`)
  - `FixSettlementGroupsRunner.cs:237` (`account`)
  - Thread `settlEnv`/length ind i de runner-konstruktører hvor det mangler.
- **generate-state**: `GenerateStateRunner` skriver `SettlAccountNumber` = repræsentant-account → pad så state matcher Settl.
- **Røres IKKE**: `ReportPayersRunner` (payer-id, ikke account), `GenerateStateFromSettlRunner` (læser Settl direkte), `ReportRunner` (ingen Settl-lookup).
- Restore mod ny SettlClient-preview, build, test, commit. CHANGELOG-række for `create` (+ evt. `compare`/`generate-state`) adfærdsændring.

## Udenfor scope
- **Owner-numre**: `CustomerOwnerResolver`/`OwnerClusterResolver`/`OwnerNumberAllocator` (syntetiske owner-kunder) røres IKKE. Kun rigtige Xellent-kundenumre normaliseres. Afklares separat hvis det viser sig nødvendigt.
