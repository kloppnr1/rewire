# NewSettlement.TimeSeriesMigration

Moves metering point time series from Xellent to Settl: reads the points from Xellent, posts them to Settl, and registers an externally invoiced period per metering point.

Run everything from the repo root with `dotnet run --project src/TimeSeries.Cli -- <command> [options]`. `--help` works on every command. Examples use `featuretest`; **production is always run with `--dry-run`**.

## Migrating time series (the main workflow)

```bash
# 1. Read what the run needs from Settl and Xellent once, and keep it on disk
dotnet run --project src/TimeSeries.Cli -- snapshot --env featuretest

# 2. See what would be posted — from the snapshot, nothing posted
dotnet run --project src/TimeSeries.Cli -- post --all --env featuretest --offline --dry-run --force

# 3. Look at the last log line and the skip files (output/*.csv), then post for real, 100 at a time
dotnet run --project src/TimeSeries.Cli -- post --all --env featuretest --offline --limit 100

# 4. Run the snapshot again to mark what is now in Settl as done; the next post leaves it out
dotnet run --project src/TimeSeries.Cli -- snapshot --env featuretest

# 5. Print the migration coverage from that snapshot (no live reads)
dotnet run --project src/TimeSeries.Cli -- coverage --env featuretest
```

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
- **From Xellent:** child relations and contracts.

Time series points are **not** stored. A batch dry run asks Xellent for counts per dated segment and checks whether hourly values can be split exactly; it reads only the first offending value when validation fails. The seed now counts them for every metering point it would post and keeps the counts (`xellent-point-counts-v3.jsonl`), so `readiness`, `resolution-changes` and offline dry runs read them back instead of counting in Xellent again. That is the slowest part of a seed; a run that only needs the Settl reads is done before it. A live run reads the points fresh. Neither Settl nor Xellent changes while the migration runs, so one snapshot serves many runs.

| Option | Meaning |
|--------|---------|
| `--env <name>` | Which environment to read. One snapshot per environment |
| `--parallel <N>` | Settl reads per metering point, N at a time. Default 6 |
| `--refresh` | Throw the stored reads away and read everything again. Done marks are kept |
| `--include-children` | Take child metering points along. **On by default** — the snapshot is a cache, so it then serves a `post --all` with or without children. `--include-children false` for parents only |

- Running it again keeps what is stored and only fills in what is missing (an interrupted seed resumes), then runs the done check again. So to mark new done metering points on an existing snapshot, just run `snapshot` again.
- A failed read is listed at the end and the command exits non-zero; run it again to retry.
- Progress of the Settl reads and the done check shows as one line that updates in place (a line every 1,000 when the output goes to a file).
- Featuretest (2,219 metering points): about 1.5 minutes, 2.4 MB. Production (61,074): 305,381 reads, a few hours.
- On featuretest a full offline dry run then takes about 1 minute the first time (Xellent counts) and a few seconds after that.

### `post --all`

Settl decides which metering points are posted; Xellent decides whose they are.

1. Every electricity metering point in Settl's list. Parents only, unless `--include-children`.
2. Per metering point, the delivery start comes from its delivery statuses. Settl can fail to mark delivery (2026-09), so metering points shown as not in delivery are checked too, using the start of their current delivery status.
3. The account is whoever held the metering point in Xellent (`El-ekstern` contracts) on the day before delivery starts. That relationship must be closed as a migration.
4. Window: `end` = delivery start, `start = max(end − 3 years, delivery boundary, the account's own contract start)`. Before posting, `end` must match the delivery start on the metering point in Settl.
5. With `--include-children`, child metering points (Settl relation or the Xellent relation table) are taken along. A child uses its own delivery status and its own Xellent contract when it has them. When it has neither, the window and the account come from its parent, and the window end is checked against the parent too. What gets posted is the child's own time series, to the child's own Settl GUID.

| Option | Meaning |
|--------|---------|
| `--env <name>` | `featuretest` (default), `production`, `production-hev`, `production-vhel` |
| `--dry-run` | Resolves everything but posts nothing |
| `--offline` | Read Settl and Xellent from the snapshot instead of asking them again. Anything missing is read live and added. Points are read fresh; posts always go to Settl |
| `--force` | Keep going past a failing metering point (the run still exits non-zero). Without it the run stops at the first failure |
| `--cos-from` / `--cos-to <yyyy-MM-dd>` | Only metering points whose delivery in Settl starts in this range (Danish dates, both included) |
| `--skip <N>` / `--limit <N>` | Pass over the first N metering points in range, then stop after N more, in identification order. Metering points already done don't count. With done marks, `--limit` alone is usually enough |
| `--skip-if-existing-points` | Skip a metering point if Settl already has points in its window |
| `--include-children` | Take child metering points along. Off by default: a run without it is parents only |
| `--skip-resolution-change` | Skip a metering point that changes resolution in the window (typically Hour → Quarter): in its series, or in what Xellent says it was read at |

What happens to each metering point:
- **Posted:** points to Settl, then an externally invoiced period with `BillableTypes=["Contracts", "ExtraInvoiceLines", "MarketPriceLists", "NationalContracts", "PaymentFee", "MovingFee"]`.
- **Failed** (Settl marks it in delivery, but Xellent shows no clean migration, or a safety check fails): logged as `ERROR`.
- **Skipped** to a CSV file (below). Metering points Settl shows as not in delivery count as "never delivered" if they have no delivery start, and as "not migrated" if Xellent shows no migration.

The last two log lines give the counts, for example: `Run completed: 2219 metering points in Settl, 2148 processed, 601 posted, 71 never delivered, 1426 not migrated, …` and, under it, `Parents/children: 2702 (2600 parents, 102 children) already done, 2148 (1500 parents, 648 children) processed, …` with the same counts split by kind, the done ones first. Failures skipped with `--force` are split the same way on their own line. With `--offline` a line before it says how many reads came from the snapshot and how many were read live.

### Skip files

Written to the output folder (`output/`), one file per reason; a file only appears if something was skipped. Every CSV this tool writes opens straight in Excel here: UTF-8 with BOM, semicolon between fields, every field quoted, so metering point numbers stay text.

Each row carries a `Type` column — Parent or Child, empty where it isn't known (a post by account or metering point rather than `--all`). The skip lines in the log end the same way, `… (parent)` or `… (child)`.

| File | Reason |
|------|--------|
| `skip-payer-6-series.csv` | The metering point's current payer in Settl is in the 6##### series (six digits starting with 6); those are not migrated. Left out before `--skip` / `--limit` count |
| `skip-missing-in-xellent.csv` | Nobody held the metering point in Xellent the day before delivery |
| `skip-not-migrated.csv` | Not in delivery in Settl, and no migration in Xellent |
| `ambiguous-revisions.csv` | More than one `InDelivery=true` delivery status |
| `skip-delivery-boundary-unresolved.csv` / `-ambiguous.csv` / `-invalid.csv` | The not-in-delivery period before delivery can't be pinned down |
| `skip-no-points-after-boundary.csv` | Every point falls before the delivery boundary |
| `skip-existing-settl-data.csv` | Settl already has points (`--skip-if-existing-points`) |
| `skip-resolution-change.csv` | Resolution change (`--skip-resolution-change`): included outgoing values use more than one resolution after dated conversion and delivery filtering |
| `known-issue-xellent-resolution.csv` | Known Xellent issue: values stored at a different resolution than Xellent says the meter was read at (e.g. hourly values where it reads every 15 minutes), with no series for the right resolution. The source mismatch remains visible here. Hourly values in dated quarter-hour reading periods are split into four equal outgoing values; source values remain unchanged |

## `readiness`

One row per metering point a `post --all` would send: which resolutions its values are in, which time series Settl has for it, what is missing, and a status. Read-only, and it creates nothing in Settl — the missing series are the list for Systemate.

```bash
dotnet run --project src/TimeSeries.Cli -- readiness --env production --offline
```

| Option | Meaning |
|--------|---------|
| `--env <name>` | Which environment |
| `--offline` | Read Settl and Xellent from the snapshot instead of asking them |
| `--out <file>` | Where to write. Default `output/readiness-<env>.csv` |
| `--include-children` | Take child metering points along, matching `post --all --include-children` |

Columns: `MeteringPointId;Type;SettlId;Account;WindowStart;WindowEnd;ValueResolutions;ValuePoints;SettlSeries;MissingSeries;Status;Reasons;Detail`. `Type` is Parent or Child.

`ValueResolutions` and `ValuePoints` describe outgoing data after conversion. Each hourly source value within a quarter-hour reading period counts as four 15-minute points. `ConversionError` blocks a metering point whose included values cannot be split exactly within five decimal places. `Reasons` says `XellentResolutionMismatch` when the meter is one of those cases.

Every row gets a status, including the ones with nothing wrong:

| Status | Meaning |
|--------|---------|
| `Ready` | Values and Settl series match; it can be sent |
| `MissingHourSeries` / `MissingQuarterSeries` / `MissingSeries` | Settl has no series for a resolution the values are in |
| `ResolutionChange` | The values are in both resolutions inside the window |
| `NoValues` | The meter has no values in the window |
| `NotInstalled` | No values, and Xellent has no meter at the address yet (meter point status "Oprettet") — nothing to wait for |
| `SeriesWithoutAgreement` | Xellent has values with no agreement period to put them in |

The run ends with a count per status.

## `coverage`

Prints one read-only migration summary from the existing environment snapshot. It makes no Settl or
Xellent calls. Completed metering points come from `done.jsonl`; every remaining migration candidate
is classified with the same value/series rules as `readiness`.

```bash
dotnet run --project src/TimeSeries.Cli -- coverage --env production
dotnet run --project src/TimeSeries.Cli -- coverage --env production --by-account
dotnet run --project src/TimeSeries.Cli -- coverage --env production --json
```

The report reconciles every unique metering point in the Settl snapshot to either the migration scope
or an explicit exclusion reason. Exclusions include children when they were not requested, 6##### payers,
never-delivered and not-migrated points, missing or ambiguous Xellent ownership, delivery-revision safety
skips, and migration/delivery validation failures. It then shows completed coverage, ready-to-migrate and
blocked totals, remaining readiness statuses, parent/child totals, and fully completed account totals.
Run `snapshot` first to refresh the underlying data; an incomplete snapshot causes the command to fail
instead of reading live.

| Option | Meaning |
|--------|---------|
| `--env <name>` | Which environment snapshot to read |
| `--json` | Write the full report as machine-readable JSON, including every account |
| `--by-account` | Print incomplete accounts with completed, ready and blocked totals |
| `--account-limit <N>` | Maximum incomplete accounts to print (default 50; `0` shows all) |
| `--include-children` | Include child metering points, matching `post --all --include-children` |

## `resolution-changes`

Writes the report supplied to Settl/Systemate to create missing hourly time series. Include only meters whose outgoing data still contains hourly values and whose Settl setup has no hourly series. Fully converted windows need no hourly series and are omitted; a switch alone does not require a row when the target series already exist.

Existing dates and columns are retained: `ChangeDate` is the first native quarter-hour point following hourly source values, or the delivery start when no such source switch exists. `XellentResolutions` describes stored source resolutions; `MissingSeries` is checked against outgoing resolutions after conversion.

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
| `--include-children` | Take child metering points along, matching `post --all --include-children` |

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
| `--include-children` | Also child metering points (Xellent relation table) |
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

- **TimeSeries.Cli** — the CLI (`snapshot`, `coverage`, `readiness`, `resolution-changes`, `post`, `extract`)
- **TimeSeries.Migration** — the runner, Xellent extraction and the snapshot store; published as the `NewSettlement.TimeSeriesMigration` package (used by MigrationDashboard)
- **TimeSeries.Core** — shared DTOs; published as `NewSettlement.TimeSeriesMigration.Core`
- Xellent and Settl access come from the `NewSettlement.XellentDomain.*` and `NewSettlement.SettlClient.Api` packages

## Deploy

Target: `C:\Services\DataMigration\TimeSeriesCli\`

## Hourly to quarter-hour conversion (story 45902)

`post` automatically splits hourly values only inside Xellent's dated quarter-hour reading periods. Each hour becomes four equal values at UTC offsets 0, 15, 30 and 45 minutes. Earlier hourly periods and native quarter-hour values keep their resolution. Extraction and source storage keep the original values.

All included converted values and required target series are checked before any point or externally-invoiced-period write for that meter. An inexact five-decimal split fails the whole meter, with the timestamp and value reported; `--force` retains its existing continue-with-failures behaviour. Settl series must already exist. The feature applies to future sends only and does not repair previous posts or reset done marks.

Refresh the snapshot to populate `xellent-point-counts-v3.jsonl` before offline reports. Older count files cannot establish conversion readiness. Strictly offline `coverage` reports missing v3 evidence instead of reusing v2 counts; other offline commands retain their documented fetch-on-miss behaviour. Done marks are preserved.

Run headers and delivery-filter counts describe source points; segment POST lines and `RESULT MP` / run totals describe outgoing points. `--skip-resolution-change` tests outgoing resolutions, so a mixed Hour/Quarter window is still skipped and a fully converted Quarter window is not.
