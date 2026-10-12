> **✅ IMPLEMENTERET** — `43ee9d35` (CHANGELOG 2026-07-29). Statussen `Ikke migreret` blev bygget og siden fjernet igen af `mobilepay-cos1-population.md`, så planens sum-succeskriterium kan ikke længere efterprøves.

# payment-match (MobilePay) — Settl-drevet, som reconcile

## Verifikation (start her)

### Hvad der beviser at det virker

Trin 1 — dry-run mod featuretest (læser kun; ingen mutationer i denne kommando):
```
cd C:\Users\sen.makj\source\repos\NewSettlement.DataMigration\src\DataMigration.OpsCli
dotnet run -- payment-match "<sti-til-settl-csv>" --all-accounts --type mb --env featuretest
```

Trin 2 — spot-check, 3 konti der kan verificeres i kildedata:
- Én migreret konto med aktiv MobilePay og betalingsaftale i CSV → forventes `Matched`
- Én migreret konto med aktiv MobilePay men uden betalingsaftale i CSV → forventes ny status
  `MANGLER betalingsaftale i Settl`
- Én konto med aktiv MobilePay der ikke har noget MP i Settl → forventes `Ikke migreret`

Trin 3 — mod produktion:
```
dotnet run -- payment-match "<sti-til-settl-csv>" --all-accounts --type mb --env production
```

### Forventet output / succeskriterium

- Antal `Matched` ≥ 449 (dagens tal efter kontrakt-filter-fixet, målt 2026-07-29 mod produktion).
  Færre = regression; markant flere = COS-pinning finder konti den gamle retning missede.
- Summen `Matched + MANGLER betalingsaftale i Settl + Ikke migreret` = antal aktive
  MobilePay-aftaler i `hol` (4.958 pr. 2026-07-29).
- Ingen række har status `Ikke migreret` samtidig med et E-nummer.
- Bemærkningen "Målepunkt hentet fra anden kontrakt end aftalens" findes ikke længere
  (gætteriet den dækkede over er væk).

## Kontekst

payment-match går i dag Xellent → vælg ét målepunkt → slå op i CSV. Retningen tvinger den til
at opfinde to regler den ikke har grundlag for (hvilken kontrakt er aktuel, hvilket målepunkt
repræsenterer aftalen), og den ene af dem var forkert. reconcile løser samme problem modsat:
Settl-MP → COS → `MeteringPointAccountResolver` pinner til kontrakten der slutter COS-1.

## Ændringer

- Udtræk reconciles `LoadInDeliveryEnvWideAsync` (privat i `ReconcileRunner`) til en delt
  service, så payment-match og reconcile bruger samme Settl-MP+COS-kilde
- Ny `MobilePaySettlMatchService` i `DataMigration.OpsCli.Library`:
  Settl-MPs → COS → `MeteringPointAccountResolver` → konto → kontoens uannullerede
  MobilePay-aftaler
- Tre statusser pr. aftale i stedet for match/ikke-match:
  - `Matched` — konto migreret, betalingsaftale findes i CSV (E-nummer)
  - `MANGLER betalingsaftale i Settl` — konto migreret, men ingen betalingsaftale i CSV.
    Ny detektion; det er den kunde der taber sin MobilePay ved overdragelsen
  - `Ikke migreret` — ingen Settl-MP peger på kontoen
- `XellentMobilePayContractReader` reduceres til "aftaler pr. konto" — kontrakt- og
  målepunkt-udvælgelsen forsvinder sammen med retningen
- Bemærkningerne "Målepunkt hentet fra anden kontrakt" og "Kildekontrakt er udløbet" udgår;
  begge var artefakter af den gamle retning. Herkomst (kildekontrakt, hoved/under,
  leveringskategori, antal aftaler) bevares uændret
- Dashboardet følger med: samme service, statusfeltet erstatter `isJoint` i tabellen

## Rettelse til commit `43ee9d35`

Den commit-besked siger at `LoadInDeliveryEnvWideAsync` blev udtrukket "så de to
kommandoer deler Settl-MP+COS-kilden". Det er sandt og samtidig vildledende: der er
**fire** env-wide pagineringer over market-v3, og udtrækket samlede kun de to.

| Sted | Bruges af |
|------|-----------|
| `Services/SettlMeteringPointScanner.cs` | report-meteringpoints, report-meter-info |
| `Runners/ReportDeliveryStartRunner.cs` | report-delivery-start |
| `Runners/ExportSettlementConfigRunner.cs` | export-settlement-config |
| `Services/Reconcile/SettlMeteringPointCosLoader.cs` | reconcile, payment-match |

De kan ikke bare slås sammen. `SettlMeteringPointCosLoader` kræver en resolverbar COS
og springer målepunkter uden leveringsperiode over; `SettlMeteringPointScanner` beholder
dem. Én fælles implementering ændrer derfor populationen for report-meteringpoints og
report-meter-info — en adfærdsændring i tre kommandoer, som skal måles for sig.

Konsolideringen er altså **ikke** løst og bør være sin egen opgave.

## Åbne punkter

- COS-hentningen er en env-wide Settl-paginering. Tidsforbruget mod produktion skal måles
  før vi ved om kommandoen er brugbar interaktivt fra dashboardet, eller kun som CLI-kørsel
- Fællesfaktura-fallbacken (match på joint invoice number) bevares eller udgår — afhænger af
  om Settl-MP-opslaget dækker de tilfælde den blev indført for
