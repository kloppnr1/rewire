# Development and package ownership

Read the [workspace rules](../../../AGENTS.md) for production access, verification,
branching, and the producer-to-consumer publication workflow. For business
questions, return to [the topic router](start.md). These implementation facts were
checked against current source on 2026-10-02; recheck affected source before edits.

## Ownership and public API changes

This repo owns six NuGet packages. Source project and namespace names remain
`DataMigration.*`; they are not duplicate consumer implementations.

| Source project under `src/` | Package |
|---|---|
| `DataMigration.Core` | `XellentDomain.Core` |
| `DataMigration.Domain` | `XellentDomain.Domain` |
| `DataMigration.Domain.Engros` | `XellentDomain.Domain.Engros` |
| `DataMigration.Domain.TimeSeries` | `XellentDomain.Domain.TimeSeries` |
| `DataMigration.Domain.Xellent` | `XellentDomain.Domain.Xellent` |
| `DataMigration.XellentData` | `XellentDomain.XellentData` |

The source projects' `.csproj` files define `PackageId`.
[Directory.Build.props](../../Directory.Build.props) defines `VersionPrefix` and
`VersionSuffix`. No CI pipeline packs or publishes these libraries.

Before changing or removing a public type or signature, search current consumer
source and report affected callers. Consumers are DataMigration, MigrationDashboard,
TimeSeriesMigration, SettlClient, and CustomerSupport. Search `.csproj` and
`Directory.Packages.props` for package references and source for the affected
symbols; exclude temporary publish copies and build output. All five currently
use the `0.8.*-*` series, including SettlClient. A series change requires consumer
reference changes; floating references do not cross series.

Consumer edits belong to their repo's expert. Consumer compilation and runtime
verification require the producer package to be published by CI first. Report
that dependency plainly; local source review alone does not verify consumers.
Do not use local packages as a substitute for publication.

## SQL names and date sentinels

Use the mapped `[Table]` and `[Column]` attributes in
[Xellent EF entities](../../src/DataMigration.XellentData/Entities/) for SQL names.
A C# property name may omit underscores: for example,
[`ExuDelpoint.DelParentdelpointnum`](../../src/DataMigration.XellentData/Entities/ExuDelpoint.cs)
maps to `DEL_PARENTDELPOINTNUM`. Do not infer SQL names from property spelling.
An EF key declaration does not prove a live database constraint.

Use [MigrationConstants](../../src/DataMigration.Core/MigrationConstants.cs)
instead of introducing another definition:

- `EndDateNull` is `1900-01-01` (contracts, contract parts, meter types).
- `EndDateFuture` is `2154-12-31` (billing methods, time series versions).
- `MeteringPointEndDate` is the separate Settl association sentinel `9999-12-31`.

Check each field's meaning in the [field reference](../xellent-field-semantics.md)
and source queries. Those values are not interchangeable and do not establish
whether a dated record is active by themselves. The current shared definitions
are here; references to multiple definitions in older documentation are historical.

## Verify code changes

From this repo root, build and run the four unit test projects in
[the solution](../../XellentDomain.slnx):

```powershell
dotnet build XellentDomain.slnx
dotnet test XellentDomain.slnx --no-build
```

The projects under `src/Tests/` are `DataMigration.Core.Tests`,
`DataMigration.Domain.Tests`, `DataMigration.Domain.Engros.Tests`, and
`DataMigration.Domain.Xellent.Tests`. There is no live-Xellent integration suite
or production mutation CLI in this solution. Changes to shared extraction or
mapping also require relevant consumer integration coverage under the workspace
rules; DataMigration owns its extraction integration projects. Do not imply
that unit tests or source inspection verified live data or a consumer host.
