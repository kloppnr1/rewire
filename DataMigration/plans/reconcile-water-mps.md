> **✅ IMPLEMENTERET** — `fb01d9ad` (fase 1) + `1771f16a` (fase 2). `LoadWaterMpsAsync` er siden flyttet fra ReconcileRunner til den delte `SettlMeteringPointCosLoader` — planens filsti er stale, funktionen er der.

## Verifikation (start her)

### Hvad der beviser at det virker

Kør:
```
cd src/DataMigration.OpsCli
dotnet run -- reconcile --mapping profiles/vand-10-pilot.json --env featuretest --commodity water
```
Succeskriterium: reconcile CSV indeholder rækker for vandkundernes MPs (Identification = 18-cifret vandmåle-ID).

## Kontekst

Implementeret i to faser:
- **Phase 1** (`3d50d040`, `fb01d9ad`): per-representative-view (dashboard) + apply virkede ikke for vand pga. forkert API-kald og hardkodet `CommodityType=Electricity`. Fikset.
- **Phase 2** (`b2f0777` i SettlClient + dette commit i DataMigration): env-wide batch-scan understøtter nu `--commodity water/all` via `FindWaterMeteringPointsAsync` i SettlClient.

## Ændringer

### SettlClient (commit `b2f0777`, VersionPrefix `0.7.37`)
- `ISettlMeteringPointClient`: tilføjet `FindWaterMeteringPointsAsync`
- `SettlMeteringPointClient`: implementeret (cache-warm + return values)
- Test-mocks opdateret

### DataMigration (dette commit)
- `ReconcileRunner.LoadInDeliveryEnvWideAsync`: TODO erstattet med `LoadWaterMpsAsync`
- `LoadWaterMpsAsync`: ny metode med samme COS-logik som electricity
- `BulkCachingMeteringPointClient`: pass-through tilføjet
- Begge `MockSettlClients.cs`: stub tilføjet
- `--commodity water/all` i OpsCli Program.cs (fase 1)
