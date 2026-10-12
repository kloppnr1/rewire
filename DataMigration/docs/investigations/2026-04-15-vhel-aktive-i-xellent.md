# VHEL — Aktive kunder i Xellent på profil el-han

**Dato:** 2026-04-15
**Profil:** `el-han` (Vesthimmerlands El-Forsyning A/S, DataArea `han`, El-ekstern)
**Kilder:**
- `…\Skrivebord\Settl\VHEL\XellentAktive\Xellent-Aktive.xlsx` (26 Debitorkonti)
- `…\Skrivebord\Settl\VHEL\XellentAktive\Rest VHEL i Datahub.xls` (13 målepunkter)
- AXDB50 prod (read-only) + Settl prod (read-only)

## TL;DR

| Bucket | Antal | Rod-årsag | Handling |
|---|---|---|---|
| **A. Migration OK, Xellent ikke lukket** | 18 | Customer findes i Settl, men ExuContractTable.Contractenddate står stadig på `EndDateNull` (1900-01-01) | Post-migration close-step skal køres for de 18 kontrakter |
| **B. Aldrig migreret (mangler i profil)** | 8 | Findes ikke i Settl, ikke i `el-han.json`. Xellent-data opfylder filteret — profilens account-liste er bare ikke opdateret | Tilføj 8 konti til `el-han.json` og kør migration igen (især nye kontrakter oprettet efter sidste profil-snapshot) |
| **C. DataHub ↔ Xellent mismatch** | 2 | DataHub melder VHEL som elleverandør på 2 MP'er. Begge er fundet i Xellent, men 1 ejer er forkert (ny kunde efter leverandørskift), 1 har inkonsistent kontrakt-/part-data | Kræver case-by-case afklaring med Xellent ops |

**Total: 28 entiteter — 18 driftsfejl (close-step), 8 profil-vedligeholdelse, 2 datakvalitet/manuelle.**

---

## Metode

1. Excel `Xellent-Aktive.xlsx` → udtræk af 26 Debitorkonti (kolonne B). Cross-ref mod `src/profiles/el-han.json` (4038 konti).
2. Excel `Rest VHEL i Datahub.xls` → 13 målepunkter (GSRN). Navne cross-ref mod Xellent-Aktive ⇒ 11 overlapper, 2 unikke.
3. Settl prod tjek (read-only) for alle 28 entiteter.
4. AXDB50 prod tjek (read-only): `CustTable`, `ExuContractTable`, `ExuContractPartTable`, `ExuDelpoint` for alle 28.

Filter (fra `src/DataMigration.Migration/Services/SettlExtractionService.cs:200-239`):
- `DataAreaId = "han"` (profil-override af konstant `"hol"`)
- `DeliveryCategory = "El-ekstern"`
- Aktiv kontrakt = `Contractenddate = EndDateNull (1900-01-01)` ELLER `>= today`

---

## Bucket A — 18 kontoer: migreret men ikke lukket i Xellent

Alle 18 har **bekræftet customer i Settl prod** og **stadig aktiv kontrakt i Xellent** (`Contractenddate = 1900-01-01`, contract part `Enddate = 1900-01-01`, produkt `FriElSpot`, DataArea `han`, DeliveryCategory `El-ekstern`). Migration lykkedes — men close-steget i Xellent (sætte slutdato på kontrakt + part) er aldrig udført.

| Konto | Navn | Xellent kontrakt | Settl CustomerId |
|---|---|---|---|
| 57729 | Fiktiv Persona | 107981 | df803763-cdc6-4ebd-8e7d-110ea0cab60b |
| 57730 | Fiktiv Personb | 107979 | 18d34730-b8b3-4deb-b853-787b9c9ac311 |
| 57876 | Enggården | 108146 | ff7be1a1-a1da-4667-a3d9-6a806b989bc5 |
| 57885 | Enggården | 108154 | 1152d871-e73a-41ea-9f0b-9bb08f9601e1 |
| 57900 | Enggården | 108178 | b746a36c-2c7f-4042-865d-f4107a845a00 |
| 57990 | Himmerland Ejendoms Invest ApS | 108274 | 0e74aee0-753a-4f2c-b1dc-25d5cc551c84 |
| 60627 | Marianne Dejligbjerg | 111092 | b6083da3-5d80-408a-bfc4-ec3199285e77 |
| 61298 | Aars Boligforening Fællesel | 111771 | 4da0b49e-b185-4b1b-b60f-032e15b948d4 |
| 61370 | Thomas Stubberup Dissing | 111887 | 626c9339-86cd-4f13-8b44-5b16a27baf97 |
| 61532 | Fiktiv Personc | 112058 | 0c35c63c-643f-4d3a-bc3f-7e6ef41283d4 |
| 61690 | Henriette Nyrup Vigsø | 112231 | f330f065-77bb-4cb7-a877-60696042a623 |
| 62084 | Fiktiv Persond | 112691 | b5ecc4b8-1b30-46f1-82e8-b8321ec3b4b4 |
| 62326 | Jørgen Houlberg | 112934 | 85e263ea-5d18-45f3-b1ec-fba09ce444b3 |
| 62475 | Michael Søgård | 113080 | 423a4199-3f0b-472a-9ee7-87b85b7d378b |
| 64254 | Fiktiv Persone | 114842 | c16f7a6f-f4ee-4aa7-91fd-62bf123445e8 |
| 64414 | Tsega Andemariam | 114991 | 4f5e28ef-9df9-4b6f-bfac-bdebc28167ff |
| 64846 | Sparekassen Danmark | 115435 | bbbc2c81-f9e0-4bae-8113-14957fe521e0 |
| 65270 | Sparekassen Danmark | 115864 | 38d3652e-e8dd-4c1c-a201-8c2427fe1d7b |

**Anbefaling:** Kør et close-step der sætter `Contractenddate` (og part `Enddate`) til migration-dato på de 18 kontrakter i Xellent. Det ser ud til at være et systematisk hul i workflow'et efter create-pipeline, ikke en bug i extraction.

---

## Bucket B — 8 kontoer: aldrig migreret, mangler i profil

Ingen af de 8 findes i Settl prod, og ingen er listet i `el-han.json`. **Men** Xellent-data viser at alle 8 opfylder profilens filter (DataArea=han, DeliveryCategory=El-ekstern, aktiv kontrakt med `FriElSpot`-produkt).

| Konto | Navn | Aktiv kontrakt | CP Startdate | Anmærkning |
|---|---|---|---|---|
| 52237 | Aars Boligforening | 117849 | 2025-11-01 | Ny kontrakt — gammel 102759 lukket 2025-10-31 |
| 53179 | Jørgen Degn | 117840 | 2024-04-02 | Ny kontrakt — gammel 102577 lukket 2024-03-31 |
| 53206 | Hans Jørgen Kastbjerg | 102598 | 2025-05-01 | Ældre aktiv kontrakt |
| 64731 | Bolig Himmerland | 115325 | 2024-01-31 | Aktiv kontrakt |
| 66499 | Aars Boligforening | 117112 | 2026-01-09 | Ny kontrakt 2026 |
| 67145 | Samira Tovar | 117755 | 2026-02-27 | Ny kontrakt 2026 |
| 67180 | Yuliia Pshenychna | 117795 | 2026-03-20 | Ny kontrakt 2026 |
| 67218 | NR Kloak | 117857 | 2026-03-30 | Ny kontrakt 2026 |

**Mønster:** 4 af 8 (66499, 67145, 67180, 67218) er kontrakter oprettet i 2026, dvs. **efter** profil-snapshot blev taget. 2 af 8 (52237, 53179) er nye kontrakter oprettet efter en lukket forgænger (kunden findes i Xellent, men kontrakten der er aktiv nu er ikke den der var på migrationstidspunktet). 2 af 8 (53206, 64731) er ældre aktive kontrakter — uklart hvorfor de aldrig er kommet med (måske udelukket manuelt på grund af konkrete forhold; tjek changelog/git-log på `el-han.json` for evt. fjernelse).

**Anbefaling:**
1. Tilføj de 8 Debitorkonti til `src/profiles/el-han.json`.
2. Kør `MigrationCli create --profile el-han` (eller equivalent gennem Dashboard) for at få dem ind i Settl.
3. Overvej en automatiseret periodisk re-scan: alle aktive Xellent-kontoer på `DataArea=han + DeliveryCategory=El-ekstern` der ikke allerede er migreret bør automatisk tilføjes.

---

## Bucket C — 2 DataHub-only kunder

DataHub-export viser disse 2 MP'er som havende VHEL som elleverandør, men de er ikke i Xellent-Aktive.xlsx.

### MP `571313108404640009` — DataHub-navn "Ruth Gundersen"

| Felt | Værdi |
|---|---|
| Findes i Xellent? | Ja — under konto **65739 — Vesthimmerlands Kommune** (kontrakt 116334) |
| Kontrakt-status | `Contractenddate = 2025-03-01` (i fortiden) — kontrakt teknisk lukket |
| Aktiv contract part | `FriElSpot`, startdate 2025-05-01, enddate `1900-01-01` |
| Forrige kunde | Ruth Elly Binderup (konto 64005, kontrakt 114608) — lukket 2025-01-07 |
| Findes i Settl? | Nej |

**Vurdering:** MP'et har leverandørskift-historie. Aktuel ejer ifølge Xellent er Vesthimmerlands Kommune (ikke Ruth Gundersen som DataHub viser). Kontrakten har inkonsistens: kontrakt-slutdato 2025-03-01, men aktiv contract part der starter senere (2025-05-01). Skal afklares med Xellent ops om kontrakt-status er korrekt.

### MP `571313113163553965` — Inga Gravesen

| Felt | Værdi |
|---|---|
| Findes i Xellent? | Ja — konto **65825 — Inga Gravesen**, kontrakt 116415 |
| Kontrakt-status | `Contractenddate = 2025-08-31` (i fortiden) — kontrakt teknisk lukket |
| Aktiv contract part | `FriElSpot`, startdate 2025-09-01, enddate `1900-01-01` |
| Findes i Settl? | Nej |

**Vurdering:** Samme inkonsistens — kontrakt slutter 2025-08-31, men contract part starter 2025-09-01 og er åben. Det er datakvalitetsproblem i Xellent (part skal lukkes når kontrakten lukkes). DataHub melder MP'et som aktivt hos VHEL, så enten er kontrakten ikke reelt lukket, eller MP'et er overgået uden DataHub-opdatering.

**Anbefaling:** Manuel afklaring pr. MP med Xellent-ops. Hvis MP'et reelt er hos VHEL, skal kontrakten genåbnes/oprettes korrekt og derefter migreres. Hvis MP'et er overgået, skal DataHub-registreringen rettes hos den ny leverandør.

---

## Sammenfatning af handlinger

| Prioritet | Handling | Omfang |
|---|---|---|
| 1 | Kør close-step i Xellent for de 18 kontrakter i Bucket A | Driftsmæssigt — 18 kontrakter |
| 2 | Tilføj 8 konti til `el-han.json` + kør migration | Profil-vedligehold + 8 nye kunder til Settl |
| 3 | Afklar de 2 DataHub-only MP'er med Xellent ops | Datakvalitet — 2 MP'er |
| 4 | Overvej automatisk profil-refresh for at undgå Bucket B i fremtiden | Procesforbedring |

## Kilder & verifikation

- Settl prod: alle 28 customer-opslag via `settl-investigator` (read-only GET).
- AXDB50 prod: alle 28 entiteter via `xellent-investigator` (read-only SELECT på CustTable, ExuContractTable, ExuContractPartTable, ExuDelpoint).
- Filter-logik: `src/DataMigration.Migration/Services/SettlExtractionService.cs:200-239`.
- Profil: `src/profiles/el-han.json`.
- Konstanter: `src/DataMigration.Core/MigrationConstants.cs` (`EndDateNull = 1900-01-01`).
