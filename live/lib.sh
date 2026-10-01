#!/usr/bin/env bash
# Shared helpers for the live gates (plan §2 H0, §7–§12). Sourced, never run.
# CLAUDE.md: stamp every log with the tip and the exit code; address processes by PID; refuse
# any cluster that is not this box's k3s; a missing or unreadable input is a failure.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
: "${KUBECONFIG:?KUBECONFIG must name the local k3s kubeconfig}"
NAMESPACE=${NAMESPACE:-descent}
TIER=${TIER:-fake}
TIP=$(git -C "$ROOT" rev-parse --short HEAD)
GATE=${GATE:-$(basename "$0" .sh)}
OUT_DIR="$ROOT/live/out"
mkdir -p "$OUT_DIR"
LOG="$OUT_DIR/$GATE-$TIER-$TIP-$(date +%Y%m%d-%H%M%S).log"
ADMIN_PORT=${ADMIN_PORT:-15000}
PF_PID=""
FAILS=0

log() { printf '%s %s\n' "$(date +%H:%M:%S)" "$*" | tee -a "$LOG"; }
kc() { kubectl -n "$NAMESPACE" "$@"; }

require_local_cluster() {
  local server
  server=$(kubectl config view --minify -o jsonpath='{.clusters[0].cluster.server}')
  case "$server" in
    https://127.0.0.1:*|https://localhost:*) log "cluster $server (local)";;
    *) log "REFUSED: $server is not the local cluster"; exit 2;;
  esac
}

# The admin listener is loopback inside the service pod: reach it through a port-forward we own.
start_admin() {
  kc port-forward pod/descent-service-0 "$ADMIN_PORT:5000" >>"$LOG" 2>&1 &
  PF_PID=$!
  for _ in $(seq 1 50); do curl -sf "127.0.0.1:$ADMIN_PORT/healthz" >/dev/null 2>&1 && return 0; sleep 0.2; done
  log "admin port-forward did not come up"; return 1
}

stop_admin() {
  if [ -n "$PF_PID" ] && ps -o args= -p "$PF_PID" 2>/dev/null | grep -q 'port-forward'; then kill "$PF_PID"; fi
  PF_PID=""
}

finish() {
  local rc=$?
  stop_admin
  [ "$FAILS" -gt 0 ] && rc=1
  log "gate=$GATE tier=$TIER tip=$TIP rc=$rc fails=$FAILS log=$LOG"
  exit "$rc"
}
trap finish EXIT

ops() { curl -sf "127.0.0.1:$ADMIN_PORT/ops/$1"; }

# jq-free JSON reads: `json '<python expr on d>' < file-or-pipe`
json() { python3 -c "import json,sys; d=json.load(sys.stdin); print($1)"; }

# wait_for <seconds> <description> <command...>: polls until the command succeeds; a timeout is a failure.
wait_for() {
  local secs=$1 what=$2; shift 2
  local deadline=$((SECONDS + secs))
  until "$@" >/dev/null 2>&1; do
    if [ $SECONDS -ge $deadline ]; then log "TIMEOUT after ${secs}s: $what"; FAILS=$((FAILS + 1)); return 1; fi
    sleep 1
  done
  log "ok: $what"
}

check() { # check <description> <command...>
  local what=$1; shift
  if "$@" >>"$LOG" 2>&1; then log "PASS $what"; else log "FAIL $what"; FAILS=$((FAILS + 1)); fi
}
