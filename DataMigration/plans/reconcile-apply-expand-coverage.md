> **✅ IMPLEMENTERET** — `2356f419`
>

# reconcile-apply — udvid coverage til flest mulige data i Settl

## Verifikation (start her)

### Hvad der beviser at det virker

Forudsætning: en frisk reconcile-CSV med drift i de nye klasser (kør `reconcile` mod featuretest).

Per ny klasse, dry-run mod **featuretest** filtreret til klassen, derefter hot-run:

1. **Customer↔MP links** (Kør:):
   `dotnet run --project src/DataMigration.OpsCli -- reconcile-apply --env featuretest --from <csv> --mapping <profil> --subtype CustomerLink --dry-run`
2. **AnnualVolume** (Kør:):
   `... reconcile-apply --env featuretest --from <csv> --mapping <profil> --entity MeteringPoint --field EstimatedAnnualVolume --dry-run`
   (kræver evt. nyt `--field`-selector — se Ændringer)
3. **Manglende hele entiteter** (Kør:):
   `... reconcile-apply --env featuretest --from <csv> --mapping <profil> --kind MissingInSettl --entity Customer --dry-run`
4. **Payer core** (efter SettlClient-preview er restored) (Kør:):
   `... reconcile-apply --env featuretest --from <csv> --mapping <profil> --entity Payer --subtype "" --dry-run`

### Forventet output / succeskriterium

- Dry-run: hver valgt row får `[WouldApply]` med læsbar detalje (`link → mpId`, `EstimatedAnnualVolume → N`, `would run scoped create (N accounts)`, `PayerInformation.X → val`). Ingen `[Skipped] outside phase-1 apply scope` for de valgte klasser. `Failed=0`.
- Spot-check (Verificér:): 2-3 records mod kildedata — for link: at MP'et faktisk hører til repræsentanten i Xellent; for AnnualVolume: at Xellent-volumen matcher dry-run-værdien; for missing-entity: at entiteten reelt mangler i Settl (`settl-api-query`).
- Hot-run mod featuretest: `[Applied]` på samme rows; gentaget dry-run bagefter → `[NoLongerDrifts]` (idempotent, re-validation virker).

## Kontekst

reconcile-apply skriver i dag kun PayerBilling, MP billing/payment-config, MP exemptions, settlement-group medlemskab og PriceList (fil). Alt andet scannes men droppes i `default → Skipped`. Vi wirer de drift-klasser der har et Settl write-API.

## Ændringer

Settlement groups (member-missing) **er allerede dækket** — ingen ny kode; group-create/member-remove har intet API (STOP).

### 1. Customer↔MP links — `ApplyCustomerLinkAsync` (lav risiko, additivt)
- Ny switch-case: `{ Entity: "MeteringPoint", SubType: "CustomerLink", Kind: "MissingInSettl" }`.
- Resolve customer-GUID via `SearchByCustomerNumberAsync(RepresentativeAccount)`, MP-GUID via `SearchByIdentificationAsync(EntityKey)`.
- Re-validering: GET customer's MP-liste; allerede linket → `NoLongerDrifts`.
- Skriv: `_customerClient.AddMeteringPointsAsync(customerId, {mpId})` (additivt).
- `RowNeedsCapture` = false (ingen capture nødvendig; rene Settl-lookups).

### 2. AnnualVolume på MP — `ApplyAnnualVolumeAsync` (medium risiko)
- Ny case afgrænset til `{ Entity: "MeteringPoint", SubType: "", Field: "EstimatedAnnualVolume" }`. Øvrige MP-master-felter forbliver `Skipped` (ingen anden master-data skrives).
- **GET først, kopiér eksisterende** (brugerkrav): læs nuværende MP; read-modify-write — current == intended → `NoLongerDrifts`.
- Skriv: anbefalet `UpdateAnnualConsumptionsAsync(mpId, volume)` (dedikeret endpoint → rører ingen andre felter). **Bekræft** at `EstimatedAnnualVolume` mapper hertil; ellers fald tilbage til master `UpdateAsync` med fuld kopi af eksisterende DTO + kun volumen sat.
- Kræver evt. nyt `--field`-selector i Program.cs hvis ikke allerede til stede.

### 3. Manglende hele entiteter — scoped create i write-mode (medium-høj risiko)
- Cases: `{ Entity: "Customer"|"Payer"|"MeteringPoint", SubType: "", Kind: "MissingInSettl" }`.
- Når target har sådanne rows: kør `CreateRunner.RunAsync(options, loadResult, ct)` **uden** `capturePlans` og med `options.DryRun=false` over `target.Accounts` (idempotent search-or-create — reconcilerer manglende entiteter + links).
- **Kør FØR** field-diff-writerne i samme target, så efterfølgende felt-writes rammer eksisterende entiteter.
- Dry-run: skriv intet, rapportér `WouldApply — would run scoped create (N accounts)`.
- Semantik-flag: dette gør apply til en "re-migrér denne repræsentant" for de targets — bredere end ét felt, men idempotent. Beskriv i CLI-output.

### 4. Payer core-felter — CROSS-REPO (høj risiko)
- **Sibling-repo `NewSettlement.SettlClient`** (separat session — bekræft repo'et er ledigt): ny `UpdatePayerCoreAsync(payerId, coreLeafPatches)` der spejler `UpdatePayerBillingAsync` (flad PUT, ikke wrapped).
- **ÅBEN BLOKER:** den sikre flade PUT sætter `PayerInformation=null` (nested round-trip fejler Settl-validering). Felter UNDER `PayerInformation.*` (navn/adresse) kan derfor formentlig **ikke** opdateres ad denne vej. Undersøg i SettlClient-repo'et hvilke payer-core-felter der reelt er skrivbare flat; afgræns apply til dem. Hvis kun top-level flade felter er mulige → meld tilbage før kodning.
- DataMigration-wiring (dette repo): `ApplyPayerCoreAsync` mod den nye wrapper, samme read-modify-write som billing.
- Sekvens: SettlClient bump → CI-preview → `dotnet restore --force` her → wire + test.

### Fælles
- Opdatér `default`-branch så kun reelt uunderstøttede klasser (Customer core, anomalier, MissingInXellent, PriceList live) rammer `Skipped "outside phase-1 apply scope"`.
- Opdatér `RowNeedsCapture` for nye carry-løse rows.
- Opdatér class-doc + CLI-beskrivelse (Program.cs) til at nævne nye klasser.
- **CHANGELOG.md**-række i samme commit (adfærdsændring i `Runners/`).
- Unit tests i `DataMigration.OpsCli.Tests` for ny switch-routing; integration via `RUN_INTEGRATION=1` (rører Settl-mapping/create).
