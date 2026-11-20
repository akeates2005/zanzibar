# Decisions

Choices made where PRD.md is silent or where the environment forced a deviation. One line of reasoning each (PRD rule 1).

## Deviations from the PRD (agreed with Andrew, 2026-11-12)

| # | PRD says | We do | Reason |
| --- | --- | --- | --- |
| D1 | .NET 10 (LTS) | **Resolved 2026-11-20: target `net10.0`.** Until then, `net9.0` | Only SDK 9.0.119 was installed at first, and Andrew chose .NET 9 rather than install .NET 10. SDK 10.0.115 was installed on 2026-11-20 and the project moved; it took more than a `TargetFramework` change (see ".NET 10 upgrade" below). |
| D2 | SpiceDB in Docker, Testcontainers, Compose | Native `spicedb` binary (memory datastore) for Phases 1-4 | Docker is not installed in this WSL environment. Andrew chose a native binary for now. |
| D3 | Phase 5 Playwright against Compose; Phase 6 "`docker compose up` from a clean clone" | **Blocked until Docker is available** | Cannot be accepted without Docker. `docker-compose.yml` can be written and reviewed but not run. Phases 5-6 stay open on those criteria. |
| D4 | "Lookups page through the cursor until exhausted" (Ports) | `LookupResources` pages through the cursor; `LookupSubjects` reads one unpaged stream | SpiceDB v1.57.1 rejects `optional_concrete_limit` on LookupSubjects (`Unimplemented: concrete limit is not yet supported`), so cursor paging cannot be used. Verified directly against the engine: unpaged LookupSubjects returned all 1500 of 1500 subjects (no 1000 cap, unlike LookupResources). Raised under PRD rule 2 and agreed by Andrew 2026-11-12. Revisit if a permission could have tens of thousands of holders or when the engine adds concrete limits. |
| D5 | Pause scenario: "SpiceDB container is paused and Alice creates a wallet: 202 with the pending flag" | With the engine unreachable **before** the request, a create is **503**; **202** is returned only when the engine fails **after** the permission check and before the tuple write | The PRD's own rules make the literal scenario unreachable: every request first checks the caller's permission through the engine, fully consistent, and a failed check never falls back to allow (rules 3 and 5), so a paused engine fails that check and the request is refused with 503. Rather than weaken fail-closed, I implemented 202 for the window the PRD's design genuinely allows and test both paths for real: the engine process is frozen with SIGSTOP (503, bounded by the deadline, nothing created, reads fail closed too, recovery on thaw), and an injected write failure after the check gives 202, invisible until the background worker catches up, then visible, with reconciliation reporting no drift. Raised under PRD rule 2 and resolved by Andrew's standing delegation of design authority (2026-11-12). |
| D6 | Phase 5 exit: "The five Playwright journeys pass against Compose" | The five journeys pass against the **native stack** (SpiceDB, stub issuer, API, built client) in the installed Google Chrome; Playwright starts and stops all four processes and never reuses a running one | Docker is unavailable (D2, D3), so Compose cannot run. The journeys, the browser and every process are the real ones; only the way the four services are started differs. Running them against Compose once Docker exists is a change to `playwright.config.ts`'s `webServer` block, not to any journey. |
| D7 | Phase 6 exit: "`docker compose up` from a clean clone reaches a working application with no manual step" | **Not verified.** The Dockerfiles and the full Compose file are written and pass static checks, and everything about them except the container runtime is exercised natively, but no image has ever been built or run | Docker is unavailable (D2, D3). What *is* proven: `scripts/check-compose.py` enforces the file's invariants (localhost-only ports, pinned SpiceDB, telemetry off, no baked secrets, non-root images, the API's authority equal to the name tokens carry), and `npm run e2e:published` runs ten browser journeys against the same `dotnet publish` output the image contains, serving the built client, in Production, with every setting from the environment and the issuer reached by a different name than its tokens carry. What is **not** proven: that the images build, that the base-image tags resolve as expected on the target, that the container user can read its files, and that service start order and networking behave. These are the first things to check when Docker is available. |
| D8 | Object types `domain`, `wallet`, `subscription`; the API names the object in the path (`/access/{type}/{id}/grants`, `/{parentType}/{id}/children`) | **`commerce/domain`, `commerce/wallet`, `commerce/subscription`**; the API names the object in the query string (`/access/grants?type=&id=`, `/children?type=&id=`) | Agreed with Andrew on 2026-11-17, as preparation for a shared, multi-team engine. Access values are unchanged; the matrix cells are renamed, not altered (PRD rule 4). See "Team-prefixed object types" below. |
| D9 | Groups is a screen of its own at `/customers/{id}/groups` and `/platform/groups` (Screens table), reached by a link | Groups is a **tab** on the customer and on the platform, beside Details and Access, at `/resources/customer/{id}/groups` and `/resources/platform/main/groups`. The PRD's URLs redirect there and keep `?group=` | Asked for by Andrew on 2026-11-19. The screen's content and its permissions (`view` on the owner; edit controls need `manage`) are unchanged. See "Groups as a tab" below. |

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

- **Solution shape.** `Poc.sln` with `src/Poc.{Domain,Application,Infrastructure}` and `tests/Poc.{UnitTests,IntegrationTests}`. `Directory.Build.props` sets `net10.0` (`net9.0` until 2026-11-20, D1), nullable, implicit usings, warnings as errors and `AnalysisLevel=latest-recommended` for every project. Architecture tests live in `Poc.UnitTests/Architecture` because the PRD layout has only two test projects.
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

## Phase 6 decisions (PRD silent or ambiguous)

**Schema-loss recovery and health**
- **One recovery path, two triggers.** `SchemaRecovery.ProbeAsync` reads the schema; if it is gone *while the system believes it is ready*, it marks the system unready, runs the full start-up sequence again in the background (retrying every 5 s until it succeeds) and logs the reset at warning level. The health probe calls it (as the PRD says) and so does `EngineWatchdog` every 10 s, so recovery does not depend on anyone asking for health. A missing schema while *not* ready (start-up, a System reset) is expected and triggers nothing; a test proves a deliberate reset is not mistaken for loss.
- **What recovery costs:** both stores are rebuilt to the seed scenario, so changes made since start-up are lost. The PRD accepts this (both stores are in memory and only a joint rebuild keeps them aligned); the log line says so.
- **Fail closed throughout.** Between the engine restarting and the rebuild finishing every answer is a 503; tests hammer the API through a real engine restart and assert it never returns a wrong deny or a stale allow. If the rebuild itself fails it is retried, not abandoned.
- **Fault injection grew** to the shapes real failures take: a response lost *after* the write landed (replay must be harmless and burn no attempt), a multi-step plan (group deletion) that breaks part way, an engine that flaps at 50% with a fixed seed, a burst of concurrent creates during an outage, default grants applied exactly once on recovery, and a real engine freeze and restart.

**Observability**
- **Every check's debug log now carries the request's trace id,** so a log line joins its trace. Spans come from an `ActivitySource` (`Poc.SpiceDb`, a span per engine call, errors marked) exported through OpenTelemetry alongside the ASP.NET span. Health probes are not traced. Exporters are opt-in: OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, console with `Telemetry__Console=true`.
- **OpenTelemetry is on 1.15 deliberately.** 1.21 (the newest) pulls `Microsoft.Extensions.*` 11.0, which does not support net9.0; 1.13 (the newest on 9.0 dependencies) has three known advisories in the OTLP exporter, which the build's vulnerability audit rejected (warnings are errors). 1.15.3 is the first patched release and its 10.0 dependencies do target net9.0. `dotnet list package --vulnerable` reports nothing across the solution.
- **Framework request logging is forced to Warning in code.** ASP.NET logs the full URL, and the user search puts what someone typed (possibly an email address) in the query string, so with the default Information level an email would be logged. The filter is in code rather than configuration so a config change cannot undo it, and a test checks the effective level.
- **"No token and no email address is ever logged" is tested, not asserted.** A session that exercises successes, refusals, validation errors, bad tokens (carrying a marker string), an engine failure and recovery is run with every one of our own log categories, authentication, EF Core and gRPC captured at trace level; the test then searches everything for any issued token, JWT-shaped text, the marker, `zanzibar.example`, the SpiceDB key, and a token following the word Bearer.

**Deployment shape**
- **The API serves the built client** (the PRD's "static build served by the API container"), so there is no separate `web` service and the Compose app lives on **:5080**, not 5173. The fallback to `index.html` uses `{*path:nonfile:regex(...)}`: without `nonfile` the fallback route matches asset paths first and ASP.NET's static-file middleware, which skips any request that already matched an endpoint, never serves them (found by a failing test). The regex keeps `/api`, `/health` and `/openapi` out of the fallback so they stay JSON. The fallback declares its access (anonymous, no data) like every other endpoint.
- **Two settings make a container network work.** `Authentication:MetadataAddress` lets the API fetch discovery and keys from `http://issuer:5090` while `Authority` stays the public name tokens carry; and the stub issuer's discovery document points `jwks_uri` at the address the document was *requested* on, not at its public name. Both are tested with a public issuer name that does not resolve at all.
- **The API image runs in Production** with everything from the environment (nothing from `appsettings.Development.json`); the issuer image must run in Development (it refuses otherwise). Images run as the base image's non-root `app` user, use the floating `9.0` tags (to be pinned to a digest once they have been built), and bake in no secret. There are no container healthchecks: the runtime image has no curl, and `/health` is available to an orchestrator.
- **No security headers or CSP are set** on the static client (Mantine injects inline styles, which would need a considered policy). Recorded as a known gap for anything beyond a demonstration.
- **The README's ten-minute demonstration is itself a test.** Its steps 2, 4, 5, 6 and 7 are Playwright journeys (`e2e/demo.spec.ts`), run against both the dev stack and the published build, so the script cannot drift from the application. Steps 1 and 8 need a person or a stopped engine and are covered by other tests.

**Quality gates added**
- `verify.sh` gains: the Compose/Dockerfile static checks (always run; proven able to fail on six planted defects), the published-build rehearsal (ten journeys against the production shape), and OpenTelemetry/vulnerability hygiene through the existing warnings-as-errors build.
- **Mutation-checked on scratch edits (all caught, all restored):** schema loss never triggering a rebuild, a missing schema reported as fine, `jwks_uri` using the public name, the SPA fallback swallowing API paths, SpiceDB calls not being spans, an email reaching a log, request logging left on, and the outbox marking a message done *before* the engine write.
- **A flaky test, found by running from a clean clone.** The first clean-clone `verify.sh` failed one tracing test that had always passed in the working tree. `ActivitySource`s are process-wide, so every test host in a test process also receives the spans of the hosts running in parallel; a test that found "the" server span by request path could pick up another test's request. Tracing tests now send a W3C `traceparent` with a trace id of their own, and find the server span as the one whose parent is the caller span they sent. That also surfaced that a first token validation makes the API call the issuer, and the trace context propagates, so the issuer's own server spans are part of the same trace (propagation works across the discovery and key fetch). The full integration suite was then run three times to look for other order- or timing-dependent tests: none.
- **A second harness race, found the same way.** `CapturingLoggerProvider.Entries` copied its `ConcurrentQueue` with a collection spread, which reads the count and then copies, so a log line arriving in between threw `IndexOutOfRange`; it only showed on the slower clean-clone run. It now uses `ToArray()`, the thread-safe snapshot. To look for others, the whole integration suite was run five times and then twice concurrently (halving the CPU each gets): 372 of 372 every time. Running `verify.sh` from a clean clone is now part of how a phase is closed, because it is slower and colder than a working tree and shakes out timing-dependent tests that never fail where they were written.

## Team-prefixed object types (D8, 2026-11-17)

Preparation for the production direction: one shared SpiceDB, with the platform team owning `user`, `platform`, `customer`
and `group`, and each app team owning its own resource types (see the 2026-11-15 design discussion).

**What was decided**
- **The shared types stay unprefixed; each team's types carry a one-level team prefix.** The PoC's business objects become
  `commerce/domain`, `commerce/wallet` and `commerce/subscription`. A type name is stored in every tuple, so adding a
  prefix after go-live means rewriting every tuple; starting with one costs a few characters. A prefix is the form SpiceDB
  documents for "multiple products within your organization", it makes "team X defines only `x/` types" a mechanical check,
  and it lets two teams each have a `wallet`.
- **Naming rule:** one prefix level, lowercase letters, digits and underscores, at least 3 characters per part. Observed
  on SpiceDB v1.57.1 on 2026-11-17: `bl/wallet` and `ab` are rejected (too short), `Billing/wallet` (uppercase) and
  `billing-wallet` (hyphen) are rejected, and `billing/sub/wallet` is accepted, but nesting is not used.
- **The API names an object in the query string, never in the path.** A path segment cannot carry `commerce/wallet`. The
  five access routes and the children route moved to the form `/access/audit` already used: `/access/grants`,
  `/access/grantable-groups`, `/access/effective` and `/children`, each with `?type=&id=`. A raw or `%2F`-encoded slash both
  resolve (unit-tested). Encoding the slash into the path was rejected: it depends on routing, the generated client and
  every proxy treating `%2F` the same way. `ResourceLocator.RouteTyped` was removed so a route-typed object cannot come
  back by accident.
- **The client has one catch-all page route, `/resources/*`.** `parseResourcePath` matches the path against the known types,
  so `commerce/wallet/uk-ops/access` and `customer/acme` both parse, and an object whose id is `access` is no longer
  mistaken for the Access tab. Links keep their shape (`/resources/{type}/{id}`).
- **Code names types through the schema constants.** Two API files had literal type strings (`ObjectViews`, the not-found
  messages); they now use `*Schema.Type`, and the unit-test schema parser accepts a prefixed definition name (a `\w+`
  pattern would have skipped them).

**Not changed, and needed for a central Access Management service**
- **The set of known types is still hard-coded** (`ResourceLocator.NamedTypes`). A central service would need every team's
  release to change and redeploy it; it should load the types from the deployed schema at start-up instead.
- **The grantable roles per type are a hand-written table too** (`GrantableRelations`). It feeds the Access tab's Role
  list (through `grantableRelations` in the grants response) and the server's `unknown_relation` check on grant, and a
  unit test keeps it equal to the schema's relations minus the structural ones (`parent`, `owner`, `member`). A central
  service cannot hand-maintain every team's roles. Two ways out: derive the list from the deployed schema at start-up
  using that same rule, which needs every team to agree which relation names are structural; or have each team declare
  its grantable roles as metadata beside its schema file, which the schema pipeline checks.
  - **Proposed answer (2026-11-17, not implemented): classify by subject type, not by name.** A relation is a grantable
    role when every subject it allows is a "people" type (`user`, `group#member`), and structural when it points at an
    object. In today's schema this gives exactly the hand-written table on every grantable type (`member` on `group` is
    person-shaped but groups are not grant targets; membership has its own command). It cannot drift: a team cannot add a
    structural relation that turns up as a role, because it points at an object. Teams agree only on the people types,
    which the platform already owns; a new kind of person (say `serviceaccount`) is added to that list once. The service
    can read subject types at start-up: `SchemaService/ReflectSchema` is in the SpiceDB v1.57.1 binary and in Authzed.Net
    1.6.0 (found in both binaries, not yet called); parsing `schema read` text is the fallback.
  - **A naming convention on top, enforced by the schema pipeline:** relations that point at objects use reserved names
    (`parent` for the hierarchy, `owner` for ownership); roles never use those names; every team resource type has a
    `parent`, so inheritance works the same way everywhere.
  - **Rejected, checked against SpiceDB v1.57.1 on 2026-11-17:** marking structural relations with an underscore (`_parent`
    and `parent_` are refused by the engine); a word prefix (`struct_parent` is accepted, but it lands in every tuple and
    arrow, and renaming means rewriting tuples); a doc-comment marker (comments survive a write and read-back, but nothing
    enforces them and it is not known whether `ReflectSchema` returns them); a reserved-name list alone (a convention
    nothing enforces).
  - **To adopt it here:** compute `GrantableRelations.For(type)` from the schema, and change the unit test to assert that
    the subject-type rule gives today's table.
- **Ids are still checked against this PoC's id format** (`EntityId.Validate`). Other teams' ids need SpiceDB's own id rules.
- **Cross-team references can cross customer boundaries.** In the 2026-11-17 namespace experiment, a `shipping/parcel`
  under customer C2, paid from a `billing/wallet` under C1, was viewable by C1's administrator through `payer->spend`.
  SpiceDB does not prevent this; each team's schema decides it. It needs a design rule, or a check in the schema pipeline,
  before teams reference each other's types.

**How it was done and checked**
- The mechanical rename (about 370 occurrences in 40 files) ran through a throwaway script that applied each pattern only
  if it matched an expected count taken from an independent `grep -o`, and wrote nothing otherwise; the full diff was then
  reviewed. Routes, the locator, the client routing and the constants were edited by hand. A follow-up search found what
  the patterns missed: wildcard filters (`domain:*`), three not-found messages and `verify.sh`'s schema read-back.
- The access matrix in `authz/validation.yaml` and the guard in `verify.sh` name the prefixed cells; all 88 assertions
  still pass with the same expected values.
- **Visible side effect: inherited grants are listed in a different order.** The API sorts them by source type name
  (`AccessQueryService.GrantsAsync`), so `commerce/domain` now comes before `customer`, and the Access tab shows a
  domain's administrators above the customer's. The order was always alphabetical by type, never by hierarchy; sorting
  nearest-ancestor-first would be the better order and is left as a separate change.

**A third harness race, found by the full gate during this change (unrelated to it)**
- One `verify.sh` run failed a test because its engine could not bind its port (`bind: address already in use`).
  `NativeSpiceDbHost.FreePort` finds a free port and releases it before the engine binds it, so in a parallel run another
  process can take it in between (outbound connections draw from the same port range). Readiness was a bare TCP connect,
  which would also succeed against whatever process had taken the port.
- **Fix:** readiness now also requires the engine's own `grpc server started serving` log line (observed on v1.57.1; it
  is written only after the bind), and a randomly chosen port that is taken is retried on a new one, up to three
  attempts. A port the caller fixed (the restart tests) is never moved.
- **Proven by two tests that take the port on purpose** (`NativeSpiceDbHostTests`). Mutation-checked: with one attempt
  the retry test fails; with connect-only readiness both fail, the fixed-port test because the harness reported the
  thief's listener as a ready engine.

## Access model read from the engine (NEXT-STEPS step 3, first increment, 2026-11-18)

**What changed.** The object types a request may name (`ResourceLocator.NamedTypes`) and the grantable roles per type
(`GrantableRelations`) were hand-written tables. Both are gone. At start-up, right after writing the schema, the app calls
SpiceDB `SchemaService/ReflectSchema` (fully consistent) and derives an `AccessModel` from what the engine holds
(`src/Poc.Application/Access/AccessModel.cs`, adapter in `SpiceDbSchemaStore.ReflectAsync`). Access values are unchanged:
the derived table equals the old one exactly, asserted against the engine (`SchemaReflectionTests`) and in unit tests.

**The rules (decided under delegated design authority; refine the 2026-11-17 proposal above):**
- A **resource type** is `platform`, or a definition with a `parent` relation and a `manage_access` permission.
- A **grantable relation** is a relation *of a resource type* whose every allowed subject is `user` or `group#member`,
  with no wildcard and no caveat. Restricting it to resource types is what keeps `group#member` out: that relation is
  person-shaped, but groups take no grants (membership has its own command). The 2026-11-17 note says this; the
  `NEXT-STEPS.md` summary ("It does") left it out.
- **Wildcards and caveats are not roles** (fail closed): the grant command writes a concrete subject with no caveat
  context, so offering such a relation would produce grants that do not mean what the screen says.
- **Before the model is loaded it is empty**, so no named type resolves (404) and nothing is grantable (422). The API is
  not served before then anyway: `ReadinessMiddleware` answers 503 until start-up finishes. Recovery after schema loss
  reruns start-up, which reloads the model.
- **An injected singleton (`AccessModelSource`), not a static**: statics are shared across parallel test hosts.

**Observed on SpiceDB v1.57.1 with Authzed.Net 1.6.0 (spike and tests, 2026-11-18):** ReflectSchema returns every
definition with relations, their subject types (`optionalRelationName`, `isPublicWildcard`, `optionalCaveatName`) and
permissions, in declaration order; a wildcard and a caveated subject type are reported as such; an engine with no schema
reflects no definitions. The test suite's schema reader now also parses subject types, and a test proves it reads
`schema.zed` exactly as the engine reflects it, so engine-free tests can use it.

**Checked by mutation:** wildcards counted as people (unit theory fails), the adapter dropping caveats (the
private-engine reflection test fails), start-up not loading the model (start-up's seed grants are refused, so no API
test host starts; that run hung rather than failing fast and was stopped, with no processes left behind).

**Not done or not verified:**
- Still hard-coded elsewhere, so "add a type to `schema.zed`, no C# change" is **not yet true** at the API: the
  permission catalogue (`SchemaCatalog`, read from the C# constants), `AccessService.PermissionFor` (a switch per type),
  `AccessQueryService.InheritedRelations`, and `StartupSequence.ObjectTypes`. The model-level version is proven
  (`A_type_added_to_the_deployed_schema_appears_in_the_model...`, against a real engine).
- Ids are still checked with `EntityId.Validate`. The SpiceDB binary contains several id patterns, and which one governs
  object ids was not confirmed; they accept `*`, which the API must never accept as an object id.
- Not tested: whether ReflectSchema returns doc comments (`schema.zed` has none), and how relations with SpiceDB's
  expiration trait are reflected (this proto has no field for it).

## A new team's type works through the API and the Access tab (step 3, part 2, with step 4's engine walk; 2026-11-18)

**Agreed with Andrew on 2026-11-18:** keep step 3's Done-when as written. That needed step 4's engine walk, so step 4 is
unpaused and done here in part.

**What changed**
- **Hard-coded type lists removed, now read from the access model:** the permission catalogue (`SchemaCatalog`, deleted),
  `AccessService.PermissionFor` (now `manage_access`, which every resource type has by definition),
  `StartupSequence.ObjectTypes`, and `EventTupleMapper.AllResourceTypes`. A deleted group's grants are now removed from
  every type that accepts `group#member`, including other teams' types; with the old list they would have survived.
- **Engine walk (`ObjectHierarchy`):** whether an object exists, and its ancestors and root customer, now come from `parent`
  tuples in the engine instead of this app's database (`IDirectoryStore.DescribeAsync` and `ObjectInfo` are deleted). The
  grant command, the group picker, the grants view and the new Access endpoint all use the same walk. The scope rule is
  stated once (`GrantRules.IsGroupInScope`), and the picker filters through it.
- **New endpoint `GET /api/access/object?type=&id=`** (`manage_access`): the name (the id where this app has no record), the
  caller's permission map for the type, and the ancestors. **New client route `/access?type=&id=`** shows the existing
  Access tab for any type. Links to another team's object go there; links to this app's types go to their own pages.

**Decided under delegated design authority**
- A **resource type must also have `view`**: the endpoint filter checks `view` on every resource route (`PRD.md:293`), so a
  type without it would fail at the engine rather than be refused cleanly.
- An object **exists if it has exactly one `parent` tuple** (`platform:main` always exists). Two parents is corrupt data and
  is refused; a chain that breaks partway up stops there, so no customer scope applies (customer groups are refused,
  platform groups are still allowed).
- **Inherited access stays as the PRD defines it** (`PRD.md:379`: each ancestor's administrators, plus the platform's support
  subjects). It is not derived from permission expressions (ReflectSchema returns permission names only); an ancestor type
  with no `administrator` relation contributes nothing.
- **A group's owner is still checked in this app's database** (`CustomerExistsAsync`): customers are platform-owned
  records, and checking the engine there would turn the PRD's "pending" (202) during an outage that starts after the
  permission check into a 503 (D5).
- **If the model is not loaded, a grant reports "engine unavailable"** (503) rather than "takes no direct grants" (422).
- **The resource tree still lists only this app's types:** it intersects the engine with this app's tables (PRD rule 5).
  Another team's objects are reached by link or URL, not by browsing.

**Behaviour changes**
- **The engine fails between the permission check and the structure read:** the grant is refused with 503 and **no audit
  row**, because nothing was attempted. A failure at the tuple write itself still records a Failed row, as the PRD
  requires. The fault injector gained `WritesDown` so that the write-failure tests keep testing the write; a new test
  covers the structure-read case.
- **Order of a group's deletes:** they now run in ordinal type order. `A_group_deletion_that_breaks_part_way...` now fails
  the 7th of 7 deletes instead of the 4th, so it still checks a part-way failure after the nesting step.

**Proven**
- **`NewTeamTypeTests`:** the API runs `schema.zed` plus a `shipping/parcel` type that has no C# anywhere, and parcels exist
  only as `parent` tuples. The Access endpoint, grantable roles, inherited administrators, grant, effective access, revoke,
  the group scope across customers, and 404 for a parcel with no parent all work.
- **`ObjectHierarchyTests`:** the engine walk against a real engine.
- **`GroupScopeAgreementTests`:** for every seed object and group, the picker offers a group if and only if granting it
  succeeds, for priya and for alice.
- **Vitest:** the Access page for an unknown type, and link routing.
- **Mutation-checked:**
  - the scope rule always passing (caught by the agreement and parcel tests);
  - a missing parent counted as existing;
  - group deletes limited to resource types;
  - links always pointing at `/resources`.

**Not verified or not done**
- **No Playwright journey opens the Access page for another team's type in a browser.** The client part is proven by
  component tests only: the e2e stack runs the real `schema.zed`, and has no way to add a type or write a parcel's parent
  tuple.
- **Not tested:**
  - a deletion race: between this app deleting an object and the projector deleting its tuples, the engine still says it
    exists, so a grant could be written;
  - `ObjectHierarchy.MaxDepth`: the current schema cannot form a cycle.
- **Still to do in steps 3 and 4:**
  - the platform-admin condition is still written twice (`AccessService`, `AccessQueryService.GrantableGroupsAsync`);
  - ids are still checked with `EntityId.Validate`.

## Steps 3 and 4 finished: one platform-admin rule, and the engine's id rule for any team's type (2026-11-18)

**What changed**
- **The platform-admin condition is stated once.** `GrantRules.RequiresPlatformAdministrator` says which groups need one,
  and `GrantRules.IsGroupGrantableBy` combines it with the scope rule. `PlatformAdministrator` is the only place that
  asks the engine (`admin` on `platform:main`; the system actor counts as one). The grant command and the group picker
  both use them. The picker now always loads platform groups and lets the rule filter them.
- **Ids follow the engine's rule where the type can be any team's:** the query-string locator (`ResourceLocator.QueryTyped`,
  used by every `/api/access/*` route and `/api/children`) and the resource named in the explain body. The new
  `EngineObjectId` holds the rule. This app's fixed-type routes and its domain keep `EntityId`'s stricter rule.

**Decided under delegated design authority**
- **Two id rules, not one.** `EntityId` is stricter than the engine on purpose (its comment), and this app makes the ids
  of its own types, so it keeps that rule. Other teams' ids are not this app's to restrict: a central service that
  refused ids the engine accepts would make other teams' objects unmanageable.
- **`*` is refused** as a resource id, as the engine refuses it.

**Evidence: what SpiceDB v1.57.1 accepts as a resource id** (observed through the HTTP gateway, so the engine and not
zed's parser decided; zed rejects many of these strings client-side before sending them)
- **Accepted** for write and check: `A|b=c+d/e_f-g`, `UPPER`, `/lead`, `-lead`, `_`, `=`, and 1024 characters.
- **Refused:**
  - 1025 characters ("must be at most 1024 bytes");
  - `*` ("alphanumeric value is required");
  - `a.b`, `a:b`, `a#b`, `a@b`, `a b`, `é`, `a%2Fb`, and the empty string (regex mismatch).
- This matches `ObjectReference.object_id` in https://github.com/authzed/api/blob/main/authzed/api/v1/core.proto
  (`^(([a-zA-Z0-9/_|\-=+]{1,})|\*)$`, `max_bytes: 1024`). That is the main branch, not a tag pinned to v1.57.1, which is
  why the engine was tested directly.

**Proven**
- **`EngineObjectIdTests`:** every observed case, both ways.
- **`ResourceLocatorTests`:** ids the engine accepts but `EntityId` refuses resolve on the query locator and not on a
  fixed-type route. Ids the engine refuses resolve on neither.
- **`NewTeamTypeTests.Another_teams_id_needs_only_to_satisfy_the_engines_id_rule`:** a parcel with id `PCL|2026/0001+x=`
  works through the Access endpoint, grant, effective access and explain. An id with `:` gets 404.
- **Mutation-checked:**
  - the picker ignoring the admin flag (caught by `GroupScopeAgreementTests`);
  - the command never requiring a platform administrator (caught by the agreement test and
    `Rule1_a_platform_group_can_be_granted_anywhere_but_only_by_a_platform_administrator`);
  - the query locator using `EntityId` (caught by the locator and parcel tests);
  - explain refusing an engine-only id (caught by the parcel test);
  - the length limit one too high;
  - the regex ending in `$` instead of `\z`. A .NET regex ending in `$` also matches before a trailing newline, and the
    test caught it.

**Changed test**
- `ResourceLocatorTests.A_malformed_id_cannot_exist_so_it_never_resolves` asserted that the query locator refuses
  `Bad_Upper` and `-leading`. The engine accepts both, so that theory is split into ids the engine refuses (neither
  locator resolves them) and ids only the engine accepts (only the query locator resolves them).

**Not verified**
- **No browser test** opens the Access page for an id with `/`, `|`, `+` or `=`. The client builds that link with
  `encodeURIComponent` (`web/src/lib/resourcePath.ts:17`), but no test sends such an id through the router.
- **Subject ids are unchanged.** User and group ids in a grant body are not format-checked. They must be present
  (`Results2.Require`), and a grant needs the user or group to exist in this app's database (`AccessService`).

**A third harness race, found under load**
- **How it showed up:**
  - The changed classes passed 3 times in a row. Then the whole suite ran alongside a second copy, and one test failed
    after 1 ms (`GroupScopeAgreementTests`, priya). Its message was filtered out of the log, so its cause is inferred,
    not observed.
  - Two more paired runs passed.
  - Three suites at once, three times, gave 2 failures in 9 runs (`SystemApiTests`, two different tests). Both were
    `address already in use` from the stub issuer's `StartAsync` in `ApiHost`.
- **The cause** is the one fixed for the engine on 2026-11-17. `FreePort` releases the port before the issuer binds it,
  and the issuer never got the retry. It cannot bind port 0 instead, because its own URL goes into the tokens before it
  starts.
- **The fix:** `ApiHost.StartIssuerAsync` retries a taken port on a new one (3 attempts), as `NativeSpiceDbHost` does.
- **Proven:**
  - `ApiHostIssuerPortTests` takes the first port on purpose, and asserts that the issuer moved and still answers discovery.
  - Mutation-checked: with the retry disabled, that test fails with the same error seen under load.
- The other `FreePort()` uses never bind the port; they are addresses meant to have nothing listening.
- **After the fix:** three suites at once, three times, 9 runs. No port failures. 8 of 9 runs passed all 396 tests; the
  ninth had the open flake below.

**Open: a negative outbox lag under load (recorded, not fixed; Andrew chose this on 2026-11-18)**
- **Seen once,** in 1 of those 9 runs:
  - test: `SystemApiTests.Health_shows_outbox_lag_while_authorisation_is_pending`;
  - assertion: `outboxLagSeconds >= 0` (`SystemApiTests.cs:185`).
- **The lag is "now" minus the time the outbox row was written, and both come from the system clock.**
  - The row is stamped with `TimeProvider.System` (`PocDbContext.cs:121`; registered in
    `InfrastructureServiceCollectionExtensions.cs:30`).
  - `/health` subtracts it from `DateTimeOffset.UtcNow` (`SystemEndpoints.cs:38`).
  - So a negative lag means the wall clock went backwards between the write and the read.
- **Likely cause, not proven:** this WSL2 machine's clock steps backwards. journald logged "Time jumped backwards" ten
  times since boot, the last at about 19:26, but the failed run ended at 19:40 and no jump was tied to it.
- **Options left open:**
  - clamp the reported lag at 0 and read "now" from the same `TimeProvider`;
  - or log both timestamps when the lag is negative and reproduce under load.

## Client restyle: theme, sidebar and resource tree (2026-11-19)

**Asked by Andrew:** "more modern". He chose the scope (theme, sidebar, tree; tables and pages unchanged), the look
(clean SaaS in indigo, with dark mode) and the icon library, subject to a clean audit.

**What changed**
- `web/src/theme.ts`: a Mantine `createTheme` with an indigo `brand` scale, the Inter font, `md` radius and soft shadows.
  Before this, `MantineProvider` had no theme. The unit-test renderer uses the same theme.
- `Shell.tsx`: a left sidebar (`AppShell.Navbar`) with an icon per section, a burger menu below `sm`, and a light/dark
  toggle in the header. The signed-in user is shown with initials above the switcher. Content is capped at 1200px.
- `ResourceBrowser.tsx`: each row has a tinted icon and a coloured type badge, with indent guide lines and a hover
  highlight. Indents are narrower on a phone.
- `web/public/favicon.svg`: Vite answered `/favicon.ico` with 404, which showed up as a console error.
- `scrollbar-gutter: stable` on the root (`index.css`). Andrew saw the content shift when switching tabs. Measured in
  headed Chrome at 1400x900: the Access tab is taller than the window, so a 15px scrollbar came and went, the content
  width changed between 1112 and 1097px, and every right-aligned control moved. With the gutter reserved, the width stays
  1097px, and the page actions and header toggle stay put across Details, Access and Groups, for the platform (priya)
  and a customer (alice).

**Decided under delegated design authority**
- **The user switcher stays a native `<select>`.** `e2e/support.ts:56` drives it with `selectOption`, and a select is the
  plainest control for a demo.
- **Link names, the `signed-in-name` test id, `tree-*` test ids and the nested `li` structure are kept**, because the
  journeys and unit tests select by them.
- **A type the client does not know gets a grey cube, never an error** (`typeVisuals.tsx`). Types come from the engine's
  schema (step 3), so another team's type reaches the client unannounced.
- **New dependencies, pinned exactly:** `@tabler/icons-react@3.50.0` (released 2026-10-27; 3.52.0 was five days old, so
  not taken) and `@fontsource-variable/inter@5.3.0` (the font is served by the app, with no third-party font request).
  `npm audit` showed the same 9 moderate findings before and after the install. All 9 come from `minimatch`
  (GHSA-g3hv-75wg-m249), reached through eslint and typescript-eslint dev tooling, not from these packages.

**Proven**
- `typeVisuals.test.tsx`: an unknown type renders the fallback. Mutation-checked: with the fallback removed, the test
  fails with `TypeError: Cannot read properties of undefined`.
- Screenshots of the tree (light, dark, 390px wide) and of a domain's Access tab, with no console errors.

**Not verified**
- The ObjectDetail, Groups, Explorer and System pages were checked only in the earlier screenshots (Explorer) or not at
  all. They inherit the theme but their layout is unchanged.
- No accessibility audit (contrast of the light badges in dark mode was judged by eye only).

## Groups as a tab of their owner (2026-11-19, D9)

**Asked by Andrew:** move "Groups of this customer" and "Platform groups" from links on the Details tab to tabs on the
customer and platform pages.

**What changed**
- `parseResourcePath` reads a `groups` tab, but only for the types that own groups (`GROUP_OWNER_TYPES`: `platform` and
  `customer`). On a domain, wallet or subscription, `/groups` is "Not found".
- `ObjectDetail` (customer) and `PlatformPage` have a Groups tab. Its body is `GroupsTab` (renamed from `GroupsPage`),
  which no longer has its own page title or back link. The links on the Details tabs are gone.
- `/customers/:id/groups` and `/platform/groups` are `GroupsRedirect`s to the tab, keeping the query string.
- The sidebar's "Platform groups" item is removed (Andrew, same day): it is now the platform's Groups tab, reached
  through Resources. This replaces the Phase 5 note above that Platform groups is a nav link shown only with platform
  `view`. The tab itself needs platform `view`, because the platform page does.
- `e2e/demo.spec.ts` step 4 clicks the Groups tab instead of the removed link.

**Decided under delegated design authority**
- **The Groups tab shows for anyone who can open the owner.** That matches the PRD's rule for the screen (`view` on the
  owner); the list itself shows only the groups the API returns.
- **The old URLs redirect rather than 404,** so links and bookmarks to the PRD's routes keep working.

**Proven**
- `resourcePath.test.ts`: the tab is read for customer and platform and refused for domain and wallet.
  Mutation-checked: allowing it for any type fails that test.
- In Chrome against the running app:
  - Alice: the customer's tabs are Details, Access, Groups, with no "Groups of this customer" link. A group created and
    then deleted from the tab leaves her on the Groups tab.
  - `/customers/acme/groups?group=uk-it` lands on `/resources/customer/acme/groups?group=uk-it`, with UK IT open.
  - Priya: the sidebar lists Resources, Explorer and System. On `/resources/platform/main/groups` "Resources" is the
    current item, and the platform's Details tab has no groups link.
  - `/platform/groups` redirects to the same place.
  - Domain tabs are Details and Access.

  - At 390px wide the three tabs and the page actions fit, and the page does not scroll sideways.

**Seen, not changed**
- Deleting a group triggers one `GET /api/groups/{id}` that answers 404, because the deleted group is refetched. The
  previous commit (`b1d5715`) does the same through the old route, so this is not new.
- At 390px a long "Remove …" button in the group's Members table runs past the card's edge. The table's layout is
  unchanged by this work.

## SDK pinned to 9.0.119 until the .NET 10 upgrade (2026-11-20)

- **Why:** SDK 10.0.115 was installed side by side with 9.0.119 (Ubuntu `noble-updates`), so that the move to
  `net10.0` can start. The repo had no `global.json`, so SDK 10 became the default for the `net9.0` tree, and the
  build failed. SDK 10 adds analyzer CA1873 ("evaluation of this argument may be expensive"), and with warnings as
  errors it fails 12 log calls that build their arguments eagerly (`SpiceDbAuthorizer.cs:31`, `:80`, `:194`;
  `AccessLog.cs:13`). Observed 2026-11-20.
- **Decision (Andrew):** `global.json` pins SDK 9.0.119 with `rollForward: latestFeature`: any 9.0 SDK at or above
  it, never 10. The upgrade is a change of its own: target framework, packages, the OpenAPI transformer
  (`Program.cs:104`, Microsoft.OpenApi 2.0), CA1873 fixed at the cause, and this pin removed.
- **Not affected:** the Dockerfiles do not copy `global.json`; they keep the `sdk:9.0` images.
- **Note:** .NET 9 left support on 2026-11-10, so this pin is a stopgap, not a resting state.
- **Removed the same day** by the .NET 10 upgrade below.

## .NET 10 upgrade (2026-11-20, resolves D1)

Target `net10.0` (`Directory.Build.props`), Microsoft packages 10.0.15 (the installed runtime is 10.0.15), SDK
10.0.115, and the `sdk:10.0` and `aspnet:10.0` images. `global.json` is gone. The other packages (OpenTelemetry 1.15,
Authzed.Net 1.6.0, IdentityModel 8.23, xunit) are unchanged and build and pass on net10.0. What else the move needed:

- **CA1873 fixed at the cause.** The check log in `SpiceDbAuthorizer` (three call sites) and all three `AccessLog`
  methods built their arguments eagerly; they now pass the values to the generated log methods, which format them only when the level is enabled. The analyzer does not accept an
  `IsEnabled` guard around a generated method (observed), so passing values is the fix. The log text is unchanged:
  it is the values' `ToString()`. One small difference: a check logged outside any trace now prints its trace id as
  the formatter's null text instead of `none`. Not observed; no test or document reads it.
- **OpenAPI transformer rewritten for Microsoft.OpenApi 2.x** (2.13.0 resolved). It has no `Nullable` flag (the
  type includes `Null`), types are an enum, and references are `OpenApiSchemaReference` and
  `OpenApiSecuritySchemeReference` ([what's new](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0#openapi)).
- **The document stays OpenAPI 3.0.** .NET 10 defaults to 3.1, which would rewrite every nullable in
  `web/openapi.json` and the client's types. Moving to 3.1 is a change of its own.
- **JSON numbers are strict** (`JsonNumberHandling.Strict`). The web defaults have always accepted `"5"` for a number,
  and .NET 10's generator now describes that, so all 21 numbers in the document became "integer or string" and the
  client's number fields would have widened. Nothing sends numbers as strings (searched the tests, client and
  journeys). Refusing them is tighter input handling, and the document says `integer` again. Tested by
  `ObjectApiTests.A_number_sent_as_a_string_is_a_400…`; mutation-checked (with `AllowReadingFromString`, that test
  and the contract test both fail).
- **What changed in the document anyway:** schemas are reordered, .NET 9's duplicate `SubjectRequest2` and
  `ReconciliationDto2` are gone, and two nullable objects are written as `oneOf` with `null` at the place they are
  used. The regenerated `schema.ts` means the same: nullability moved from the type to its uses. `tsc` passes.
- **The issuer's `Program` is declared `internal`.** .NET 10 generates a public `Program` for every web project that
  declares none ([source](https://learn.microsoft.com/aspnet/core/release-notes/aspnetcore-10.0#miscellaneous)). The
  integration tests reference both web projects, so two public `Program` types were ambiguous (CS0433). The tests host
  the issuer through `DevIssuerApp`, so its `Program` need not be public.
- **Accepted as is:** when `ApiExceptionHandler` handles an exception, the framework no longer logs it or tags the
  request metric with `error.type` ([source](https://learn.microsoft.com/aspnet/core/breaking-changes/10/exception-handler-diagnostics-suppressed)).
  Our handler already logs every 500 and 503 itself. Restore the metric tag with `SuppressDiagnosticsCallback` if a
  dashboard needs it.
- **Not verified:** the 10.0 images, which are Ubuntu-based ([source](https://learn.microsoft.com/dotnet/core/compatibility/containers/10.0/default-images-use-ubuntu)),
  still provide the non-root `app` user that both Dockerfiles switch to. Docker has never run here (D2, D3, D7).
