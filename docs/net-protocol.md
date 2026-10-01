# The game's UDP protocol and handshake — what the gateway can influence

Plan phase H0f (`docs/plans/plan_host.md` §2). Sources: Valve's public documentation (the
Developer Wiki's *Server queries* page), the public Source SDK 2013, long-standing community
documentation of the Source network protocol, and what the shipped engine was observed to do
on this box (H0a/H0b, §12). The shipped Source SDK Base 2013 engine (`srcds_linux64`) is
closed, so **nothing here is settled until a capture confirms it**. Every row carries a
`Capture` column; captures (a real client against a real `srcds_linux64`, directly and
through the H0b relay, `tcpdump` on both sides) are run later and fill it.

Conventions. All integers on the wire are **little-endian**: connectionless packets are
written with `bf_write` (public SDK `tier1/bitbuf`), whose word stores are little-endian,
and every field of the handshake packets is byte-aligned. `i32` = `WriteLong`,
`i16` = `WriteShort`, `u8` = `WriteByte`, `cstr` = NUL-terminated string (`WriteString`),
`bytes` = raw (`WriteBytes`).

## 1. Packet classes on the wire

The first 4 bytes of every datagram, read as a little-endian `i32`, classify it.

| first 4 bytes | class | receive path | Capture |
|---|---|---|---|
| `FF FF FF FF` (-1) | connectionless (OOB); byte 4 is the type char | handled as connectionless before any netchannel lookup | pending (H0f capture) |
| `FE FF FF FF` (-2) | split packet | reassembled first; the reassembled payload is then classified again | pending (H0f capture) |
| `FD FF FF FF` (-3) | compressed | decompressed after split reassembly; payload after the 4 bytes starts with `LZSS` or `SNAP` id (header `{u32 id; u32 actualSize LE}`) | pending (H0f capture) |
| anything else | netchannel packet; bytes 0-3 are the outgoing sequence number | matched to a channel by exact `ip:port`; no channel → silently dropped | pending (H0f capture) |

- Order of processing: IP ban filter → split reassembly → decompress
  → connectionless check → netchannel lookup. A connectionless packet may in principle arrive
  split or compressed; the client's handshake packets are never either (built in a
  `MAX_ROUTABLE_PAYLOAD` = 1260 buffer; sent
  with no channel, so `nMaxRoutable = MAX_ROUTABLE_PAYLOAD`).
- Minimum message: 5 bytes, header + type.
- Split header, packed: `i32 netID=-2`, `i32 sequenceNumber`,
  `u16 packetID` (high byte = packet number, low byte = packet count),
  `u16 nSplitSize` (payload bytes per split, 576-12 .. 1260-12).
- The gateway rule this gives: **`FF FF FF FF` is parseable handshake/A2S; every other datagram
  is opaque and forwarded.** A split or compressed OOB packet is not produced by the client's
  handshake, so the gateway may forward those as opaque.

## 2. The handshake

Sequence:

```
client                                   server
A2S_GETCHALLENGE 'q' (resend every cl_resend s) ->
                                    <- S2C_CHALLENGE 'A'
C2S_CONNECT 'k'                        ->
                                    <- S2C_CONNECTION 'B'  or  S2C_CONNREJECT '9'
netchannel (SIGNONSTATE_CONNECTED .. FULL)
```

### 2.1 A2S_GETCHALLENGE (client → server), 20 bytes

| off | field | type | value | Capture |
|---|---|---|---|---|
| 0 | header | i32 | -1 | pending (H0f capture) |
| 4 | type | u8 | `'q'` 0x71 `A2S_GETCHALLENGE` | pending (H0f capture) |
| 5 | clientChallenge | i32 | the client's challenge, random per `connect` (top 4 bits clear) | pending (H0f capture) |
| 9 | padding | cstr | `"0000000000"` + NUL, 11 bytes | pending (H0f capture) |

Server reads only the `i32` and answers unconditionally, no rate
limit on this type.

### 2.2 S2C_CHALLENGE (server → client), 39 bytes with Steam auth

| off | field | type | value | Capture |
|---|---|---|---|---|
| 0 | header | i32 | -1 | pending (H0f capture) |
| 4 | type | u8 | `'A'` 0x41 `S2C_CHALLENGE` | pending (H0f capture) |
| 5 | magic | i32 | `S2C_MAGICVERSION` 0x5A4F4933; client disconnects on mismatch | pending (H0f capture) |
| 9 | challenge | i32 | the server's challenge for the client's address | pending (H0f capture) |
| 13 | clientChallenge | i32 | echo of the request's; client ignores the packet if it differs from its own challenge | pending (H0f capture) |
| 17 | authProtocol | i32 | `PROTOCOL_STEAM` = 3 on a dedicated server | pending (H0f capture) |
| 21 | steam2 key length | i16 | 0; client disconnects if non-zero | pending (H0f capture) |
| 23 | GS SteamID | u64 | server's own SteamID, raw native bytes | pending (H0f capture) |
| 31 | secure (VAC) | u8 | `BSecure` | pending (H0f capture) |
| 32 | padding | cstr | `"000000"` + NUL, 7 bytes | pending (H0f capture) |

The GS SteamID and secure flag feed the client's ticket request. A second, different
`S2C_CHALLENGE` while the client is still waiting for its connection is expected to be handled
like the first: the client sends a new connect with the new challenge (open question 1).

### 2.3 C2S_CONNECT (client → server) — the byte table the C# reader implements

`N`, `P`, `V` are the lengths of the three strings **including** their NUL.

| off | field | type | client side | server side | Capture |
|---|---|---|---|---|---|
| 0 | header | i32 = -1 | — | — | pending (H0f capture) |
| 4 | type | u8 = `'k'` 0x6B | — | — | pending (H0f capture) |
| 5 | protocol | i32 = `PROTOCOL_VERSION` 24 | — | exact match required | pending (H0f capture) |
| 9 | authProtocol | i32 = 3 (`PROTOCOL_STEAM`) | — | 1..3 valid | pending (H0f capture) |
| 13 | challenge | i32 | the value from the last `S2C_CHALLENGE` | checked first and again in `ConnectClient` | pending (H0f capture) |
| 17 | clientChallenge | i32 | the client's own random value | echoed in `S2C_CONNECTION`/`S2C_CONNREJECT` | pending (H0f capture) |
| 21 | name | cstr, N | the `name` convar | buffer 256 | pending (H0f capture) |
| 21+N | password | cstr, P | the `password` convar | buffer 256 | pending (H0f capture) |
| 21+N+P | product version | cstr, V | `steam.inf` `PatchVersion` | buffer 32; prefix compare, reject old/new | pending (H0f capture) |
| T-2 where T = 21+N+P+V | ticket length L | i16 | — | reject `<0` or `>2048`; later `<=0` or `>=2048`; `<=8` refused by Steam path | pending (H0f capture) |
| T | **SteamID64** (ticket blob byte 0) | u64 LE | the client's own SteamID, prepended to the ticket | read as the claimed identity | pending (H0f capture) |
| T+8 | Steam auth session ticket | bytes, L-8 | from `ISteamUser::GetAuthSessionTicket`; `L` counts the 8-byte prefix | passed to `ISteamGameServer::BeginAuthSession` with the SteamID | pending (H0f capture) |

- No other field is expected after the ticket (open question 3). The whole packet is
  ≤ 1260 bytes and never split.
- `STEAM_KEYSIZE` = 2048.
- **SteamID offset:** SteamID64 is the first 8 bytes of the ticket blob, i.e. at absolute
  offset `T = 23 + N + P + V` (the `i16` length sits at `21+N+P+V`), little-endian on x86. It
  is **in the clear** and **unverified** at this point: Steam's `BeginAuthSession` is what
  validates it (open question 2).
- **Inferred:** the Steam ticket from `ISteamUser::GetAuthSessionTicket`
  itself also carries the SteamID (public reverse-engineering: `u32 20`, `u64 GC token`,
  `u64 SteamID`, `u32 timestamp`, … → blob offset 8+12 = 20). The C# reader should use the
  prefix at blob offset 0 and treat the inner copy only as a cross-check.
- The client caches the ticket keyed by the dialled `ip:port`, GS SteamID and secure flag and
  re-sends the same bytes on the next connect to the same address; a different GS SteamID in
  a later `S2C_CHALLENGE` cancels it and mints a new one. The SteamID prefix is the same either way.

### 2.4 S2C_CONNECTION and S2C_CONNREJECT (server → client)

| packet | layout | source | Capture |
|---|---|---|---|
| `S2C_CONNECTION` | i32 -1, u8 `'B'` 0x42, i32 clientChallenge, cstr `"0000000000"` (20 bytes) | — | pending (H0f capture) |
| `S2C_CONNREJECT` | i32 -1, u8 `'9'` 0x39, i32 clientChallenge, cstr reason (e.g. `#GameUI_ServerRejectBadChallenge`) | — | pending (H0f capture) |
| client accepts `'B'` | only in `SIGNONSTATE_CHALLENGE` and if the echoed challenge is its own; then the netchannel's remote is the `ip:port` the `'B'` came from | — | pending (H0f capture) |
| client accepts `'9'` | same two checks, then disconnects with the reason | — | pending (H0f capture) |
| server challenge → netchannel | both sides key the netchannel on the challenge; the client uses the challenge of the **last connect sent** | — | pending (H0f capture) |

`S2C_CONNECTION` does not carry the server challenge; the netchannel does, in every packet (§5).
An out-of-band redirect packet is not a lever: the client is not known to act on one.

### 2.5 Client resend and give-up

| item | value | source | Capture |
|---|---|---|---|
| resend interval | `cl_resend` 6 s (1.5..20) | — | pending (H0f capture) |
| what is resent | a new `A2S_GETCHALLENGE` (not the connect), while still in `SIGNONSTATE_CHALLENGE` | — | pending (H0f capture) |
| give-up | after 4 attempts (≈ 24 s at the default) | — | pending (H0f capture) |
| source filter | client drops any OOB packet whose **IP** differs from the dialled address; the port is not compared | — | pending (H0f capture) |

## 3. Client-controlled fields (token candidates)

| field | where | max | managed server can read it? | Capture |
|---|---|---|---|---|
| name | C2S_CONNECT off 21 | 255 + NUL | yes: `ClientConnect(edict, name, address, …)` and later the `name` userinfo | pending (H0f capture) |
| password | C2S_CONNECT | 255 + NUL | **no**: compared by the engine to `sv_password` only; the client convar is `FCVAR_SERVER_CANNOT_QUERY` | pending (H0f capture) |
| product version | C2S_CONNECT | 31 + NUL | no; must prefix-match the server's `steam.inf` | pending (H0f capture) |
| clientChallenge | C2S_CONNECT off 17, A2S_GETCHALLENGE off 5 | i32 | no (engine-internal, `client->Connect(..., clientChallenge)`) | pending (H0f capture) |
| ticket blob | C2S_CONNECT off T | 2047 | the SteamID, after Steam validation, via the player's network id; the bytes, no | pending (H0f capture) |
| `connect <addr> <tag>` second arg | sets `cl_connectmethod`, `FCVAR_USERINFO`; kept across `retry`; `redirect` overwrites it with `"redirect"` | no spaces (split on `" "`) | yes, as userinfo after connect: `IVEngineServer::GetClientConVarValue` | pending (H0f capture) |
| other `FCVAR_USERINFO` convars | sent over the netchannel after connect, not in C2S_CONNECT | — | yes, `GetClientConVarValue` | pending (H0f capture) |

A token in `cl_connectmethod` travels end to end without the relay touching a byte, but only
after the netchannel is up, and only if the client was told to `connect addr token`.

## 4. Every use of the peer address

"Relay effect" assumes the H0b topology: every client reaches a backend from the gateway's one
pod IP, each session from its own source port (or, under `AddressPerPlayer`, its own IP).

| use | keyed on | source | relay effect | Capture |
|---|---|---|---|---|
| challenge derivation | **IP only**: `CRC32((u64)ip_be<<32 + nonce)` | — | every session from the gateway IP gets the **same** challenge within a nonce window; the port is irrelevant. Loopback is exempt | pending (H0f capture) |
| challenge lifetime | nonce rotates every `CHALLENGE_NONCE_LIFETIME` 6 s, current and previous accepted | — | a challenge is good for 6-12 s; a held connect older than 6 s may be refused `#GameUI_ServerRejectBadChallenge` | pending (H0f capture) |
| `MAX_REUSE_PER_IP` 5 | IP only, counts human clients that are connected but not yet fully in game, refuses when that count exceeds 5 | port ignored | Whether the shipped engine enforces it is unknown (open question 4). If it does, it counts every client of the gateway IP from `S2C_CONNECTION` until fully spawned — including map load and download — not just the handshake | pending (H0f capture) |
| `sv_max_connects_sec` | IP only | — | default 2.0 over a 4 s window (`sv_max_connects_window`): the 9th `C2S_CONNECT` from the gateway IP in a window is **silently dropped** (`return false`, no reject). Counted after the challenge check, only for `C2S_CONNECT` | pending (H0f capture) |
| `sv_max_queries_sec` | IP only | — | 3.0/s over 30 s for OOB types the engine does not handle itself (A2S etc.); moot while the gateway answers A2S | pending (H0f capture) |
| IP ban filter (`addip`/`banip`) | IP + mask, port ignored; drops **every** packet, before OOB dispatch | `sv_filterban` 1 | an IP ban on an instance bans the whole gateway. Never use; ban by SteamID (`banid`) | pending (H0f capture) |
| reconnect detection | exact `ip:port` | — | a connect from a peer that already owns a slot silently replaces that slot (`%s:reconnect`). With a pinned peer a returning player takes over its own stale slot; with an ephemeral peer the stale slot lives until timeout | pending (H0f capture) |
| netchannel lookup | exact `ip:port` | — | a changed source port (NAT rebind, new gateway socket) after `S2C_CONNECTION` is an unknown peer: its packets are dropped and the client times out | pending (H0f capture) |
| `sv_lan 1` | IP: same class B as server or RFC1918/loopback | `sv_lan` → no authentication | pod IPs (10.x) pass. An `AddressPerPlayer` pool must be RFC1918 (100.64/10 is not in the list) | pending (H0f capture) |
| Steam auth | **not** compared: the address is used only for a log line; `BeginAuthSession` gets ticket + SteamID | loopback remapped | relay transparent to the engine's Steam path; what Steam's backend does with the ticket's embedded client IP is not observable here | pending (H0f capture) |
| lan-only fallback | failed `NotifyClientConnect` tolerated when `BLanOnly` | — | — | pending (H0f capture) |
| game DLL / plugins | `ClientConnect(edict, name, "ip:port", …)` | — | sees the gateway peer; resolved by `ResolvePeer` | pending (H0f capture) |
| `INetChannelInfo` | `GetPlayerNetInfo(i)->GetAddress` | — | same | pending (H0f capture) |
| A2S challenge (`'W'`) | IP only (same derivation) | — | same as the connect challenge | pending (H0f capture) |
| client-side source check | IP only | — | gateway replies (its own or relayed) must leave from the public IP the client dialled | pending (H0f capture) |
| `retry` / `redirect` | re-dial the last dialled address / a new address | §6 | `retry` returns to the gateway address, so the route decides | pending (H0f capture) |

## 5. Netchannel framing

| item | detail | source | Capture |
|---|---|---|---|
| header | `i32 outSeq`, `i32 inSeqAck`, `u8 flags`, `u16 checksum`, `u8 reliableState`, [`u8 choked` if flag], `i32 challenge` | — | pending (H0f capture) |
| flags | `RELIABLE` 1, `COMPRESSED` 2, `ENCRYPTED` 4, `SPLIT` 8, `CHOKED` 16, `CHALLENGE` 32 | — | pending (H0f capture) |
| checksum | 16-bit fold of CRC32 over the bytes after the checksum field; multiplayer PC only; mismatch drops | — | pending (H0f capture) |
| challenge in every packet | always set (`PACKET_FLAG_CHALLENGE`); receiver drops on mismatch, and drops a packet without it once one was seen | — | pending (H0f capture) |
| sequencing | stale or duplicate sequence dropped | — | pending (H0f capture) |
| reliable streams | 8 subchannels (`MAX_SUBCHANNELS`), 2 streams (normal, file), fragments of `FRAGMENT_SIZE` 256 | — | pending (H0f capture) |
| `net_maxfragments` | fragment bytes per packet, default 1260, range 256..1260 | — | pending (H0f capture) |
| MTU / split | split above `net_maxroutable` (1260, FCVAR_USERINFO) clamped by `sv_maxroutable` (1260), 576..1260 | — | pending (H0f capture) |
| compression | whole-datagram (`-3` header), LZSS or Snappy | — | pending (H0f capture) |
| encryption / signing | **none**: `PACKET_FLAG_ENCRYPTED` is defined and never set; integrity is checksum + challenge only | — | pending (H0f capture) |
| keepalive | no dedicated message; liveness is ordinary traffic (`net_NOP` is only padding) | — | pending (H0f capture) |
| signon timeout | `SIGNON_TIME_OUT` 300 s, both sides, until FULL (and on changelevel) | — | pending (H0f capture) |
| server timeout in game | `sv_timeout` 65 s | — | pending (H0f capture) |
| client timeout in game | `cl_timeout` 30 s | — | pending (H0f capture) |
| floor / "timing out" | any timeout clamped to ≥ `CONNECTION_PROBLEM_TIME` 4 s, ≤ 3600 s | — | pending (H0f capture) |

A relay adds no bytes, so it cannot break the checksum, the challenge or the fragment sizes.
**Gateway session expiry must be longer than the longest silence the engine tolerates:**
65 s in game (server side), and up to 300 s during signon (map load, download).

## 6. Server-executable commands that move a client

| command | flags | behaviour | source | Capture |
|---|---|---|---|---|
| `retry` | `FCVAR_SERVER_CAN_EXECUTE` | re-connects to the last dialled address (the gateway) with a new client challenge | — | pending (H0f capture) |
| `redirect <addr>` | `FCVAR_SERVER_CAN_EXECUTE` | connects to `addr` with the method `redirect`; **refused** when `cl_connectmethod` is `serverbrowser_internet`, `quickpick*`, `quickplay*`, `matchmaking` or `coaching` | — | pending (H0f capture) |
| `connect <addr> [tag]` | `FCVAR_DONTRECORD` only | not server-executable when server commands are restricted; for a mod they are **not** restricted by default (only Valve's own mods restrict them), so any server `stuffcmd` runs | — | pending (H0f capture) |
| across a download | a connect disconnects first, which aborts the HTTP download, partial data persisted to the download cache and resumed later | — | pending (H0f capture) |

## 7. `netadr_t` is IPv4-only

`netadrtype_t` is `NA_NULL, NA_LOOPBACK, NA_BROADCAST, NA_IP`;
the address is `unsigned char ip[4]; unsigned short port;`; UDP
sockets are `PF_INET` / `AF_INET`. An IPv6 prefix per player
is not available. Capture: pending (H0f capture).

## 8. A2S queries

| query | format | source | Capture |
|---|---|---|---|
| engine handling (game server) | `A2S_INFO`/`PLAYER`/`RULES` are **not** answered by the engine itself; they are passed to the Steam game-server library (`HandleIncomingPacket`) when it shares the game socket | — | pending (H0f capture) |
| engine handling (SourceTV) | `CHLTVServer` answers `A2S_INFO` itself: `'I'`, u8 protocol, name, map, gamedir, description, i16 appid, u8 players, u8 max, u8 bots(0), u8 `d`/`l`, u8 os, u8 password, u8 secure, version, u8 EDF … | — | pending (H0f capture) |
| `A2S_SERVERQUERY_GETCHALLENGE` `'W'` → `'A'` + i32 | legacy query challenge | — | pending (H0f capture) |
| A2S_INFO request | `FF FF FF FF 54` `"Source Engine Query\0"` [+ i32 challenge when challenged] | Valve Server Queries (developer.valvesoftware.com/wiki/Server_queries) | pending (H0f capture) |
| A2S_INFO challenge (Dec 2020) | server may answer `FF FF FF FF 41` + i32 challenge; client re-sends the request with that challenge appended | Valve Server Queries (developer.valvesoftware.com/wiki/Server_queries) | pending (H0f capture) |
| A2S_INFO response `'I'` 0x49 | u8 protocol, cstr name, cstr map, cstr folder, cstr game, i16 id, u8 players, u8 max players, u8 bots, u8 server type (`d`/`l`/`p`), u8 environment (`l`/`w`/`m`/`o`), u8 visibility, u8 VAC, cstr version, u8 EDF; if EDF&0x80 i16 port; &0x10 u64 SteamID; &0x40 i16 SourceTV port + cstr name; &0x20 cstr keywords; &0x01 u64 GameID | Valve Server Queries (developer.valvesoftware.com/wiki/Server_queries) | pending (H0f capture) |
| A2S_PLAYER | `FF FF FF FF 55` + i32 challenge (-1 to ask) → `'A'` 0x41 + i32 → re-send → `'D'` 0x44: u8 count, then per player u8 index, cstr name, i32 score, f32 duration | Valve Server Queries (developer.valvesoftware.com/wiki/Server_queries) | pending (H0f capture) |
| A2S_RULES | `FF FF FF FF 56` + i32 challenge (-1 to ask) → `'A'` + i32 → `'E'` 0x45: i16 count, then cstr name, cstr value per rule; may arrive split (`-2` header: i32 id, u8 total, u8 number, i16 size — the same byte order as the engine's split header) | Valve Server Queries (developer.valvesoftware.com/wiki/Server_queries) | pending (H0f capture) |

## 9. Lever table

| lever | can / cannot | basis | chosen for §8 need | alternative | Capture |
|---|---|---|---|---|---|
| backend-facing source port as session id | can | netchannel and reconnect key on exact `ip:port` | session identity (§8.1), `ResolvePeer` | `cl_connectmethod` token | pending (H0f capture) |
| pinned port per player (`PortPerPlayer`) | can | as above; a returning player replaces its own stale slot | per-player peer (§8.1a) | ephemeral port, pin from the next hop | pending (H0f capture) |
| pinned IP per player (`AddressPerPlayer`) | can (CNI permitting) | challenge, reuse, connect-rate and IP bans are keyed on IP (§4) | removes every per-IP limit | port pin + admission + raised `sv_max_connects_sec` | pending (H0f capture) |
| handshake queueing / hold | can | client resends `getchallenge` every 6 s, gives up after 4 (§2.5) | admission, flip, identity-first (§8.1b) | none; drop would cost a 6 s resend | pending (H0f capture) |
| answering `A2S_GETCHALLENGE` at the gateway | can | client accepts any `S2C_CHALLENGE` from the dialled IP with its own clientChallenge | identity-first step 1 | forward to hub on an ephemeral peer | pending (H0f capture) |
| re-challenging with the backend's challenge | can | client sends a new connect on every `S2C_CHALLENGE` while in `SIGNONSTATE_CHALLENGE`; challenge is IP-derived, so fetching it from the pinned peer (or any port of the gateway IP) yields the value the backend will check | identity-first step 2 | — | pending (H0f capture) |
| reading the SteamID from C2S_CONNECT | can (read-only) | blob offset 0, §2.3 | identity-first route and pin | hub's `PlayerJoined` | pending (H0f capture) |
| A2S answers | can | engine delegates A2S to the Steam library; the gateway owns the public socket | §8.3 | forward to hub (shares the hub's per-IP query limit) | pending (H0f capture) |
| client-carried token (`connect addr token` → `cl_connectmethod`) | can, only via a `connect` the server or user issues; lost on `redirect` | — | not chosen | backup identity if a port pin is lost | pending (H0f capture) |
| `retry` vs `redirect` | can | `retry` re-dials the gateway; `redirect` is blocked for server-browser joins | `retry` for hops | `redirect` / `connect` to a NodePort (plan §14.5) | pending (H0f capture) |
| netchannel payload | cannot | checksum + challenge in every packet (§5) | never touched | — | pending (H0f capture) |
| challenge derivation | cannot | inside the engine, IP + nonce | — | — | pending (H0f capture) |
| `MAX_REUSE_PER_IP` | cannot (hard-coded) | — | admission, or `AddressPerPlayer` | — | pending (H0f capture) |
| `sv_max_connects_sec` | can (convar) | — | raised on every instance | `AddressPerPlayer` | pending (H0f capture) |

### Identity-first, in detail

1. Client `A2S_GETCHALLENGE` (clientChallenge `c` at off 5): the gateway answers with a
   well-formed `S2C_CHALLENGE` (§2.2, echo `c`, authProtocol 3, key length 0, a GS SteamID,
   secure flag).
2. Client `C2S_CONNECT`: gateway reads SteamID at `T`, holds the packet, picks peer and route.
3. From the pinned peer the gateway sends `A2S_GETCHALLENGE` **with the client's own `c`**. The
   backend's `S2C_CHALLENGE` then already echoes `c` and carries the backend's GS SteamID and
   secure flag, so the gateway can forward it **unchanged** from its public socket.
4. The client sends a second `C2S_CONNECT` with the backend challenge; the gateway forwards it
   from the pinned peer. If the GS SteamID differs from step 1's, the client mints a fresh
   ticket — same SteamID prefix. Filling step 1 with the
   backend's last-seen GS SteamID avoids that.
5. Steps 3-4 must complete within the challenge window (6-12 s, §4).

## 10. What §8 says that this note contradicts or qualifies

- **`MAX_REUSE_PER_IP` (§8.1, H0b).** Whether the shipped engine enforces it is unknown. What it
  would count is clients of one IP that are **connected but not yet fully spawned**
  (signon ≥ CONNECTED and ≠ FULL), port ignored, refusing when that count exceeds 5 — so the
  7th, not the 6th, if the new client is not yet counted (the call site is unknown). That span
  runs from `S2C_CONNECTION` through map load and download to `SIGNONSTATE_FULL`, not from
  `getchallenge` to the first netchannel packet. If H0b shows 2013 enforces it, the admission
  window in §8.1 must end at "player active" (a server signal such as `PlayerJoined`), not at
  "first netchannel packet or 5 s"; if 2013 does not, admission is unnecessary.
- **Session expiry 60 s (§8.1).** Shorter than `sv_timeout` 65 s and far shorter than the
  300 s signon timeout. A client silent during a long map load could lose its gateway session
  while the engine still holds its slot. Expiry should be ≥ 300 s, or ≥ 65 s once the service
  reports the player active.
- **"The engine's challenge is bound to the backend that issued it" (§8.1b).** Bound to the
  backend's nonce **and the peer IP**, not the port, and valid only 6-12 s. `Gateway.HoldMs`
  (15 s) exceeds that: a `C2S_CONNECT` held past ~6 s is refused `BadChallenge`. Holds of
  handshake packets should replay `getchallenge`, never a stale connect.
- **Replay of held `getchallenge`s on a flip (§8.1b.2).** Each `S2C_CHALLENGE` makes the client
  send a connect; several replayed `getchallenge`s produce several connects from one peer, and
  each later one takes the reconnect path with a new Steam
  auth. Replay only the newest held `getchallenge`.
- **"Nothing can be added to the datagram" (§8.1).** True for the relay, but a client-carried
  token exists end to end (`connect addr token` → `cl_connectmethod`, readable by the server).
  Not chosen; noted as the alternative.
- **SteamID in C2S_CONNECT (§8.1a).** Expected in the clear at blob
  offset 0, unverified until Steam answers. Consistent with the plan's R11 caveat.
- **`sv_max_connects_sec` (§8.1).** Per IP, 2.0 over a 4 s window, counted only for connects
  that pass the challenge check, excess dropped without a reject — the symptom is a client
  resend, not an error message.
- **IP bans.** `addip` on an instance bans the gateway. Admin tooling must never issue it.

## 11. The sidecar transport (ruled 2026-09-30): what it changes

Sections 4 and 9 were written for plan §8.1 as it stood: every client reaching a backend from the
gateway's one pod IP, told apart by source port. The user has since ruled a different path, and
the gateway (`Host.Gateway`) and the pod sidecar (`Host.Sidecar`, `proto/relay.proto`) implement it:

```
client ──UDP──▶ gateway :27015 ──gRPC PeerRelay, one stream per session──▶ sidecar :5010 (in the game pod)
                                                                              │ UDP from 127.1.x.y:29005,
                                                                              ▼ one address per live client
                                                                         srcds 127.0.0.1:27015
```

- **Why.** Each client reaches the engine from **its own loopback address** inside the pod, so every
  per-IP mechanism of §4 applies per player rather than to the gateway as a whole. The engine's
  `netadr_t` is IPv4-only (§7), so a unique IPv4 per client is the only way to get that, and
  inside a pod 127.0.0.0/8 is free for the taking (Linux routes the whole /8 to `lo`; the sidecar
  binds `127.1.x.y` without configuring anything). The real address never reaches the engine;
  the game server asks the sidecar (`PeerInfo.Resolve`, loopback only) with the address
  `INetChannelInfo::GetAddress` reports.
- **Allocation.** Addresses come from `SIDECAR_AddressPool` (default `127.1.0.0/16`) in order and are
  unique within the instance only. A SteamID that reconnects to the same instance gets its address
  back while the instance lives; a session with no SteamID yet gets a fresh one; an address released
  by one player goes to another only after 60 s. Every peer binds `SIDECAR_PeerPort` (29005; not 27005, which srcds's own
  client socket takes on the wildcard address), so a
  returning player's `ip:port` is identical and the engine's reconnect path
  lets it take over its own stale slot.
- **The engine must listen on 127.0.0.1** (or 0.0.0.0): the sidecar sends to `SIDECAR_EngineEndpoint`
  (127.0.0.1:27015) and accepts replies only from it. An engine bound with `+ip <pod ip>` alone
  would never hear the sidecar.
- **Not 127.0.0.1 itself.** `IsLocalhost` is exactly 127.0.0.1 and a
  localhost client skips the server password and has its address
  remapped for Steam. Any other 127.x is an ordinary `NA_IP`
  (`IsLoopback` means the engine's in-process buffers, `NA_LOOPBACK`), so
  the challenge check is **not** skipped for it. The sidecar refuses
  a pool that contains 127.0.0.1.

| mechanism (§4) | through the UDP relay (§4's column) | through the sidecar | source | Capture |
|---|---|---|---|---|
| challenge derivation | one challenge for every client of the gateway IP | one per player address | — | pending (H0f capture) |
| `MAX_REUSE_PER_IP` | would count every client of the gateway, hence §8.1's admission | counts one player's own slots: moot; **admission dropped** | — | pending (H0f capture) |
| `sv_max_connects_sec` | 2 connects / 4 s for the whole gateway; must be raised | per player; the default is fine | — | pending (H0f capture) |
| IP ban (`addip`) | bans the gateway | bans one player's address on one instance (sticky for that SteamID while the instance lives); still prefer `banid` | — | pending (H0f capture) |
| reconnect detection (exact `ip:port`) | pinned port needed for a player to take over its stale slot | same address + fixed port for a returning SteamID | — | pending (H0f capture) |
| netchannel continuity across a gateway restart | lost unless the port can be rebound | identified sessions reopen on the same `ip:port`; unidentified ones via `Open.peer_hint` from SyncTable | — | pending (H0f capture) |
| `sv_lan 1` | pod IPs (10.x) pass | 127.x is `IsReservedAdr` and passes | — | pending (H0f capture) |
| Steam auth (`sv_lan 0`) | the engine reports the gateway's pod IP to Steam | the engine reports 127.x to `NotifyClientConnect` (only 127.0.0.1 is remapped) — H0c must show Steam accepts it | — | pending (H0f capture) |
| game DLL `ClientConnect` / `GetAddress` | the gateway peer, resolved by `ResolvePeer` | the player's loopback peer, resolved by `PeerInfo.Resolve` on 127.0.0.1:5011 | — | pending (H0f capture) |

Lever table (§9) rows this replaces or adds:

| lever | can / cannot | chosen for | alternative | Capture |
|---|---|---|---|---|
| a loopback address per player behind a pod sidecar | can (any 127.x except 127.0.0.1, inside the pod) | session identity, per-player engine limits, real address via `PeerInfo` | backend-facing source port per session + admission (§9 rows 1-3) | pending (H0f capture) |
| per-backend handshake admission | not needed | — | kept only if H0b shows the sidecar path still trips a per-IP limit | pending (H0f capture) |
| per-source-IP `HandshakesPerSecond` at the gateway | can | abuse limit at the public edge (Q24) | none | pending (H0f capture) |
| hold until the sidecar answers `Opened` | can | flips and cold start: a new backend gets nothing before its stream is up | client resend (6 s, §2.5) | pending (H0f capture) |

What the extra hop costs, to measure in H0b: one gRPC (HTTP/2 over TCP) hop per direction inside the
cluster. Streams of different sessions to one pod share a TCP connection, so a lost segment delays
every session on it (head-of-line blocking) where UDP would have lost one datagram; the gateway opens
more than one HTTP/2 connection per sidecar when streams exceed one connection's limit
(`EnableMultipleHttp2Connections`). Added RTT and jitter under loss are H0b rows.

## 12. Interpose mode: the engine sees the real client address (D-H11 addition)

With the relay inside the launcher (D-H11), the launcher can serve the engine's own socket calls:
`Relay.Mode = Interpose` (`RELAY_Mode`; default `Loopback`, §11, until Interpose is proven against the
real engine). The engine then sees each client's **real** `ip:port`; no 127.x address is involved and
`PeerInfo.Resolve(peer)` returns `peer == client_addr`.

What the engine does on its UDP game socket, from `objdump -T` of the shipped 64-bit libraries in the
engine image and its observed behaviour (§12.1):

| call | where | Capture |
|---|---|---|
| `socket(PF_INET, SOCK_DGRAM)`, `ioctlsocket(FIONBIO)` → non-blocking | — | pending (H0f capture) |
| `bind` to the game port (+ `-ip`), also a client socket on `clientport` 27005 and HLTV | — | pending (H0f capture) |
| `recvfrom` through tier0's VCR hook: `libtier0_srv.so` references `recvfrom@GLIBC_2.2.5` | — | pending (H0f capture) |
| `sendto` from `engine_srv.so` (`sendto@GLIBC_2.2.5`), also from the queued-packet thread (`net_queued_packet_thread`), so the hook runs on more than one engine thread | — | pending (H0f capture) |
| no `select` / `poll` on the game socket: the dedicated frame sleeps with `usleep` and then drains `recvfrom` until EAGAIN, so no doorbell is needed (`engine_srv.so` does import `select`, for the TCP rcon sockets) | — | pending (H0f capture) |
| `ioctlsocket(FIONREAD)` only in `_DEBUG` builds | — | pending (H0f capture) |

Mechanism (`src/Host.Launcher/Interpose.cs`): the NativeAOT executable exports `recvfrom` and `sendto`
(`[UnmanagedCallersOnly(EntryPoint=…)]`, `--export-dynamic-symbol`) with the symbol version
**`GLIBC_2.2.5`** (`interpose.map`): the dynamic loader binds a versioned reference only to a definition
of the same version name, and the executable is first in the global scope, so the engine libraries
loaded afterwards bind to the launcher's. Only the fd bound to the game port (found by `getsockname`,
cached) and calls with `flags == 0` are diverted; `recvfrom` then returns the next relayed datagram and
writes the client's real `sockaddr_in`, and `sendto` to a live session's address goes down its stream.
Everything else — other fds, other addresses, no session, any exception — falls through to libc's own
function, looked up on an explicit `libc.so.6` handle (no recursion; libc sets errno). .NET's own
sockets (the relay's Kestrel and gRPC) use `recvmsg`/`sendmsg`, not these.

What it changes, against §4 and §11:

| mechanism | Loopback (§11) | Interpose | Capture |
|---|---|---|---|
| address the engine sees | 127.x per player | the client's real address | pending (H0f capture) |
| `sv_lan 1` | 127.x passes (`IsReservedAdr`) | a public client address fails `IsReservedAdr` and is refused: Interpose needs `sv_lan 0`, hence Steam auth (H0c) | pending (H0f capture) |
| Steam auth (`sv_lan 0`) | the engine reports 127.x | the engine reports the real address, as without a relay | pending (H0f capture) |
| IP bans, `MAX_REUSE_PER_IP`, `sv_max_connects_sec` | per player per instance | per real address (players behind one NAT share them again) | pending (H0f capture) |
| game DLL `GetAddress` | loopback peer → `PeerInfo` | the real address directly | pending (H0f capture) |

Proven on this box (2026-09-30, commit 438caea): with a scratch C library standing in for
`dedicated_srv.so` (normal link, `GLIBC_2.2.5` references, never committed), the published launcher in
Interpose mode delivered a relayed datagram whose source the library read as `203.0.113.7:27005`, and
the library's `sendto` to that address came back down the PeerRelay stream; the Loopback control saw
`127.66.0.1`; the launcher built without `interpose.map` delivered nothing.

### 12.1 Against the real engine: sends to 127.0.0.1 bypass the socket (found and fixed, f7e83e5)

Setup (podman on this box): the engine image with the dev test mod (`+sv_lan 1 -insecure`), relay
published on loopback, a local gateway routed to it, and the H0b probe (the real `A2S_GETCHALLENGE`
and `C2S_CONNECT` bytes of §2) sending from `127.0.0.1:<port>` (`/tmp/lane-g/g3/run.sh`).

| run | client saw | `/healthz` interpose | engine | Capture |
|---|---|---|---|---|
| h0a-b50f9c7, `RELAY_Mode=Interpose` | no `S2C_CHALLENGE` | served 6, **sent 0** | — | pending (H0f capture) |
| same + `+net_showudp 1` | no `S2C_CHALLENGE` | served 6, sent 0 | `UDP -> 127.0.0.1:34601: sz=39 OOB 'A'` ×3 — the reply was built, to the right (real) address | pending (H0f capture) |
| same + `+net_usesocketsforloopback 1` | `S2C_CHALLENGE` 39 B, `S2C_CONNECTION` 20 B | served 2, sent 4 | `Client "h0b-probe" connected (127.0.0.1:50650)` | pending (H0f capture) |
| g3-f7e83e5 (the fix), Interpose, no extra args | `S2C_CHALLENGE` 39 B, `S2C_CONNECTION` 20 B | served 2, sent 4 | `connected (127.0.0.1:47722)`; probe's own address `127.0.0.1:47722` | pending (H0f capture) |
| g3-f7e83e5, Loopback (control) | both, as before | — | `connected (127.1.0.1:29005)` | pending (H0f capture) |

Cause: the engine logs the send (`net_showudp`) and then diverts every destination that is
exactly 127.0.0.1 to the engine's
in-process loopback buffers, unless `net_usesocketsforloopback` is set (default 0). So the engine never called `sendto` for the reply:
not a symbol, destination or fd problem (the `net_showudp` line shows the right destination; recvfrom
on the same fd was served). Fix: in Interpose mode the launcher adds `+net_usesocketsforloopback 1` to
the engine's arguments unless given (`LauncherApp.EngineArgs`). It changes nothing for any other
destination, and Loopback mode's 127.1.x.y peers are not `IsLocalhost`. Only a client whose real
address is 127.0.0.1 — a local test like this one — hits the branch; a player through the
LoadBalancer never does. Note that a 127.0.0.1 client is also treated as local for the password check, which only a
local tester can be.

## Open questions the captures must settle

1. Does the 2013 client send a second `C2S_CONNECT` on a second `S2C_CHALLENGE`?
2. Is the SteamID64 at ticket-blob offset 0 in 2013, little-endian, and does the inner Steam ticket repeat it at blob offset 20?
3. Is `PROTOCOL_VERSION` 24 and `S2C_MAGICVERSION` 0x5A4F4933 on the wire in 2013; any extra field after the ticket?
4. Does the 2013 server call `CheckIPConnectionReuse`, where, and at what count does the Nth half-connected client from one IP get refused?
5. Is the challenge carried in every netchannel packet in 2013 (flag 0x20 + i32), and does it equal the `S2C_CHALLENGE` value?
6. Does the netchannel ever go silent > 60 s during map load or HTTP download, and what are 2013's `sv_timeout`, `cl_timeout`, signon timeout?
7. Does 2013 `srcds` (its bundled Steam library) answer `A2S_INFO` with the Dec 2020 challenge?
8. Does `retry`/`redirect` keep the client's UDP source port, and does a `retry` during an HTTP download resume from the cache?
9. Does a reconnect from the same peer end the old Steam auth session, or does `BeginAuthSession` return `DuplicateRequest` for a SteamID whose stale slot is still held on an ephemeral peer?
10. Does the client accept an `S2C_CHALLENGE` whose GS SteamID differs from the first one without a visible delay (new ticket mint)?
11. Does Steam accept a client the engine reports from a 127.x address (`sv_lan 0`, the sidecar path)? (H0c)
12. Does `srcds_linux64` answer the sidecar's 127.1.x.y peers from 127.0.0.1:27015 (the source the sidecar accepts), with the engine bound to 0.0.0.0?
13. Added RTT and jitter of the gateway → sidecar gRPC hop, and its behaviour under packet loss (head-of-line blocking across sessions on one connection). (H0b)
14. ~~Interpose mode against the real engine~~ — settled on this box (§12.1): relayed datagrams are read with the real address and the replies reach the stream once sends to 127.0.0.1 use the socket. Still open: a non-loopback client (the LoadBalancer path) and Steam auth with `sv_lan 0` (H0c).
