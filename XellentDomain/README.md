# XellentDomain

Shared utility-data library set: domain model, Xellent loader, EF entities, and helpers. Consumed via NuGet by DataMigration, MigrationOps, CustomerSupport (Bifrost), TimeSeriesMigration, and other repos.

## Projects

| Project | Package id | Purpose |
|---|---|---|
| `DataMigration.Core` | `XellentDomain.Core` | Shared models and helpers (constants, WarningCollector, CSV) |
| `DataMigration.Domain` | `XellentDomain.Domain` | Source-agnostic domain model (Customer, Contract, MeteringPoint, …) |
| `DataMigration.Domain.Engros` | `XellentDomain.Domain.Engros` | Wholesale/engros domain types (settlement basis) |
| `DataMigration.Domain.TimeSeries` | `XellentDomain.Domain.TimeSeries` | Time-series domain types (raw measured metering point values) |
| `DataMigration.Domain.Xellent` | `XellentDomain.Domain.Xellent` | Xellent → Domain mapper + Xellent query utilities (invoices, contract parts, account groupings) |
| `DataMigration.XellentData` | `XellentDomain.XellentData` | EF Core entities for the Xellent (Dynamics AX 2009) database |

Assembly + namespace names remain `DataMigration.*` so consumer `using` statements keep working unchanged.

## Tests

Unit tests live under `src/Tests/` and are part of the solution. Run with `dotnet test`.

The Xellent live-DB regression suite (`DataMigration.Xellent.IntegrationTests`) stays in the `DataMigration` repo because it exercises the full extraction pipeline (SettlExtractionService).

## Build

```
dotnet build
dotnet test
```

## Versioning

Manual `<Version>` in `Directory.Build.props`. Bump on each merge to `master`. Initial version `0.1.0-preview` until the upstream domain-model PR (`#4670` in DataMigration) merges; then `1.0.0`.

## Documentation

- `docs/xellent-datamodel.md` — **hvordan Xellent selv hænger sammen**: kæden fra
  kontrakt til målepunkt, hoved-/underkontrakter, paraply-kontrakter uden
  leveringskategori, betalingsaftalernes felter og koder, dato-sentinels, og
  hvem der ejer et målepunkt. Læs den før du skriver en ny forespørgsel mod
  Xellent — den findes fordi de forhold gentagne gange er blevet gættet forkert.
  Hver påstand er enten målt med dato eller markeret som udledt.
- `docs/domain-model.md` — overview of the domain types and the Xellent loader contract.
