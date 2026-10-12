> **✅ IMPLEMENTERET** — `fe3832f4`
>

# Plan: MP-niveau BuyerReference klar til production

## Verifikation (start her)

### Trin 0 — verificeret allerede (denne session)
- Settl featuretest billing-API'et eksponerer `BuyerReference` på MP `PaymentConfiguration`
  (bekræftet på MP `e9e91761-...` / ident `571313113161269318`). Payer-niveau hedder fortsat
  `BuyerReferenceIdentifier` — IKKE samme navn.

### Trin 1 — SettlClient build + unit tests (kør efter DTO + create-wiring)
Kør i `NewSettlement.SettlClient`:
```
dotnet build
dotnet test src/Tests/DataMigration.SettlClient.Tests
```
Forventet: 0 build errors; alle tests passed.

### Trin 2 — vent på CI-preview af SettlClient.Api/SettlClient
Efter commit+push til `develop`: verificér i ADO at pipeline har publiceret
`{version}-preview.{BuildId}` til feed `NewSettlement`. Derefter i `NewSettlement.DataMigration`:
```
dotnet nuget locals http-cache --clear
dotnet restore --force
```
Verificér: `src/DataMigration.OpsCli.Library/obj/project.assets.json` peger på den nye preview-version.

### Trin 3 — DataMigration build + unit tests
Kør i `NewSettlement.DataMigration`:
```
dotnet build
dotnet test src/Tests/DataMigration.Migration.Tests
```
Forventet: 0 errors; alle tests passed.

### Trin 4 — fix-payment-config dry-run mod featuretest
Kør (bruger kører selv — eksakt kommando):
```
dotnet run --project src/DataMigration.OpsCli -- fix-payment-config --profile <profil> --env featuretest --dry-run
```
Forventet output: `Phase B (MP): N MP(s) ...` og `WouldUpdate MP <id>: BuyerRef: '' → '<navn>' (Backfill)`
for MP'er hvor Xellent har en kontaktperson der afviger fra Payeren. `Updated=0` (dry-run).

### Trin 5 — spot-check
Vælg 2-3 MP'er fra dry-run-outputtet. Verificér i kildedata at den foreslåede BuyerRef =
`CONTACTPERSON.NAME` for kontraktens `Custcontactpersonid`, og at MP'ens kontrakt-block faktisk
afviger fra Payerens (ellers skulle feltet arves, ikke overrides).

### Trin 6 — hot-run mod featuretest
```
dotnet run --project src/DataMigration.OpsCli -- fix-payment-config --profile <profil> --env featuretest
```
Forventet: `Updated=N` matcher WouldUpdate fra trin 4. Verificér via Settl-GET at
`PaymentConfiguration.BuyerReference` nu er sat på en spot-check-MP.

> Production forbliver dry-run — ingen hot-run mod production i denne opgave.

## Kontekst
BuyerReference på Settl MP-billing var on-hold fordi Settl-API-property'en ikke var klar; den
findes nu i featuretest, så MP-niveau-feltet skal wires gennem hele kæden (create + fix-runner).

## Ændringer

### NewSettlement.XellentDomain — INGEN ændring
- `ApplyMpBillingOverrides` sammenligner allerede hele `BuyerInvoicingReferences`-record'en
  (value-equality over alle 4 felter inkl. `BuyerReferenceIdentifier`) og skriver hele
  contract-blokken på MP ved afvigelse. MP.BuyerInvoicing.BuyerReferenceIdentifier populeres
  korrekt i forvejen.

### NewSettlement.SettlClient
- `MeteringPointModels.cs`: tilføj `public string? BuyerReference { get; set; }` til
  `MeteringPointPaymentConfigurationDto` (serialiseres som `BuyerReference` — matcher live API).
- `SettlClient.cs` `EnsureBillingConfigAsync`: tilføj `buyerReference`-parameter, inkludér i
  `hasPaymentOverride`-betingelsen, sæt `BuyerReference` på `PaymentConfiguration`, og medtag i
  log-linjen.
- `SettlClient.cs` kaldested (~linje 337): send `mp.BuyerInvoicing?.BuyerReferenceIdentifier` med.
- Unit test: udvid eksisterende EnsureBillingConfig/translator-test så BuyerReference round-trip dækkes.
- Bump `VersionPrefix` i `Directory.Build.props` (tjek feed for højeste eksisterende preview først).
- Commit til `develop`, vent på CI-preview.

### NewSettlement.DataMigration
- `FixPaymentConfigRunner.cs` Phase B (`RunMpPhaseAsync`):
  - Candidate-filter (~linje 259): tilføj `|| !string.IsNullOrEmpty(mp.BuyerInvoicing?.BuyerReferenceIdentifier)`.
  - Læs `existingBuyerRef = pc?.BuyerReference`; diff mod `mpRefs?.BuyerReferenceIdentifier`.
  - Inkludér i `hasMismatch`/`hasBackfill` + diffs-listen.
  - Skriv `BuyerReference` på `UpdateBillingMeteringPointRequest.PaymentConfiguration`
    (Backfill ? ny : eksisterende).
  - Ret doc-kommentaren så "4 felter" nu matcher koden.
- `dotnet restore --force` på den nye SettlClient.Api-preview først.
- `CHANGELOG.md`: tilføj række (fix-payment-config — MP-niveau BuyerReference) i samme commit.
- Commit til `develop`.

## Rækkefølge (cross-repo NuGet)
1. SettlClient: DTO + create + test + bump → commit/push develop.
2. Vent på CI-preview (ADO).
3. DataMigration: restore → Phase B wiring → test → commit develop.
