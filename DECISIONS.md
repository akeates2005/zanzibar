# Decisions

Choices made where PRD.md is silent or where the environment forced a deviation. One line of reasoning each (PRD rule 1).

## Deviations from the PRD (agreed with Andrew, 2026-11-12)

| # | PRD says | We do | Reason |
| --- | --- | --- | --- |
| D1 | .NET 10 (LTS) | Target `net9.0` | Only SDK 9.0.119 is installed; Andrew chose to target .NET 9 rather than install .NET 10. Authzed.Net targets net8.0+, so it is compatible. Moving to net10.0 later is a `TargetFramework` change. |
| D2 | SpiceDB in Docker, Testcontainers, Compose | Native `spicedb` binary (memory datastore) for Phases 1-4 | Docker is not installed in this WSL environment. Andrew chose a native binary for now. |
| D3 | Phase 5 Playwright against Compose; Phase 6 "`docker compose up` from a clean clone" | **Blocked until Docker is available** | Cannot be accepted without Docker. `docker-compose.yml` can be written and reviewed but not run. Phases 5-6 stay open on those criteria. |
| D4 | "Lookups page through the cursor until exhausted" (Ports) | `LookupResources` pages through the cursor; `LookupSubjects` reads one unpaged stream | SpiceDB v1.57.1 rejects `optional_concrete_limit` on LookupSubjects (`Unimplemented: concrete limit is not yet supported`), so cursor paging cannot be used. Verified directly against the engine: unpaged LookupSubjects returned all 1500 of 1500 subjects (no 1000 cap, unlike LookupResources). Raised under PRD rule 2 and agreed by Andrew 2026-11-12. Revisit if a permission could have tens of thousands of holders or when the engine adds concrete limits. |
| D5 | Pause scenario: "SpiceDB container is paused and Alice creates a wallet: 202 with the pending flag" | With the engine unreachable **before** the request, a create is **503**; **202** is returned only when the engine fails **after** the permission check and before the tuple write | The PRD's own rules make the literal scenario unreachable: every request first checks the caller's permission through the engine, fully consistent, and a failed check never falls back to allow (rules 3 and 5), so a paused engine fails that check and the request is refused with 503. Rather than weaken fail-closed, I implemented 202 for the window the PRD's design genuinely allows and test both paths for real: the engine process is frozen with SIGSTOP (503, bounded by the deadline, nothing created, reads fail closed too, recovery on thaw), and an injected write failure after the check gives 202, invisible until the background worker catches up, then visible, with reconciliation reporting no drift. Raised under PRD rule 2 and resolved by Andrew's standing delegation of design authority (2026-11-12). |
| D6 | Phase 5 exit: "The five Playwright journeys pass against Compose" | The five journeys pass against the **native stack** (SpiceDB, stub issuer, API, built client) in the installed Google Chrome; Playwright starts and stops all four processes and never reuses a running one | Docker is unavailable (D2, D3), so Compose cannot run. The journeys, the browser and every process are the real ones; only the way the four services are started differs. Running them against Compose once Docker exists is a change to `playwright.config.ts`'s `webServer` block, not to any journey. |

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

## Phase 4 decisions (PRD silent or ambiguous)

**Shape of the API**
- **Type names in routes are singular** (`/api/access/{type}/{id}/grants`, `/api/{parentType}/{id}/children`, `type=wallet`) because they are object-type names shared with the schema; collection routes keep the PRD's plurals (`/api/customers/{id}`). `{type}` accepts only `platform`, `customer`, `domain`, `wallet`, `subscription`; groups have their own routes.
- **No-view means 404 on creates too.** A create route checks `view` and the create permission on the parent (PRD rules 3 and 4). So `POST /api/customers` is 404 for a user who cannot view `platform:main` (Alice, Frank) and 403 `create_customer` for one who can (Sam).
- **Malformed ids and unknown types are 404 without asking the engine.** `ResourceLocator` rejects anything `EntityId` would reject, so a path such as `a%23member` or `%2A` never reaches the engine as a tuple.
- **Revoke names its grant in the query string** (`DELETE …/grants?relation=&subjectType=&subjectId=`, and `DELETE …/members?type=&memberId=`), so no client or proxy has to send a body on DELETE. A group grantee is `{type: "group", id}` in the API; the service turns it into `group#member`.
- **Two checks cannot be declared in metadata** because the second object is in the body: a wallet move's target domain (`create_wallet`) and the explorer's resource (`manage_access`). The route declares what it can (`move` on the wallet; `HandlerChecked` for explain) and the handler checks the rest through the same `ResourceAccess` rule, so the 404/403 logic exists once. A test lists the handler-checked endpoints, and another proves each refuses.
- **Write responses.** Create returns 201 with the object and its permission map; delete returns 204; move and create return 202 `{id, authorisationPending: true}` when authorisation could not land. Renames, top-ups, quantity changes and cancellation raise no authorisation event, so they never return 202.
- **Object responses carry `ancestors`** (root first): ones the caller cannot view have `id: null` and `viewable: false`, so a client can label but never link (PRD open question 2, default yes). The tree uses the same shape.
- **Customer viewers see an empty children list, not a 404,** when they can view the parent but nothing under it.
- **The user directory returns emails to any signed-in user** (PRD open question 7 default). Emails are never logged.

**Security**
- **One enforcement filter on the `/api` group; no declaration means refusal (500), never allow.** Filters are not endpoint metadata, so the filter's presence is proven by behaviour: every `/api` route returns 401 anonymously, and every resource route returns 404 for a user with no access, aimed at real seeded objects of the right type. A second sweep proves a view-only user gets 403 naming the declared permission on every mutating route.
- **Only `/health` and `/openapi/{documentName}.json` are anonymous,** and a test pins that list. The API description contains no data.
- **Token validation:** signature, issuer, audience and lifetime, 30 s clock skew, claims kept as issued (`MapInboundClaims = false`). The `extension_userId` claim must be present, a valid id, and not `system`, checked at validation (401) and again when the caller is resolved. `sub` is never a fallback (tested with a token whose `sub` says `priya`). Tokens claiming roles or groups gain nothing (tested).
- **No internal detail reaches callers.** Engine failures are a generic 503 with `Retry-After`; unexpected errors are a generic 500 with a trace id; the exception is logged, never echoed.
- **While start-up or a System reset rebuilds the stores, `/api` returns 503 with `Retry-After`** (a readiness gate), so nothing reads a half-built database. `/health` reports not ready.
- **The API has no default preshared key** (PRD rule 8: the development default lives in Compose only). `Authentication:Authority` is mandatory and start-up fails without it.
- **Start-up runs in a hosted service that retries every 5 s,** so the API can come up before SpiceDB does.

**Architecture**
- **`IRelationshipReader` split from `IRelationshipStore`.** `AccessQueryService` (grant listings, group detail) only reads tuples; giving it the reader means it cannot write one. The architecture test's writer allow-list is unchanged, and a new test asserts the read side does not depend on the store.
- **The stub issuer and the seeder share `SeedUsers`** (in `Poc.Domain`, which references nothing), so they cannot disagree about who exists. The issuer refuses to start outside Development, binds loopback by default, and its `TokenFactory.Issue` (used by tests to mint tokens a user could never get) is never exposed over HTTP.
- **`/health` reads the schema on every probe** (PRD) and reports `ok`, `schema-missing` or `unreachable`; automatic recovery from schema loss is Phase 6.
- **Explain returns the engine's trace** (this SpiceDB version does return one), flattened to at most 500 nodes. The trace names objects on the resolution path; it is available only to holders of `manage_access` on the resource.
- **Dockerfiles and the `api`, `issuer` and `web` Compose services are deferred to Phase 6** (Docker is unavailable; D2, D3).

**Quality gates added**
- **Fitness function: no drift after every HTTP test.** `ApiTestBase` flushes the outbox and runs reconciliation when each test ends and fails the test if the stores disagree (the PRD's "reconciliation reports no drift after the full integration run", asserted continuously). Proven able to fail with a throwaway test that deleted a tuple.
- **The OpenAPI document must describe exactly the routes the application has,** with unique operation ids and the bearer scheme.
- **Mutation-checked (all caught, all restored):** no-view answered 403, missing permission treated as allowed, the enforcement filter removed, audience not validated, `sub` accepted as a fallback, the readiness gate removed, engine failure mapped to 404, children and the tree showing what the caller cannot view, deleting a customer that owns groups, cross-customer moves, the explorer and the grant listing skipping their checks, and a move skipping `create_wallet` on the target.
- **Test harness hygiene.** A mutation round left two orphaned SpiceDB test engines. Causes found in the harness: xunit never calls `DisposeAsync` when `InitializeAsync` throws, and a crashed or aborted test run leaves children behind. Fixed: `ApiHost` and `PocHost` dispose what they started when start-up fails, and `NativeSpiceDbHost` keeps a registry of live engines that a process-exit hook kills. Both were proven with throwaway tests (and the reaper shown to matter: without it one engine survives). `verify.sh` now has a stage that fails the gate if any test engine (identified by `--metrics-enabled=false`) is still running.

## Phase 5 decisions (PRD silent or ambiguous)

**Tooling**
- **TypeScript is pinned to 5.9, not the current 7.x.** `typescript-eslint` supports TypeScript below 6.1 and `openapi-typescript` needs 5.x, so 7.x would break lint and type generation. Revisit when both support it. Everything else is current: React 19.3, Vite 8, Mantine 9, TanStack Query 5, React Router 7, ESLint 10, Vitest 5, Playwright 1.65.
- **`npm audit` reports 10 moderate findings, all dev-only:** a `minimatch` denial-of-service reached through ESLint's own glob handling on our repository's files. `npm audit --omit=dev` reports 0, so nothing in the shipped bundle is affected. Accepted; cleared by upgrading ESLint when a fixed `minimatch` is available.
- **Strict everywhere:** TypeScript `strict`, `noUncheckedIndexedAccess`, `verbatimModuleSyntax`, no unused locals or parameters; ESLint `strictTypeChecked` and `stylisticTypeChecked` plus the React hooks rules, `--max-warnings 0`. The lint caught real issues (refs read during render, loose `||` on strings). Generated `schema.ts` is excluded from lint only.
- **The bundle is not code-split** (660 kB, advisory warning only). Routes are few and this is a proof of concept.

**The API contract**
- **`web/openapi.json` is committed and is the contract.** `OpenApiContractTests` fails if it differs from what the running API serves (run it with `POC_UPDATE_OPENAPI=1` after an intentional change), and `verify.sh` fails if `web/src/api/schema.ts` differs from what `openapi-typescript` generates from it. API types are generated, never hand-written (PRD). Error shapes are in the document: every `/api` operation describes its 400/401/403/404/409/422/503 as an RFC 9457 `Problem` carrying `permission` or `code`.
- **API additions the UI needed:** `GET /api/me` now returns `platformPermissions` (drives the System link, platform groups and "New customer"); `GET /api/access/{type}/{id}/grantable-groups` returns exactly the groups the service rules allow, because a domain admin such as Bob cannot view the customer's group list and would otherwise be offered none; group detail returns the caller's `permissions` so edit controls can be disabled; the explain trace is a **flat pre-order list with a depth**, because the generator turned the recursive type into a nullable type plus a numbered copy that no client wants.
- **A platform page exists** (`/resources/platform/main`) though the PRD's screen list omits it: platform-wide roles are granted on `platform:main`, so its Access tab needs somewhere to live.

**Client design**
- **The token lives only in memory, inside an API client bound to it.** `createApi(token)` captures the bearer token, so a query cached under one user's key could only ever have been fetched with that user's token. Tests assert that no storage or cookie ever holds it. Only the chosen user's *id* is remembered (localStorage), as a demo convenience.
- **Every query key starts with the user id; switching cancels in-flight queries, replaces the session and clears the cache.** After every successful command *all* of the current user's queries are invalidated, which is coarser than the PRD's list (tree, object, access data) but cannot forget a part of it. A 401 (the one-hour token expired) triggers one re-issue and a refetch.
- **Disabled, not hidden.** `ActionButton` shows a missing permission as a disabled control with a tooltip naming it (the tooltip sits on a wrapper because a disabled button gets no pointer events). A cancelled subscription blocks Change quantity and Cancel with its own reason. The permission map only enables controls; the server never trusts it, and a journey proves a direct API call is refused with 403 naming the permission.
- **The tab, nav links and pickers follow the PRD's visibility rules:** the Access tab only for `manage_access`; System and Platform groups only from `platformPermissions`; the Explorer link is always shown and its screen reports a refusal in place. The group picker uses the server's grantable list; nested-group choices are limited to the same owner.
- **Ancestors the user cannot view are plain text and merge by (type, name).** The API gives the client nothing else about them (PRD open question 2), so two different unviewable customers with the same name would show as one label.
- **The user switcher is a native select** (instant, and reliable under test); the searchable user picker uses Mantine's `Select` with server-side search and client filtering turned off.
- **Move targets are narrowed client-side** to other visible domains under the same customer; the server remains the only judge (it also needs `create_wallet` on the target and refuses cross-customer moves with 422).

**Found by the journeys, fixed**
- **The API read its schema relative to the working directory,** so under `dotnet run` from another directory (or a container with a different `WORKDIR`) start-up never wrote the schema and the API stayed unhealthy forever. A relative `Poc:SchemaPath` now resolves against the application's base directory, where the build copies the schema.
- **A number field rejected normal input.** Mantine reports `250.50` as a string, which the dialogs treated as "not a number", leaving the Top up button disabled. One tested helper (`toNumber`) now reads both forms.
- **Two bugs in my own journey helper, caught because an assertion looked vacuous:** an init script reset the remembered user on every navigation, silently signing the page back in as someone else. `openAs` now seeds the user only on first load, and `switchTo` waits until the shell shows the new person.

**Quality gates added**
- `verify.sh` runs, for the client: generated-types freshness, `tsc --noEmit` (strict), ESLint, Vitest (43 tests: tree building, move targets, problem handling, the permission-aware button, session and token handling, the browser's label-versus-link rule), the production build, and the five Playwright journeys in Chrome. The hygiene stage now also fails if the end-to-end stack leaves a SpiceDB, API or issuer process running, with patterns anchored to the executable so the invoking shell can never match.
- **Mutation-checked on scratch edits (all caught, all restored):** actions ignoring permissions (2 unit tests and 3 journeys), unviewable ancestors rendered as links (a journey, then a unit test added because only the slow journey caught it), and a stale generated-types file.
