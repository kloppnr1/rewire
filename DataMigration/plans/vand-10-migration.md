## Verifikation (start her)

### Hvad der beviser at det virker
- Dry-run featuretest: ~8 customers found/would-create, 0 fejl
- Hot-run featuretest: customers + payer + MP-links oprettet i Settl featuretest
- Dry-run production: ~8 customers found/would-create, 0 fejl
- Hot-run production: customers + payer + MP-links oprettet i Settl production

### Featuretest — trin 1: dry-run
```
cd src/DataMigration.Migration
dotnet run -- create vand-10-pilot --dry-run --env featuretest
```
Forventet output:
- 8 customers would-create (371627 + 467824 = 0 MPs, ingen Vand-kontrakter → no-op)
- 313809: 1 customer, kun Vand-alm (slutter 2026-03-31, ingen VandSettl-efterfølger)
- 0 errors

### Featuretest — trin 2: spot-check
Verificér i dry-run output:
- 104804 (Bjarke Langkilde) — Vand-alm + VandSettl → begge linkes til Vand_Almindelig
- 313809 (Fiktiv Personf) — kun Vand-alm, ingen fremtidig kontrakt-del
- 405013 (Fiktiv Persong) — Vand-alm + VandSettl

### Featuretest — trin 3: hot-run
```
dotnet run -- create vand-10-pilot --env featuretest
```
Forventet: ~8 customers created i featuretest. Verificér en konto i Settl featuretest UI.

### Production — trin 4: dry-run
```
dotnet run -- create vand-10-pilot --dry-run --env production
```
Forventet: samme counts som featuretest dry-run.

### Production — trin 5: hot-run
```
dotnet run -- create vand-10-pilot --env production
```

## Kontekst
10 vandkunder migreres fra Xellent til Settl. Vandmålere er pre-populated eksternt i Settl — `create` søger men opretter dem ikke. 371627 og 467824 er El-ekstern konti uden Vand-kontrakter og vil producere 0 MPs (stille no-op).

## Ændringer
- `profiles/vand-10-pilot.json` — ny profil: 10 konti, ReferenceDate 2026-04-01, DeliveryCategory Vand, LegacyTg4Merge, VandSettl → Vand_Almindelig (Vand-alm er fortid ved denne dato)
