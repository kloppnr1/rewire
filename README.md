# rewire

A single .NET 9 solution that combines four NewSettlement repositories as folders, with their git history kept:

| Folder | Contents |
|--------|----------|
| `XellentDomain/` | Core, Domain and XellentData libraries |
| `SettlClient/` | Typed Settl HTTP client and API contracts |
| `DataMigration/` | MigrationCli (`create`) and OpsCli |
| `TimeSeriesMigration/` | Time series extract and Settl post (`TimeSeries.Cli`) |

The folders reference each other with `ProjectReference`, so a change in one folder reaches the others at the next build. Nothing is published as a NuGet package.

## Build and test

```powershell
dotnet build rewire.slnx
dotnet test rewire.slnx
```

All tests run on a clean clone with no network access to Xellent or Settl: the integration tests use an in-memory Xellent (SQLite) and a fake Settl behind the real typed clients.

## Documentation

- Each folder has its own `README.md` and `docs/`.
- Production deployment of TimeSeries.Cli and OpsCli: [docs/production-runbook.md](docs/production-runbook.md).
