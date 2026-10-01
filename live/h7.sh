#!/usr/bin/env bash
# Plan §10 gate, live (TIER=fake): two clients in a party descend 1 → 2 → town → back with their
# inventories and the Live item count unchanged across every hop; a solo client asking for depth 1
# gets a different level; a hero dies, the carried items are Corpse-owned within the one RPC, the
# level pod is deleted, Lost & Found holds exactly the corpse's contents, and reclaim pays the fee.
# Every client only ever talks to the LoadBalancer. Assumes `make deploy TIER=fake` has run.
GATE=h7
. "$(dirname "$0")/lib.sh"
require_local_cluster
DRIVER="$HOME/.dotnet/dotnet $ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll"
[ -f "$ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll" ] || { log "build Host.LiveDriver first"; exit 2; }
start_admin

LB=$(kc get svc descent-gateway -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):27015
log "gateway LoadBalancer $LB"
live_hub() { ops instances | json '[i for i in d if i["kind"]=="Hub" and i["state"]=="Live"][0]["podIp"]'; }
wait_for 120 "a live hub" live_hub
HUB_ID=$(ops instances | json '[i for i in d if i["kind"]=="Hub" and i["state"]=="Live"][0]["id"]')
check "levels seeded for depths 1 and 2" curl -sf -X POST "127.0.0.1:$ADMIN_PORT/ops/dev/levels?from=1&to=2&per=3"

# Fresh SteamIDs per run: D-H12 claims outlive a run by Travel.SessionIdle.
RUN=$(printf '%06d' $((10#$(date +%s) % 1000000)))
A=765611982${RUN}01; B=765611982${RUN}02; C=765611982${RUN}03
STATUS="$OUT_DIR/$GATE-clients.json"
$DRIVER clients "$LB" "$A,$B,$C" 900 "$STATUS" >>"$LOG" 2>&1 &
CLIENTS=$!
trap 'kill "$CLIENTS" 2>/dev/null || true; finish' EXIT

ctl_on() { # ctl_on <instance id> <Method> [json]: the fake's control port on that instance's pod
  local ip body=${3:-}
  [ -n "$body" ] || body='{}'
  ip=$(ops instances | json "[i for i in d if i['id']=='$1'][0]['podIp']")
  $DRIVER ctl "$ip:5020" "$2" "$body"
}
all_connected() { json 'len(d)==3 and all(c["connected"] for c in d)' < "$STATUS" | grep -q True; }
wait_for 60 "3 clients connected through the LoadBalancer" all_connected
leased_on() { # leased_on <instance id> <steamid...>
  local inst=$1; shift
  for id in "$@"; do ctl_on "$inst" Player "{\"steamid\":\"$id\"}" | json 'd.get("leased", False)' | grep -q True || return 1; done
}
wait_for 60 "the hub leased all three characters" leased_on "$HUB_ID" "$A" "$B" "$C"
char_of() { ctl_on "$HUB_ID" Player "{\"steamid\":\"$1\"}" | json 'd["characterId"]'; }
CA=$(char_of "$A"); CB=$(char_of "$B")

PARTY=$(ctl_on "$HUB_ID" Party "{\"steamids\":[\"$A\",\"$B\"]}" | json 'd["partyId"]')
log "party $PARTY: $A ($CA), $B ($CB); solo $C"

# on_instance <instance id> <steamid...>: each client's route reached it and its lease is there.
on_instance() {
  local inst=$1; shift
  for id in "$@"; do
    json "[c for c in d if c['steamid']=='$id'][0]['instance']=='$inst' and [c for c in d if c['steamid']=='$id'][0]['connected']" < "$STATUS" | grep -q True || return 1
  done
  leased_on "$inst" "$@"
}
# trip <from instance> <Method> <request json> <label>: sets TARGET; the party lands on it.
trip() {
  local reply; reply=$(ctl_on "$1" "$2" "$3")
  echo "$2 from $1: $reply" >>"$LOG"
  TARGET=$(echo "$reply" | json 'd.get("instanceId","")')
  [ -n "$TARGET" ] || { log "FAIL $4: no target ($reply)"; FAILS=$((FAILS + 1)); exit 1; }
  wait_for 180 "$4 ($TARGET)" on_instance "$TARGET" "$A" "$B"
}
live_items() { ops items/totals | json 'd["live"] - d["byOwner"]["Reserve"]'; }
inventories() { # "steamid:item,item;..." for the party, read on the instance it stands on
  for id in "$A" "$B"; do printf '%s:%s;' "$id" "$(ctl_on "$1" Backpack "{\"steamid\":\"$id\"}" | json '",".join(sorted(d.get("itemIds",[])))')"; done
}
loot() { # loot <instance> <steamid>: a boss kill, every drop picked up
  local ids; ids=$(ctl_on "$1" Kill "{\"steamid\":\"$2\",\"robot\":\"heavy\",\"boss\":true}" | json '" ".join(d.get("itemIds",[]))')
  [ -n "$ids" ] || return 1
  for item in $ids; do ctl_on "$1" Pickup "{\"steamid\":\"$2\",\"itemId\":\"$item\"}" | json 'd.get("ok",False)' | grep -q True || return 1; done
}

# 1 → loot → 2 → town → back to 1, the inventories and the Live count unchanged across each hop.
trip "$HUB_ID" Descend "{\"steamid\":\"$A\",\"depth\":1,\"partyId\":\"$PARTY\"}" "the party on depth 1"
L1=$TARGET
L1_HASH=$(ops instances | json "[i for i in d if i['id']=='$L1'][0]['levelHash']")
check "$A loots on depth 1" loot "$L1" "$A"
check "$B loots on depth 1" loot "$L1" "$B"
INV=$(inventories "$L1"); LIVE=$(live_items)
log "inventories $INV live $LIVE"
same_state() { [ "$(inventories "$1")" = "$INV" ] && [ "$(live_items)" = "$LIVE" ]; }

trip "$L1" Descend "{\"steamid\":\"$A\",\"depth\":2,\"partyId\":\"$PARTY\"}" "the party on depth 2"
L2=$TARGET
check "inventories and Live count unchanged after 1 → 2" same_state "$L2"
trip "$L2" TownPortal "{\"steamid\":\"$A\"}" "the party back in town"
check "the town portal leads to the hub" test "$TARGET" = "$HUB_ID"
check "inventories and Live count unchanged after 2 → town" same_state "$HUB_ID"
trip "$HUB_ID" Descend "{\"steamid\":\"$A\",\"depth\":1,\"partyId\":\"$PARTY\"}" "the party back on depth 1"
BACK=$TARGET
check "back on depth 1 is the same level (D-H12)" test "$(ops instances | json "[i for i in d if i['id']=='$BACK'][0]['levelHash']")" = "$L1_HASH"
check "inventories and Live count unchanged after town → 1" same_state "$BACK"

# A solo player asking for depth 1 gets another level.
SOLO=$(ctl_on "$HUB_ID" Descend "{\"steamid\":\"$C\",\"depth\":1}" | json 'd.get("instanceId","")')
solo_there() { json "[c for c in d if c['steamid']=='$C'][0]['instance']=='$SOLO'" < "$STATUS" | grep -q True; }
wait_for 180 "the solo player on its own level ($SOLO)" solo_there
check "the solo level's hash differs from the party's" test "$(ops instances | json "[i for i in d if i['id']=='$SOLO'][0]['levelHash']")" != "$L1_HASH"

# Death: within the one RPC everything carried is Corpse-owned.
CARRIED=$(ctl_on "$BACK" Backpack "{\"steamid\":\"$A\"}" | json '" ".join(sorted(d.get("itemIds",[])))')
[ -n "$CARRIED" ] || { log "FAIL $A carries nothing to lose"; FAILS=$((FAILS + 1)); exit 1; }
DIED=$(ctl_on "$BACK" Die "{\"steamid\":\"$A\"}" | json '" ".join(sorted(d.get("itemIds",[])))')
check "the death reports exactly the carried items" test "$DIED" = "$CARRIED"
all_corpse() { for i in $CARRIED; do ops "items/$i" | json 'd["ownerKind"]' | grep -qx Corpse || return 1; done; }
check "every carried item is Corpse-owned right after the RPC" all_corpse

# The party portals out; the level pod is deleted; its sweep moves the corpse to Lost & Found.
trip "$BACK" TownPortal "{\"steamid\":\"$A\"}" "the party in town after the death"
BPOD=$(ops instances | json "[i for i in d if i['id']=='$BACK'][0]['podName']")
log "deleting $BPOD ($BACK)"
kc delete pod "$BPOD" --wait=false >>"$LOG" 2>&1
ended() { ops instances | json "[i for i in d if i['id']=='$BACK'][0]['state']" | grep -qE 'Crashed|Reaped'; }
wait_for 90 "the dead hero's level ended" ended
lf_items() { ops "characters/$CA" | json '" ".join(sorted(e["itemId"] for e in d["lostAndFound"]))'; }
check "Lost & Found holds exactly the corpse's contents" test "$(lf_items)" = "$CARRIED"

# Reclaim from the hub pays the fee.
curl -sf -X POST "127.0.0.1:$ADMIN_PORT/ops/dev/australium?character=$CA&amount=100000" >>"$LOG"
BEFORE=$(ops "characters/$CA" | json 'd["australium"]')
ENTRY=$(ops "characters/$CA" | json 'd["lostAndFound"][0]["id"]')
ITEM=$(ops "characters/$CA" | json 'd["lostAndFound"][0]["itemId"]')
FEE=$(ops "characters/$CA" | json 'd["lostAndFound"][0]["fee"]')
check "the entry carries a fee" test "$FEE" -gt 0
check "reclaim returns the entry's item" sh -c "$DRIVER ctl \"\$(curl -sf 127.0.0.1:$ADMIN_PORT/ops/instances | python3 -c 'import json,sys; print([i for i in json.load(sys.stdin) if i[\"id\"]==\"$HUB_ID\"][0][\"podIp\"])'):5020\" Reclaim '{\"steamid\":\"$A\",\"itemId\":\"$ENTRY\"}' | grep -q $ITEM"
check "reclaim paid the fee" test "$(ops "characters/$CA" | json 'd["australium"]')" = "$((BEFORE - FEE))"

# Every client only ever used one address: its session's client address never changed.
one_addr() { ops sessions | json "all(len({s['clientAddr'] for s in d if s.get('steamId')==id})==1 for id in ['$A','$B','$C'])" | grep -q True; }
check "each client kept one address through the LoadBalancer" one_addr
