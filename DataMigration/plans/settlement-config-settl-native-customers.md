> **✅ IMPLEMENTERET** — `345c92c2` (CHANGELOG 2026-08-12). Følges af `e47afde7`, der rettede et derivat-problem med paddede kundenumre.

# Kunder der kun findes i Settl må ikke arve den forrige holders Xellent-kontrakt

## Kontekst

`export-settlement-config` slår skema, skabelon og produkter op pr. målepunkt på den
Xellent-kontrakt der sluttede ved COS−1, men kundenummeret læses i Settl. Er målepunktet
skiftet hånd ved cutover, beskriver Xellent-siden den forrige holder.

Klinik L v/Line Loch (600016) landede på den måde i `HalvårBagud`: hendes ene målepunkt lå
indtil 31. maj hos boligforeningen Møllevænget & Storgaarden, der afregnes halvårligt. Settl
siger selv "Kvartalsvis Aconto" om hende. 600016 findes slet ikke som Xellent-konto.

Målt på production-eksporten 12-08-2026: **287 af 6.940 kunder har et kundenummer der ikke
findes i CustTable** — alle 287 fik alligevel en plan og en skabelon på rækken, og 253 af dem
er uenige med den Settl-drevne segment-regel.

## Ændringer

- En kunde hvis nummer ikke findes som Xellent-konto får ingen afregningsplan, ingen skabelon
  og ingen produkter. Felterne ryddes på målepunktet, før nogen gruppering, så skema, segment,
  kundefakta og sammenligning alle følger af én beslutning.
- Hendes skema kommer fra hendes settlement group. Har hun ingen gruppe, står hun i intet skema
  — det er ikke en konflikt, for der er ikke to kilder der er uenige, og den tælles i
  `totals.customersWithoutSchedule`.
- Segmentet kan kun komme fra den Settl-drevne regel; skabelon-reglen har intet at sige om
  hende, og hendes målepunkter listes i `segmentUnresolved`.
- Sammenligningsrækken siger hvorfor Xellent-felterne er tomme, i stedet for at lade dem læse
  som "Xellent havde ikke noget".
- CVR/CPR læses nu i Xellent for alle kunder i stedet for kun dem Settl ikke navngav — samme
  opslag afgør begge dele, og Settls egen identifikation vinder stadig hvor den findes.

Afgrænsning: kriteriet er "findes kundenummeret som Xellent-konto", ikke
`customersMovedBySettlLookup` (383). De ~96 ekstra dér findes i Xellent og er merge-drift
(Sparekassen Kronjylland-typen), hvor Xellent-kolonnerne beskriver den rigtige kunde.

## Verifikation

1. Kør:
   `dotnet run --project src/DataMigration.OpsCli -- export-settlement-config --env production --output C:\tmp\sc-fix.json`
2. Se om opsummeringen skriver `...uden Xellent-konto: 287 kunde(r)` og
   `Uden skema: 3 kunde(r)`.
3. Verificér i filen: `HalvårBagud` går fra 7 til 3 kunder (600013, 600016, 600038 og 600058
   forsvinder), 600016 optræder under `KvartalAC`, og hendes række i `customers` har tom
   `billingPlanNums` og `contractTemplates`.
