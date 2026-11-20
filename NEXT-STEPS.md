# Next steps (handover, 2026-11-17)

What to build next, in order, for the agent that picks this up. Read `CLAUDE.md` first (rules, commands, hooks), then
the D8 section of `DECISIONS.md`. This file says *what* and *why*; `DECISIONS.md` holds the evidence.

## Where things stand

- **The PoC is built and green.** `./verify.sh` passes 12 stages at commit `69cf107`. A clean clone passed on the same
  code just before the last `DECISIONS.md` notes were added. Phases 1-4 are accepted, with the agreed deviations D1, D2
  and D4; phases 5 and 6 are **complete except D3/D7**, because Docker has never run here.
- **Direction (Andrew's working assumptions, 2026-11-15):** the PoC grows into a reusable authorisation layer for several
  .NET app teams that serve the same customers. One shared SpiceDB; a platform team owns `user`, `platform`, `customer`,
  `group`, every grant and a central Access Management service; app teams own their resource types and get NuGet
  packages. He chose these from options offered as recommended; treat them as working assumptions, not commitments.
- **D8 is done:** team-prefixed types (`commerce/wallet`), and every API route names its object in the query string.

## How to read the status labels

| Label | Meaning |
|---|---|
| **Decided** | Andrew agreed. Implement it. |
| **Proposed** | Designed and checked, not agreed. Confirm with Andrew (`AskUserQuestion`, with a recommendation) before building. |
| **Open** | Needs a design decision first. Bring options, don't build. |
| **In progress** | Being built now. This is where the app is going: where it disagrees with the app, follow the plan. |
| **Done** | Finished. Change the status from In progress to Done when the work is complete. |

## Environment limits that shape the order

- **No Docker, no Postgres, no CockroachDB on this machine.** Anything that needs a durable datastore, a container or two
  SpiceDB processes sharing one database cannot be proven here. That is why step 1 is CI.
- SpiceDB v1.57.1 and zed v1.2.1 are in `~/.local/bin`. .NET SDKs 10.0.115 and 9.0.119 (apt); the project targets net10.0 (D1 resolved 2026-11-20). Google Chrome is installed (Playwright).
- `git push` needs gh as a one-off credential helper:
  `git -c credential.helper= -c credential.helper='!gh auth git-credential' push origin main`. Push only when asked.

## The plan, in order

### 1. CI on GitHub Actions: Proposed, the enabler

Run `./verify.sh` on every push, plus a job that runs `docker compose up --build` from a clean checkout and waits for
`/health`. This finally tests D3 and D7 (the only acceptance criteria never verified), and gives later steps a place with
Postgres.
- The workflow must install the pinned SpiceDB and zed (checksums as in `DECISIONS.md`, "Tool versions"), .NET 10, Node and
  Chrome or Playwright's Chromium.
- Not checked: that GitHub's hosted Ubuntu runners ship Docker Compose. I believe they do, but look it up before relying on it.
- **Ask first:** pushing a workflow is outward-facing, so show Andrew the file before pushing it.
- **Done when:** both jobs are green on GitHub, and D3, D6 and D7 in `DECISIONS.md` are updated with the result.

### 2. Harden the app in place: Proposed (2026-11-15 production design, step 1)

Each of these is its own increment, ending green:
1. **Start-up stops wiping.** `StartupSequence` deletes every relationship and clears the database on each start
   (`src/Poc.Infrastructure/Startup/StartupSequence.cs:47-53`). Production start-up should flush the outbox and stop;
   seeding becomes a one-off command.
2. **The app stops writing the schema.** It writes the whole schema at start-up (line 47). On a shared engine that would
   delete other teams' definitions: a schema write replaces the whole schema and refuses only where data exists (proven
   2026-11-15 and 2026-11-17). Schema moves to a pipeline step: compile the per-team files, run `zed validate` on each
   team's `validation.yaml`, then one serialised write.
3. **Durable stores.** Postgres for the app database (today in-memory SQLite, `InfrastructureServiceCollectionExtensions.cs:35`)
   and for SpiceDB's datastore. CockroachDB is a poor fit while the app relies on full consistency: its docs say
   `fully_consistent` does not guarantee read-after-write there.
4. **A consistency policy** instead of `FullyConsistent` on every call (`SpiceDbAuthorizer.cs`). Use at-least-as-fresh with
   the stored ZedToken by default, and full consistency for access-management screens.
- **Ask first:** each of these changes PRD decisions (in-memory stores, reseed on start, fully consistent everywhere), so
  record them as deviations.
- **Needs step 1** to prove anything that touches Postgres.

### 3. Read types, roles and id rules from the deployed schema: Done (2026-11-18; see DECISIONS.md, "Steps 3 and 4 finished")

Today three things are hard-coded for this one app:
- the known object types: `ResourceLocator.NamedTypes` (`src/Poc.Api/Security/AccessRequirement.cs:21`);
- the grantable roles per type: `GrantableRelations` (`src/Poc.Application/Access/GrantableRelations.cs`);
- the id format: `EntityId.Validate`.

A central service would need every team's release to change and redeploy it.
1. **Spike `ReflectSchema` first.** It exists in the SpiceDB v1.57.1 binary and in Authzed.Net 1.6.0, but has never been
   called. If it doesn't work, parse `schema read` text; `tests/Poc.UnitTests/Authorization/SchemaModel.cs` shows the shape.
2. **Grantable roles use the subject-type rule:** a relation is a role when every allowed subject is a people type
   (`user`, `group#member`). Change `GrantRulesTests.Grantable_relations_are_exactly_the_schemas_non_structural_relations`
   to assert that the rule gives today's table. It does.
3. **Known types** are `platform` plus every definition that has a `parent` and a `manage_access` permission; today
   that gives exactly `NamedTypes`. Validate ids against SpiceDB's own id rules.
- **Done when:** adding a type with grantable roles to `schema.zed` makes it appear in the API and the Access tab with no
  C# change, and a test proves it.

### 4. Remove the group-scope duplication: Done (2026-11-18; engine walk, single scope rule, two-way test, single platform-admin rule)

On 2026-11-15 Andrew paused this with "let's wait". The duplication:
- the object's root customer is computed twice: `DescribeAsync` (`EfPorts.cs:61`) and `AncestorsAsync` (`EfQueries.cs:84`);
- the scope rule is stated twice: `GrantRules.RequireGroupInScope` and `AccessQueryService.GrantableGroupsAsync`;
- the platform-admin condition is written twice (`AccessService.cs:63`, and again in `AccessQueryService`).

**Revise before building.** The paused plan had the picker reuse `DescribeAsync`, which reads this app's database. The
production design says a central service cannot read team databases and must walk `parent` tuples in SpiceDB. So:
- find the root customer by walking `parent` in the engine, once, used by both the command and the picker;
- state the scope rule once in `GrantRules`;
- add a two-way test across every seed object and group: a group is offered if and only if granting it succeeds.

### 5. Cross-team references across customers: Open

Proven on 2026-11-17: a `shipping/parcel` under customer C2, paid from a `billing/wallet` under C1, is viewable by C1's
administrator through `payer->spend`. SpiceDB does not prevent this; each team's schema decides it. Bring Andrew options
(a schema-pipeline lint, a rule that cross-team arrows may only carry view-type permissions, or an explicit allow-list)
before any team references another's types. Reproduction: the test is not in the repository. Its 2026-11-17 rebuild is a
temporary scratchpad script that will be cleared, and the original commands are in the 2026-11-15 session transcript.
Commit a reproduction under `spikes/` as part of this item.

### 6. Later phases (production design, steps 2-4): Proposed outline

- **Extract packages inside this repo**, with this app as the first consumer: ports and reference types, the SpiceDB
  adapter, the endpoint filter and error mapping, the outbox projector, flush worker and reconciliation, and the test
  fixtures and `validation.yaml` runner. The projector names `SubscriptionCreated` and `DefaultGrantPolicy` directly
  (`OutboxProjector.cs:109`); that becomes a mapper interface and a post-apply hook.
- **Split out the Access Management service:** `AccessService`, `AccessQueryService`, `GrantRules`, the group endpoints
  and `GrantAudit`. Default grants become a call to it.
- **Engine access on open-source SpiceDB:** every preshared key has full read and write (confirmed in the docs on
  2026-11-15). App teams get the key to a second cluster started with `--datastore-readonly` on the same primary
  database. Only the platform holds the writable key, so app projectors write structural tuples through a central
  endpoint that checks the team owns the type.
  - Not tested: whether ZedTokens work across the two clusters.
  - The read-only cluster must use the primary, not a replica, while full consistency is used.
- **Onboard one second team** and write the contract and onboarding guide from what they hit. Not reusable until then.

## Smaller items

| Item | Status | Notes |
|---|---|---|
| Inherited grants sorted nearest-ancestor-first | Proposed | `AccessQueryService` sorts by type name; since D8 that happens to read well for a wallet. |
| BFF between the client and the API | Proposed, not scheduled | Validated 2026-11-15 with a spike: YARP 2.3.0 builds on net9.0. Corrections: no shared contracts project (the DTOs embed Application types); aggregation is worth little. The cookie also needs an "expected user" header, or tabs act as each other. Re-check against the D8 query-string routes. |
| Security headers / CSP on the static client | Open | Known gap (Phase 6 decisions). |
| Image tags pinned, container healthchecks | Open | Needs step 1. |

## Process tooling not yet built (proposed in the 2026-11-17 review)

Andrew chose `CLAUDE.md` and the hooks; these remain options:
- **Scripts:** `scripts/close-phase.sh` (the `CLAUDE.md` checklist as one command), `scripts/test.sh <filter>`
  (build, then test, failures only), `scripts/stack.sh up|down` (PID files, no `pkill`).
- **Repeatable mutation testing:** recover the ~50 mutations listed in `DECISIONS.md` into a committed catalogue and
  `scripts/mutate.py`, as an optional `verify.sh` stage.
- **Clean-clone helper:** today's scratchpad `gate.sh` verified the *uncommitted* tree from a clean clone, using a
  temporary index and ref. It lives in a temporary scratchpad that will be cleared, so rewrite it into `scripts/`.
- **Agents:** a phase-gate reviewer (checks report claims against evidence), a mutation tester, a design claim-checker.
- **A status table** at the top of `DECISIONS.md`: one row per phase, accepted or blocked by which D-item.

## Rules that matter most for this work

- Ask Andrew about anything marked Proposed or Open, PRD conflicts, matrix changes, and anything outward-facing.
  Decide and record everything else (`CLAUDE.md`, "Decision rights").
- Look up versions and API behaviour; never recall them. Assert what the engine was observed to do.
- Close every change with the `CLAUDE.md` checklist: `verify.sh` green, mutation-checked, clean clone, `DECISIONS.md`
  updated, then commit with the summary. The commit hook enforces the `verify.sh` part.
