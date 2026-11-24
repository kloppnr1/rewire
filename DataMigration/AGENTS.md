# DataMigration — Codex instructions

Read the [workspace instructions](../AGENTS.md) for safety, verification,
branch and package rules. Read [shared project knowledge](docs/ai/start.md)
before changing extraction, account lookup, migration state or CLI behaviour.

- Treat the current CLI registrations and source as evidence. `CLAUDE.md` is
  historical input; several command, project and resolver claims are outdated.
- Translate Xellent accounts through representative mapping before Settl
  customer lookup. Fingerprint merging is the default; TG4 merging is opt-in.
- OpsCli includes write commands. Start with a non-writing preview, check the
  source evidence and require explicit user escalation for production writes.
- Preserve the migration-state JSON contract for downstream consumers.
- Build, unit-test, run relevant integration projects explicitly, then smoke-test
  the real CLI for code changes. Integration projects are outside the solution.
- Record runtime behaviour changes in the root changelog; skip docs-only work.
- Leave commits and release timing to the user. User-requested commits in this
  repo go directly to master; direct pushes to master are allowed.
