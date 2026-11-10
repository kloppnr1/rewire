> **✅ IMPLEMENTERET** — `7c7d48ec`
>

# reconcile apply: skriv udvalgte reconcile-afvigelser tilbage til Settl (dashboard-drevet)

To repos. Motor + kontrakt i **DataMigration** (NuGet); selektion/Fix-UI + endpoint i
**MigrationDashboard**. Cross-repo NuGet-hop imellem (bump→CI→restore — aldrig lokal pack).

## Verifikation (start her)

### DataMigration — motor (build-gate, Claude)
- `dotnet build` OpsCli-solution → 0 errors
- `dotnet test DataMigration.OpsCli.Tests` → ny ReconcileApplier-suite grøn (felt-path→write-mapping,
  scoped-capture-matching, dry-run-planning, NoLongerDrifts-skip)
- `RUN_INTEGRATION=1` ved commit (rører extraction + Settl-mapping)

### DataMigration — CLI smoke (verifikations-harness, bruger kører)
Tynd `reconcile-apply` CLI mod **featuretest**, scoped til én rep:
```
cd src/DataMigration.OpsCli
dotnet run -- reconcile-apply --env featuretest --account <repr> --dry-run
```
Forventet: scoped capture (sekunder), `=== apply (dry-run) ===` med `WouldUpdate/WouldAdd`-linjer +
summary pr. writer. Ingen writes. → spot-check 2-3 mod frisk `reconcile`-CSV → hot-run uden
`--dry-run` → kør `reconcile --env featuretest` igen: de fixede rækker væk.

### MigrationDashboard — Fix-flow (bruger, efter CI-restore)
Reconcile-view → filtrér → markér rækker (eller helе repræsentanter) → **Fix valgte (dry-run)**:
forventet preview-panel med samme WouldUpdate-tal som CLI'en for samme rep. Derefter **Fix valgte**
(hot, featuretest) → rækkerne forsvinder ved næste scan.

**Production:** altid tvunget dry-run (CLI + endpoint). Hot kun ved eksplicit bruger-eskalering.
**Hot kræver eksplicit selektor** (mindst én række/rep); tom selektor = menu/preview, aldrig apply-alt.

## Kontekst
Reconcile finder afvigelser read-only; brugeren skal kunne filtrere resultatet i dashboardet og
trykke Fix på udvalgte rækker/repræsentanter for at skrive create-true intended tilbage til Settl.

## Beslutninger (bruger)
Genbrug capture (typed intended) · overskriv altid med intended · kun drift-ramte felter · scope =
FieldDiff/Billing + MissingInSettl/Exemption + PriceList (CSV-artefakt til re-import) · selektion i
dashboard (filter + Fix), ned til enkeltrækker OG repræsentant-niveau · begge repos nu.

## Performance-nøgle
Apply re-kører IKKE env-wide scan. Pr. valgt repræsentant: scoped capture
`LoadAsync(MemberAccounts, refDate=CosFrom−1)` → CreateRunner-capture → typed intended. Springer alle
tre env-wide pagineringer over → sekunder. Re-validering = writerens egen read-modify-write GET;
rækker der ikke længere driver skrives ikke (rapporteres NoLongerDrifts).

## Ændringer — DataMigration (motor, ships som NuGet)
- **RowId på `ReconcileDriftRow`** (+ `ReconcileRowDto` i dashboard-DTO): deterministisk hash af
  `RepresentativeAccount|Entity|SubType|EntityKey|Field|Kind`. Additivt felt → flyder til dashboard.
- **Kontrakt-DTO'er** (OpsCli.Library, offentlige — dashboard kalder dem):
  `ReconcileApplyRequest{ Env, DryRun, Targets[] }`,
  `ReconcileApplyTarget{ RepresentativeAccount, Accounts[], Cos, RowIds[]? (null=alle actionable under rep) }`,
  `ReconcileApplyResult{ per-row: Applied|WouldApply|NoLongerDrifts|NotFound|Failed|Skipped, PriceListCsv? }`.
- **Ny `ReconcileApplier`** (egen fil): konstrueres som `ReconcileRunner` (mapping + exemption + klienter).
  `ApplyAsync(request, ct)`: grupperér targets pr. rep → scoped capture pr. rep (genbrug step-5-logikken,
  udtræk til delt helper) → for hver valgt actionable række: dispatch til writer:
  - *Payer billing* (FieldDiff): GET raw JSON → overskriv kun drift-ramte `PayerBilling.*`-noder fra
    serialiseret typed intended → strip Uri/Links → PUT `{TransactionId,Payer}` via `UpdateRawAsync`.
  - *MP billing* (FieldDiff): `GetBillingByIdAsync` → byg `UpdateBillingMeteringPointRequest`, carry
    eksisterende `BillingConfiguration`/`PaymentConfiguration`, overskriv kun drift-ramte fra intended →
    `UpdateBillingAsync`. Mirror FixPaymentConfigs `Start`/`LifeTime`-envelope (verificeres i dry-run at
    den ikke laver utilsigtet ny historik-periode).
  - *MP exemption* (MissingInSettl): `AddMeteringPointExemptionsRequest` fra intended
    `PlannedMeteringPoint.Exemptions` → `AddExemptionsAsync` (additivt).
  - *PriceList* (alle kinds): saml rep'ens intended `CustomerPriceListAssociationRow` →
    `CustomerPriceListAssociationExcelWriter` → returnér som artefakt (ikke live Settl-write).
  - Per-writer outcome-tællere; dry-run/production → kun WouldUpdate/WouldAdd.
- **ReconcileRunner-refaktor**: udtræk per-rep scoped-capture (step 5-kroppen) til intern delt helper
  som både env-wide scan og apply bruger. `ScanAsync` (dashboard-NuGet-kald) uændret signatur.
- **Tynd CLI `reconcile-apply`** (Program.cs): `--env --account/--rep --rows --subtype --dry-run`
  + production-dry-run-tvang + menu ved tom selektor. Verifikations-harness.
- **CHANGELOG.md**: ny række (OpsCli mutation-adfærd) i samme commit.
- **Bump `VersionPrefix`** (Directory.Build.props) → commit develop → CI preview.

## Ændringer — MigrationDashboard (efter CI-restore af floating NuGet)
- **`ReconcileApplyService`** (Dashboard.Api): bygger `ReconcileApplyRequest` fra de valgte rækker
  (rep + MemberAccounts + CosFrom + RowIds, alt allerede i `ReconcileRowDto`), kalder
  `ReconcileApplier.ApplyAsync`. Genbrug `CreateReconcileClientsForEnvironment`.
- **Endpoint** (Program.cs): `POST /api/reconcile/apply` (env, dryRun, valgte RowIds/reps) → resultat;
  PriceList-CSV som download-respons. Production → tvunget dry-run server-side.
- **ReconcileView.tsx**: række-checkbox + per-rep "vælg alle i rep" på newRep-headeren + "Fix valgte
  (dry-run)" / "Fix valgte" knapper der virker på den aktuelle markering (sub-mængde af `filteredRows`).
  Preview-panel viser apply-resultatet. Hot deaktiveret ved tom markering.
- **api.ts**: `applyReconcile(...)`-klient + typer.

## Cross-repo sekvens (begge nu)
1. DataMigration: motor + RowId + kontrakt + CLI → build/test → commit develop → bump → push.
2. Vent på CI (publisher `{ver}-preview.{BuildId}`).
3. MigrationDashboard: `dotnet restore --force` (floating trækker preview) → endpoint + service + UI →
   build/test → commit develop.

## Out of scope phase 1
Customer-core FieldDiff, MissingInSettl Customer/Payer/MP/Link, SettlementGroup, MP-master FieldDiff,
Anomaly. Writer-dispatch pr. SubType gør dem additive senere.
