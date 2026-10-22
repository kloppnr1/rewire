# NewSettlement.TimeSeriesMigration

Moves metering point time series from Xellent to Settl: reads the points from Xellent, posts them to Settl, and registers an externally invoiced period per metering point.

Run everything from the repo root with `dotnet run --project src/TimeSeries.Cli -- <command> [options]`. `--help` works on every command. Examples use `featuretest`; **production is always run with `--dry-run`**.

## Migrating time series (the main workflow)

Every parent metering point ends as **done**, **skipped** or **excluded**. On the way it can be **ready** (can be sent now), **blocked** (someone else has to fix something first) or **failed** (a call to Settl went wrong). Child metering points are never in scope. The operator runbook is the "Time series migration process" doc; these are its five steps.

```bash
# Once: read what the runs need from Settl and Xellent and keep it on disk (a few hours in production)
dotnet run --project src/TimeSeries.Cli -- snapshot --env featuretest

# 1. Check where every metering point stands — the dry run is the check, nothing is sent
dotnet run --project src/TimeSeries.Cli -- post --all --env featuretest --offline --dry-run --force

# 2. Send the ready ones: 100 first, then the rest (done ones are left out by themselves)
dotnet run --project src/TimeSeries.Cli -- post --all --env featuretest --offline --force --limit 100
dotnet run --project src/TimeSeries.Cli -- post --all --env featuretest --offline --force

# 3. Retry the failed ones: the same command again
dotnet run --project src/TimeSeries.Cli -- post --all --env featuretest --offline --force

# 4. Hand each output/blocked-*.csv to whoever fixes that group. When a group is fixed, re-read only
#    the blocked metering points (minutes, not hours), then run step 1 again
dotnet run --project src/TimeSeries.Cli -- snapshot --env featuretest --refresh-blocked

# 5. Exclude by hand, with a reason: add a line to output/snapshot/<env>/excluded.csv

# Any time: the states of the last run (also while it runs), and how far a running snapshot is
dotnet run --project src/TimeSeries.Cli -- progress --env featuretest
```

The migration is finished when step 1 shows ready 0, blocked 0 and failed 0.

### The states summary

`post --all` ends with one line per state and the count per reason under it:

```
States (dry run: ready = would be posted): done 440 · ready 166 · skipped 1,556 · excluded 0 · blocked 61 · failed 0
  Done: 440
         440  already in Settl
  Ready: 166
         166  would be posted
  Skipped: 1,556
         …
  Blocked: 61
          47  missing Hour series in Settl  → blocked-missing-hour-series-in-settl.csv
           …
```

- While it runs, the console shows one line that updates in place (`1,200/1,766 checked · done 440 · ready 12 · …`). Every line per metering point goes to `output/post-<env>-<time>.log`; its path is the first line on screen.
- The same states are written to `output/snapshot/<env>/states.json` every 5 seconds and at the end, and `progress` shows them.
- **Blocked** never stops the run and never makes the exit code non-zero. Each blocked reason gets its own `output/blocked-<reason>.csv` (metering point, type, Settl id, customer number, reason, detail) to hand over. The files are written again by every `post --all`.
- **Failed** means only that a call to Settl went wrong. Without `--force` the run stops at the first one; with it the run goes on. Either way the exit code is non-zero.

### Done marks

The last step of `snapshot` is a done check. For every metering point a `post --all` would post, it works out the same window the post would use. It then asks Settl whether any of the metering point's series already has points in that window (the `--skip-if-existing-points` check). Those metering points are written to `output/snapshot/<env>/done.jsonl` (Settl ID, account, revision, window, existing point count, time).

The check runs `--parallel` at a time. Settl answers one year at a time, and the first year with points settles it. Every answer is kept in the snapshot (`settl-points-summary.jsonl`), so the next seed reads it instead of asking again. Those answers can go stale; that is accepted.

A live `post --all` also marks each metering point it posts as done.

Every `post --all` — live or dry run, online or offline — leaves done metering points out from the start, and they don't count toward `--skip` / `--limit`. The log says how many (`Already done: …`). To post a metering point again, remove its line from `done.jsonl`.

`snapshot` ends with how far the migration is, e.g. `Migration: 440 of 661 metering points done (66%), 221 still to post`. Every `post --all` starts with the same line, using the done list as it is now and the total from the last snapshot.

The snapshot also keeps each metering point's current payer in Settl (`settl-payer.jsonl`, 100 metering points per call), so the 6##### payer check needs no Settl calls offline.

On featuretest: 440 of 661 candidates were already in Settl. Checking the remaining 221 took 36 seconds, and 2 seconds on the next seed.

### `snapshot`

Reads everything `post --all` needs and stores it in `output/snapshot/<env>/`:
- **From Settl:** the metering point list, and per metering point its delivery statuses, its current delivery status (only that section — no consumer names or addresses) and its time series.
- **From Xellent:** child relations (only to know which metering points are children, so they can be left out) and contracts.

Time series points are **not** stored. A batch dry run asks Xellent for counts per dated segment and checks whether hourly values can be split exactly; it reads only the first offending value when validation fails. The seed now counts them for every metering point it would post and keeps the counts (`xellent-point-counts-v3.jsonl`), so `resolution-changes` and offline dry runs read them back instead of counting in Xellent again. That is the slowest part of a seed; a run that only needs the Settl reads is done before it. A live run reads the points fresh. Neither Settl nor Xellent changes while the migration runs, so one snapshot serves many runs.

| Option | Meaning |
|--------|---------|
| `--env <name>` | Which environment to read. One snapshot per environment |
| `--parallel <N>` | Settl reads per metering point, N at a time. Default 6 |
| `--refresh` | Throw the stored reads away and read everything again. Done marks are kept |
| `--refresh-blocked` | Read again only the metering points listed in the `blocked-*.csv` files of the last `post --all` (after someone fixed a group); every other stored read is kept. Featuretest: 61 metering points in 19 seconds |

- Running it again keeps what is stored and only fills in what is missing (an interrupted seed resumes), then runs the done check again. So to mark new done metering points on an existing snapshot, just run `snapshot` again.
- A failed read is listed at the end and the command exits non-zero; run it again to retry.
- Progress of each step shows as one line that updates in place. Every line also goes to `output/snapshot-<env>-<time>.log`; the list of failed reads is only there.
- Featuretest (2,219 metering points): about 1.5 minutes, 2.4 MB. Production (61,074): 305,381 reads, a few hours.
- On featuretest a full offline dry run then takes about 1 minute the first time (Xellent counts) and a few seconds after that.

### `post --all`

Settl decides which metering points are posted; Xellent decides whose they are.

1. Every electricity metering point in Settl's list, parents only. Children (a Settl relation or the Xellent relation table) are never in scope.
2. Per metering point, the delivery start comes from its delivery statuses, the way DataMigration picks it: the first in-delivery period, which must have started as a change of supplier. A later move-in, gap or return never replaces it, and the metering point's current delivery status does not matter. A first delivery that is a move-in, or has no start reason, is skipped as not migrated; start reason Unknown, or two start reasons on the first day, is blocked. Settl can fail to mark delivery (2026-09), so metering points shown as not in delivery anywhere are checked too, using the start of their current delivery status.
3. The account is whoever held the metering point in Xellent (`El-ekstern` contracts) on the day before delivery starts. That relationship must be closed as a migration.
4. Window: `end` = delivery start, `start = max(end − 3 years, delivery boundary, the account's own contract start)`.

| Option | Meaning |
|--------|---------|
| `--env <name>` | `featuretest` (default), `production`, `production-hev`, `production-vhel` |
| `--dry-run` | Resolves everything but posts nothing |
| `--offline` | Read Settl and Xellent from the snapshot instead of asking them again. Anything missing is read live and added. Points are read fresh; posts always go to Settl |
| `--force` | Keep going past a failed metering point (the run still exits non-zero). Without it the run stops at the first failure. Blocked ones never stop the run |
| `--cos-from` / `--cos-to <yyyy-MM-dd>` | Only metering points whose delivery in Settl starts in this range (Danish dates, both included) |
| `--skip <N>` | Pass over the first N metering points in range, in identification order. Metering points already done don't count |
| `--limit <N>` | Stop once N metering points are sent (ready, in a dry run). Skipped, excluded, blocked and done ones don't count. With done marks, `--limit` alone is usually enough |
| `--skip-if-existing-points` | Skip a metering point if Settl already has points in its window |
| `--skip-resolution-change` | Skip a metering point that changes resolution in the window (typically Hour → Quarter): in its series, or in what Xellent says it was read at |

What happens to each metering point:
- **Done:** points to Settl, then an externally invoiced period with `BillableTypes=["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"]`. A dry run counts these as **ready**.
- **Skipped** to a CSV file (below): a rule says it is not migrated, for example never delivered, no migration in Xellent at COS − 1, or no meter installed.
- **Blocked** to a `blocked-*.csv` file: something has to be fixed first, for example a missing series in Settl or unclear delivery dates.
- **Failed:** a call to Settl went wrong; logged as `ERROR`.

The summary starts with the counts, for example: `Run completed: 2219 metering points in Settl, 2148 processed, 601 posted, 71 never delivered, 1426 not migrated, …, 168 children skipped.` Failures skipped with `--force` follow on their own line. With `--offline` a line after it says how many reads came from the snapshot and how many were read live.

### Skip files

Written to the output folder (`output/`), one file per reason; a file only appears if something was skipped. Every CSV this tool writes opens straight in Excel here: UTF-8 with BOM, semicolon between fields, every field quoted, so metering point numbers stay text.

Each row carries a `Type` column — Parent or Child, empty where it isn't known (a post by account or metering point rather than `--all`). The skip lines in the log end the same way, `… (parent)` or `… (child)`.

| File | Reason |
|------|--------|
| `skip-not-installed.csv` | No values in the window and Xellent status 5 "Oprettet": no meter installed or connected. Nothing to post; not counted as posted. A metering point with a meter but no values is blocked instead |
| `skip-payer-6-series.csv` | The metering point's current payer in Settl is in the 6##### series (six digits starting with 6); those are not migrated. Left out before `--skip` / `--limit` count |
| `skip-missing-in-xellent.csv` | Nobody held the metering point in Xellent the day before delivery |
| `skip-not-migrated.csv` | No migration in Xellent at COS − 1: not in delivery in Settl and no migration, or a contract not closed as a migration |
| `ambiguous-revisions.csv` | More than one `InDelivery=true` delivery status |
| `skip-delivery-boundary-unresolved.csv` / `-ambiguous.csv` / `-invalid.csv` | The not-in-delivery period before delivery can't be pinned down |
| `skip-no-points-after-boundary.csv` | Every point falls before the delivery boundary |
| `skip-existing-settl-data.csv` | Settl already has points (`--skip-if-existing-points`) |
| `skip-resolution-change.csv` | Resolution change (`--skip-resolution-change`): included outgoing values use more than one resolution after dated conversion and delivery filtering |
| `known-issue-xellent-resolution.csv` | Known Xellent issue: values stored at a different resolution than Xellent says the meter was read at (e.g. hourly values where it reads every 15 minutes), with no series for the right resolution. The source mismatch remains visible here. Hourly values in dated quarter-hour reading periods are split into four equal outgoing values; source values remain unchanged |

### Excluded by hand

`output/snapshot/<env>/excluded.csv` is the operator's list of metering points that are never sent. One row per metering point: identification, then the reason; further columns (who, when) are for the operator. Semicolon or comma between fields; a header row is ignored. `post --all` leaves them out before anything else and counts them as Excluded with their reason.

## `progress`

Read-only. Shows how far a (running) snapshot is — phase, step, done marks, what remains — and the states of the last `post --all` run, live while it runs. Safe to run from another session. `--watch` repaints every `--refresh` seconds (default 10).

```bash
dotnet run --project src/TimeSeries.Cli -- progress --env production --watch
```

## `resolution-changes`

Writes the report supplied to Settl/Systemate to create missing hourly time series. Both columns come from Xellent's reading periods (`EXU_TIMEREADINGSPERIOD`): hourly values are converted to the reading period's resolution, so Settl receives what the table says. A metering point is listed when Settl has no hourly series for an hourly reading period in the post window; a switch alone does not need a row when the series already exist.

`ChangeDate` is the start of the first 15-minute reading period after an hourly one, inside the post window, or the delivery start when there is no such switch. `MissingSeries` is the reading-period resolutions in the window that Settl has no series for (the values' resolutions when Xellent records no reading period there). `post` blocks on the same rule (`blocked-missing-hour-series-in-settl.csv`, `blocked-missing-quarter-series-in-settl.csv`). `XellentResolutions` describes the stored values.

Read-only. Already-posted meters count towards totals but receive no row; existing done marks and 6##### payer exclusions remain unchanged. Unchanged hourly periods before a dated quarter-hour reading period still require an hourly target.

A meter Settl states no delivery for (no delivery status has `InDelivery=true`, so the window rests on the current delivery status) is kept off the list too: its window isn't settled, so the switch date isn't either. Those meters go to `<out>-unclear-delivery.csv` next to the list, same columns, so they aren't lost — they are just not for Systemate yet.

```bash
dotnet run --project src/TimeSeries.Cli -- resolution-changes --env production --offline
```

| Option | Meaning |
|--------|---------|
| `--env <name>` | Which environment |
| `--offline` | Read Settl and Xellent from the snapshot instead of asking them |
| `--out <file>` | Where to write. Default `output/resolution-changes-<env>.csv` |

Columns: `MeteringPointId;Type;ChangeDate;XellentResolutions;SettlSeries;MissingSeries;WindowStart;WindowEnd` (`Type` is Parent or Child; WindowEnd = delivery start, not included). The dashboard's "Opløsningsskift" card lists a switch at any time, so it can have more meters.

## Other commands

For one metering point or account — troubleshooting and spot checks. These always read live (no snapshot).

```bash
# Post one metering point / one account
dotnet run --project src/TimeSeries.Cli -- post --metering-point 571313135300627988 --env featuretest --dry-run
dotnet run --project src/TimeSeries.Cli -- post --account 405013 --env featuretest --dry-run

# Extract to JSON files on disk (output/), nothing posted
dotnet run --project src/TimeSeries.Cli -- extract --metering-point 571313135300627988 --start 2024-01-01 --end 2025-01-01
dotnet run --project src/TimeSeries.Cli -- extract --account 405013
```

| Option | Meaning |
|--------|---------|
| `--metering-point <id>` / `--account <number>` | Pick one |
| `--env <name>` | As for `post --all`; for `extract` it picks the Xellent database. Default `featuretest` |
| `--start <yyyy-MM-dd>` | Window start, included. Default: today − 3 years (`TimeSeries:DefaultYears`) |
| `--end <yyyy-MM-dd>` | Window end, **not included**. Default: today |
| `--delivery-category <name>` | Default `El-ekstern` |
| `--simulate-missing` | `extract` only: fill gaps with simulated values |

`post` in these modes also takes `--dry-run`, `--force`, `--skip-if-existing-points` and `--skip-resolution-change`. It drops points before the metering point's delivery boundary in Settl, and skips a metering point with none left.

## Configuration

Environments are: `featuretest`, `production`, `production-hev`, `production-vhel`. Selected via `--env <name>` (default `featuretest`).

`appsettings.json` ships with `BaseUrl` for each environment + a default `ConnectionStrings:XellentDatabase` (AXDB50, used by all `production*` envs); `featuretest` overrides Xellent to AXDB50_TEST. Credentials are stored per-environment under `SettlApi:Environments:{env}:*`.

For local dev, set credentials via user-secrets (mirrors the deployed shape):

```bash
cd src/TimeSeries.Cli

dotnet user-secrets set "SettlApi:Environments:featuretest:SubscriptionKey" "..."
dotnet user-secrets set "SettlApi:Environments:featuretest:Credentials:ClientId" "..."
dotnet user-secrets set "SettlApi:Environments:featuretest:Credentials:ClientSecret" "..."
dotnet user-secrets set "SettlApi:Environments:featuretest:Credentials:Username" "..."
dotnet user-secrets set "SettlApi:Environments:featuretest:Credentials:Password" "..."
```

In CI/deploy, the pipeline writes the same paths into `appsettings.json` directly (staging deploy: `featuretest` only; production deploy: `production`, `production-hev`, `production-vhel`).


## Project Structure

- **TimeSeries.Cli** — the CLI (`snapshot`, `post`, `progress`, `resolution-changes`, `extract`)
- **TimeSeries.Migration** — the runner, Xellent extraction and the snapshot store; published as the `NewSettlement.TimeSeriesMigration` package (used by MigrationDashboard)
- **TimeSeries.Core** — shared DTOs; published as `NewSettlement.TimeSeriesMigration.Core`
- Xellent and Settl access come from the `NewSettlement.XellentDomain.*` and `NewSettlement.SettlClient.Api` packages

## Deploy

Target: `C:\Services\DataMigration\TimeSeriesCli\`

## Hourly to quarter-hour conversion (story 45902)

`post` automatically splits hourly values only inside Xellent's dated quarter-hour reading periods. Each hour becomes four equal values at UTC offsets 0, 15, 30 and 45 minutes. Earlier hourly periods and native quarter-hour values keep their resolution. Extraction and source storage keep the original values.

All included converted values and required target series are checked before any point or externally-invoiced-period write for that meter. An inexact five-decimal split fails the whole meter, with the timestamp and value reported; `--force` retains its existing continue-with-failures behaviour. Settl series must already exist. The feature applies to future sends only and does not repair previous posts or reset done marks.

Refresh the snapshot to populate `xellent-point-counts-v3.jsonl` before offline reports. Older count files cannot establish conversion readiness. Offline commands retain their documented fetch-on-miss behaviour. Done marks are preserved.

Run headers and delivery-filter counts describe source points; segment POST lines and `RESULT MP` / run totals describe outgoing points. `--skip-resolution-change` tests outgoing resolutions, so a mixed Hour/Quarter window is still skipped and a fully converted Quarter window is not.
