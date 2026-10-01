#!/usr/bin/env bash
# Plan §12 soak, live (TIER=fake): four bot-driven parties cycling hub → 1 → 2 → town for
# DURATION seconds (default an hour). At the end: reconcile reports nothing (no orphan lease, no
# World item on a dead instance, minted − terminal = live), every Crashed row has its audit row,
# the service's RSS is flat, and no game pod is an orphan. Assumes `make deploy TIER=fake` has run.
GATE=h9
. "$(dirname "$0")/lib.sh"
require_local_cluster
DURATION=${DURATION:-3600}
PARTIES=${PARTIES:-4}
SAMPLE=${SAMPLE:-$((DURATION / 40 < 5 ? 5 : (DURATION / 40 > 60 ? 60 : DURATION / 40)))}
DRIVER="$HOME/.dotnet/dotnet $ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll"
[ -f "$ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll" ] || { log "build Host.LiveDriver first"; exit 2; }
start_admin

LB=$(kc get svc descent-gateway -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):27015
log "gateway LoadBalancer $LB; $PARTIES parties for ${DURATION}s"
live_hub() { ops instances | json '[i for i in d if i["kind"]=="Hub" and i["state"]=="Live"][0]["podIp"]'; }
wait_for 120 "a live hub" live_hub
HUB_ID=$(ops instances | json '[i for i in d if i["kind"]=="Hub" and i["state"]=="Live"][0]["id"]')
check "levels seeded for depths 1 and 2" curl -sf -X POST "127.0.0.1:$ADMIN_PORT/ops/dev/levels?from=1&to=2&per=$((PARTIES + 2))"

RUN=$(printf '%06d' $((10#$(date +%s) % 1000000)))
IDS=()
for p in $(seq 1 "$PARTIES"); do IDS+=("765611983${RUN}${p}1" "765611983${RUN}${p}2"); done
STATUS="$OUT_DIR/$GATE-clients.json"
WORK="$OUT_DIR/$GATE-$TIP"
rm -rf "$WORK"; mkdir -p "$WORK"
$DRIVER clients "$LB" "$(IFS=,; echo "${IDS[*]}")" $((DURATION + 600)) "$STATUS" >>"$LOG" 2>&1 &
CLIENTS=$!
PIDS=()
cleanup() { for p in "${PIDS[@]}" "$CLIENTS"; do kill "$p" 2>/dev/null || true; done; finish; }
trap cleanup EXIT

ctl_on() { # ctl_on <instance id> <Method> [json]
  local ip body=${3:-}
  [ -n "$body" ] || body='{}'
  ip=$(ops instances | json "[i for i in d if i['id']=='$1'][0]['podIp']")
  $DRIVER ctl "$ip:5020" "$2" "$body"
}
all_connected() { json "len(d)==${#IDS[@]} and all(c['connected'] for c in d)" < "$STATUS" | grep -q True; }
wait_for 90 "${#IDS[@]} clients connected through the LoadBalancer" all_connected
leased_on() { local inst=$1; shift; for id in "$@"; do ctl_on "$inst" Player "{\"steamid\":\"$id\"}" | json 'd.get("leased", False)' | grep -q True || return 1; done; }
wait_for 90 "the hub leased every character" leased_on "$HUB_ID" "${IDS[@]}"

on_instance() {
  local inst=$1; shift
  for id in "$@"; do
    json "[c for c in d if c['steamid']=='$id'][0]['instance']=='$inst' and [c for c in d if c['steamid']=='$id'][0]['connected']" < "$STATUS" | grep -q True || return 1
  done
  leased_on "$inst" "$@"
}
until_ok() { local secs=$1; shift; local end=$((SECONDS + secs)); until "$@" >/dev/null 2>&1; do [ $SECONDS -lt $end ] || return 1; sleep 1; done; }
# party_trip <from> <Method> <json> <a> <b>: echoes the target once both members stand on it.
party_trip() {
  local reply target
  reply=$(ctl_on "$1" "$2" "$3") || return 1
  target=$(echo "$reply" | json 'd.get("instanceId","")')
  [ -n "$target" ] || { echo "no target: $reply" >&2; return 1; }
  until_ok 180 on_instance "$target" "$4" "$5" || { echo "never landed on $target" >&2; return 1; }
  echo "$target"
}
# One party's loop: hub → 1 (a boss, its drops picked up while there is room) → 2 → town.
party() {
  local n=$1 a=$2 b=$3 out="$WORK/party-$1" party l1 l2 town cycles=0 fails=0 picked=0
  party=$(ctl_on "$HUB_ID" Party "{\"steamids\":[\"$a\",\"$b\"]}" | json 'd["partyId"]') || { echo "fails=1 cycles=0 reason=party" > "$out"; return; }
  while [ $SECONDS -lt "$END" ]; do
    if l1=$(party_trip "$HUB_ID" Descend "{\"steamid\":\"$a\",\"depth\":1,\"partyId\":\"$party\"}" "$a" "$b" 2>>"$out.err") \
      && { for item in $(ctl_on "$l1" Kill "{\"steamid\":\"$a\",\"robot\":\"heavy\",\"boss\":true}" | json '" ".join(d.get("itemIds",[]))'); do
             ctl_on "$l1" Pickup "{\"steamid\":\"$a\",\"itemId\":\"$item\"}" | json 'd.get("ok",False)' | grep -q True && picked=$((picked + 1)); done; true; } \
      && l2=$(party_trip "$l1" Descend "{\"steamid\":\"$a\",\"depth\":2,\"partyId\":\"$party\"}" "$a" "$b" 2>>"$out.err") \
      && town=$(party_trip "$l2" TownPortal "{\"steamid\":\"$a\"}" "$a" "$b" 2>>"$out.err") && [ "$town" = "$HUB_ID" ]; then
      cycles=$((cycles + 1))
    else
      fails=$((fails + 1)); echo "$(date +%T) cycle $((cycles + fails)) failed" >>"$out.err"
      # Back to town from wherever the party stands before the next cycle.
      here=$(json "[c for c in d if c['steamid']=='$a'][0]['instance']" < "$STATUS")
      [ "$here" = "$HUB_ID" ] || party_trip "$here" TownPortal "{\"steamid\":\"$a\"}" "$a" "$b" >/dev/null 2>>"$out.err" || true
    fi
    echo "cycles=$cycles fails=$fails picked=$picked" > "$out"
  done
}
rss() { curl -sf "127.0.0.1:$ADMIN_PORT/metrics/http" | awk '/^process_working_set_bytes /{print int($2/1048576)}'; }
check "the service reports its working set" test -n "$(rss)"

END=$((SECONDS + DURATION))
for p in $(seq 1 "$PARTIES"); do
  party "$p" "${IDS[$(((p - 1) * 2))]}" "${IDS[$(((p - 1) * 2 + 1))]}" &
  PIDS+=($!)
done
log "parties running: ${PIDS[*]}"
RSS_LOG="$WORK/rss"
: > "$RSS_LOG"
while [ $SECONDS -lt "$END" ]; do
  echo "$SECONDS $(rss)" >> "$RSS_LOG"
  log "$(cat "$WORK"/party-? 2>/dev/null | tr '\n' ' ') rss=$(tail -1 "$RSS_LOG" | cut -d' ' -f2)MiB"
  sleep "$SAMPLE"
done
for p in "${PIDS[@]}"; do wait "$p" || true; done
PIDS=()

# Every party cycled, none failed.
for p in $(seq 1 "$PARTIES"); do
  check "party $p cycled with no failure ($(cat "$WORK/party-$p"))" \
    sh -c "grep -q 'fails=0' '$WORK/party-$p' && ! grep -q 'cycles=0 ' '$WORK/party-$p'"
done

# The ledger and the leases: reconcile reports nothing, and the totals balance.
ops items/totals > "$WORK/totals.json"
check "minted − terminal = live ($(cat "$WORK/totals.json" | json '(d["minted"], d["terminal"], d["live"])'))" \
  sh -c "python3 -c 'import json,sys; d=json.load(open(\"$WORK/totals.json\")); sys.exit(0 if d[\"balanced\"] and d[\"minted\"]>0 else 1)'"
curl -sf -X POST "127.0.0.1:$ADMIN_PORT/ops/reconcile" > "$WORK/reconcile.json"
check "reconcile finds nothing ($(json 'len(d)' < "$WORK/reconcile.json") findings)" sh -c "[ \"\$(python3 -c 'import json; print(len(json.load(open(\"$WORK/reconcile.json\"))))')\" = 0 ]"

# No Crashed row without its audit row.
ops instances > "$WORK/instances.json"
crashed_audited() {
  for id in $(json '" ".join(i["id"] for i in d if i["state"]=="Crashed")' < "$WORK/instances.json"); do
    ops "audit?target=$id&action=instance.crashed" | json 'len(d)' | grep -qx '[1-9][0-9]*' || { echo "no audit row for $id"; return 1; }
  done
}
check "every Crashed row has an instance.crashed audit row" crashed_audited

# No orphan pod: every game pod belongs to a non-terminal row with the same uid.
orphans() {
  kc get pods -l descent/kind -o jsonpath='{range .items[*]}{.metadata.name} {.metadata.uid}{"\n"}{end}' | while read -r name uid; do
    json "any(i['podName']=='$name' and i['podUid']=='$uid' and i['state'] not in ('Crashed','Reaped','Failed') for i in d)" < "$WORK/instances.json" | grep -q True \
      || { kc get pod "$name" -o jsonpath='{.metadata.deletionTimestamp}' | grep -q . || echo "orphan $name $uid"; }
  done
}
no_orphans() { local o; o=$(orphans); [ -z "$o" ] || { echo "$o"; return 1; }; }
check "no orphan game pod" no_orphans

# Service RSS flat: the last quarter's peak within 25 % of the second quarter's median.
check "service RSS flat ($(awk '{print $2}' "$RSS_LOG" | tr '\n' ' ')MiB)" python3 - "$RSS_LOG" <<'EOF'
import sys, statistics
v = [int(l.split()[1]) for l in open(sys.argv[1]) if len(l.split()) == 2]
if len(v) < 8: sys.exit(1)
q = len(v) // 4
base = statistics.median(v[q:2 * q])
sys.exit(0 if max(v[-q:]) <= base * 1.25 else 1)
EOF
