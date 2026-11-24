> **✅ IMPLEMENTERET** — `497f4e80` + `05879b7d`
>

# Fjern direkte Xellent DB-adgang fra DataMigration

## Status: FÆRDIG ✓

Alle fire faser gennemført (2026-05-12).

## Kontekst

Xellent-databaseadgang centraliseres i `UtilityData`. Dette repo
må kun forholde sig til Domain-objekter (`XellentLoadResult`) og Settl API — ingen
`XellentDbContext`-injection, ingen EF/SQL-queries.

---

## Fase 1 — Udvid `XellentLoadResult` i UtilityData *(separat session)* ✓

Gennemført i UtilityData-repo. `XellentLoadResult` udvidet med:

| Felt | Bruges af |
|------|-----------|
| `MpOrderIdentifiers: Dictionary<string, string?>` | `UpdateBillingAsync` (use case 2) |
| `MpActiveContractTemplates: Dictionary<string, IReadOnlyList<string>>` | billing-exemption step |
| `Contract.PaymentTerm` | contract-name resolution |
| `MeteringPointAssignment.ContractPartEndDate` | Settl contract-links |
| `AccountGroupings: IReadOnlyList<AccountGrouping>` | gruppering af accounts |

---

## Fase 2 — Refaktorér `SettlMappingService` *(dette repo)* ✓

`SettlExtractionService` omskrevet til ren transformation over `XellentLoadResult`
(ingen `XellentDbContext`). Omdøbt til `SettlMappingService` da service ikke
længere ekstrahere fra DB (commit `986fdaf0`).

`CreateRunner.RunAsync` tager nu `XellentLoadResult` direkte — DB-loading sker
hos caller (Program.cs, MigrationService.cs, test-fixtures).

---

## Fase 3 — OpsCli direkte SQL-runners *(no-op)* ✓

Beslutning: ingen ændring. `DataMigration.OpsCli` beholder `XellentData`
PackageReference. Runners der laver direkte DB-opslag:

| Runner | Årsag til at beholde |
|--------|----------------------|
| `PbsMatchRunner` | Bruger `Referencetomaincontract` + metering point type codes — ikke i domain model |
| `GenerateSeedRunner` | Snapshot-tool — henter hele join-stack |
| `GenerateStateRunner._queryInvoices` | `CustInvoiceJours` — faktura-domæne udenfor `XellentLoadResult` |
| `ReportMeterInfoRunner`, `ReportMeteringPointsRunner` | Specialiserede målertabeller hører ikke i `XellentLoadResult` |

---

## Fase 4 — Fjern `XellentData` fra Migration-pipeline *(dette repo)* ✓

- `UtilityData.XellentData` PackageReference fjernet fra
  `DataMigration.Migration.csproj` og `DataMigration.Api.csproj` (commit `05879b7d`)
- `src/Tests/DataMigration.Xellent.IntegrationTests/` bevares i dette repo —
  afhænger af `SettlMappingService`, `ContractNameResolver`, `ProfileLoader` fra
  `DataMigration.Migration`; kan ikke flyttes til UtilityData uden at gøre
  `DataMigration.Migration` til NuGet-pakke
- XellentData tilgængeligt transitivt via `Domain.Xellent → XellentData`
