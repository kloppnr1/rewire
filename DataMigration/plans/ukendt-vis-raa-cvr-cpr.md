> **✅ IMPLEMENTERET** — `0d6cdf6b` (gruppering + rå værdier i create-rapporten), `a3060cc` i MigrationDashboard (sektionen), og kundemodalen i `report.html` som sidste flade. Beslutningen om at læse alternativ-kundenummeret som CVR er bevidst ikke truffet her.

# Ukendt: grupperet efter fejlkilde, med de rå værdier

## Kontekst

"Ukendt" siger i dag kun at kunden hverken har CVR eller CPR — ikke hvorfor. Og der ER
noget at se på: mapperen kasserer et CVR der ikke er 8 cifre og et CPR der ikke er 10
tegn, og `EXU_ALTERNATIVECUSTOMERNUMBER` læses kun når der står `PNR=` foran. En liste
med 1.798 navne er ikke en opgave nogen kan gå i gang med; fire grupper er.

Målt på produktion, El-Ekstern, konti med en kontrakt der stadig løber — 1.798 ukendte
fordelt på fejlkilde:

| Fejlkilde | Konti |
|---|---:|
| Ingen identifikation nogen steder | 1.386 |
| CVR står i alternativ-kundenummeret (kode 2) | 381 |
| Alternativ-kundenummer med en kode vi ikke tolker | 31 |

Grupperne nøgles på koden, ikke på hvor mange cifre der tilfældigvis står. Det flytter
faktisk kunder: fire af de 8-cifrede alternativ-numre har en anden kode end 2 og er
altså ikke CVR-numre. Ingen af de 1.798 har et forkert udfyldt CVR- eller CPR-felt —
de to grupper findes i klassifikationen, men er tomme på El-Ekstern.

De 385 er rigtige CVR-numre — 29189668 er Randers Kommune. Det er dem der kan lukkes
uden at nogen skal ringe til en kunde.

**Alle alternativer er tjekket.** På netop de 1.798 er `VATNUM`, `IDENTIFICATIONNUMBER`,
`EGH_IDENTIFYNUM`, CVR og CPR på disponent 2 alle tomme uden undtagelse, og kun 7 har et
EAN. Der er ét felt tilbage at hente identifikation fra, og det er alternativ-nummeret.

**Koden er nøglen, ikke præfikset.** Feltet har en kode ved siden af sig
(`EXU_ALTERNATIVECUSTOMERCODE`): kode 6 er CPR (145.050, næsten alle med `PNR=` foran),
kode 2 er CVR (47.627, ingen præfiks, alle 8-cifrede). Bekræftet 13-08-2026 — dagens
`PNR=`-regel er en tekstkonvention oven på en kode der siger det samme mere præcist.
Der er en håndfuld småkoder (7, 3, 255, 1, 5, 0) med tilsammen godt 2.100 konti på hele
basen; deres betydning er ikke afklaret, og de skal ikke tolkes på gætværk.

## Ændringer

**XellentDomain** (producer — koster et versionsbump og en CI-runde)

- Kunden bærer de rå Xellent-værdier med ud ved siden af de normaliserede: CVR og CPR på
  disponent 1 som de står, plus alternativ-nummeret og dets kode. De normaliserede felter
  er uændrede — det er stadig dem migreringen bruger.
- Kun det rå, ingen fortolkning. At læse et 8-cifret alternativ-nummer som CVR flytter
  385 kunder fra "migreres ikke" til erhvervskunde i Settl; det er en selvstændig
  beslutning, og den her ændring leverer grundlaget for at træffe den.

**DataMigration**

- Rapportens Ukendt-sektion grupperes efter fejlkilde med de fire grupper ovenfor, hvert
  gruppenavn med sit antal, kunderne under. Rækkefølgen er antal, størst først.
- Hver række viser CVR, CPR og alternativ-nummeret som de står i Xellent. Tom celle
  betyder tomt felt — det er selve svaret, ikke en manglende oplysning.
- Gruppen "alle felter tomme" fylder mest og siger mindst. Den foldes sammen som
  default, så de tre grupper man kan handle på står øverst og åbne.
- Samme grupper i kundemodalen i `report.html`: hvilken gruppe kunden hører til, og de
  tre rå felter.

**MigrationDashboard**

- Samme gruppering og samme tre felter i sektionen.

## Verifikation

1. XellentDomain: build + tests, bump, push, vent på CI.
2. DataMigration: restore, build, tests. Ny test på grupperingen med én kunde i hver af
   de fire grupper plus en kunde med `PNR=`, som slet ikke må være ukendt.
3. `create --offline --dry-run` på en håndfuld konti fra hver gruppe, plukket fra
   produktion: hver skal lande i sin gruppe med de rå værdier synlige.
4. `report` mod featuretest og et kig i kundemodalen på de samme konti.
5. Dashboard: build, tests, `npm run build`.
6. Til sidst: antallene i sektionens gruppeoverskrifter skal stemme med tabellen øverst
   her, kørt på samme population.
