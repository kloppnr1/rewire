# Production runbook: TimeSeries.Cli and OpsCli

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

It only reads from Settl. It writes `output\MeteringPointMeters_V8_production_<date>_correction.xlsx`, which is checked and then imported into Settl by hand. Next to it are `..._correction_backup.csv`, every meter entry as Settl held it before the import (keep it), and `..._correction_metertype.csv`, the MeterType to set by hand in Settl after the import, because the template cannot carry it.

## OpsCli from rewire

The `DataMigration` Azure DevOps pipeline deploys OpsCli from that repository, so it does not carry changes made in rewire. Until rewire has its own pipeline, deploy OpsCli by hand the same way as TimeSeries.Cli. The server's `C:\Services\DataMigration\OpsCli\appsettings.json` holds the production Xellent and Settl settings the pipeline wrote; it is never overwritten.

### 1. Build

On a workstation with the VPN connected, from the repository root, on the branch or commit to deploy:

```powershell
git pull
dotnet test rewire.slnx
dotnet publish DataMigration\src\DataMigration.OpsCli -c Release -r win-x64 --self-contained true -o publish\OpsCli
Remove-Item publish\OpsCli\appsettings.json
```

Stop if a test fails. The last line keeps the build from replacing the server's settings.

### 2. Copy to the server

On `SVDKRA1VBIFROST`, first keep the current version, then copy the build over it:

```powershell
Remove-Item C:\Services\DataMigration\OpsCli-previous -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item C:\Services\DataMigration\OpsCli C:\Services\DataMigration\OpsCli-previous -Recurse
```

Copy the contents of `publish\OpsCli` into `C:\Services\DataMigration\OpsCli`, overwriting what is there. No OpsCli command may be running while you copy, including a scheduled `settl-export.ps1` run.

If the change edits `DataMigration\src\DataMigration.Migration\appsettings.json` (OpsCli uses that file), make the same edit by hand in the server's file. Leave the credential values as they are.

### 3. Check

```powershell
cd C:\Services\DataMigration\OpsCli
.\DataMigration.OpsCli.exe --help
```

It lists the commands. To go back: copy `C:\Services\DataMigration\OpsCli-previous` over `C:\Services\DataMigration\OpsCli`.

MigrationCli (`create`) still deploys only through the `DataMigration` pipeline. See [DataMigration/README.md](../DataMigration/README.md) for the commands.
