# Zanzibar authorisation PoC

- If notes, plans or instructions disagree with the app, follow the app and point out the mismatch in your reply. The
  exception is a plan marked "in progress": that's where the app is going, so follow the plan. When you finish a plan,
  change its status to done.
- Leave notes, TODOs and plans you weren't asked about alone. If you notice one that looks relevant, list it at the end of
  your reply instead of acting on it.

A .NET 10 modular monolith with SpiceDB, a stub OIDC issuer and a React client. `PRD.md` is the specification,
`DECISIONS.md` records every choice and deviation (the numbered D-items), and `LESSONS.md` is the retrospective. Read those files for
detail; this file holds the rules that must hold in every session. **`NEXT-STEPS.md` is the current plan**: what to build
next, in order, with each item's status (Decided / Proposed / Open / In progress / Done). A SessionStart hook prints git state, open deviations,
whether `verify.sh` has passed on the current tree, and any leftover processes or ports.

## Decision rights (scoped override of the global CLAUDE.md)

Andrew has delegated **design** authority for this codebase. Where the PRD is silent or ambiguous, decide, record the
choice in `DECISIONS.md` with one line of reasoning, and continue. Do not stop at a phase boundary just to ask permission
to continue a task you were given.

Still stop and ask (with `AskUserQuestion` and a recommendation) for:
- the PRD contradicting itself or the real engine (PRD rule 2), or any change to the access matrix (rule 4);
- facts you have not verified: an API, a version or a behaviour. Look it up or run it first; if you can't, ask;
- outward-facing or irreversible actions: push, publishing, deleting data, adding a dependency with advisories.

## Words for status

- **Accepted:** the PRD's exit criterion passed as written inside `./verify.sh`.
- **Complete except Dn:** built and green, but a criterion is unmet or unverified. Phases 5 and 6 are in this state
  (D3, D7: Docker has never run). Never call the project or a phase "complete" while a D-item it touches is open.
- Every report states what was **not** verified next to what was.

## Claims

- Cite `file:line` for claims about this code, and a URL or doc for claims about SpiceDB, .NET or packages. Label
  anything else "from memory, not checked".
- Before presenting a design proposal, check each claim against the code and docs. Spike anything load-bearing.
- Assert what the engine was observed to do, never what you assume (it answers a bad key with `PermissionDenied`).
- Where to look things up: .NET, ASP.NET Core and EF Core docs and breaking changes through the Microsoft Learn MCP
  tools; package versions at `https://api.nuget.org/v3-flatcontainer/<id-lowercase>/index.json`, and a package's
  dependencies and target frameworks in `.../<id>/<version>/<id>.nuspec`; third-party changes in the project's
  `CHANGELOG.md` on GitHub (raw.githubusercontent.com).

## Commands (copied from verify.sh; paths are case-sensitive: `src/Poc.Api`, not `Poc.API`)

- Full gate: `./verify.sh` (the only definition of passing). It takes minutes, so run it with `run_in_background`
  and wait for the notification. Never use `sleep` to wait.
- Build: `dotnet build Poc.sln -c Release --nologo -v quiet`. Format: `dotnet format Poc.sln`.
- One test class: `dotnet test tests/Poc.IntegrationTests -c Release --filter 'FullyQualifiedName~X'`. Use
  `--no-build` only chained after a successful `dotnet build ... &&` (the hook enforces this). Never pipe the build
  into `grep` before the `&&`: the `&&` then tests grep's exit, and `grep " error "` succeeds when the build fails.
  Send the build to a log instead:
  `dotnet build Poc.sln -c Release --nologo -v quiet > "$S/build.log" 2>&1 && dotnet test ... --no-build; grep -E " error " "$S/build.log"`.
- After an intentional API change: `POC_UPDATE_OPENAPI=1 dotnet test tests/Poc.IntegrationTests -c Release --filter
  'FullyQualifiedName~OpenApiContractTests'`, then `npm run gen:api` in `web/`, and commit both files.
- SDK: no `global.json`, so the newest installed SDK builds the project. A newer SDK brings newer analyzers, which have
  broken the build on an unchanged target before (SDK 10 with net9, CA1873). The SessionStart hook warns when the SDK
  is newer than the target.
- Web (in `web/`): `npm run typecheck`, `npm run lint`, `npm test`, `npm run gen:api`, `npx playwright test`.
- Ports: API 5080, issuer 5090, Vite 5173, SpiceDB 50051; verify uses 50061/9091 and e2e uses 9092.

## Hard rules (each cost real time; LESSONS.md section 5)

- Edit source with Edit/Write only. No `sed -i`, `perl -pi` or heredoc writes into `src/`, `tests/`, `web/src`,
  `web/e2e` or `authz/`. Confirm an edit applied before reading a test result.
  Exception (agreed 2026-11-17): a purely mechanical rename across many files may use a throwaway script that applies
  each pattern only if it matches an expected count taken from an independent `grep -o`, and writes nothing otherwise.
  Review the full diff, then search for what the patterns missed (a wildcard `type:*` was missed the first time).
- Never `pkill -f`/`pgrep -f` with an unanchored pattern; it matches your own shell. Kill by PID. The processes are named
  `Poc.Api` and `Poc.DevIssuer`, not `dotnet`.
- `rm -rf` only on `"${VAR:?}"` or a literal project path.
- Filter long output to failures. Silence the stdout of helper processes (SpiceDB's request log buries the real error).
  For repeated or load runs, save each run's full output to a file and grep the files: a filtered pipe loses the message.
- Never `dotnet build` while a background test run is using `bin/`.
- Anchor validation regexes with `\z`, not `$` (in .NET, `$` also matches before a trailing newline).
- Fix analyzer errors at the cause; never suppress. These recurred: CA1861 (constant array argument: use a
  `static readonly` field), CA1716 (a name that is a keyword: `error`, `Public`), CA1710/CA1711 (`Collection` suffix
  rules), CA1822 (make it `static`), CA1826 (index, not `First()`), CA1847 (`Contains(char)`), CA1707 (underscores are
  allowed in `tests/` only), CA1873 (costly argument to a log call: give the `[LoggerMessage]` method the object, such
  as a `ResourceRef` rather than `ref.ToString()`, and let the generated code format it. An `IsEnabled` guard around the
  call does not satisfy it).

## Tests

- A test that has never failed is unproven. Mutation-check new behaviour: break it, watch the test fail, restore.
  Mutations must compile: CS0162 rejects `return;` before code, so use a condition the compiler can't fold.
- Assert invariants by name, never counts. Wait for an observable effect (`EventuallyAsync`), never sleep.
- Statics are shared across parallel test hosts (`ActivitySource`, loggers): key lookups on something unique per test.
- After one flaky test, run the suite 3 to 5 times plus once concurrently. If it will not reproduce, run three copies at
  once. When you fix a race, find and fix every copy of its pattern.

## Closing a phase or a change (LESSONS.md section 11)

1. `./verify.sh` green, and any new gate shown able to fail.
2. New behaviour mutation-checked; suite repeated 3 to 5 times, once under load.
3. Clean clone, run in the background: `git clone . "${D:?}" && (cd "$D" && ./verify.sh)` with `D` set to a fresh
   scratch directory.
4. No leftover processes or ports (the SessionStart hook output shows the check).
5. `DECISIONS.md` updated; the report says what was not verified. Write the docs before the final `verify.sh`: the
   recorded pass covers Markdown too, so a later doc edit means running it again. If the change alters a fact the docs
   state (a version, a port, a command, a count), grep every `*.md` and `.claude/` file for the old value first:
   `git grep -n "<old value>" -- '*.md' .claude`.
6. Commit with the `verify.sh` summary in the message. The commit hook refuses any non-Markdown commit unless
   `verify.sh` passed on exactly this working tree (it records `.verify/last-pass`). The clean-clone step is not enforced
   by a hook; it is your responsibility.

Push only when asked. `gh` is authenticated as the repository owner.
