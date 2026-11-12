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

stage "Compose file"
if command -v docker >/dev/null 2>&1; then
  docker compose config -q || fail "docker-compose.yml"
  pass "docker compose config"
else
  skip "compose" "docker not installed (DECISIONS.md D2); docker-compose.yml is unverified"
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

stage "Test hygiene: no engine processes left behind"
# Test engines are started with --metrics-enabled=false (the dev helper keeps metrics on), so any survivor is a test leak.
if pgrep -f 'spicedb serve .*--metrics-enabled=false' >/dev/null 2>&1; then
  pgrep -af 'spicedb serve .*--metrics-enabled=false' | cut -c1-160
  fail "the test run left SpiceDB engine processes running"
fi
pass "no SpiceDB test engines left running"

stage "Not yet implemented (later phases)"
skip "web" "no client until Phase 5 (tsc strict, eslint, Playwright)"

summary
printf '\nverify.sh: OK (%d passed, %d skipped)\n' "${#PASSED[@]}" "${#SKIPPED[@]}"
