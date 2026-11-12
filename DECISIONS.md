# Decisions

Choices made where PRD.md is silent or where the environment forced a deviation. One line of reasoning each (PRD rule 1).

## Deviations from the PRD (agreed with Andrew, 2026-11-12)

| # | PRD says | We do | Reason |
| --- | --- | --- | --- |
| D1 | .NET 10 (LTS) | Target `net9.0` | Only SDK 9.0.119 is installed; Andrew chose to target .NET 9 rather than install .NET 10. Authzed.Net targets net8.0+, so it is compatible. Moving to net10.0 later is a `TargetFramework` change. |
| D2 | SpiceDB in Docker, Testcontainers, Compose | Native `spicedb` binary (memory datastore) for Phases 1-4 | Docker is not installed in this WSL environment. Andrew chose a native binary for now. |
| D3 | Phase 5 Playwright against Compose; Phase 6 "`docker compose up` from a clean clone" | **Blocked until Docker is available** | Cannot be accepted without Docker. `docker-compose.yml` can be written and reviewed but not run. Phases 5-6 stay open on those criteria. |

The integration test fixture will sit behind an interface so it can start a native SpiceDB process now and swap to Testcontainers later without changing tests.

## Tool versions (checked against upstream on 2026-11-12, per PRD rule 3)

| Tool | PRD cites | Latest stable | Pinned | Notes |
| --- | --- | --- | --- | --- |
| SpiceDB | v1.54.0 (pkg.go.dev link) | v1.57.1 (2026-11-10) | v1.57.1 | Installed to `~/.local/bin/spicedb`; sha256 verified against the release `checksums.txt`. |
| zed | "zed 1.0" | v1.2.1 | v1.2.1 | Installed to `~/.local/bin/zed`; sha256 verified. |
| Authzed.Net | 1.5.0 | 1.6.0 | 1.6.0 (to confirm when the adapter is built) | net8.0 target; depends on Grpc.Net.Client 2.80.0 and Google.Protobuf 3.34.1. API names are still unverified against a compiled build. |

## Environment

- git repository initialised in place on `main`.

## Phase 1 decisions (PRD silent)

- **SpiceDB telemetry off.** SpiceDB reports usage to `telemetry.authzed.com` by default, and checks for new releases. Every invocation (native script, Compose) passes `--telemetry-endpoint=` and `--skip-release-check=true`. A proof of concept should make no unrequested outbound calls.
- **No Compose healthcheck on SpiceDB.** `spicedb` has no health-probe subcommand and I have not confirmed one is available in the image. The API waits for SpiceDB at start-up (PRD), so a container healthcheck is not needed.
- **Compose holds only SpiceDB in Phase 1.** `api`, `issuer` and `web` services need Dockerfiles that arrive in Phases 4-6; adding them now would break `docker compose config`. The file binds SpiceDB to 127.0.0.1 only.
- **`verify.sh` reports skipped stages explicitly.** A skip is listed in the summary and is not a pass. Skips for .NET and web stages are removed as those phases land, at which point their absence becomes a failure.
- **Matrix presence guard.** `verify.sh` checks that each of the 70 PRD matrix cells is asserted by name, because a count threshold let a thinned matrix pass. Values are checked by `zed validate`.
- **Extra assertions beyond the matrix.** `validation.yaml` adds 18 assertions (9 true, 9 false) under separate comments, covering group membership, nested groups, support reaching a subscription, wallet `create_subscription`, customer `delete` being platform-admin only, and cross-customer isolation. They are derived from the schema's stated rules and do not alter any PRD matrix cell.
- **`validation.yaml` is generated once, then hand-owned.** The generator was a throwaway script; the YAML is the source of truth from here.
- **Validator confirmed able to fail.** Flipping a matrix assertion and weakening a schema rule each made `zed validate` and `verify.sh` exit 1 (tested on scratch copies).
- **Native SpiceDB helper.** `scripts/spicedb-local.sh start|stop|status` runs the pinned binary with the memory datastore; it is the stand-in for Compose (D2).
