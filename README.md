# Zanzibar authorisation proof of concept

A working demonstration of **resource-level authorisation with a Zanzibar-style engine**. Who may do what to which customer,
domain, wallet or subscription is decided by [SpiceDB](https://authzed.com/spicedb), never by roles in a token. The point is
to show that a hierarchy, inheritance, groups and nested groups, isolation between customers, and instant revocation can all
be expressed in one small schema and kept correct by tests that run against the real engine.

- **Engine:** SpiceDB (memory datastore) with one schema, `authz/schema.zed`.
- **API:** .NET 9 modular monolith, ASP.NET Core minimal APIs, SQLite in memory through EF Core.
- **Client:** React 19, TypeScript (strict), Vite, TanStack Query, React Router, Mantine.
- **Sign-in:** a stub token issuer that publishes OIDC discovery and keys, so the API validates tokens exactly as it would
  against Azure AD B2C. Moving to B2C is configuration.
- **Spec:** [`PRD.md`](PRD.md). Every choice the PRD left open, and every place reality disagreed with it, is in
  [`DECISIONS.md`](DECISIONS.md).

## What it demonstrates

| Idea | Where to see it |
| --- | --- |
| Admin inherits **downward**; viewer, spender and operator do not | Alice and Erin in the demo below |
| **Support** (platform) can read everything and change nothing | Sam |
| **Groups**, **nested groups**, and rules the schema cannot express (no cycles, same owner, no cross-customer grants) | Heidi, Bob, the Groups screen |
| **Isolation**: one customer can never reach another's objects | Grace |
| **Instant revocation**: the next request already reflects a change | moving a wallet, removing a group member |
| **404, not 403**, for what you may not see (existence is never leaked) | any user, any URL |
| **Fail closed**: if the engine cannot answer, nothing is allowed | stop SpiceDB mid-demo |
| **Self-healing**: an engine restart (which wipes a memory datastore) is noticed and both stores are rebuilt | restart SpiceDB mid-demo |

## Run it

### Prerequisites

.NET 9 SDK, Node 22, and the `spicedb` binary (v1.57.1) and `zed` (v1.2.1) on your `PATH`. Chrome is needed only for the
browser tests. Docker is **not** needed for any of this.

### Four terminals

```bash
# one-time
dotnet build Poc.sln
(cd web && npm ci)

# 1. SpiceDB, in memory, on :50051 (development key: zanzibar-dev-only-key)
scripts/spicedb-local.sh start

# 2. the stub token issuer, on :5090 (Development only)
dotnet run --project src/Poc.DevIssuer

# 3. the API, on :5080. It has no default SpiceDB key, so supply one.
export Poc__SpiceDb__PresharedKey=zanzibar-dev-only-key
dotnet run --project src/Poc.Api

# 4. the client, on :5173
cd web && npm run dev
```

Open **http://localhost:5173**. The API waits for SpiceDB, writes the schema and seeds both stores (about 15 seconds); until
then `/api` answers 503. `curl http://localhost:5080/health` says when it is ready.

Stop with `Ctrl-C` in each terminal and `scripts/spicedb-local.sh stop`.

### Docker Compose (written, not yet run)

```bash
docker compose up --build        # then open http://localhost:5080
```

`docker-compose.yml` and the two Dockerfiles exist and pass static checks (localhost-only ports, pinned SpiceDB, no baked
secrets, non-root images), but **they have never been built or run**: Docker was not available where this was developed. What
*is* verified is everything about them except the container runtime: `npm run e2e:published` (in `web/`) runs the five browser
journeys against the same `dotnet publish` output the image contains, serving the built client, in the Production environment,
with every setting from the environment and the issuer reached by a different name than its tokens carry. If `docker compose
up` misbehaves, suspect the images first, then compare with that rehearsal.

In Compose the app is on **:5080** (the API serves the client), not :5173.

## The seed scenario

Two customers, ten users. Sign in as any of them with the selector in the header.

```
platform
 ├─ Acme Ltd
 │   ├─ Acme UK        ── UK Operations (wallet) ── Microsoft 365 E3, Azure Plan
 │   │                 └─ UK R&D (wallet)        ── GitHub Enterprise
 │   └─ Acme Ireland   ── Ireland Operations     ── Microsoft 365 Business Premium
 └─ Globex Corporation
     └─ Globex Main    ── Globex Operations      ── Dynamics 365 Sales
```

| User | How they got access | What it shows |
| --- | --- | --- |
| **priya** | platform administrator | everything, everywhere |
| **sam** | in platform group *Support team*, which holds *support* | read everything, change nothing |
| **alice** | administrator of Acme | admin inherits down, but not into Globex |
| **bob** | in Acme group *UK IT*, administrator of the Acme UK domain | access through a group, at domain level |
| **heidi** | in *UK contractors*, which is nested inside *UK IT* | access through a **nested** group |
| **carol** | in *Finance AP*, which is a spender on UK Operations | spender: may spend and add subscriptions, nothing else |
| **dave** | operator on one subscription | sees one subscription and nothing above it |
| **erin** | viewer of Acme | sees the customer and **nothing below it** (viewer does not inherit) |
| **grace** | administrator of Globex | the other customer: complete isolation |
| **frank** | nothing | the empty state |

The acceptance test for the whole build is this table: `authz/validation.yaml` asserts all 70 cells of the access matrix and
`zed validate` proves them before any application code runs.

## A ten-minute demonstration

Run the four terminals above. Reset to a clean slate at any point from **System → Reset data** (as Priya) or by restarting the
API. Times are rough.

**1. The whole picture (1 min).** Open the app. You start as **Priya**. *Resources* shows every object in the tree; the header
has a **System** link that no one else gets. Open **Acme UK → UK Operations**. The *Your access* panel lists every permission of
a wallet with a tick or a cross.

**2. Inheritance and isolation (2 min).** Switch to **Alice** (header selector; it is instant and clears all cached data). She
sees Acme and everything under it, and nothing of Globex. Open **Acme UK → Access** tab: *Inherited access* lists the
administrators of the customer and the platform, each labelled with where it comes from. Now switch to **Grace** and paste
`/resources/wallet/uk-ops` into the address bar: *"This wallet does not exist, or you do not have access to it."* Not a 403: the
system will not even confirm the wallet exists.

**3. Viewer does not inherit; disabled, not hidden (1 min).** Switch to **Erin**. She sees *Acme Ltd* and nothing beneath it.
On the customer page *Rename*, *Add domain* and *Delete* are disabled; hover one and the tooltip names the missing permission.
Direct API calls for those actions are refused with a 403 that names it too.

**4. Groups, nested groups, and removing one (2 min).** Switch to **Heidi** and open **Acme UK**: she administers it, through
*UK contractors*, which is nested inside *UK IT*. Switch to **Alice**, open the customer page and its **Groups of this
customer** link, and choose *UK contractors*. Try to add the group *UK IT* as a member: refused, because it would make a cycle.
Now open **UK IT** and remove its member **UK contractors**. Switch back to **Heidi**: everything is gone, on her very next
request. **Bob** is untouched, because he is in *UK IT* directly.

**5. A spender creates something, and gets just enough (1 min).** Switch to **Carol** and open **UK Operations**. *Top up* is
disabled; *Add subscription* is enabled. Add one. Carol can open it and *Change quantity*, but *Cancel subscription* is disabled:
the system gave her the **operator** role on the thing she created and nothing more. As **Alice**, open the new subscription's
**Access** tab: the audit shows the grant, by *system*.

**6. Moving things (1 min).** As **Alice**, open **UK R&D** and *Move to another domain* → **Acme Ireland**. Switch to **Bob**:
UK R&D and its GitHub subscription have vanished (his role is on Acme UK), while UK Operations is still his.

**7. Why? (1 min).** As **Alice**, open **Explorer**: user **Heidi**, object **Wallet: UK Operations**, permission **spend**,
**Check**. *Allowed*, with the trace the engine produced: the wallet's admin comes from its domain, whose administrators are the
members of *UK IT*, which include *UK contractors*, which Heidi belongs to.

**8. Fail closed, then heal (1 min).** Stop SpiceDB (`scripts/spicedb-local.sh stop`) and reload as anyone: every request is a
**503**, never an allow and never a stale answer. Start it again (`scripts/spicedb-local.sh start`). Within about ten seconds
the API notices the engine has lost its schema, rebuilds both stores and says so in its log. (Changes made since start-up
are lost; that is the memory datastore.) `curl localhost:5080/health` shows each stage.

Bonus, as **Priya**: **System** shows the outbox and reconciliation. *Run reconciliation* compares every parent and owner
reference in the database with SpiceDB; it never repairs on its own.

## How it works, briefly

- **The schema is the model.** `authz/schema.zed` defines five object types. Application code asks the engine about
  *permissions*, never relations. Relations are written in exactly two places: the projector and the grant commands.
- **Structure flows through an outbox.** Creating a wallet saves it and an outbox message in one transaction; a projector
  turns the message into the `wallet#parent` tuple. If SpiceDB is unreachable the API answers **202** with
  `authorisationPending`, the object stays invisible, and a background worker catches up (an outage never uses up retries).
- **Grants live only in SpiceDB**, written by Access Management with an audit row in the database. Three rules the schema
  cannot state (no cycles, nesting only within one owner, no granting another customer's group) are enforced there.
- **One enforcement filter** guards every `/api` route from per-endpoint metadata. A test fails if any route lacks a
  declaration, and the filter refuses an undeclared route rather than allowing it.
- **Two stores, always rebuilt together.** Both are in memory, so start-up wipes and reseeds both, a reconciliation job
  compares them, and an engine restart is detected and healed.

## Operating it

| | |
| --- | --- |
| **Health** | `GET /health` (anonymous, no data): `status`, `ready`, `engine` (`ok`, `schema-missing`, `unreachable`), outbox pending/failed/lag. 503 until the system is genuinely ready. |
| **Logs** | Every permission check at *debug* (subject, permission, resource, result, duration, trace id); grants and revokes at *information*; recovery at *warning*. No token and no email address is ever logged (a test checks it across a busy session). |
| **Traces** | OpenTelemetry: an ASP.NET span per request and a `spicedb.*` span per engine call in the same trace. Set `OTEL_EXPORTER_OTLP_ENDPOINT` to export, or `Telemetry__Console=true` to print them. |
| **Reset** | System screen → *Reset data*, or `POST /api/system/reset` as a platform admin. |

### Configuration

Everything environment-specific comes from configuration (environment variables use `__` for nesting).

| Setting | Meaning |
| --- | --- |
| `Poc__SpiceDb__PresharedKey` | **Required.** SpiceDB key. No default in the API; Compose and the dev helper supply a development one. |
| `Poc__SpiceDb__Endpoint` | SpiceDB gRPC address (default `http://localhost:50051`). |
| `Poc__SpiceDb__AllowInsecureTransport` | Permit plain HTTP to SpiceDB (local and private networks only). |
| `Authentication__Authority` | **Required.** The issuer tokens name. |
| `Authentication__MetadataAddress` | Where to fetch discovery and keys, if different from the authority (containers). |
| `Authentication__Audience` | Expected audience (default `poc-api`). |
| `Cors__WebOrigin` | The single origin the API allows (default `http://localhost:5173`). |
| `VITE_API_URL`, `VITE_ISSUER_URL` | Client build-time addresses. Empty `VITE_API_URL` means "the origin I was served from". |

## Tests

```bash
./verify.sh      # the only definition of "passing"; CI runs the same script
```

It runs, in order: `zed validate` over the matrix; the schema loaded into a live SpiceDB; format, build (warnings are
errors) and every .NET test; the generated client types; the client's type check, lint and unit tests; the browser journeys
in Chrome against the dev stack and again against the published build; and that nothing was left running.

The .NET integration tests run against a **real SpiceDB**, not a mock. Highlights: the full 10 × 7 matrix over HTTP; every
behaviour scenario; an architecture test listing who may touch tuples; fault injection (lost responses, a plan failing part
way, a flapping engine, a real engine freeze, a real engine *restart*); and, after every HTTP test, a reconciliation check
that the database and the engine agree.

## Layout

```
authz/            schema.zed and validation.yaml (the matrix)
src/Poc.Domain    aggregates and events, references nothing
src/Poc.Application   use cases, ports, permission constants
src/Poc.Infrastructure  EF Core, outbox, projector, SpiceDB adapter, seeding, recovery
src/Poc.Api       endpoints, authentication, the enforcement filter
src/Poc.DevIssuer the stub token issuer
web/              the React client, its generated API types, and the Playwright journeys
tests/            unit + architecture tests, and integration tests against a real SpiceDB
scripts/          the dev SpiceDB helper, the published-build runner, the Compose checks
```

## Known limits

- **Memory datastores.** A restart of SpiceDB or the API loses all data by design; the system rebuilds the seed scenario.
- **No real directory.** Users come from the stub issuer; there is no sign-up, invitation, time-bound grant or billing.
- **Docker images are unverified** (see above).
- **The client bundle is not code-split** and there is no response compression or caching: this is a demonstration, not a
  tuned service.
