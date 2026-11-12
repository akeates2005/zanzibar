# Zanzibar authorisation proof of concept: plan and architecture

Nov 11, 2026 · @Andrew Keates

The proof of concept is a .NET modular monolith with SpiceDB as the authorisation engine, a local stub token issuer and a React client. It is built in six phases, and each phase ends in a test that must pass. This document is the specification a coding agent implements from.

## Decisions and scope

Four decisions came from Andrew: SpiceDB, a .NET API with a React client, an in-memory database, and a local stub issuer with a platform and support root. The rest are mine, made to keep the build small and the result trustworthy. Each is listed so it can be challenged.

| Area | Decision | Reason |
| --- | --- | --- |
| Engine | SpiceDB in Docker, memory datastore, pinned release tag | Chosen. Matches the schema drafted in discussion. |
| API | .NET 10 (LTS), ASP.NET Core minimal APIs, one deployable | A modular monolith keeps contexts separate without distributed plumbing. |
| Application database | SQLite in in-memory mode through EF Core, one shared open connection | Honours the in-memory choice. The EF Core InMemory provider ignores transactions, and the outbox depends on them. |
| Client | React 19, TypeScript, Vite, TanStack Query, React Router, Mantine | Conventional, quick to build, typed end to end. |
| Sign-in | A small stub issuer project that publishes OIDC discovery and signing keys | Chosen. The API validates tokens exactly as it would for B2C. |
| User identifier | Internal user ID in an `extension_userId` claim | Matches the B2C custom attribute approach agreed earlier. |
| Inheritance | Admin inherits downward. Support read access inherits downward. Viewer, spender and operator do not. | Admin inheritance is the stated requirement. Support needs a read path. Everything else stays explicit. |
| Groups | Owned by one customer or by the platform. Nested groups allowed, cycles rejected. | Prevents one customer's group being granted on another customer's objects. |
| Grants | Stored only in SpiceDB, with an audit row in the application database | The engine is the source of truth for grants from day one. No role tables exist to migrate. |
| Consistency | Every check and lookup runs fully consistent | Simple and always correct at this scale. The token from each write is stored, so production can relax it later. |
| Restart behaviour | The API wipes SpiceDB relationships and reseeds both stores at start-up | Both stores are in memory. Reseeding together is the only way they stay aligned. |

Out of scope: real Azure AD B2C, invitations, time-bound grants, legacy shadow mode, pricing and wallet debits, production hosting, and performance tuning. The design leaves a seam for each, noted where relevant.

## Architecture

The API is one process with three internal contexts, and only two of its components ever talk to SpiceDB. Commerce code checks permissions through a port and never writes tuples. The projector turns domain events into structural tuples. Access Management writes grant tuples directly.

&#91;embedded content: component architecture · 8 components, 9 calls\]

Commerce reaches the engine only to ask questions. The Identity context is omitted from the picture; it reads and writes the application database and nothing else.

| Component | Responsibility | Talks to |
| --- | --- | --- |
| Web client (`web/`) | Object screens, access screens, user switcher | API, stub issuer |
| Stub issuer (`Poc.DevIssuer`) | Issues signed tokens for seeded users; publishes discovery and keys | None |
| Commerce context | Customers, domains, wallets, subscriptions; raises domain events | Application database, authorisation port |
| Access Management context | Grants, groups, effective access, permission explorer | Authorisation port, relationship store, application database |
| Identity context | User directory used by pickers and by the stub issuer seed | Application database |
| Authorisation projector | Maps outbox events to structural tuples; reconciles drift | Application database, relationship store |
| SpiceDB adapter | Implements the authorisation port and relationship store over gRPC | SpiceDB |
| SpiceDB | Stores the schema and tuples; answers checks and lookups | None |

### Solution layout

```
/docker-compose.yml          spicedb, api, issuer, web
/authz/schema.zed            the schema in this document
/authz/validation.yaml       assertions run by zed validate
/src/Poc.Domain              aggregates, domain events, value objects
/src/Poc.Application         use cases, ports, permission constants
/src/Poc.Infrastructure      EF Core, outbox, projector, SpiceDB adapter, seeding
/src/Poc.Api                 endpoints, authentication, enforcement filter
/src/Poc.DevIssuer           stub token issuer
/web                         React client
/tests/Poc.UnitTests
/tests/Poc.IntegrationTests  API plus a real SpiceDB container
/web/e2e                     Playwright journeys
```

`Poc.Domain` references nothing. `Poc.Application` references only `Poc.Domain`. The `Authzed.Net` package is referenced by `Poc.Infrastructure` alone, and an architecture test enforces that.

### Ports

```csharp
public interface IAuthorizer
{
    Task<bool> CheckAsync(SubjectRef subject, string permission, ResourceRef resource, CancellationToken ct);
    Task<IReadOnlyDictionary<string, bool>> CheckManyAsync(SubjectRef subject, ResourceRef resource,
        IReadOnlyCollection<string> permissions, CancellationToken ct);
    Task<IReadOnlyList<string>> LookupResourcesAsync(SubjectRef subject, string resourceType,
        string permission, CancellationToken ct);
    Task<IReadOnlyList<SubjectRef>> LookupSubjectsAsync(ResourceRef resource, string permission,
        string subjectType, CancellationToken ct);
}

public interface IRelationshipStore
{
    Task<string> WriteAsync(IReadOnlyCollection<RelationshipUpdate> updates, CancellationToken ct); // returns ZedToken
    Task<IReadOnlyList<Relationship>> ReadAsync(RelationshipFilter filter, CancellationToken ct);
    Task<string> DeleteAsync(RelationshipFilter filter, CancellationToken ct);
}
```

`CheckManyAsync` uses `CheckBulkPermissions`. Lookups page through the cursor until exhausted. All calls carry a five-second deadline and surface failure as a typed `AuthorizationUnavailableException`, which the API maps to HTTP 503. A failed check never falls back to allow.

## Authorisation model

The schema below is the complete model and is saved as `authz/schema.zed`. Application code checks permissions only, never relations. Relations appear in exactly two places: the projector and the grant commands.

```
definition user {}

definition platform {
    relation administrator: user | group#member
    relation support: user | group#member

    permission admin = administrator
    permission support_view = support
    permission view = admin + support_view
    permission manage_access = admin
    permission create_customer = admin
    permission create_group = admin
}

definition group {
    relation owner: customer | platform
    relation member: user | group#member

    permission manage = owner->admin
    permission view = manage + member
}

definition customer {
    relation parent: platform
    relation administrator: user | group#member
    relation viewer: user | group#member

    permission admin = administrator + parent->admin
    permission support_view = parent->support_view
    permission view = viewer + admin + support_view
    permission edit = admin
    permission delete = parent->admin
    permission manage_access = admin
    permission create_domain = admin
    permission create_group = admin
}

definition domain {
    relation parent: customer
    relation administrator: user | group#member
    relation viewer: user | group#member

    permission admin = administrator + parent->admin
    permission support_view = parent->support_view
    permission view = viewer + admin + support_view
    permission edit = admin
    permission delete = parent->admin
    permission manage_access = admin
    permission create_wallet = admin
}

definition wallet {
    relation parent: domain
    relation administrator: user | group#member
    relation spender: user | group#member
    relation viewer: user | group#member

    permission admin = administrator + parent->admin
    permission support_view = parent->support_view
    permission spend = spender + admin
    permission view = viewer + spend + support_view
    permission edit = admin
    permission top_up = admin
    permission move = admin
    permission delete = parent->admin
    permission manage_access = admin
    permission create_subscription = spend
}

definition subscription {
    relation parent: wallet
    relation administrator: user | group#member
    relation operator: user | group#member
    relation viewer: user | group#member

    permission admin = administrator + parent->admin
    permission support_view = parent->support_view
    permission change_quantity = operator + admin
    permission view = viewer + change_quantity + support_view
    permission cancel = admin
    permission delete = parent->admin
    permission manage_access = admin
}
```

### What each assignable role gives

| Level | Role (relation) | Permissions on that object | Flows to children |
| --- | --- | --- | --- |
| Platform | administrator | Everything, on every object | Yes, as admin |
| Platform | support | view | Yes, as view only |
| Customer, domain | administrator | view, edit, manage\_access, create child | Yes, as admin |
| Customer, domain | viewer | view | No |
| Wallet | administrator | view, edit, top\_up, move, spend, manage\_access | Yes, as admin |
| Wallet | spender | view, spend, create\_subscription | No |
| Wallet | viewer | view | No |
| Subscription | administrator | view, change\_quantity, cancel, manage\_access | Not applicable |
| Subscription | operator | view, change\_quantity | Not applicable |
| Subscription | viewer | view | Not applicable |

### Rules the schema cannot express

Three rules live in the Access Management service, because a Zanzibar schema has no way to state them. Each has a dedicated test.

1. A group may be granted on an object only if the group's owner is that object's root customer. Platform groups may be granted anywhere, and only by a platform administrator.
2. A group may be nested in another group only if both have the same owner.
3. Nesting group B inside group A is rejected if A is already a member of B, directly or indirectly. The service checks `group:B#member@group:A#member` before writing.

Deleting an object requires admin on its parent, not on the object itself. An administrator granted on a single wallet can run it but cannot remove it.

### Permission constants

`Poc.Application` holds one static class of permission and relation names per object type. A unit test parses `schema.zed` and fails if any constant is missing from the schema, or any schema permission has no constant.

## Domain model and data

The application database holds the business objects, the user and group directories, the outbox and the grant audit. It holds no grants and no roles.

| Table | Key columns | Notes |
| --- | --- | --- |
| `Customers` | `Id`, `Name` | Root aggregate. Parent is always `platform:main`. |
| `Domains` | `Id`, `CustomerId`, `Name` | Name unique within the customer. |
| `Wallets` | `Id`, `DomainId`, `Name`, `Balance` | Balance changes only through top-up. |
| `Subscriptions` | `Id`, `WalletId`, `ProductName`, `Quantity`, `Status` | Status is Active or Cancelled. Quantity must be 1 or more. |
| `Users` | `Id`, `DisplayName`, `Email` | Directory for pickers. The engine never reads it. |
| `Groups` | `Id`, `OwnerType`, `OwnerId`, `Name` | Owner is a customer or the platform. Membership lives in SpiceDB. |
| `OutboxMessages` | `Sequence`, `Type`, `Payload`, `CreatedAt`, `ProcessedAt`, `Attempts`, `LastError` | Written in the same transaction as the aggregate. |
| `GrantAudit` | `Id`, `Action`, `ResourceType`, `ResourceId`, `Relation`, `SubjectType`, `SubjectId`, `ActorUserId`, `At`, `Status`, `ZedToken` | Action is Grant or Revoke. Status is Pending, Applied or Failed. |

Every business table also carries `CreatedAt`, `CreatedBy`, a `RowVersion` concurrency token and `AuthzToken`, the ZedToken from the last tuple write for that row.

### Identifiers

Identifiers are strings. Generated ones are version 7 GUIDs in lower case without dashes. Seed data uses fixed readable identifiers such as `acme` and `uk-ops`, so tuples are easy to read in demonstrations. The same identifier is used in the database and in the tuple, with no translation layer.

### Aggregates and events

Each aggregate raises a domain event for every structural change, and the repository writes those events to the outbox inside the same transaction. The events are `CustomerCreated`, `CustomerDeleted`, `DomainCreated`, `DomainDeleted`, `WalletCreated`, `WalletMoved`, `WalletDeleted`, `SubscriptionCreated`, `SubscriptionDeleted`, `GroupCreated` and `GroupDeleted`. Renames, top-ups, quantity changes and cancellation raise no authorisation event, because they change no relationship.

Deleting an object that still has children is refused with HTTP 409. This removes the orphan problem: a child can never outlive the parent its inherited admins come from.

### SQLite in memory

The connection string is `Data Source=poc;Mode=Memory;Cache=Shared`. The API opens one keep-alive connection at start-up and holds it for the life of the process, because the database vanishes when the last connection closes. Each `DbContext` opens its own connection to the same named database. The schema is created with `EnsureCreated`, and foreign keys are switched on. SQLite allows one writer at a time, so command handlers must not hold a transaction open across a SpiceDB call. `RowVersion` is an integer the repository increments, since SQLite has no native row version.

## Tuple lifecycle

Structural tuples are written by the projector from outbox events. Grant tuples are written by Access Management commands. No other code writes to SpiceDB.

### Structural tuples

| Event | Tuple operations, in one write request |
| --- | --- |
| `CustomerCreated` | Touch `customer:{id}#parent@platform:main` |
| `DomainCreated` | Touch `domain:{id}#parent@customer:{customerId}` |
| `WalletCreated` | Touch `wallet:{id}#parent@domain:{domainId}` |
| `WalletMoved` | Delete `wallet:{id}#parent@domain:{from}` and touch `wallet:{id}#parent@domain:{to}` |
| `SubscriptionCreated` | Touch `subscription:{id}#parent@wallet:{walletId}` |
| `GroupCreated` | Touch `group:{id}#owner@customer:{ownerId}` or `group:{id}#owner@platform:main` |
| Any `...Deleted` for a business object | Delete every relationship where the object is the resource |
| `GroupDeleted` | Delete every relationship where the group is the resource, then every relationship whose subject is `group:{id}#member`, one delete per resource type |

Touch is SpiceDB's upsert, so replaying an event is harmless. Deletes are idempotent by nature.

### How a command runs

1. The endpoint filter checks the caller's permission through the port.
2. The handler changes the aggregate and saves. The row and its outbox messages commit together.
3. The handler calls `projector.FlushAsync()`, which processes all pending outbox messages in sequence order and stores the returned ZedToken on the affected row.
4. If the flush succeeds, the API returns 200 or 201. If SpiceDB is unreachable, the API returns 202 with `authorisationPending: true`, and the object stays invisible until the background worker catches up.

A hosted background service calls the same flush every two seconds. Flushing is guarded by a semaphore so only one runs at a time and order is preserved. A message that fails five times is left unprocessed with its error recorded, blocks later messages for the same object, and shows on the System screen.

### Default grant on creation

A subscription can be created by a wallet spender who is not an admin. Without a grant, that person could not see what they just created. When `SubscriptionCreated` is processed and the creator lacks `admin` on the new subscription, a policy issues a normal grant command making the creator its `operator`. The grant goes through the audit like any other. No other creation writes a default grant, because every other create permission already requires admin.

### Grant tuples

A grant or revoke runs in this order: check `manage_access` on the resource, apply the service rules from the authorisation model, write a Pending audit row, write the tuple, then mark the audit row Applied with the ZedToken. If the tuple write fails, the row is marked Failed and the API returns 503. Grants use touch and revokes use delete, so repeating either is safe.

Group membership follows the same sequence, with `manage` on the group as the required permission and `group:{id}#member@user:{userId}` or `group:{id}#member@group:{childId}#member` as the tuple.

### Start-up and reconciliation

At start-up the API waits for SpiceDB, writes the schema, deletes all relationships for every object type, seeds the database and flushes the outbox. Seed grants are then issued through the real grant commands, with a system actor, so the audit is complete from the first row.

A reconciliation job compares every parent and owner reference in the database with the matching tuples in SpiceDB, in both directions. It runs every 60 seconds and on demand from the System screen. It reports missing and unexpected tuples and repairs nothing by itself; a platform administrator triggers repair explicitly.

SpiceDB's memory datastore loses everything if its container restarts. The health check therefore reads the schema on each probe. If the schema is missing while the API is running, the API marks itself unready, runs the full start-up sequence again and logs the reset.

## API

Every endpoint declares the permission it requires as endpoint metadata, and one filter enforces it. A test enumerates all endpoints and fails if any lacks the declaration, so an unprotected route cannot ship unnoticed.

### Enforcement rules

1. Authentication is required everywhere except `/health`. The fallback policy denies anonymous callers.
2. The filter resolves the caller from the `extension_userId` claim and rejects tokens without it.
3. For a route on an existing object, the filter bulk-checks `view` and the required permission together. No `view` returns 404, so existence is never leaked. `view` without the required permission returns 403 naming the missing permission.
4. For a create route, the check is made on the parent object.
5. List endpoints return only objects the caller can view, using `LookupResources` intersected with the database query.
6. Every object response includes a `permissions` map for the caller, computed with one bulk check. The client uses it to enable controls. The server never trusts it.
7. Errors use RFC 9457 problem details. SpiceDB failure returns 503.

### Object endpoints

| Method and route | Required permission | On |
| --- | --- | --- |
| `GET /api/me` | Authenticated | None |
| `GET /api/tree` | Authenticated; returns viewable objects with ancestor names | None |
| `POST /api/customers` | `create_customer` | `platform:main` |
| `GET /api/customers/{id}` | `view` | Customer |
| `PATCH /api/customers/{id}` | `edit` | Customer |
| `DELETE /api/customers/{id}` | `delete` | Customer |
| `POST /api/customers/{id}/domains` | `create_domain` | Customer |
| `GET`, `PATCH`, `DELETE /api/domains/{id}` | `view`, `edit`, `delete` | Domain |
| `POST /api/domains/{id}/wallets` | `create_wallet` | Domain |
| `GET`, `PATCH`, `DELETE /api/wallets/{id}` | `view`, `edit`, `delete` | Wallet |
| `POST /api/wallets/{id}/top-up` | `top_up` | Wallet |
| `POST /api/wallets/{id}/move` | `move` on the wallet and `create_wallet` on the target domain | Wallet, target domain |
| `POST /api/wallets/{id}/subscriptions` | `create_subscription` | Wallet |
| `GET`, `DELETE /api/subscriptions/{id}` | `view`, `delete` | Subscription |
| `POST /api/subscriptions/{id}/quantity` | `change_quantity` | Subscription |
| `POST /api/subscriptions/{id}/cancel` | `cancel` | Subscription |
| `GET /api/{parentType}/{id}/children` | `view` on the parent; children filtered by `view` | Parent |

A wallet may move only to a domain under the same customer. Moving across customers is refused with 422, because the wallet's group grants would then point at another customer's groups.

### Access management endpoints

| Method and route | Required permission | Purpose |
| --- | --- | --- |
| `GET /api/access/{type}/{id}/grants` | `manage_access` | Direct grants, plus inherited admin and support grants from each ancestor, labelled with their source |
| `PUT /api/access/{type}/{id}/grants` | `manage_access` | Grant a relation to a user or a group |
| `DELETE /api/access/{type}/{id}/grants` | `manage_access` | Revoke one grant |
| `GET /api/access/{type}/{id}/effective?permission=` | `manage_access` | Users who hold the permission, resolved through groups and inheritance |
| `POST /api/access/explain` | `manage_access` on the resource in the request | Allowed or denied for any user, permission and resource, with the check trace where the pinned SpiceDB version returns one |
| `GET /api/access/audit?type=&id=` | `manage_access` | Grant audit for the resource |
| `GET`, `POST /api/customers/{id}/groups` | `view`, `create_group` on the customer | List and create customer groups |
| `GET`, `POST /api/platform/groups` | `view`, `create_group` on the platform | List and create platform groups |
| `GET`, `DELETE /api/groups/{id}` | `view`, `manage` on the group | Group detail with members and where it is granted |
| `PUT`, `DELETE /api/groups/{id}/members` | `manage` on the group | Add or remove a user or a nested group |
| `GET /api/users?query=` | Authenticated | Directory search for pickers |

### System endpoints

| Method and route | Required permission | Purpose |
| --- | --- | --- |
| `GET /health` | None | Liveness, SpiceDB reachability, outbox lag |
| `GET /api/system/outbox` | `admin` on the platform | Pending and failed messages |
| `POST /api/system/reconcile` | `admin` on the platform | Run reconciliation; optional `repair=true` |
| `POST /api/system/reset` | `admin` on the platform | Wipe and reseed both stores |

Grant requests name the relation, and the service validates it against the relations allowed for that object type. A request for an unknown relation, an unknown subject or a subject type the relation does not accept returns 422 before anything is written.

## User interfaces

The client has two halves that share one shell: screens for working with objects, and screens for managing who can. Switching the signed-in user in the top bar is the main demonstration device, so it must be instant and must clear all cached data.

| Screen | Route | What the user does there | Visible when |
| --- | --- | --- | --- |
| Shell and user switcher | All | Pick a seeded user; see name and identifier; navigate | Always |
| Resource browser | `/resources` | Browse the tree of viewable objects; ancestors the user cannot view show as plain labels | Always; empty state for a user with no access |
| Object detail | `/resources/{type}/{id}` | See fields, children and a "Your access" panel; run actions | `view` on the object |
| Access tab | `/resources/{type}/{id}/access` | See direct and inherited grants; grant and revoke; see who holds a permission; read the audit | `manage_access` on the object |
| Groups | `/customers/{id}/groups`, `/platform/groups` | Create groups; add and remove users and nested groups; see where a group is granted | `view` on the owner; edit controls need `manage` |
| Permission explorer | `/explorer` | Pick a user, a permission and an object; see allowed or denied and why | `manage_access` on the chosen object |
| System | `/system` | See outbox state and reconciliation results; reset the data | Platform `admin` |

### Object detail

Each object type has its own action set, taken from the `permissions` map in the response. An action the user lacks is shown disabled with a tooltip naming the missing permission, not hidden. Hiding would make the proof of concept harder to read. A direct call to the API for a disabled action must still return 403, and an end-to-end test proves it.

| Object | Actions |
| --- | --- |
| Customer | Rename, add domain, delete |
| Domain | Rename, add wallet, delete |
| Wallet | Rename, top up, move to another domain, add subscription, delete |
| Subscription | Change quantity, cancel, delete |

The "Your access" panel lists every permission for the object type with a tick or a cross for the current user.

### Access tab

The tab has four parts. Direct grants is an editable table of subject, subject type, role, granted by and date, with a remove control per row. Inherited access is a read-only table showing each ancestor's administrators and the platform's support subjects, each linked to the object it comes from. Add grant is a form with a user or group picker and a role dropdown limited to that object type's relations; the group picker offers only groups the service rules allow. Who can is a permission dropdown that lists the users holding it, however they got it.

### Client rules

All server state goes through TanStack Query, keyed by user identifier. Switching user fetches a new token from the stub issuer, replaces it in memory and clears the query cache. Tokens are never written to local storage. Every mutation invalidates the tree, the affected object and its access data. API types are generated from the API's OpenAPI document, not written by hand.

## Identity

The API authenticates with the standard JWT bearer handler pointed at an authority URL, so moving to B2C later is a configuration change. The stub issuer exists only to stand in for that authority.

| Endpoint on the stub issuer | Purpose |
| --- | --- |
| `GET /.well-known/openid-configuration` | Discovery document naming the issuer and the keys endpoint |
| `GET /.well-known/jwks.json` | Public signing key |
| `GET /dev/users` | Seeded users for the switcher |
| `POST /dev/token` with a user identifier | A signed access token for that user, valid for 60 minutes |

The issuer generates an RSA key at start-up and signs with RS256. It refuses to start unless the environment is Development, and the Compose file binds it to localhost only.

### Token contents

| Claim | Value | Used by the API |
| --- | --- | --- |
| `iss`, `aud`, `exp` | Issuer URL, API audience, expiry | Validated |
| `sub` | A random GUID per user, standing in for the B2C object ID | Ignored for authorisation |
| `extension_userId` | The internal user identifier | Becomes the SpiceDB subject `user:{value}` |
| `name`, `emails` | Display values | Shown in the shell only |

The token carries no roles and no groups. That absence is deliberate: every resource-level decision comes from the engine. A token without `extension_userId` is rejected with 401, and the API never falls back to `sub`.

### Moving to B2C or External ID

Three settings change: authority, audience and the claim name if the directory emits it differently. The client replaces the user switcher with MSAL. The directory must populate `extension_userId` at sign-up through an API connector or a custom policy step, and must never expose it in a profile edit flow.

## Seed scenario

The seed data is two customers and ten users, chosen so that every inheritance path and every isolation boundary has at least one user who exercises it. The access matrix below is the acceptance test for the whole build.

### Objects

```
platform:main
  customer:acme
    domain:acme-uk
      wallet:uk-ops        subscription:m365-e3, subscription:azure-plan
      wallet:uk-rnd        subscription:github
    domain:acme-ie
      wallet:ie-ops        subscription:m365-bp
  customer:globex
    domain:globex-main
      wallet:globex-ops    subscription:dynamics
```

### Users, groups and grants

| User | How access is granted | Tuples |
| --- | --- | --- |
| priya | Direct platform administrator | `platform:main#administrator@user:priya` |
| sam | Member of platform group `support-team`, which holds support | `group:support-team#member@user:sam`, `platform:main#support@group:support-team#member` |
| alice | Direct administrator of Acme | `customer:acme#administrator@user:alice` |
| bob | Member of Acme group `uk-it`, administrator of the UK domain | `group:uk-it#member@user:bob`, `domain:acme-uk#administrator@group:uk-it#member` |
| heidi | Member of `uk-contractors`, which is nested in `uk-it` | `group:uk-contractors#member@user:heidi`, `group:uk-it#member@group:uk-contractors#member` |
| carol | Member of Acme group `finance-ap`, spender on one wallet | `group:finance-ap#member@user:carol`, `wallet:uk-ops#spender@group:finance-ap#member` |
| dave | Direct operator on one subscription | `subscription:m365-e3#operator@user:dave` |
| erin | Direct viewer of Acme | `customer:acme#viewer@user:erin` |
| grace | Direct administrator of Globex | `customer:globex#administrator@user:grace` |
| frank | Nothing | None |

### Expected access

| User | view customer acme | admin domain acme-uk | spend wallet uk-ops | change\_quantity m365-e3 | cancel m365-e3 | view wallet ie-ops | view customer globex |
| --- | --- | --- | --- | --- | --- | --- | --- |
| priya | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| sam | Yes | No | No | No | No | Yes | Yes |
| alice | Yes | Yes | Yes | Yes | Yes | Yes | No |
| bob | No | Yes | Yes | Yes | Yes | No | No |
| heidi | No | Yes | Yes | Yes | Yes | No | No |
| carol | No | No | Yes | No | No | No | No |
| dave | No | No | No | Yes | No | No | No |
| erin | Yes | No | No | No | No | No | No |
| grace | No | No | No | No | No | No | Yes |
| frank | No | No | No | No | No | No | No |

Erin's row is the one to watch: a customer viewer sees the customer and nothing below it. That is the stated inheritance rule applied literally, and it is listed under open questions.

### Behaviour scenarios

The matrix proves the static model. These scenarios prove that change takes effect at once, and each is an integration test.

| Scenario | Expected result |
| --- | --- |
| Alice moves `uk-rnd` from `acme-uk` to `acme-ie` | Bob and Heidi lose all access to `uk-rnd` and `github` on the next request |
| Alice removes `uk-contractors` from `uk-it` | Heidi loses all access; Bob keeps his |
| Carol creates a subscription in `uk-ops` | Carol can view it and change its quantity, cannot cancel it, and the audit shows the default grant |
| Carol tries to top up `uk-ops` | 403 naming `top_up` |
| Dave requests `wallet:uk-ops` | 404, not 403 |
| Grace tries to grant an Acme group on a Globex wallet | 422, nothing written |
| Alice tries to nest `uk-it` inside `uk-contractors` | 422, cycle rejected |
| Alice tries to delete `acme-uk` while it has wallets | 409 |
| Alice revokes Dave's operator grant | Dave's next request for `m365-e3` returns 404 |
| The SpiceDB container is paused and Alice creates a wallet | 202 with the pending flag; after it is unpaused the wallet appears and reconciliation reports no drift |

## Testing and quality gates

The build is accepted on tests against a real SpiceDB, not on mocks of it. A mocked engine would only prove that the code agrees with my assumptions about SpiceDB.

| Layer | Tooling | What it proves |
| --- | --- | --- |
| Schema | `zed validate` over `authz/validation.yaml` | The seed tuples and the expected access matrix hold in the schema alone, before any application code exists |
| Unit | xUnit | Aggregate invariants, event-to-tuple mapping, service rules, permission constants match the schema |
| Architecture | xUnit with NetArchTest | Layer references; only Infrastructure references `Authzed.Net`; every endpoint declares a permission |
| Integration | xUnit, `WebApplicationFactory`, Testcontainers running the pinned SpiceDB image | The full access matrix through the HTTP API, once per user and column; every behaviour scenario |
| Resilience | Integration tests with a fault-injecting relationship store | Outbox retry, the 202 path, idempotent replay, reconciliation finds seeded drift |
| End to end | Playwright against Compose | Five journeys: admin manages a wallet, spender creates a subscription, viewer sees disabled actions, admin grants and revokes through the Access tab, user with no access sees the empty state |

### Gates

Nullable reference types are on and warnings are errors in every .NET project. The client builds under TypeScript strict mode with ESLint clean. One command, `./verify.sh`, runs formatting checks, all builds, schema validation and every test layer, and it is the only definition of passing. Continuous integration runs the same script.

Three fitness functions stay in the suite permanently: the permission constants test, the endpoint declaration test, and the reconciliation check asserting no drift after the full integration run.

### Observability

Every permission check logs subject, permission, resource, result and duration at debug level, with the request's trace identifier. Grant and revoke log at information level. The API exposes OpenTelemetry traces with a span per SpiceDB call. No token and no email address is ever logged.

## Build order

The phases run in sequence, and a phase is finished only when its exit criterion passes inside `./verify.sh`. The model is proven first, in isolation, because every later phase depends on it being right.

| Phase | Delivers | Exit criterion |
| --- | --- | --- |
| 1. Model | Repository skeleton, Compose with SpiceDB, `schema.zed`, `validation.yaml` holding the seed tuples and the access matrix, `verify.sh` | `zed validate` passes with every matrix cell asserted |
| 2. Engine adapter | Ports, SpiceDB adapter, schema writer, permission constants, Testcontainers fixture | Adapter integration tests pass for check, bulk check, both lookups, write, read and delete |
| 3. Domain and projection | Aggregates, EF Core on in-memory SQLite, outbox, projector, start-up sequence, seeding, reconciliation | After start-up, reconciliation reports no drift and the matrix passes through the port |
| 4. API | Stub issuer, authentication, enforcement filter, object endpoints, access and group endpoints, system endpoints, OpenAPI document | The matrix and every behaviour scenario pass through HTTP; the endpoint declaration test passes |
| 5. Client | Shell, user switcher, resource browser, object detail, Access tab, groups, explorer, System screen | The five Playwright journeys pass against Compose |
| 6. Hardening | Fault-injection tests, health and schema-loss recovery, logging and tracing, README with a ten-minute demonstration script | Resilience tests pass; `docker compose up` from a clean clone reaches a working application with no manual step |

### Rules for the coding agent

1. Implement this document as written. Where it is silent, choose the simplest option and record the choice in `DECISIONS.md` with one line of reasoning.
2. Where this document appears wrong or contradicts SpiceDB's actual behaviour, stop and report it. Do not work around it silently.
3. Check the pinned SpiceDB release and `Authzed.Net` version against their current documentation before writing the adapter. API names in this document are from memory and search, not from a compiled build.
4. Never weaken a test to make it pass. A matrix cell that fails is a defect in the schema or the code, and the matrix changes only with Andrew's agreement.
5. Never add a code path that allows access when the engine is unavailable or returns an error.
6. Never write a tuple from Commerce code, and never check a relation where a permission exists.
7. Commit at the end of each phase with `./verify.sh` green, and include its output summary in the commit message.
8. Keep secrets out of the repository. The SpiceDB preshared key comes from an environment variable with a development default in Compose only.

### Run-time defaults

| Service | Port | Notes |
| --- | --- | --- |
| Web client | 5173 | Vite in development; static build served by the API container in Compose |
| API | 5080 | CORS allows the web origin only |
| Stub issuer | 5090 | Development environment only |
| SpiceDB gRPC | 50051 | `serve --datastore-engine memory`, preshared key from the environment |

## Open questions

None of these blocks the build. Each has a default already in the design, and each changes a few schema lines and matrix cells if the answer differs.

| Question | Default in this design | Cost of changing later |
| --- | --- | --- |
| Should a viewer at customer or domain level see the objects below? | No. Only admin and support inherit. | One `+ parent->view` per definition; several matrix cells flip |
| May users see the names of ancestors they cannot view, for breadcrumbs? | Yes, names only, never as links | Client and tree endpoint change; no schema change |
| Is "spender can create subscriptions" the right meaning of spend? | Yes | One line in the wallet definition |
| Should deleting an object need admin on its parent, or on itself? | On its parent | One line per definition |
| Is there a reseller or partner layer between platform and customer? | No | A new definition and a parent change on customer; cheapest to add before real data exists |
| Should a wallet ever move between customers? | No, refused | Needs a rule for the wallet's existing group grants |
| Who may search the user directory? | Any signed-in user | Must be scoped per customer before real users are loaded |
| Will internal staff groups come from Entra? | Not in the proof of concept; platform groups are native | A sync job writing `group#member` tuples; no schema change |

## Sources

Version facts below come from search results on 11 November 2026 and were not checked against a compiled build. Rule 3 for the coding agent covers that gap.

- [Authzed.Net on NuGet](https://www.nuget.org/profiles/authzed): the official SpiceDB client for .NET, version 1.5.0, targeting .NET 8 or later.
- [authzed-dotnet README](https://www.nuget.org/packages/Authzed.Net): client set-up over gRPC with a bearer token.
- [SpiceDB module page](https://pkg.go.dev/github.com/authzed/spicedb@v1.54.0): release line and container usage.
- [AuthZed April 2026 release notes](https://authzed.com/changelog/april-2026-release): YAML validation files and zed 1.0.
- [Azure AD B2C FAQ, Microsoft Learn](https://learn.microsoft.com/azure/active-directory-b2c/faq): end of sale and support to at least May 2030.
