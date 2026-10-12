# TimeSeriesMigration — Codex instructions

Read [workspace instructions](../AGENTS.md) for safety, verification, branches, and producer/consumer package flow, including when this repo is opened directly. Read [the knowledge entry point](docs/ai/start.md) before changing the relevant migration behaviour.

- Keep changes focused and preserve existing work, including `plans/`. Plans belong there and should use concise bullets.
- Production posts require `--dry-run` unless the user explicitly authorizes a production write. Use `--env featuretest` for hot verification. This CLI has no `staging` environment.
- Keep Xellent reads and Settl investigations read-only. Avoid personal data in logs and reports. On the first corporate network/VPN failure, stop and report it; do not retry in a loop.
- Xellent timestamps are UTC; Danish calendar boundaries are converted separately. Stored resolution determines posted resolution except story 45902: hourly values in dated quarter-hour reading periods are split only for sending. Follow the current knowledge entry point, not old guide examples.
- For code changes, build the solution, run both unit test projects, run the separate Xellent integration project when extraction or API mapping changes, then smoke-test the CLI. Commands and prerequisites are in the entry point.
- Keep these instructions short. Put validated domain knowledge in shared docs and link it here. `CLAUDE.md` remains Claude's setup; its historical tool names, deleted paths, and package names are not current Codex configuration.
- Leave changes uncommitted unless the user asks to commit; commits go to `develop`.
