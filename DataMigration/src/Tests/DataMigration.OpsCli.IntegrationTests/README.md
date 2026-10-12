# DataMigration.OpsCli.IntegrationTests

Live-run verifikation af OpsCli's fix-* runners mod **AXDB50_TEST** (Xellent) +
**Settl featuretest**.

## Hvorfor et separat projekt?

- CI-agenten har ingen netværksadgang til `xel2012` eller Settl featuretest →
  projektet er bevidst **ikke i `DataMigration.sln`** og ignoreres dermed af
  pipeline-build/test. Samme konvention som `DataMigration.Xellent.IntegrationTests`.
- Køres lokalt af udviklere eller fra Claude (Plan C `post-migration-fix` skill).

## Forudsætninger

- Windows-maskine med Kerberos/Integrated Security-adgang til `xel2012`
- Corporate VPN oppe
- `testsettings.json` i denne folder (gitignored) — brug `testsettings.example.json` som skabelon
- Settl featuretest-credentials via `dotnet user-secrets set ...` på
  `DataMigration.OpsCli.IntegrationTests`

## Kørsel

```bash
# Hele suiten (alle tests har [Trait("Integration", "true")])
dotnet test src/Tests/DataMigration.OpsCli.IntegrationTests/

# Kun en enkelt runner
dotnet test src/Tests/DataMigration.OpsCli.IntegrationTests/ \
  --filter "FullyQualifiedName~FixBilling"
```

## CI

Projektet er **ikke** i `DataMigration.sln` og bygger derfor ikke i Azure-pipelinen.
Hvis du vil tilføje et manuelt pipeline-stage (fx pre-deploy gate mod staging), brug:

```yaml
- task: DotNetCoreCLI@2
  condition: and(succeeded(), eq(variables['runIntegrationTests'], 'true'))
  displayName: 'OpsCli integration tests (manual)'
  inputs:
    command: 'test'
    projects: 'src/Tests/DataMigration.OpsCli.IntegrationTests/DataMigration.OpsCli.IntegrationTests.csproj'
    arguments: '--configuration Release --filter "Integration=true"'
```

Agenten skal have VPN + Settl-credentials via variable group `DataMigration-Staging`.

## Scope

Tests validerer tre eksisterende fix-* runners (`FixBillingRunner`, `FixGridRunner`,
`FixJointRunner`) mod en curated set of konti i `Fixtures/FixTestAccounts.cs`.

### Cleanup-strategi

Settl featuretest understøtter **ikke** delete af Customer / MeteringPoint /
BillingConfiguration (B-prereq-1). Samtidigt accepterer featuretest kun 6-cifrede
numeriske `CustomerNumber`-værdier — vi kan derfor ikke bruge et `VRFY-`-prefix.

Løsningen:
- **Genbrug det reelle Xellent `Accountnum`** som CustomerNumber i featuretest
  (samme som produktions-migreringen gør).
- Test-klasser provisionerer den samme lille liste konti igen og igen; Settl akkumulerer
  dem over tid uden skade (266 DK-kunder total i featuretest pr. 2026-04-15 — rigeligt
  plads).
- **Payers + contract-associations** kan slettes og ryddes op per test-klasse.
- **Sweep-utility** (`OpsCli sweep-test-customers`) targeterer `FixTestAccounts.All`
  direkte: arkiverer (ikke sletter) Customer/MP, sletter Payers/associations. Kører
  nightly, kan også køres ad-hoc før en test-run.

### 6 scenarier per runner

1. **Happy-path** — fix kører, Settl-state matcher forventet
2. **Idempotens** — anden kørsel muterer intet
3. **InactiveContract past cutoff** — fix springer over + tæller `MpsNotInXellent`
4. **XellentCleared** — default skip, `--allow-clear` påfører clear
5. **AlreadyCorrect** — read-back uændret
6. **NonRepresentative** — fix'en lander på rep's MPs, ikke non-rep's

Se `../../.claude-plans/` (Plan B) for design-kontekst.

## Status

- [x] csproj + test-base stillet op
- [x] FixTestAccounts validated mod AXDB50_TEST (2026-04-15)
- [x] Settl featuretest client wiring (B-prereq-1/2/3)
- [x] FixBillingRunnerIntegrationTests (4 tests)
- [x] FixGridRunnerIntegrationTests (4 tests, inkl. mutate→fix for MarketBalanceArea)
- [x] FixJointRunnerIntegrationTests (3 tests)
- [ ] `sweep-test-customers` utility — Payers + contract-associations teardown

**11/11 green pr. 2026-04-15, 9s total.**
