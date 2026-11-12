# Lessons learnt

A retrospective on building this proof of concept in six phases (model, adapter, domain and projection, API, client,
hardening). It records what cost time, what caught real bugs, and what to do differently. It is written for the next person
(or agent) to work on this codebase, so each lesson says what happened, why it matters, and what to do.

[`DECISIONS.md`](DECISIONS.md) records *what was decided and why*. This file records *how we got misled and how we stopped
being*. Where a lesson has a longer story, it points there.

## The five things that mattered most

1. **Verify from a clean clone before closing a phase.** The first two clean-clone runs each failed one test that had passed
   in the working tree every time. Both were races in test code, and both are the kind that only shows when the machine is
   slower and colder. Do `git clone . /tmp/x && cd /tmp/x && ./verify.sh` before saying "done".
2. **A test that has never failed has not been shown to work.** Break the behaviour on purpose and watch the test fail. We did
   this for every phase (about 50 deliberate defects in total) and it found real gaps; see section 2.
3. **Check the spec against the real system early.** Two PRD scenarios were impossible as written (section 3). Both were found
   by running the real engine, not by reading.
4. **Never wave through a surprise.** "Passed on the first run", "leftover: 2", "exit 144", "OK (it just printed nothing)":
   each looked like noise and each was a real finding.
5. **Say what was not verified, in the same breath as what was.** Docker was never available. Every report said so, and the
   gaps are listed (D7) rather than implied away.

## 1. Process

- **Close each phase with the full gate, then the clean clone.** `./verify.sh` is the only definition of passing. The clean
  clone catches missing files, hidden dependencies on local state, and timing assumptions.
- **Write `DECISIONS.md` as you go, not at the end.** Every deviation (D1 to D7) was recorded when it was made, with the
  evidence. It made the later reports honest and the PRD conflicts auditable.
- **Ask when there is a genuine fork; record when there is not.** Early on, two real questions (Docker, SDK) were asked with a
  recommendation. Once design authority was delegated, the right move was to decide, record the decision, and flag any conflict
  with the PRD in the report (as with D4 and D5), not to ask again.
- **Surface a PRD conflict instead of working around it.** PRD rule 2 exists for this. D4 and D5 were both found by running
  something real, both were reported, and both resolutions are in `DECISIONS.md`.
- **Do phase work in increments that each end green.** Writing a layer, building, running one filtered test class, then the
  next layer kept every failure small. Big-bang test runs produced walls of output that hid the one real failure.
- **Re-run the whole suite several times after finding one flaky test.** One flake means look for others. We ran it five
  times, then twice concurrently to halve the CPU each copy gets. That is cheap and it is how you find timing-dependent tests.

## 2. Testing: how tests lied to us

- **A first-run pass is a prompt to mutation-test, not a result.** Most suites passed first time. Mutation checks confirmed
  almost all of them, and found the gaps that mattered:
  - The matrix guard counted assertions (`>= 70`), so deleting a user's rows still passed (the extras padded the count). It now
    checks each of the 70 cells by name.
  - A UI rule (unviewable ancestors must not be links) was caught only by a slow browser journey; a fast component test was
    then added.
  - A Phase 6 helper bug made an end-to-end assertion vacuous (see section 6).
- **Mutations must compile.** With warnings as errors, `if (x) return;` replaced by `return;` is "unreachable code" and fails
  to build, which tests nothing. Use a condition the compiler cannot fold (`|| something.Length > 0`), and have the mutation
  script fail loudly when an edit did not apply or did not compile. Several early "mutations" were silently compile errors.
- **Test the failure path of every gate you write.** The matrix guard, the process-leak check, the Compose checker, and the
  generated-types check were each shown to fail on a planted defect. A gate that cannot fail is decoration. The Compose
  checker's first version flagged the word "secret" in a *comment*, which is exactly what this practice finds.
- **Do not assert your own assumption about the engine.** We guessed SpiceDB answers a bad key with `Unauthenticated`
  (it says `PermissionDenied`), guessed an OpenAPI operation count of 45 (it was 41, which equals the route count, so the test
  now compares against the live route table), and used an xunit v3 API (`TestContext.Current`) in an xunit v2 project. Look at
  the real behaviour first; assert what you saw.
- **Assert the invariant, not the count.** "Exactly the routes the application has are described", "every PRD cell is present
  by name", "reconciliation reports no drift after every HTTP test". These survive change; magic numbers do not.
- **A test that passes in isolation can still be wrong.** One test passed for weeks because it picked "the" span by request
  path while other test hosts in the same process produced the same path (section 4).
- **Fitness functions beat reviews.** Four are permanent: constants equal the schema, every endpoint declares access, only
  named types may touch tuples (architecture test), and reconciliation agrees after every HTTP test. The architecture test
  fired on a real design slip (a read-side service had been given the writable store, which led to the `IRelationshipReader`
  split); the reconciliation check was proven able to fail with a throwaway test that deleted a tuple.

## 3. Reality versus the spec

- **LookupSubjects cannot be paged on SpiceDB 1.57.1** (`concrete limit is not yet supported`), although the PRD says lookups
  page through the cursor. Unpaged returned all 1,500 subjects, so the deviation is safe at this scale (D4). Found by running
  the adapter test against the real engine, not by reading docs.
- **The "paused engine gives 202" scenario is unreachable.** Every request first checks permission through the engine and never
  falls back to allow, so a paused engine gives 503. 202 only exists for a failure *after* the check (D5). The PRD's own rules
  contradicted its scenario. We kept fail-closed and tested both paths for real, including by freezing the engine with SIGSTOP.
- **An outage must not use up retries.** "Five failed attempts then dead" conflicts with "survive a pause". Distinguishing
  transient failures (engine unreachable, `IsTransient`) from rejections is what reconciles them.
- **SQLite shared-cache concurrency was a feared problem that did not exist.** 300 concurrent writers plus readers passed five
  times with no gate. Measure before building a lock; we avoided an unnecessary write gate by testing first.
- **Engine behaviours worth knowing:** the lookup-resources cap is 1,000 by default; delete can be partial and must be looped;
  a bad preshared key is `PermissionDenied`; SpiceDB reports telemetry to the vendor by default and must be told not to;
  the explain trace is recursive, which the OpenAPI generator mangles, so we expose it flat with a depth.

## 4. Concurrency, statics and processes in tests

- **Process-wide state is shared between "isolated" tests.** `ActivitySource`s are global, so every test host's tracer provider
  also receives the spans of hosts running in parallel. Give each test something no other test can share (here: its own W3C
  `traceparent` trace id) and look things up by that. Remember a trace can legitimately contain more spans than you expect: a
  first token validation makes the API call the issuer, and context propagates, so the issuer's spans join the trace.
- **Never copy a concurrent collection with a spread or `.Count` then index.** `[.. concurrentQueue]` threw
  `IndexOutOfRange` when a log line arrived mid-copy. `ToArray()` is the snapshot. It only failed on a slow run.
- **xunit does not call `DisposeAsync` when `InitializeAsync` throws.** A half-started host leaked a SpiceDB process. Clean up
  inside the failing start-up path, keep a registry of live child processes that a process-exit hook kills, and make the gate
  fail if any survive. (We proved the exit hook matters: without it one engine survives.)
- **Wait for an observable effect, never sleep.** `EventuallyAsync` with a clear failure message made every timing test
  deterministic. The few fixed delays that remain are for "nothing should happen in this window" assertions.
- **Fake what fails the way production fails.** A fault-injecting store that can lose a response *after* a successful write,
  fail on the Nth call of a multi-step plan, or flap with a seeded random generator found properties that "up or down" faults
  never would. Seed the randomness so a failing run repeats.
- **Real processes beat mocks for the engine.** Every integration test ran against a real SpiceDB binary, including a real
  restart (memory wiped) and a real freeze (SIGSTOP). That is why the recovery design is trustworthy.

## 5. Shell, editing and tooling traps (these cost real time)

- **`pkill -f` and `pgrep -f` match the shell that contains the pattern.** It killed my own shell twice (exit 144), and a
  heredoc containing the pattern made a hygiene check "find" the invoking shell. Anchor patterns to the executable
  (`^[^ ]*spicedb serve ...`), kill by PID, and never put a literal pattern in a command whose own text a shell will match. A
  `Monitor` loop watching for a command line that contains itself never ends.
- **Match the process's real name.** The published API shows as `Poc.Api`, not `dotnet`, so a cleanup written for `dotnet`
  missed it and left a listener on the port.
- **Regex and multi-line `perl -0pi` edits fail silently or damage files.** Zero-width captures prepended a method to the top
  of a source file; a `sed` pattern containing `|` changed nothing and the next test still failed; escaped quotes in perl
  replacement text were wrong repeatedly. Prefer the Edit tool or a small Python script that asserts the old text exists, and
  confirm an edit applied (`cmp`, `grep`) before drawing conclusions from a test run.
- **`dotnet test --no-build` after a failed build runs the previous binary.** An analyzer failure (CA1710 on a new collection
  type) broke the build, and I read stale results until the failing assertions stopped matching the code I had just written.
  Always check that the build succeeded first, or drop `--no-build` while iterating.
- **The harness caps a foreground command at about ten minutes** and silently moves it to the background; `sleep` is blocked.
  Long runs (stress, clean-clone verify) go in `run_in_background` or under `Monitor`, and you wait for the notification.
- **Noisy children flood the context.** SpiceDB's request log buried the one real error in a Playwright run. Silence stdout of
  helper processes (`stdout: 'ignore'`), keep stderr for the one that must explain itself, and filter output to failures.
- **Strict analyzers (warnings as errors) are a feature.** They rejected a vulnerable package, unreachable code in mutations,
  an unsupported target-framework combination, missing `static`, and a missing `IDisposable` on a type that owns a semaphore.
  Fix the cause; do not suppress. We turned off exactly one rule (underscores in test names), scoped to `tests/`.
- **Copy run instructions from a command you just ran.** I verified `dotnet run --project src/Poc.Api` and then typed
  `src/Poc.API` in the how-to-run answer. Paths are case-sensitive on Linux.

## 6. Frontend and browser tests

- **Make the helper's behaviour match the real one.** The `openAs` helper registered an init script that reset the remembered
  user on *every* navigation, so a later `page.goto` silently signed the page back in as someone else. An earlier assertion
  ("Erin sees Acme Ireland") passed vacuously because Alice sees it too. A later assertion exposed it. After a user switch,
  wait for the shell to show the new person (name and select) before navigating, and assert *who* is signed in.
- **Selectors must be specific.** `getByLabel('User')` also matched the "A user" radio; `getByRole('option', {name})` matched an
  `<option>` in the always-present native user switcher. Scope to a landmark or a listbox. Use real landmarks (`<nav
  aria-label>`), not an `aria-label` on a `div`.
- **A tooltip on a disabled control needs a wrapper.** Disabled buttons receive no pointer events, so the tooltip sits on a
  surrounding span.
- **Libraries report values you do not expect.** Mantine's number input gives a string for `250.50` while typing, which made a
  "Top up" button permanently disabled for ordinary input. Parse both forms in one tested helper.
- **Disabled, not hidden, but never trusted.** The permission map enables controls; a journey proves a direct API call for each
  disabled action is still refused with the permission named.
- **Generated types from a committed contract.** `web/openapi.json` is committed; a test fails if the API disagrees; `verify.sh`
  fails if the generated types disagree. The generator mishandles recursive types, so keep response shapes flat.
- **Keep the token out of storage, in the client object.** Binding the token into the API client (not a global) meant a cached
  query could only ever have been fetched with its own user's token, which tests assert.

## 7. Dependencies and versions

- **Read peer ranges before picking a major.** TypeScript 7 broke `typescript-eslint` (needs `<6.1`) and `openapi-typescript`
  (needs 5.x), so we pinned 5.9 and recorded why.
- **Read the dependency graph, not just the package version.** OpenTelemetry 1.21 pulled `Microsoft.Extensions.*` 11.0, which
  does not support net9.0; 1.13 (the newest on 9.0 dependencies) had three known advisories in the OTLP exporter, which the
  build's vulnerability audit rejected. 1.15.3 was the first patched release. Run `dotnet list package --vulnerable` and
  `npm audit` and *read* them: the npm findings were dev-only and accepted explicitly; the NuGet one was not.
- **Look up real versions; do not recall them.** Every package version, image tag and API name in this repository was checked
  against a registry or a compiled assembly (reflection over Authzed.Net for the message shapes, for example).

## 8. Configuration and deployment

- **Minimal hosting reads configuration at the top of `Program`, before a test factory's overrides apply.** Use
  `UseSetting` (host settings), not `ConfigureAppConfiguration`, in `WebApplicationFactory`.
- **A relative path means "relative to the process", which is wrong.** The schema path resolved against the working directory,
  so `dotnet run` from another directory, or a container with a different `WORKDIR`, never wrote the schema and the API stayed
  unhealthy forever. Resolve relative paths against `AppContext.BaseDirectory`. A browser journey found it; no unit test would.
- **The public name and the reachable name of a service differ in containers.** Tokens carry `http://localhost:5090`, but the
  API reaches the issuer as `http://issuer:5090`. That needed `Authentication:MetadataAddress` and a `jwks_uri` derived from the
  request host. Test it with a public name that does not resolve at all so the test cannot pass by accident.
- **ASP.NET static files are skipped when an endpoint already matched.** The SPA fallback route matched asset paths first.
  `{*path:nonfile:regex(...)}` fixed it, and the regex keeps `/api`, `/health` and `/openapi` out of the fallback.
- **Framework request logging can leak what a user typed.** It logs the full URL, and the user search puts the typed term in
  the query string. Pin that category to Warning in code (so config cannot undo it) and test that no email appears in any log.
- **Rehearse what you cannot run.** With no Docker, the production shape (published output, Production environment,
  environment-only configuration, split issuer naming, client served by the API) was run natively by ten browser journeys, and
  the Compose invariants are enforced statically. State plainly what remains unproven (D7).

## 9. Dead ends and over-engineering we avoided or backed out of

- Docker/Testcontainers: unavailable, so a native SpiceDB process behind an `ISpiceDbHost` interface, swappable later.
- An application-level SQLite write gate: measured, not needed.
- Paging `LookupSubjects`: the engine rejects it; unpaged is complete here.
- Hiding disabled actions: the PRD wanted them visible with the missing permission named, which is also easier to demonstrate.
- A recursive explain DTO: the generator mangles it; flat with a depth is simpler for every client.
- Letting the Application layer depend on a logging package or on the engine client: an `IAccessLog` port and a read-only
  `IRelationshipReader` split kept the layering rules enforceable by the compiler and by an architecture test.
- Reusing already-running servers in Playwright: it risks running journeys against someone's half-used data, so it never does.

## 10. What worked and should be kept

- One source of truth for the model: `authz/validation.yaml` is read by `zed validate` *and* by the .NET and HTTP tests.
- Real-engine integration tests, with the engine started by the test and cleaned up by a reaper.
- Per-phase exit criteria that are executable, with the commit message carrying the `verify.sh` summary.
- Mutation scripts as first-class tooling (apply, run filtered tests, restore, report, and fail loudly if the edit did not apply).
- Making every endpoint declare its access, enforced by one filter, with a sweep that aims every route at real objects as a user
  who can see nothing (expect 404) and as a user who can only view (expect 403 naming the permission).
- Reading screenshots and traces when a browser test fails, instead of guessing from the log.

## 11. A checklist before closing a phase

1. `./verify.sh` is green, and each stage was shown able to fail.
2. New behaviour has a test that fails when the behaviour is removed (mutation-check it).
3. Run the suite three to five times, once under load; fix anything intermittent now.
4. No leftover processes (`pgrep` anchored, by PID) and no stray ports.
5. `git clone . /tmp/x && cd /tmp/x && ./verify.sh` is green.
6. `DECISIONS.md` has every deviation and every unverified claim; the report states them next to the results.
7. Commit with the `verify.sh` summary in the message.
