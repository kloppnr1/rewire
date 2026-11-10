# rewire

Four NewSettlement repos as folders of one .NET 9 solution, with their git history. The folders reference each other with `ProjectReference`. Nothing is published as a package inside this repo.

## Folders

| Folder | Was | What it is | Guide |
|--------|-----|------------|-------|
| `XellentDomain/` | NewSettlement.XellentDomain | Core, Domain and XellentData libraries | [CLAUDE.md](XellentDomain/CLAUDE.md), [start](XellentDomain/docs/ai/start.md) |
| `SettlClient/` | NewSettlement.SettlClient | Typed Settl HTTP client and API contracts | [CLAUDE.md](SettlClient/CLAUDE.md), [start](SettlClient/docs/ai/start.md) |
| `DataMigration/` | NewSettlement.DataMigration | MigrationCli (`create`) and OpsCli | [CLAUDE.md](DataMigration/CLAUDE.md), [start](DataMigration/docs/ai/start.md) |
| `TimeSeriesMigration/` | NewSettlement.TimeSeriesMigration | Time series extract and Settl post | [CLAUDE.md](TimeSeriesMigration/CLAUDE.md), [start](TimeSeriesMigration/docs/ai/start.md) |

Read the folder's own guide before changing it. The folder files still describe NuGet packages, floating refs and per-repo branches. In this repo that is out of date: a change in one folder reaches the others at the next build.

## Build and test

```powershell
dotnet build rewire.slnx
dotnet test XellentDomain/src/Tests/DataMigration.Core.Tests          # and each other unit test project
```

Unit test projects: `DataMigration.Core.Tests`, `DataMigration.Domain.Tests`, `DataMigration.Domain.Engros.Tests`, `DataMigration.Domain.Xellent.Tests`, `DataMigration.SettlClient.Tests`, `DataMigration.Migration.Tests`, `DataMigration.OpsCli.Tests`, `TimeSeries.Core.Tests`, `TimeSeries.Cli.Tests`.

The three `*IntegrationTests` projects are not in `rewire.slnx`. They read Xellent and Settl, need the VPN and a `testsettings.json`, and have no skip without config. Run them one by one, never through `dotnet test rewire.slnx`.

CLI smoke: `dotnet run --project DataMigration/src/DataMigration.Migration -- --help`, the same for `DataMigration/src/DataMigration.OpsCli`, and `dotnet run --project TimeSeriesMigration/src/TimeSeries.Cli -- post --help --env featuretest` (TimeSeries.Cli loads config before it parses `--help`).

`nuget.config` lists nuget.org only.

## Commit gate

`.claude/hooks/pre-tool-guard.js` runs before every Bash and PowerShell call. It blocks Xellent queries that select personal columns, and it blocks `git commit` unless `rewire.slnx` builds and the affected unit tests pass. Affected means the test project's `ProjectReference` graph reaches a changed project, across folders. Put `RUN_INTEGRATION=1` in front of the commit when the change touches extraction, Settl mapping, Core/XellentData or a mutating CLI.

## Safety

- Xellent (`xel2012`; prod `AXDB50`, test `AXDB50_TEST`) and Settl hold real customer data. Reading is fine. Never write to Xellent. Never write to Settl production unless the user says so.
- No personal data in queries, logs, reports or transcripts: identifiers, counts and dates only. No `SELECT *`, no `sqlcmd -i`.
- Xellent and Settl need the corporate VPN. On the first "named pipe", "network-related" or "could not open a connection" error: stop, say the VPN is likely down, and wait.

## Mutation workflow

Production is dry-run. `production`, `production-hev` and `production-vhel` need the user's explicit yes for a hot run, every time.

1. Preview with `--dry-run` (or the command's documented plan mode). An unknown flag does not make a run safe.
2. Spot-check 2-3 changes from the preview against the source data.
3. Hot run on `featuretest`, then verify.

| CLI | `--dry-run` | `--env` | Default env |
|-----|------------|---------|-------------|
| OpsCli (mutating commands) | Command-specific; check first | Yes | `featuretest` unless overridden |
| MigrationCli `create` | Yes | Yes | `featuretest` |
| TimeSeries.Cli `post` | Yes | Yes | `featuretest` unless `--env` or `DOTNET_ENVIRONMENT` overrides it |

- Name the target explicitly. Environment variables and local config can override the default.
- Check each OpsCli command's write behaviour in DataMigration's guide; do not infer it from the name.
- `apply-aborabat-repair` is production-only and has no `--dry-run`: leaving out `--execute` shows its plan. Its reviewed workbook does not replace the user's yes.
- TimeSeries.Cli has no `staging` environment. `--offline` only changes where reads come from; posts still go to Settl.

## Domain rules

- Customer, Payer, MeteringPoint and Contract are independent top-level Settl entities with their own endpoints. Take the shortest API path to the entity you need; go through a parent only when you need the parent.
- Three identifiers, never mixed up: the EAN (13 digits, e-invoicing, on the Payer billing config), the metering point identification (18 digits, national id, the Settl lookup key) and the Settl GUID. An 18-digit number from Settl metering point data is an identification, not an EAN.
- Representation: normal merging uses same-CVR fingerprint or legal-contact DAR relationships; email is part of both keys. `CVR + TechnicGroup4` is the legacy rule for profiles with `Extraction.LegacyTg4Merge=true`. The lowest Accountnum is the representative. Code that turns a Xellent Accountnum into a Settl customer lookup must resolve the representative first, with XellentDomain's resolver. Never rebuild the merge keys.
- `migration-state.json` (DataMigration) is the only migration state. `migration-tracking.db` is dead.
- Time series timestamps are UTC, stored and sent. Never add a CET/CEST conversion to points. Danish calendar dates matter only for agreement, reading-period and window bounds.

## Git

Branch `develop`; `master` points at the same commit. Never push unless the user asks.
