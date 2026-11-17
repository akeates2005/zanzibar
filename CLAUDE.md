# Zanzibar authorisation PoC

A .NET 9 modular monolith with SpiceDB, a stub OIDC issuer and a React client. `PRD.md` is the specification,
`DECISIONS.md` records every choice and deviation (D1 to D7), and `LESSONS.md` is the retrospective. Read those files for
detail; this file holds the rules that must hold in every session. A SessionStart hook prints git state, open deviations,
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

## Commands (copied from verify.sh; paths are case-sensitive: `src/Poc.Api`, not `Poc.API`)

- Full gate: `./verify.sh` (the only definition of passing). It takes minutes, so run it with `run_in_background`
  and wait for the notification. Never use `sleep` to wait.
- Build: `dotnet build Poc.sln -c Release --nologo -v quiet`. Format: `dotnet format Poc.sln`.
- One test class: `dotnet test tests/Poc.IntegrationTests -c Release --filter 'FullyQualifiedName~X'`. Use
  `--no-build` only chained after a successful `dotnet build ... &&` (the hook enforces this).
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
- Fix analyzer errors at the cause; never suppress. These recurred: CA1861 (constant array argument: use a
  `static readonly` field), CA1716 (a name that is a keyword: `error`, `Public`), CA1710/CA1711 (`Collection` suffix
  rules), CA1822 (make it `static`), CA1826 (index, not `First()`), CA1847 (`Contains(char)`), CA1707 (underscores are
  allowed in `tests/` only).

## Tests

- A test that has never failed is unproven. Mutation-check new behaviour: break it, watch the test fail, restore.
  Mutations must compile: CS0162 rejects `return;` before code, so use a condition the compiler can't fold.
- Assert invariants by name, never counts. Wait for an observable effect (`EventuallyAsync`), never sleep.
- Statics are shared across parallel test hosts (`ActivitySource`, loggers): key lookups on something unique per test.
- After one flaky test, run the suite 3 to 5 times plus once concurrently.

## Closing a phase or a change (LESSONS.md section 11)

1. `./verify.sh` green, and any new gate shown able to fail.
2. New behaviour mutation-checked; suite repeated 3 to 5 times, once under load.
3. Clean clone, run in the background: `git clone . "${D:?}" && (cd "$D" && ./verify.sh)` with `D` set to a fresh
   scratch directory.
4. No leftover processes or ports (the SessionStart hook output shows the check).
5. `DECISIONS.md` updated; the report says what was not verified.
6. Commit with the `verify.sh` summary in the message. The commit hook refuses any non-Markdown commit unless
   `verify.sh` passed on exactly this working tree (it records `.verify/last-pass`). The clean-clone step is not enforced
   by a hook; it is your responsibility.

Push only when asked. `gh` is authenticated as the repository owner.
