# rewire

Four NewSettlement repos as folders of one .NET 9 solution, with their git history:

| Folder | What it is |
|--------|------------|
| `XellentDomain/` | Core, Domain and XellentData libraries |
| `SettlClient/` | Typed Settl HTTP client and API contracts |
| `DataMigration/` | MigrationCli (`create`) and OpsCli |
| `TimeSeriesMigration/` | Time series extract and Settl post (`TimeSeries.Cli`) |

The folders reference each other directly. A change in one folder reaches the others at the next build. Nothing is published as a NuGet package.

## Build and test

```powershell
dotnet build rewire.slnx
dotnet test rewire.slnx
```

Every test runs on a clean clone. No VPN, no Xellent, no Settl: the integration tests use an in-memory Xellent and a fake Settl.

## Running TimeSeries.Cli in production

Production runs only on the production server, `SVDKRA1VBIFROST`. The CLI lives in `C:\Services\DataMigration\TimeSeriesCli`. The `appsettings.json` in that folder holds the production Settl credentials. Never overwrite it, and never put production credentials anywhere else.

### 1. Build on your laptop

Connect the VPN. Then, from the rewire folder:

```powershell
git pull
dotnet test rewire.slnx
dotnet publish TimeSeriesMigration\src\TimeSeries.Cli -c Release -r win-x64 --self-contained true -o publish\TimeSeriesCli
Remove-Item publish\TimeSeriesCli\appsettings.json
```

Stop if a test fails. The last line removes the credential-free `appsettings.json` from the build, so it can't replace the server's file.

### 2. Copy to the server

Copy the contents of `publish\TimeSeriesCli` into `C:\Services\DataMigration\TimeSeriesCli` on `SVDKRA1VBIFROST`, overwriting what is there. The server keeps its own `appsettings.json`.

If the change you are deploying edits `TimeSeriesMigration\src\TimeSeries.Cli\appsettings.json`, make the same edit by hand in the server's file. Leave the credential values as they are.

### 3. Run on the server

Open PowerShell on the server, in the CLI folder. Output files land in `output\` below it.

```powershell
cd C:\Services\DataMigration\TimeSeriesCli
.\TimeSeries.Cli.exe <command> --env production --dry-run ...
```

- Always pass `--env`. Without it the CLI uses `DOTNET_ENVIRONMENT`, and only then `featuretest`.
- Commands that write to Settl (`post`) run with `--dry-run` first. Read the output, spot-check two or three metering points, and only then run it again without `--dry-run`.
- `--offline` is not a safety net. It only changes where reads come from. Only `--dry-run` stops writes.
- Read-only commands, such as `switch-import`, need no `--dry-run`. Run `.\TimeSeries.Cli.exe <command> --help --env production` to see what a command does and whether it writes.

Other environments: `production-hev` and `production-vhel`, same rules. `featuretest` is the test environment; there is no `staging`.

### Example: the switch-import correction

```powershell
cd C:\Services\DataMigration\TimeSeriesCli
.\TimeSeries.Cli.exe switch-import --env production --correct --in output\resolution-switches-production-imported.csv
```

It only reads from Settl. It writes `output\MeteringPointMeters_V8_production_<date>_correction.xlsx`, which you check and import into Settl by hand.

## The other CLIs

MigrationCli and OpsCli still reach the production server through their own repo, `NewSettlement.DataMigration`, and its Azure DevOps pipeline. See `DataMigration/README.md` for their commands.
