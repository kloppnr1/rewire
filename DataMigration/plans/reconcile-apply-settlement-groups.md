> **✅ IMPLEMENTERET** — `CHANGELOG 2026-06-12`
>

# reconcile-apply: SettlementGroup membership (member-missing)

## Verifikation (start her)

### Hvad der beviser at det virker
Forudsætter en frisk reconcile-CSV med findingen (rep 258599, gruppe "MånedOpg+MånedAconto", SettlementGroup/MissingInSettl).

**Trin 0 — build + unit:**
```
dotnet build src/DataMigration.OpsCli/DataMigration.OpsCli.csproj
dotnet test src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj
```
Forventet: 0 errors; alle tests passed (inkl. nye ReconcileApplier-cases).

**Trin 1 — dry-run mod featuretest (kun denne ene rep + subtype):**
```
dotnet run --project src/DataMigration.OpsCli -- reconcile-apply --env featuretest `
  --from <reconcile.csv> --rep 258599 --subtype SettlementGroup --dry-run
```
Forventet: linje `[WouldApply] 258599 Customer/SettlementGroup 258599 MånedOpg+MånedAconto — add to group <guid>`; Summary `WouldApply=1 Failed=0`.

**Trin 2 — spot-check (Settl, read-only):**
- Verificér via settl-api-query at gruppen "MånedOpg+MånedAconto" findes i featuretest, og at kunde 258599 IKKE er medlem endnu.

**Trin 3 — hot-run mod featuretest:**
```
dotnet run --project src/DataMigration.OpsCli -- reconcile-apply --env featuretest `
  --from <reconcile.csv> --rep 258599 --subtype SettlementGroup
```
Forventet: `[Applied] ...`; Summary `Applied=1 Failed=0`. Re-kør → `[NoLongerDrifts]` (idempotent).

## Kontekst
reconcile-apply kan i dag ikke rette SettlementGroup-drift; findingen "kunde mangler som medlem af eksisterende gruppe" er den eneste auto-fixbare SettlementGroup-variant (create ADD'er kun).

## Ændringer
- **ReconcileApplier.cs**: ny dispatch-case `{ Entity: "Customer", SubType: "SettlementGroup", Kind: "MissingInSettl" }` → samle pr. rep → ny `ApplySettlementGroupsAsync`.
- **ApplySettlementGroupsAsync** (mirror af FixSettlementGroupsRunner-mutationen + ApplyPriceListAsync's intended-re-resolve):
  1. Re-resolve create-true intended grupper for rep'en: `SettlementGroupExpectationResolver.ResolveByCustomer(loadResult.Customers, mapping.Mappings.SettlementGroup)` → hvis row'ens gruppenavn (Field) ikke er i settet → `Skipped` ("no longer intended by create").
  2. Resolve kunde-GUID: `_customerClient.SearchByCustomerNumberAsync(PadForEnv(EntityKey, env))` → null → `NotFound`.
  3. Resolve gruppe: `_settlementGroupClient.FindByNameAsync("DK", Field)` → null/inaktiv → `Skipped` ("group missing in Settl — not auto-creatable"). Dette afviser implicit "group-missing"-varianten.
  4. `ListCustomerIdsAsync(groupId)` → allerede medlem → `NoLongerDrifts`.
  5. dry-run → `WouldApply`; ellers `AddCustomersAsync(groupId, { CustomerId, IncludeCustomerSubsidiaries=false })` → `Applied`/`Failed`.
- Signatur-justering: `ApplyTargetAsync` skal give writeren adgang til `loadResult`, `mapping`, `env` (loadResult haves allerede lokalt; videregives som de øvrige writers).
- **Out of scope (eksplicit afvist)**: group-missing-varianten (trin 3 → Skipped), Anomaly (extra membership) → falder fortsat i `default`/Skipped.
- **Tests**: ingen isoleret unit-test — writeren afhænger (som de øvrige ReconcileApplier-writers) af den scoped CreateRunner-capture, så der er ingen seam uden Xellent-DB. Verificeres via CLI dry-run/hot-run (bruger-kørt) + OpsCli live-run integrationstests. Eksisterende reconcile unit-tests (diff/selection/mapping) skal fortsat passe.
- **Doc**: opdatér class-summary i ReconcileApplier.cs + plans-note; CHANGELOG-række (adfærdsændring i OpsCli-runner).
