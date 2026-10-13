# NewSettlement.TimeSeriesMigration

Moves metering point time series from Xellent to Settl. The workspace rules in `../CLAUDE.md` apply here too.

## Where the knowledge is

- `docs/ai/start.md` — the entry point. Read the topic you need before changing migration behaviour: delivery window, ownership, resolution, time boundaries, snapshot and done marks, writes, verification commands. Each rule links the source that proves it.
- `README.md` — commands, options, skip files and report columns.
- `docs/settl-api-integration.md` and the integration tests' `README.md` are partly historical (deleted client paths, a removed normalization service). When a document and the source disagree, the source wins.

## Rules

- **Production is dry-run.** `production`, `production-hev` and `production-vhel` get `--dry-run` unless the user explicitly authorizes a production write. Hot verification runs on `--env featuretest`.
- **There is no `staging` environment.** The "Staging" deploy server runs `featuretest`. Always pass `--env`: without it the CLI takes `DOTNET_ENVIRONMENT`, and only then `featuretest`.
- `--offline` is not a safety net. It only changes where reads come from; posts still go to Settl. Only `--dry-run` stops the writes.
- Xellent reads and Settl investigations are read-only. No personal data in queries, logs or reports — identifiers, counts and dates only.
- Xellent and the Settl endpoints need the corporate VPN. On the first failure with "named pipe", "network-related", "netværksstien", "could not open a connection" or "not found or was not accessible": stop, say the VPN is likely down, and wait. Never retry in a loop.
- Plans live in `plans/`: short bullets, no code. Leave the files already there alone.
- Commits go to `develop`, never `master`. When the user is working in this repo, commit each verified change as soon as its gates are green — one fix per commit; push only when she asks. Expert agents still do not commit.

## Easy to get wrong

- **Timestamps are UTC.** `EMS_TIMESERIESVALUES.TIMEOFVALUE` is stored as UTC and sent as UTC. Never add a CET/CEST conversion to points. Danish calendar dates matter only for agreement, reading-period and window bounds.
- **Resolution.** Values are posted at the resolution they are stored at, with one exception (story 45902): hourly values inside a dated quarter-hour reading period are split into four equal 15-minute points, for sending only. Stored values and extraction stay unchanged. A split that is not exact to five decimals fails the whole metering point. There is no flag for this.
- **`BillableTypes`** on externally invoiced periods is `["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"]`, agreed with Systemate 2026-08-27. Do not change it without the user's confirmation.
- Batch mode takes its metering points from Settl's list, never from DataMigration profile files, and never looks up the Settl customer — merged accounts are not Settl customers.
- `post` always extracts from Xellent again. There is no post-from-JSON mode.
- A failure stops the run unless `--force` is set, and writes already made are not rolled back.
- The dashboard parses the `Run completed:` totals line and the `OUTGOING MP` line. Keep their format.

## Packages and layout

- Publishes `NewSettlement.TimeSeriesMigration` (from `TimeSeries.Migration`) and `NewSettlement.TimeSeriesMigration.Core` (from `TimeSeries.Core`), series 0.3.x. The version lives in `Directory.Build.props`. MigrationDashboard consumes both, and can only be verified after CI has published.
- Consumes `NewSettlement.SettlClient.Api` (`0.7.*-*`) and `NewSettlement.XellentDomain.Domain.TimeSeries` / `.XellentData` (`0.8.*-*`). There are no local forks of either.
- The runner, extraction and snapshot store are in `src/TimeSeries.Migration`. `src/TimeSeries.Cli` is only `Program.cs` and config. Dashboard.Api has moved to the MigrationDashboard repo.

## Verification

In this order; the exact commands are in `docs/ai/start.md`.

1. Build `NewSettlement.TimeSeriesMigration.slnx`.
2. Test `TimeSeries.Core.Tests` and `TimeSeries.Cli.Tests`.
3. Test `TimeSeries.Xellent.IntegrationTests` when the change touches extraction, Settl mapping or `post`. The project is outside the solution, so neither a solution-level `dotnet test` nor CI runs it. It needs the VPN and a `testsettings.json` pointing at `AXDB50_TEST`. Do not accept a changed golden baseline without reviewing why it changed.
4. CLI smoke test: `dotnet run --project src/TimeSeries.Cli -- post --help --env featuretest`, then `post --dry-run`. Config loads before `--help` is parsed, so even help needs a valid environment.

## Investigation

Use the `xellent-investigator` and `settl-investigator` agents and the `settl-api-query` skill. They live at user level (`~/.claude/`), not in this repo.
