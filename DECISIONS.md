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

## Phase 3 decisions (PRD silent or ambiguous)

**Domain and persistence**
- **`CustomerDomain`, not `Domain`.** A type named `Domain` inside the `Poc.Domain` namespace is ambiguous with the namespace itself. The table is still `Domains` and the events are still `DomainCreated`/`DomainDeleted`.
- **Ids are validated** to `^[a-z0-9][a-z0-9_-]*$`, at most 128 characters (`EntityId`). Ids are used verbatim as database keys and tuple object ids with no translation layer (PRD), so a user-supplied id such as `a#member` or `*` must never reach a tuple. This is stricter than the engine's own rule, on purpose.
- **Audit columns.** `CreatedAt`, `CreatedBy`, `RowVersion`, `AuthzToken` are on Customers, Domains, Wallets, Subscriptions and Groups. `Users` is a directory, not a business table, and has none. `GrantAudit` gains a `SubjectRelation` column (`member` for `group#member` subjects), which the PRD's column list lacks but the tuple needs.
- **Foreign keys use Restrict.** FK failures arrive from SQLite as extended code 787 on insert but as plain error 19 ("FOREIGN KEY constraint failed") for `ON DELETE RESTRICT`; both map to `ConflictException`.
- **No application-level write gate on SQLite.** The PRD warns that shared-cache SQLite allows one writer at a time. Tested empirically: 40 concurrent writers + readers pass in the suite, and 300 + 300 passed five times in a row as an ad-hoc stress run, with no `database table is locked` errors (Microsoft.Data.Sqlite waits on SQLite's unlock notification). The rule "never hold a transaction across a SpiceDB call" is still observed.
- **`AuthzToken` is stamped with `ExecuteUpdate`,** so a stamp never bumps `RowVersion` and cannot conflict with a user's edit. A stamp failure after a successful grant is logged, not surfaced: the tuple is already written.
- **Reset deletes rows** (children first, identity counters reset) instead of dropping tables, so the keep-alive connection and schema are untouched.
- **Seed values the PRD leaves open:** product names, quantities and balances are invented; user emails use the reserved `zanzibar.example` domain.

**Outbox and projector**
- **Attempts count only failures a retry cannot fix.** The PRD says a message "that fails five times" is left unprocessed, but its own pause scenario (SpiceDB paused, then unpaused) needs messages to survive any outage. `AuthorizationUnavailableException.IsTransient` (gRPC `Unavailable` or `DeadlineExceeded`) distinguishes the two: an outage records `LastError`, stops the flush to keep order, and never increments `Attempts`; a rejection (bad request, bad key, unknown event) increments it. Tested with a 12-flush outage.
- **A failed message blocks later messages for the same object** (its `Target`), within the flush and afterwards; other objects proceed. A dead message (5 attempts) stays unprocessed and visible through `IOutboxInspector` for the System screen. There is no per-message retry yet; the System reset rebuilds both stores.
- **`Attempts` also counts the successful attempt,** so a processed message shows `Attempts = 1`.
- **Uninterpretable messages** (unknown type, bad payload) fail visibly like any other; they are never skipped.
- **Order within a flush** is sequence order; only one flush runs at a time (semaphore), and the start-up reset takes the same gate (`RunExclusiveAsync`) so the background worker cannot project into a store that is being rebuilt.
- **Default grant condition.** The PRD says "when the creator lacks `admin`". The policy grants `operator` when the creator holds neither `admin` nor `change_quantity`, which equals the PRD rule on first processing and makes a replayed message a no-op instead of writing a second audit pair. Seed and other system-made subscriptions get no default grant (there is no real creator).
- **Subscription creation by a non-admin spender** is the only creation with a default grant, as the PRD states.

**Access Management**
- **`Actor.System` is the only authorisation bypass** and exists for the seeder and the default-grant policy. It skips the engine permission check but not the service rules or the audit. `Actor.ForUser("system")` throws, so a token cannot claim it; the API will build actors only through `ForUser`.
- **Permission is checked before validation,** so an unauthorised caller learns nothing from a validation message. A user granting on a non-existent object gets 403, not 404, for the same reason; the system actor gets `NotFound`.
- **Revoking a platform group's grant is also restricted to platform administrators,** matching the PRD rule for granting. The PRD is silent on revoke; the conservative reading avoids a customer admin removing platform support access.
- **The cycle check is a relation check** (`group:B#member` with subject `group:A#member`), as the PRD specifies. It is the one place a relation is checked rather than a permission, because `group` has no permission equal to membership.
- **Group create/delete do not check permissions inside the service;** the endpoint filter does (`create_group` on the owner, `manage` on the group), as for every create and delete route. Grants and membership changes check internally, as the PRD's grant sequence requires.
- **Application has no logging dependency,** so grant/revoke logging goes through an `IAccessLog` port implemented in Infrastructure at information level (user ids and objects only).
- **The audit store uses its own `DbContext`,** so an audit write can never commit another command's half-finished changes.

**Reconciliation and start-up**
- **Grants are out of scope for reconciliation.** It covers parent and owner references only (PRD); a test pins that boundary.
- **Objects with unprocessed outbox messages are skipped** and counted in `SkippedPending`, because their tuples are in flight. The pending set is read before the expected set so an object created in between is skipped, not misreported.
- **Start-up waits for the engine** (500 ms polling, 60 s default). A transient failure is retried until the timeout (`TimeoutException`); a non-transient one (wrong preshared key) fails at once.
- **Start-up leaves the system not ready on any failure** and the next run recovers; concurrent runs are serialised.
- **Carry-over for Phase 4:** deleting a customer that still owns groups should be refused with 409 like other deletions with children, because a group's owner edge would otherwise dangle. The PRD lists only domains, wallets and subscriptions as children.

**Quality gates added**
- Architecture: only the projector, `AccessService`, reconciliation, the start-up reset and the adapter may depend on `IRelationshipStore` (PRD rule 6); a deliberately added rogue writer fails the test.
- Fitness: every domain event has a tuple mapping and survives the outbox round trip; the grantable-relations table equals the schema's non-structural relations.
- Mutation-checked on scratch edits (all caught, all restored): transient failures counting as attempts, a move that forgets the old parent, group deletion skipping subject cleanup, rule 1 disabled, the cycle check disabled, the default grant disabled, grants skipping the permission check, reconciliation ignoring unexpected tuples, the outbox not written with the aggregate, a seed grant omitted, the row version not bumped, and a per-item bulk error read as "deny".
