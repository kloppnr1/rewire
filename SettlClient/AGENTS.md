# SettlClient instructions

Follow the [workspace instructions](../AGENTS.md), especially production safety,
verification, `develop` commits, and the CI-only producer-to-consumer package flow.
Leave changes uncommitted unless the user asks for a commit.

Read [the repo knowledge entry point](docs/ai/start.md) before changing API routes,
request models, migration translators, or public interfaces. Keep shared contract
knowledge there and link to its source instead of duplicating it here.

This repo owns the shared Settl REST client and migration orchestrator. Preserve
unrelated fields and entity links when updating Settl; request shapes and supported
versions differ by operation. Resolve the documented metering-point PUT/PATCH
conflict before relying on a master PUT for a new operation.
