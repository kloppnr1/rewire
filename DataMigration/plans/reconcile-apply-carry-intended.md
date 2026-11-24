> **✅ IMPLEMENTERET** — `CHANGELOG 2026-06-12`
>

# Plan: reconcile-apply bærer struktureret intended (drop per-target capture)

## Verifikation (start her)

### Runde 1 — exemptions (DataMigration)
Kør (bygger + unit tests):
```
dotnet build src/DataMigration.OpsCli.Library/DataMigration.OpsCli.Library.csproj
dotnet test  src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj
```
Forventet: 0 build-errors; alle eksisterende tests grønne + nye carry-tests grønne.

Dernæst CLI dry-run mod featuretest fra en reconcile-CSV med exemption-drift:
```
dotnet run --project src/DataMigration.OpsCli -- reconcile-apply --env featuretest \
  --from <reconcile.csv> --subtype Exemption --kind MissingInSettl --dry-run
```
Forventet output: loglinjen "rep N: scoped capture ..." er **væk** for exemption-only targets
(erstattet af "rep N: N exemption(s) from carried intended"); WouldApply-tæller = antal valgte
exemption-rækker; kørselstid sekunder, ikke minutter.

### Spot-check
2-3 exemption-rækker: ProductElementId + procent i dry-run-output matcher `Field` + `Intended`
i CSV'en. Gammel CSV (uden IntendedJson-kolonne) → falder tilbage til capture uden fejl.

### Hot-run (efter bruger-OK)
Samme kommando uden `--dry-run` mod featuretest. Verificér Applied-tæller + at exemptions står i Settl.

## Kontekst
Apply genberegner create-true intended via Xellent-load + CreateRunner pr. repræsentant — ~2 min for
113 exemptions spredt over mange reps. Scanet har allerede den strukturerede intended; bær den med i
stedet for at genberegne.

## Ændringer (runde 1 — exemptions)
- `JsonValueDiff.Diff/DiffNodes`: udvid resultat med rå intended-JSON-token (round-trip-bar) — kun additivt felt.
- `ReconcileDriftRow`: nyt `string? IntendedJson` (sidste optional param). Scan udfylder for exemption-rækker (serialiseret `BillingExemptionDto`).
- CSV: `WriteCsv` tilføjer IntendedJson som sidste kolonne; `ReconcileApplySelection` læser den (optional). `ReconcileCsvRow` + `ReconcileApplyRow` får feltet.
- `ReconcileApplier`: capture springes over når targetens rækker alle bærer intended (ingen PriceList/SettlementGroup). `ApplyExemptionsAsync` bruger parset IntendedJson; fallback til plan-capture når feltet er null (gammel cache/CSV).

## Ændringer (runde 2 — payer-billing + MP-billing)
- Compare-rækker bærer rå leaf-JSON-token i IntendedJson.
- `ApplyPayerBillingAsync` / `ApplyMpBillingAsync`: brug per-række IntendedJson som wantedVal; fallback til plan.

## Ændringer (runde 3 — SettlementGroup + PriceList, isoleret commit)
- SettlementGroup: bær intended gruppenavne (lille sæt) → drop Xellent-load.
- PriceList: persistér association-rows i cachen → drop separat Xellent-read. Verificeres mod kendt PriceList-drift før commit.

## Ændringer (dashboard-repo, efter DataMigration)
- MigrationDashboard target-bygning: map `ReconcileDriftRow.IntendedJson` → `ReconcileApplyRow.IntendedJson`.

## Kompatibilitet
IntendedJson er additivt + nullable overalt. Gamle caches/CSV'er deserialiserer til null → capture-fallback. Ingen regression.
