# Decisions

Choices made where PRD.md is silent or where the environment forced a deviation. One line of reasoning each (PRD rule 1).

## Deviations from the PRD (agreed with Andrew, 2026-11-12)

| # | PRD says | We do | Reason |
| --- | --- | --- | --- |
| D1 | .NET 10 (LTS) | Target `net9.0` | Only SDK 9.0.119 is installed; Andrew chose to target .NET 9 rather than install .NET 10. Authzed.Net targets net8.0+, so it is compatible. Moving to net10.0 later is a `TargetFramework` change. |
| D2 | SpiceDB in Docker, Testcontainers, Compose | Native `spicedb` binary (memory datastore) for Phases 1-4 | Docker is not installed in this WSL environment. Andrew chose a native binary for now. |
| D3 | Phase 5 Playwright against Compose; Phase 6 "`docker compose up` from a clean clone" | **Blocked until Docker is available** | Cannot be accepted without Docker. `docker-compose.yml` can be written and reviewed but not run. Phases 5-6 stay open on those criteria. |
| D4 | "Lookups page through the cursor until exhausted" (Ports) | `LookupResources` pages through the cursor; `LookupSubjects` reads one unpaged stream | SpiceDB v1.57.1 rejects `optional_concrete_limit` on LookupSubjects (`Unimplemented: concrete limit is not yet supported`), so cursor paging cannot be used. Verified directly against the engine: unpaged LookupSubjects returned all 1500 of 1500 subjects (no 1000 cap, unlike LookupResources). Raised under PRD rule 2 and agreed by Andrew 2026-11-12. Revisit if a permission could have tens of thousands of holders or when the engine adds concrete limits. |

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

## Phase 2 decisions (PRD silent)

- **Solution shape.** `Poc.sln` with `src/Poc.{Domain,Application,Infrastructure}` and `tests/Poc.{UnitTests,IntegrationTests}`. `Directory.Build.props` sets `net9.0` (D1), nullable, implicit usings, warnings as errors and `AnalysisLevel=latest-recommended` for every project. Architecture tests live in `Poc.UnitTests/Architecture` because the PRD layout has only two test projects.
- **Extra port `ISchemaStore`.** The PRD lists a "schema writer" in Phase 2 and a health probe that reads the schema, but names no interface. `ISchemaStore { WriteAsync, ReadAsync }` returns `null` from `ReadAsync` when the engine holds no schema (SpiceDB answers `NotFound`), which is the signal for the schema-loss recovery in Phase 6.
- **One exception for every engine failure.** Every `RpcException` (unreachable, deadline, bad key, rejected request, per-item bulk error) becomes `AuthorizationUnavailableException` carrying the original as `InnerException`. The PRD names only "failure"; collapsing them guarantees there is no branch that can fall through to allow. Caller cancellation stays `OperationCanceledException`.
- **Wrong preshared key returns `PermissionDenied`** (observed on v1.57.1), not `Unauthenticated`. Tests assert the observed code.
- **Conditional results count as denied.** The schema defines no caveats, so none are expected; if one ever appears it fails closed.
- **`LookupResources` page size defaults to 500**, below the engine's default 1000-result cap (`--max-lookup-resources-limit`). The option is validated to 1..1000. The cap is real: a 1200-resource lookup is a test, and the pager is mutation-tested (stopping after page one fails 3 tests).
- **Delete loops over partial deletions** (1000 per request, partial allowed) until the engine reports `Complete`, and throws if a partial response deletes nothing, so it cannot spin.
- **`RelationshipFilter` requires a resource type** so an unconstrained delete cannot be built by accident. Start-up wipes one object type at a time.
- **Empty `WriteAsync` throws `ArgumentException`.** There is no ZedToken to return for no write; callers (the projector) must not make one.
- **Plain HTTP to SpiceDB is refused unless `AllowInsecureTransport` is set**, so the preshared key is never sent in clear by accident. Local development and the Compose network set it.
- **Permission constants layout.** One `static class {Type}Schema` per object type in `Poc.Application.Authorization.Schema`, each with `Type` plus nested `Relations` and `Permissions` classes. A unit test parses `authz/schema.zed` and fails on any difference in either direction (mutation-tested both ways). The suffix avoids clashing with Domain aggregate names.
- **Observability groundwork.** Each engine call opens an `ActivitySource` span (`Poc.SpiceDb`), and each check logs subject, permission, resource, result and duration at debug level. No token is ever logged.
- **Integration test host.** `ISpiceDbHost` is implemented by `NativeSpiceDbHost` (free loopback port, memory datastore, telemetry off). Tests share one engine and isolate themselves with unique ids. A missing `spicedb` binary fails the run; it never skips. A Testcontainers host can replace it without touching tests (D2).
- **Test naming.** CA1707 (underscores in names) is disabled for `tests/` only, in `.editorconfig`, so test names can read as sentences.
- **Gate checks proved able to fail.** Each of these was broken on a scratch copy and made `verify.sh` exit 1: a failing unit test, a formatting violation, a constant missing from the schema, a schema permission with no constant, `Authzed.Net` referenced from Application, and a pager that stops early.
