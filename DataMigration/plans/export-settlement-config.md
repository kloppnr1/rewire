> **✅ IMPLEMENTERET** — `1bd17b5d`. Bemærk at beskrivelsen er overhalet: kundenummeret læses nu i Settl (`62c014c7`), skemaet afgøres af settlement group ved uenighed (`a074f17d`), og segmentet er skabelon + CVR/CPR (`a1189ae2`).

# export-settlement-config

## Verifikation (start her)

### Hvad der beviser at det virker

1. Build:
```
dotnet build DataMigration.sln
```

2. Unit tests (gruppering, segment-konflikter, config-loader):
```
dotnet test src/Tests/DataMigration.OpsCli.Tests/DataMigration.OpsCli.Tests.csproj
```

3. Read-only CLI-run mod featuretest (lille udsnit først):
```
cd src/DataMigration.OpsCli
dotnet run -- export-settlement-config --env featuretest --limit 50 --output ..\..\staging-data\settlement-config-sample.json
```

4. Fuld kørsel:
```
cd src/DataMigration.OpsCli
dotnet run -- export-settlement-config --env featuretest --output ..\..\staging-data\settlement-config.json
```

### Forventet output / succeskriterium

- Trin 1-2: 0 errors, alle tests passed.
- Trin 3-4: exit code 0. Stderr viser scannede MPs, hvor mange i levering, hvor mange pinnet til en
  Xellent-konto ved COS-1, samt kunder/schedules/unresolved — derefter én linje pr. schedule og en
  `Customer segments`-blok med én linje pr. segment plus konflikt-optælling.
- `unresolved` bør være lav. Hver post har en årsag: `NotFoundInXellent` (MP oprettet direkte i
  Settl), `NoContractAtChangeOfSupplier` (migreret kontrakt slettet/ændret), `NoBillingPlan`,
  `NoRepresentative`. De to første har et stående segment-svar i konfigurationen, og hver række
  bærer nu også kunden — fundet via Settls betalere, som er eneste vej fra målepunkt til kunde.
  Linjen `...heraf med segment` viser hvor mange af dem der ikke længere venter på en beslutning.
- Umappede koder listes til sidst — det er grundlaget for at færdiggøre `settlement-config.json`.
- Spot-check: vælg 2 kundenumre fra to forskellige schedules og verificér `Billingplannum` +
  `Contracttemplatenum` på kundens kontrakt i Xellent.
- Spot-check V_LadePriv: privatkunderne skal nu stå i segmentet `Privat_Opladning_Eje`, ikke i
  `segmentConflicts`. Kunder med uenige målepunkter skal stå i `Erhverv`, og linjen
  `...heraf uenige MPs` skal vise hvor mange det er.
- Spot-check uresolvede: de 96 `NotFoundInXellent` skal alle stå med `Erhverv`, og de 18
  `NoContractAtChangeOfSupplier` med `Erhverv` undtagen dem hvis Settl-kunde har et CPR-nummer.
  Står der `(kundens eget)` på en række, er kunden allerede placeret af sine øvrige målepunkter —
  det svar skal stå ved magt.

## Kontekst

Post-migration skal der kunne udtrækkes hvilke kundenumre der hører til hvilken Settlement Schedule
og hvilket Customer Segment. Udtrækket er ikke profil-baseret: populationen er el-MPs i levering i
Settl. Begge værdier kommer fra samme Xellent-kontraktrække, så de kan aldrig divergere.

## Afklaret

- Population: market-v3-MPs i Settl — parent-MPs med `InDelivery=true` (samme udvælgelse som
  `reconcile`), pinnet til kontrakten der sluttede ved `COS-1`.
- Schedule = `ExuContractTable.Billingplannum`. Segment = `ExuContractTable.Contracttemplatenum`.
- Kundenr. = repræsentanten (repræsentations-princippet holdes automatisk).
- Mapping ligger i én fast fil, `src/DataMigration.OpsCli/settlement-config.json`, med sektionerne
  `schedules` og `segments`. Kopieres til output-mappen og læses fra siden af exe'en. Ingen CLI-flag.
  Fallback er altid den rå Xellent-kode — både ved manglende fil og manglende entry. Kun en fil der
  findes men er ugyldig JSON fejler.
- `segments`-værdier må være en streng (entydigt) eller en liste (tvetydig, kræver manuel afgørelse).
- Segment er kunde-niveau: kun kunder med præcis ét entydigt segment havner i `segments[]`. Resten
  står i `segmentConflicts[]` med skabelon, kandidater og MP-ider. MPs uden skabelon i
  `segmentUnresolved[]`.
- Kunder med kontrakter på flere billing plans optræder under hvert relevant schedule (schedules er
  MP-niveau, modsat segmenter).
- Janni har 2026-08-11 afgjort de to konflikt-typer for de allerede migrerede: ladekunderne i Settl er
  alle Eje, og kunder hvis målepunkter peger på forskellige segmenter hører alle til Erhverv. Begge
  svar står i `settlement-config.json` (`V_LadePriv` og `multipleSegments`), så konflikt-listen kun
  indeholder det der stadig er uafklaret.

## Udestår

- Kør trin 3-4 og udfyld `schedules`-sektionen ud fra de umappede plan-koder.
- Bekræft at `segments`-sektionens 10 skabeloner dækker alle `Contracttemplatenum`-værdier i data.
