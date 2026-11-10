# Ported from NewSettlement.MigrationOps

This project was moved from `NewSettlement.MigrationOps/src/DataMigration.OpsCli/` to `NewSettlement.DataMigration/src/DataMigration.OpsCli/` on 2026-04-14.

## Why the move

OpsCli was the only MO consumer of `DataMigration.Migration` library, and the library had drifted ~3000 lines between MO's stale copy and DM's actively-refactored copy. Moving OpsCli to DM eliminates the drift by source: DM's `Migration` is now the only copy. MO's `Migration` and `SettlApi` libraries were deleted as part of the move (no remaining MO consumers).

## Git history

Filer blev kopieret friskt; blame peger på porteren fra `2026-04-14` for hele træet. Original git-historik bevaret i MO's `feat/move-opscli-to-dm` branch, commit-range `52255c2..<move-commit>`.

`git filter-repo`-baseret historik-bevaring blev valgt fra for at holde PR-størrelsen håndterbar. Når blame-arkæologi er nødvendig:

```bash
git -C <path-to-MigrationOps> log --all --oneline -- src/DataMigration.OpsCli/<file>
```

## Rename at port time

- `DataMigration.Migration.Runners.MigrationReport` (MO) collided with DM's existing `MigrationReport` class in `CreateRunner.cs`. MO's class renamed to `DataMigration.OpsCli.Reporting.OpsMigrationReport`. `MigrationReportGenerator` similarly renamed to `OpsMigrationReportGenerator`.
- `DataMigration.Migration.Runners.AccountGrouping` namespace moved to `DataMigration.OpsCli.Reporting`. `AccountGrouping` was MO-only — no DM equivalent, no rename needed.

## Related planning docs

- `docs/post-migration-fixes.md` — pattern reference for authoring `fix-*` commands (also moved from MO).
- `.claude/plans/modular-imagining-deer.md` (in `NewSettlement.MigrationOps`) — full move plan and findings.
