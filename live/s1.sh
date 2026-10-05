#!/usr/bin/env bash
# plan_steam_auth S9, live (TIER=fake): Steam auth at the gateway with the fake Steam, through the
# LoadBalancer. Assumes `make deploy TIER=fake ISOLATED=1` has run (namespace tf2-descent-iso, client
# UDP on 27215). The temp-ban step bans this box's address, so it runs last and lifts the ban.
GATE=s1
. "$(dirname "$0")/lib.sh"
require_local_cluster
DRIVER="$HOME/.dotnet/dotnet $ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll"
[ -f "$ROOT/bin/Debug/Host.LiveDriver/SourceSharp.Host.LiveDriver.dll" ] || { log "build Host.LiveDriver first"; exit 2; }
PORT=${GATEWAY_PORT:-27215}
start_admin

LB=$(kc get svc tf2-descent-gateway -o jsonpath='{.status.loadBalancer.ingress[0].ip}'):$PORT
log "gateway LoadBalancer $LB"
hub_ip() { kc get pod -l sourcesharp/kind=hub --field-selector=status.phase=Running -o jsonpath='{.items[0].status.podIP}'; }
live_hub() { ops instances | json '[i for i in d if i["kind"]=="Hub" and i["state"]=="Live"][0]["podIp"]'; }
wait_for 120 "a live hub" live_hub
HUB=$(hub_ip)
steam_on() { ops gateway | json 'd["steam"]["mode"]=="Fake" and d["steam"]["loggedOn"]' | grep -q True; }
wait_for 60 "the gateway's fake Steam logged on" steam_on

route_auth() { ops gateway | json "[r['auth'] for r in d['routes'] if r['steamId']=='$1'][0]"; }
hub_events() { $DRIVER ctl "$HUB:5020" Status | json '" | ".join(d.get("events", []))'; }
attempt() { # attempt <steamid[:tail]> <count> -> the last outcome
  local out="$OUT_DIR/$GATE-attempt.json"
  $DRIVER attempts "$LB" "$1" "$2" "$out" >>"$LOG" 2>&1
  json 'd[-1]["outcome"]' < "$out"
}

# 1. A client is validated before it reaches the hub; its route and verdict row say so.
A=76561198200000001
STATUS_A="$OUT_DIR/$GATE-a.json"
$DRIVER clients "$LB" "$A" 600 "$STATUS_A" >>"$LOG" 2>&1 &
CLIENTS_A=$!
GATE_PIDS+=("$CLIENTS_A")
a_on() { json 'd[0]["connected"]' < "$STATUS_A" | grep -q True; }
wait_for 60 "A connected through the gateway" a_on
a_valid() { route_auth "$A" | grep -qx valid; }
wait_for 30 "A's route is valid at the gateway" a_valid
a_row() { ops steam-auth | json "any(r['steamId']=='$A' and r['state']=='valid' for r in d)" | grep -q True; }
wait_for 30 "A's verdict row is stored" a_row

# 2. One connection per SteamID: a second client for A is refused while A plays (and it is not a strike).
SECOND=$(attempt "$A:second" 1)
log "second A: $SECOND"
check "a second A is refused 'already connected'" grep -q "already connected" <<<"$SECOND"

# 3. A real leave releases the ticket and the SteamID: a rejoin from a new port is admitted at once.
$DRIVER ctl "$HUB:5020" Leave "{\"steamid\":\"$A\"}" >>"$LOG" 2>&1
a_ended() { route_auth "$A" | grep -qx none; }
wait_for 30 "A's auth ended at the gateway after leaving" a_ended
REJOIN=$(attempt "$A:rejoin" 1)
log "rejoin A: $REJOIN"
check "A rejoins at once from a new port" grep -q "^connected" <<<"$REJOIN"
kill "$CLIENTS_A" 2>/dev/null || true

# 4. A gateway restart: a playing client is re-authed through the game and keeps playing.
#    The gateway's process restarts in place (its container, same pod, same IP). Replacing the pod is not a
#    test of this on k3s: servicelb masquerades every client to the node (10.42.0.1) and a new pod IP gives the
#    client's flow a new source port, so the carried session never sees it again (measured 2026-10-04:
#    carried 10.42.0.1:13681 "never resumed", the client back as 10.42.0.1:52208). A load balancer that keeps
#    the client's address (externalTrafficPolicy Local on a real one) does not have that problem.
B=76561198200000002
STATUS_B="$OUT_DIR/$GATE-b.json"
$DRIVER clients "$LB" "$B" 600 "$STATUS_B" >>"$LOG" 2>&1 &
CLIENTS_B=$!
GATE_PIDS+=("$CLIENTS_B")
b_on() { json 'd[0]["connected"]' < "$STATUS_B" | grep -q True; }
wait_for 60 "B connected" b_on
# Settled before the restart: B relaying under a valid route, and its handshake long over, so no resent
# connect can reach the new process and validate B afresh (seen at 8ca1a2e: a restart in the same second as
# the connect re-admitted B instead of re-authing it).
b_route_valid() { route_auth "$B" | grep -qx valid; }
wait_for 30 "B's route valid" b_route_valid
sleep 5
EPOCH_BEFORE=$(ops gateway | json 'd["steam"]["epoch"]')
GW_POD=$(kc get pod -l app=tf2-descent-gateway -o jsonpath='{.items[0].metadata.name}')
GW_UID=$(kc get pod "$GW_POD" -o jsonpath='{.metadata.uid}')
RESTARTS_BEFORE=$(kc get pod "$GW_POD" -o jsonpath='{.status.containerStatuses[0].restartCount}')
log "restarting the gateway in pod $GW_POD uid $GW_UID (restarts $RESTARTS_BEFORE, epoch $EPOCH_BEFORE)"
kc exec "$GW_POD" -- kill 1 >>"$LOG" 2>&1 || true
restarted() { [ "$(kc get pod "$GW_POD" -o jsonpath='{.metadata.uid} {.status.containerStatuses[0].restartCount}')" = "$GW_UID $((RESTARTS_BEFORE + 1))" ]; }
wait_for 60 "the gateway container restarted in the same pod" restarted
new_gateway() { local e; e=$(ops gateway | json 'd["steam"]["epoch"] if d["reachable"] else ""'); [ -n "$e" ] && [ "$e" != "$EPOCH_BEFORE" ]; }
wait_for 120 "a new gateway with a new Steam logon" new_gateway
b_reauthed() { json 'd[0]["reauths"]>=1 and d[0]["connected"]' < "$STATUS_B" | grep -q True && route_auth "$B" | grep -qx valid; }
b_carried() { route_auth "$B" | grep -qxE 'reauthing|valid'; }
wait_for 30 "B's session carried into the restarted gateway" b_carried
wait_for 90 "B re-authed through the game and valid again" b_reauthed
check "the hub carried B's reauth ($(hub_events | grep -o "reauth $B [A-Za-z]*" | head -1))" grep -q "reauth $B Ok" <<<"$(hub_events)"
check "B still on the hub after the restart" json 'd[0]["connected"] and d[0]["retries"]==0' < "$STATUS_B"

# 5. A ticket Steam refuses: the reason reaches the client, nothing reaches the hub.
C=76561198200000003
DENIED=$(attempt "$C:deny:VAC banned" 1)
log "denied C: $DENIED"
check "a denied ticket is refused with Steam's reason" grep -q "Steam auth failed: VAC banned" <<<"$DENIED"

# 6. A SteamID ban is enforced by the gateway, before Steam is asked; lifting it lets the player in.
D=76561198200000004
curl -sf -X POST "127.0.0.1:$ADMIN_PORT/ops/bans?kind=steamid&value=$D&reason=gate" >>"$LOG"
BANNED=$(attempt "$D" 1)
check "a banned SteamID is refused 'banned'" grep -q "rejected: banned" <<<"$BANNED"
curl -sf -X DELETE "127.0.0.1:$ADMIN_PORT/ops/bans?kind=steamid&value=$D" >>"$LOG"
check "the lifted SteamID connects" grep -q "^connected" <<<"$(attempt "$D:lifted" 1)"

# 7. Last: five bad tickets from this box become a temp ban on its address; packets are dropped; lift it.
E=76561198200000005
attempt "$E:deny:invalid" 5 >>"$LOG"
auto_ban() { ops bans | json '[b["value"] for b in d if b["kind"]=="address" and b["source"]=="auto"][0]'; }
wait_for 30 "the gateway's temp ban stored" auto_ban
BANNED_IP=$(auto_ban)
log "temp-banned address $BANNED_IP"
check "a temp-banned address gets no answer at all" grep -q "no answer" <<<"$(attempt "$E:after-ban" 1)"
curl -sf -X DELETE "127.0.0.1:$ADMIN_PORT/ops/bans?kind=address&value=$BANNED_IP" >>"$LOG"
check "after the lift this box connects again" grep -q "^connected" <<<"$(attempt "$E:after-lift" 1)"
kill "$CLIENTS_B" 2>/dev/null || true
