#!/usr/bin/env bash
# Plan §7 gate, live (TIER=fake): 1 hub + 3 levels, each joined through the LoadBalancer; a level
# pod deleted is `crashed` with its sweep audited and its leases released; a service restart
# adopts every pod by name and uid. Assumes `make deploy TIER=fake` has run.
GATE=h4
. "$(dirname "$0")/lib.sh"
require_local_cluster
DRIVER="$HOME/.dotnet/dotnet $ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll"
[ -f "$ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll" ] || { log "build Host.LiveDriver first"; exit 2; }
start_admin

LB=$(kc get svc descent-gateway -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):27015
log "gateway LoadBalancer $LB"
hub_ip() { kc get pod -l descent/kind=hub --field-selector=status.phase=Running -o jsonpath='{.items[0].status.podIP}'; }
live_hub() { ops instances | json '[i for i in d if i["kind"]=="Hub" and i["state"]=="Live"][0]["podIp"]'; }
wait_for 120 "a live hub" live_hub
HUB=$(hub_ip)

check "levels seeded" curl -sf -X POST "127.0.0.1:$ADMIN_PORT/ops/dev/levels?from=1&to=1&per=3"

# Three players on the hub through the LoadBalancer, held for the whole gate.
IDS=76561198100000001,76561198100000002,76561198100000003
STATUS="$OUT_DIR/$GATE-clients.json"
$DRIVER clients "$LB" "$IDS" 400 "$STATUS" >>"$LOG" 2>&1 &
CLIENTS=$!
all_on_hub() { json 'all(c["connected"] for c in d) and len(d)==3' < "$STATUS" | grep -q True; }
wait_for 60 "3 clients connected through the LoadBalancer" all_on_hub
joined() { for id in ${IDS//,/ }; do $DRIVER ctl "$HUB:5020" Player "{\"steamid\":\"$id\"}" | json 'd.get("leased", False)' | grep -q True || return 1; done; }
wait_for 60 "the hub leased all three characters" joined

# Each solo player descends to depth 1: three level pods, three different levels.
for id in ${IDS//,/ }; do $DRIVER ctl "$HUB:5020" Descend "{\"steamid\":\"$id\",\"depth\":1}" >>"$LOG"; done
# Only the instances these clients stand on count: other runs' levels may still be live.
client_instances() { json '" ".join(sorted(c["instance"] for c in d))' < "$STATUS"; }
on_levels() {
  json 'len(set(c["instance"] for c in d))==3 and all(c["connected"] for c in d)' < "$STATUS" | grep -q True || return 1
  local ids; ids=$(client_instances)
  ops instances | json "all(any(i['id']==x and i['kind']=='Level' and i['state']=='Live' for i in d) for x in '$ids'.split())" | grep -q True
}
wait_for 180 "each client on its own live level" on_levels
LEVELS=$(client_instances)
distinct_hashes() { ops instances | json "len({i['levelHash'] for i in d if i['id'] in '$LEVELS'.split()})==3" | grep -q True; }
check "the three levels have three different hashes" distinct_hashes

# Crash: delete one level pod. The assertion is the row, the audit and the leases, not the pod's absence.
VICTIM=${LEVELS%% *}
VPOD=$(ops instances | json "[i for i in d if i['id']=='$VICTIM'][0]['podName']")
SWEEPS_BEFORE=$(ops audit/counts | json 'd.get("instance.sweep",0)')
log "deleting $VPOD ($VICTIM)"
kc delete pod "$VPOD" --wait=false >>"$LOG" 2>&1
crashed() { ops instances | json "[i for i in d if i['id']=='$VICTIM'][0]['state']" | grep -q Crashed; }
wait_for 90 "the deleted level is crashed" crashed
swept() { [ "$(ops audit/counts | json 'd.get("instance.sweep",0)')" -gt "$SWEEPS_BEFORE" ]; }
wait_for 30 "its sweep is audited" swept
no_lease() { ! ops leases | json "[l for l in d if l['instanceId']=='$VICTIM']" | grep -q characterId; }
check "no lease left on the crashed instance" no_lease

# Adoption: restart the service; every surviving pod keeps its row (same uid), none crashes.
BEFORE=$(ops instances | json '",".join(sorted(i["id"]+"/"+(i["podUid"] or "") for i in d if i["state"]=="Live"))')
log "live before restart: $BEFORE"
stop_admin
kc rollout restart statefulset/descent-service >>"$LOG" 2>&1
kc rollout status statefulset/descent-service --timeout=180s >>"$LOG" 2>&1
start_admin
adopted() { [ "$(ops instances | json '",".join(sorted(i["id"]+"/"+(i["podUid"] or "") for i in d if i["state"]=="Live"))')" = "$BEFORE" ]; }
wait_for 90 "every live instance adopted with the same pod uid" adopted
check "an instance.adopted audit row" sh -c "curl -sf 127.0.0.1:$ADMIN_PORT/ops/audit/counts | grep -q instance.adopted"

kill "$CLIENTS" 2>/dev/null || true
