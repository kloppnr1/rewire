# Production runbook: TimeSeries.Cli

Production runs only on the production server, `SVDKRA1VBIFROST`. The CLI lives in `C:\Services\DataMigration\TimeSeriesCli`. The `appsettings.json` in that folder holds the production Settl credentials. It is never overwritten, and production credentials are kept nowhere else.

## 1. Build

On a workstation with the VPN connected, from the repository root:

```powershell
git pull
dotnet test rewire.slnx
dotnet publish TimeSeriesMigration\src\TimeSeries.Cli -c Release -r win-x64 --self-contained true -o publish\TimeSeriesCli
Remove-Item publish\TimeSeriesCli\appsettings.json
```

Stop if a test fails. The last line removes the credential-free `appsettings.json` from the build, so it cannot replace the server's file.

## 2. Copy to the server

Copy the contents of `publish\TimeSeriesCli` into `C:\Services\DataMigration\TimeSeriesCli` on the server, overwriting what is there. The server keeps its own `appsettings.json`.

If the change being deployed edits `TimeSeriesMigration\src\TimeSeries.Cli\appsettings.json`, make the same edit by hand in the server's file. Leave the credential values as they are.

## 3. Run on the server

Open PowerShell on the server, in the CLI folder. Output files land in `output\` below it.

```powershell
cd C:\Services\DataMigration\TimeSeriesCli
.\TimeSeries.Cli.exe <command> --env production --dry-run ...
```

- Always pass `--env`. Without it the CLI uses `DOTNET_ENVIRONMENT`, and only then `featuretest`.
- Commands that write to Settl (`post`) run with `--dry-run` first. Read the output, spot-check two or three metering points, and only then run again without `--dry-run`.
- `--offline` is not a safety net. It only changes where reads come from. Only `--dry-run` stops writes.
- Read-only commands, such as `switch-import`, need no `--dry-run`. `.\TimeSeries.Cli.exe <command> --help --env production` shows what a command does and whether it writes.

Other environments: `production-hev` and `production-vhel`, same rules. `featuretest` is the test environment; there is no `staging`.

## Example: the switch-import correction

```powershell
cd C:\Services\DataMigration\TimeSeriesCli
.\TimeSeries.Cli.exe switch-import --env production --correct --in output\resolution-switches-production-imported.csv
```

It only reads from Settl. It writes `output\MeteringPointMeters_V8_production_<date>_correction.xlsx`, which is checked and then imported into Settl by hand.

## The other CLIs

MigrationCli and OpsCli reach the production server through their original repository, `NewSettlement.DataMigration`, and its Azure DevOps pipeline. See [DataMigration/README.md](../DataMigration/README.md) for their commands.
