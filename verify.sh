#!/usr/bin/env bash
# The only definition of "passing" (PRD: Testing and quality gates). CI runs this same script.
# Every stage either passes, fails the script, or is listed as SKIPPED in the summary with its reason.
# A SKIPPED stage is NOT a pass.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

PASSED=(); SKIPPED=()
red() { printf '\033[31m%s\033[0m\n' "$*"; }
stage() { printf '\n== %s ==\n' "$1"; }
pass() { PASSED+=("$1"); }
skip() { SKIPPED+=("$1 -- $2"); printf 'SKIPPED: %s\n' "$2"; }
fail() { red "FAILED: $1"; summary; exit 1; }
summary() {
  printf '\n---------------- verify.sh summary ----------------\n'
  for s in "${PASSED[@]:-}"; do [[ -n "$s" ]] && printf '  PASS     %s\n' "$s"; done
  for s in "${SKIPPED[@]:-}"; do [[ -n "$s" ]] && printf '  SKIPPED  %s\n' "$s"; done
}
need() { command -v "$1" >/dev/null 2>&1 || fail "required tool '$1' not found on PATH"; }

need zed; need spicedb; need bash

# --- Phase 1: the model, proven in isolation -------------------------------------------------
stage "Schema: zed validate (seed tuples + full access matrix)"
zed validate --fail-on-warn authz/validation.yaml || fail "authz/validation.yaml"
zed validate --fail-on-warn authz/schema.zed || fail "authz/schema.zed"
# Guard against the matrix being silently thinned: each of the 10 users x 7 PRD columns must be asserted
# (true or false; zed checks the value against the schema). Presence is checked by name, not by count.
missing=0
for u in priya sam alice bob heidi carol dave erin grace frank; do
  for cell in customer:acme#view domain:acme-uk#admin wallet:uk-ops#spend subscription:m365-e3#change_quantity \
              subscription:m365-e3#cancel wallet:ie-ops#view customer:globex#view; do
    grep -qxE "\s+- \"${cell}@user:${u}\"" authz/validation.yaml || { red "matrix cell not asserted: ${cell}@user:${u}"; missing=$((missing+1)); }
  done
done
(( missing == 0 )) || fail "$missing PRD matrix cell(s) missing from authz/validation.yaml"
pass "zed validate: schema + validation.yaml (all 70 PRD matrix cells present and passing)"

stage "Schema: loads into a live SpiceDB"
SMOKE_PORT="${VERIFY_SPICEDB_PORT:-50061}"; SMOKE_METRICS="${VERIFY_SPICEDB_METRICS_PORT:-9091}"
SMOKE_KEY="verify-smoke-key"; SMOKE_STATE="$(mktemp -d)"
SPICEDB_PRESHARED_KEY="$SMOKE_KEY" SPICEDB_GRPC_PORT="$SMOKE_PORT" SPICEDB_METRICS_PORT="$SMOKE_METRICS" \
  SPICEDB_STATE_DIR="$SMOKE_STATE" scripts/spicedb-local.sh start || fail "could not start native spicedb"
smoke() { SPICEDB_GRPC_PORT="$SMOKE_PORT" SPICEDB_STATE_DIR="$SMOKE_STATE" scripts/spicedb-local.sh stop >/dev/null; rm -rf "$SMOKE_STATE"; }
trap smoke EXIT
Z=(zed --endpoint "localhost:$SMOKE_PORT" --token "$SMOKE_KEY" --insecure)
"${Z[@]}" schema write authz/schema.zed >/dev/null || fail "schema.zed rejected by live SpiceDB"
"${Z[@]}" schema read | grep -q "definition subscription" || fail "schema read-back missing 'subscription'"
pass "live SpiceDB accepts schema.zed and returns it"

stage "Compose file and Dockerfiles: static checks"
# Always runs: localhost-only ports, a pinned SpiceDB image, telemetry off, no baked secrets, non-root images, and that the API's
# authority matches the name tokens carry. Needs python3 with PyYAML.
command -v python3 >/dev/null 2>&1 || fail "python3 is required for the Compose checks"
python3 scripts/check-compose.py || fail "docker-compose.yml / Dockerfile checks"
pass "compose and Dockerfile static checks"

stage "Compose file: docker compose config"
if command -v docker >/dev/null 2>&1; then
  docker compose config -q || fail "docker-compose.yml"
  pass "docker compose config"
else
  skip "compose" "docker not installed (DECISIONS.md D2/D3): the images were never built or run; their runtime configuration is rehearsed by the published-build stage"
fi

# --- Phase 2+: .NET --------------------------------------------------------------------------
need dotnet
export PATH="$HOME/.local/bin:$PATH"   # native spicedb/zed install location (DECISIONS.md D2)

stage ".NET: formatting"
dotnet format Poc.sln --verify-no-changes || fail "dotnet format (run 'dotnet format Poc.sln' to fix)"
pass "dotnet format --verify-no-changes"

stage ".NET: build (nullable on, warnings are errors)"
dotnet build Poc.sln -c Release --nologo -v quiet || fail "dotnet build"
pass "dotnet build (Release, warnings as errors)"

# Runs one test project and fails if it ran no tests (a misconfigured project must not pass).
run_tests() { # <label> <project>
  local out; out="$(mktemp)"
  dotnet test "$2" -c Release --no-build --nologo 2>&1 | tee "$out"
  local rc=${PIPESTATUS[0]}
  local line; line="$(grep -E '^(Passed|Failed)!' "$out" | tail -1)"
  rm -f "$out"
  (( rc == 0 )) || fail "$1 tests"
  local count; count="$(sed -nE 's/.*Passed:[[:space:]]*([0-9]+).*/\1/p' <<<"$line")"
  [[ -n "$count" && "$count" -gt 0 ]] || fail "$1 tests ran no tests"
  pass "$1 tests ($count passed)"
}

stage "Unit + architecture tests (schema constants, layering)"
run_tests "unit" tests/Poc.UnitTests

stage "Integration tests (adapter against a real SpiceDB)"
run_tests "integration" tests/Poc.IntegrationTests

# --- Phase 5: the React client ---------------------------------------------------------------
need node; need npm
if [[ -d web/node_modules ]]; then :; else
  stage "Web: install dependencies"
  (cd web && npm ci) || fail "npm ci"
fi

stage "Web: API types are current with web/openapi.json"
# The generated types are checked in. Regenerate to a temporary file and compare, so this works before and after a commit.
(cd web && npx --no-install openapi-typescript openapi.json -o src/api/.schema.check.ts >/dev/null) || fail "openapi-typescript"
if ! diff -q web/src/api/schema.ts web/src/api/.schema.check.ts >/dev/null; then
  rm -f web/src/api/.schema.check.ts
  fail "web/src/api/schema.ts is out of date: run 'npm run gen:api' in web/"
fi
rm -f web/src/api/.schema.check.ts
pass "generated API types match web/openapi.json"

stage "Web: TypeScript strict, ESLint, unit tests, production build"
(cd web && npm run typecheck --silent) || fail "tsc --noEmit (strict)"
(cd web && npm run lint --silent) || fail "eslint"
(cd web && npm test --silent 2>&1 | tail -6) ; [[ ${PIPESTATUS[0]} -eq 0 ]] || fail "client unit tests"
(cd web && npm run build --silent >/dev/null) || fail "vite build"
pass "client: tsc strict, eslint (0 warnings), vitest, vite build"

stage "Web: Playwright journeys against the native stack (SpiceDB, issuer, API, client) in Chrome"
# Docker is unavailable (DECISIONS.md D2/D3), so Playwright starts the native processes itself. Chrome must be installed.
command -v google-chrome >/dev/null 2>&1 || fail "google-chrome is required for the end-to-end journeys"
(cd web && npx --no-install playwright test 2>&1 | grep -vE 'WebServer|chunk|dynamic import|codeSplitting|chunkSize' | tail -12) ; [[ ${PIPESTATUS[0]} -eq 0 ]] || fail "Playwright journeys"
pass "10 Playwright journeys (the PRD's five, plus the README demonstration)"

stage "Web: the same journeys against the published API serving the client (rehearses the container images)"
# `dotnet publish` output in the Production environment with the built client in wwwroot, every setting from the environment,
# and the issuer reached by a different name than its tokens carry: everything about `docker compose up` but the runtime.
(cd web && npm run e2e:published --silent 2>&1 | grep -vE 'WebServer|chunk|dynamic import|codeSplitting|chunkSize' | tail -12) ; [[ ${PIPESTATUS[0]} -eq 0 ]] || fail "published-build rehearsal"
pass "10 Playwright journeys against the published API serving the built client"

stage "Test hygiene: no engine, API or issuer processes left behind"
# Test engines are started with --metrics-enabled=false (the dev helper keeps metrics on), so any survivor is a test leak.
# The end-to-end stack is identified by its fixed ports and project names.
# Anchored to the executable (the command line must start with it), so a shell whose text merely mentions these names never matches.
leaks="$(pgrep -af '^[^ ]*spicedb serve .*(--metrics-enabled=false|--metrics-addr=127\.0\.0\.1:9092)|^[^ ]*dotnet .*(Poc\.Api|Poc\.DevIssuer)\.dll' || true)"
if [[ -n "$leaks" ]]; then
  printf '%s\n' "$leaks" | cut -c1-160
  fail "the test run left SpiceDB, API or issuer processes running"
fi
pass "no test processes left running"

summary
printf '\nverify.sh: OK (%d passed, %d skipped)\n' "${#PASSED[@]}" "${#SKIPPED[@]}"
