#!/usr/bin/env bash
# Run SpiceDB natively (memory datastore) while Docker is unavailable. See DECISIONS.md D2.
#   scripts/spicedb-local.sh start|stop|status
# Env: SPICEDB_PRESHARED_KEY (dev default below), SPICEDB_GRPC_PORT (50051), SPICEDB_METRICS_PORT (9090)
set -euo pipefail
KEY="${SPICEDB_PRESHARED_KEY:-zanzibar-dev-only-key}"
GRPC_PORT="${SPICEDB_GRPC_PORT:-50051}"
METRICS_PORT="${SPICEDB_METRICS_PORT:-9090}"
STATE_DIR="${SPICEDB_STATE_DIR:-${XDG_RUNTIME_DIR:-/tmp}/zanzibar-spicedb-${GRPC_PORT}}"
PID_FILE="$STATE_DIR/pid"; LOG_FILE="$STATE_DIR/spicedb.log"

is_running() { [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; }

case "${1:-}" in
  start)
    is_running && { echo "spicedb already running (pid $(cat "$PID_FILE")) on :$GRPC_PORT"; exit 0; }
    mkdir -p "$STATE_DIR"
    SPICEDB_GRPC_PRESHARED_KEY="$KEY" nohup spicedb serve \
      --datastore-engine=memory \
      --grpc-addr=":$GRPC_PORT" --metrics-addr=":$METRICS_PORT" \
      --telemetry-endpoint="" --skip-release-check=true \
      >"$LOG_FILE" 2>&1 &
    echo $! >"$PID_FILE"
    for _ in $(seq 1 50); do
      if (exec 3<>"/dev/tcp/127.0.0.1/$GRPC_PORT") 2>/dev/null; then echo "spicedb up on :$GRPC_PORT (pid $(cat "$PID_FILE"))"; exit 0; fi
      is_running || { echo "spicedb exited during start-up; log:" >&2; tail -20 "$LOG_FILE" >&2; exit 1; }
      sleep 0.2
    done
    echo "spicedb did not open :$GRPC_PORT within 10s; log:" >&2; tail -20 "$LOG_FILE" >&2; exit 1 ;;
  stop)
    if is_running; then kill "$(cat "$PID_FILE")"; rm -f "$PID_FILE"; echo "stopped"; else echo "not running"; fi ;;
  status)
    if is_running; then echo "running (pid $(cat "$PID_FILE")) on :$GRPC_PORT"; else echo "not running"; exit 1; fi ;;
  *) echo "usage: $0 start|stop|status" >&2; exit 2 ;;
esac
