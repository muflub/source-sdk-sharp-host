# plan_steam_auth.md: Steam ticket validation at the gateway, Steam off on instances, bans, the server browser

Status: **reviewed, all rulings recorded** (2026-10-04, revision 3). Untracked until reviewed. Amends plan_host
Q24 ("ticket validation is a §12 item"), §8.1b (the gateway "never rewrites" a packet; ruled
2026-10-04: it may), §8.3 (A2S: the Steam library joins in), D-H10/D-H11 (Interpose keeps real
addresses away from the engine), 7e (single presence) and the managed-only rule (one exempt
project). Lanes: G (gateway, most of it), D (store, proto, SDK), I (instances, launcher, images),
A (admin). Cross-repo: the game (`source-sdk-sharp`) gets three changes (§5). The proto and
`Host.Contracts` changes are serialised as usual.

## 0. What the code does today (read this session)

| fact | where |
|---|---|
| The gateway owns the public UDP socket and parses only connectionless handshake packets. | `src/Host.Gateway/Relay/GatewayRelay.cs:120-147` |
| Identity-first answers `getchallenge` itself, reads the SteamID from `C2S_CONNECT`, holds the connect while it calls `Identify`, then replays the client's `getchallenge` to the backend. It is **off by default** until the H0f capture. | `GatewayRelay.cs:180-260`, `GatewayOptions.cs:21` |
| `SourceConnectReader` finds the ticket blob (i16 length L, then SteamID64 and the ticket) but returns only the SteamID. | `Handshake/SourceCodec.cs:75-107` |
| The gateway's own `S2C_CHALLENGE` stamps `GameServerSteamId` (unset, 0) and secure = 0. | `SourceCodec.cs:21,43-56` |
| The client caches its ticket per (dialled ip:port, GS SteamID, secure flag) and mints a new one when a later challenge carries a different GS SteamID. | `docs/net-protocol.md` §2.3 |
| A SourceSharp client is always `-insecure` and refuses a secure server. The mods' `steam_appid` is 243750, and the dedicated args carry `-insecure`. | `../source-sdk-sharp/game/mods.yaml` (defaults) |
| `sv_lan 1` accepts only reserved addresses (RFC1918, loopback). Interpose shows the engine the real address. | `docs/net-protocol.md` §4, §12 |
| Client-connect Steam auth is **in the engine** (closed `engine_srv.so`: the connect path calls the Steam game-server library itself; `sv_lan` decides whether it does). The game DLL can't remove it; it can only set `sv_lan`. | `docs/net-protocol.md` §2.3, §4 |
| `SyncTable` carries `banned_addresses` (admin "ban address"), and **the gateway never enforces them**. | `GatewayControlClient.cs:105`, `AdminAdapters.cs:115-120` |
| `accounts.banned` is set by the admin, but **`Identify` never reads it**, and a ban doesn't kick. | `GatewayRegistry.cs:107-130`, `Host.Admin/AdminActions.cs:140` |
| Single presence today: refuse while the old session is live, take over after 15 s of silence (`SilentAfter`, a constant). It is checked only in the service's `Identify`. | `GatewayRegistry.cs:30-31` |
| The game's `PlayerLeft` reaches the service and is a **no-op** in the registry. | `GatewayRegistry.cs:181` |
| The service already builds `ServerInfo` (name, map, players, rules, …) for the gateway's `A2sResponder`. | `proto/gateway.proto:160`, `A2s/A2sResponder.cs` |
| The managed-only fact rejects `AllowUnsafeBlocks` in every project; native calls go through `NativeLibrary` and marshalled delegates. | `src/Host.Testing/ManagedOnlyRule.cs`, `src/Host.Launcher/Engine.cs` |
| The example SDK (`~/Downloads/sdk/dotnet`, 2103 lines): flat API through `NativeLibrary` (no `DllImport`), `unsafe`/`fixed` in 7 files, `SteamGameServer015`, one pump thread with manual dispatch. It has `AuthPlayerAsync` (`BeginAuthSession`, resolved by `ValidateAuthTicketResponse_t` or `GSClientApprove/Deny`), `EndAuthSessionAsync`, and shared-socket `HandleIncomingPacketAsync` / `EnumerateOutgoingPacketsAsync`. The 64-bit `libsteam_api.so` is in `~/Downloads/sdk/redistributable_bin/linux64/`. | read this session |
| **The 2013 engine can already carry game traffic over Steam networking (FakeIP / Steam Datagram Relay).** Both `engine.so` (243750, client) and `engine_srv.so` (dedicated) contain `-enablefakeip`, `Requesting FakeIP as per -enablefakeip`, `SendMessageToFakeIP to %s returned %d`, `SteamNetworkingSockets012`, and (dedicated, with symbols) `NET_ShouldUseSteamFakeIP`, `NET_CheckCleanupFakeIPConnection`, `CBaseServer::IsUsingFakeIP` and `CSteam3Server::OnFakeIPResult`. So a client can dial a FakeIP address (169.254.x.y) and its netchannel datagrams travel through Steam's relays instead of raw UDP. | `strings`/`objdump -T` this session on the local installs of both apps |
| The Steamworks headers next to the example SDK expose it flat: `SteamAPI_SteamGameServerNetworkingSockets_SteamAPI_v013`, `BeginAsyncRequestFakeIP`, `GetFakeIP`, `CreateFakeUDPPort`, and `ISteamNetworkingFakeUDPPort::{SendMessageToFakeIP, ReceiveMessages, ScheduleCleanup, DestroyFakeUDPPort}`. Also `ISteamNetworkingUtils::GetRealIdentityForFakeIP`, which maps a sender's FakeIP to the Steam identity Steam authenticated. A game server must request FakeIP **before logon**, failures are reported only after minutes of retries, and the FakeIP **replaces the public IP everywhere it would appear, the server browser included**. Datagrams are capped at 4096 B (1200 recommended). The example C# SDK binds none of this. | `~/Downloads/sdk/public/steam/steam_api_flat.h:1009-1177`, `steamnetworkingfakeip.h`, `isteamnetworkingsockets.h:862-886` |

## 1. Rulings (✔ = ruled by the user; overrule any row)

| id | question | ruling |
|---|---|---|
| S-1 | where the Steam game server runs | **In the gateway process**: it owns the public socket, it sees the ticket, and its GS SteamID is what the challenges carry. A ticket can't be replayed to Steam ("already claimed", ✔ 2026-10-04), so recovery after a gateway restart is **client re-auth** (S-20), not keeping the process alive. This replaces revision 2's separate `steam-auth` process with A/B drain (✔ "no longer needed"). |
| S-2 | the SDK code ✔ | Copied to `src/Host.Steam` (namespace `SourceSharp.Host.Steam`), owned here, `src` only (no sample, no binaries). **`AllowUnsafeBlocks` is allowed in this one project.** `ManagedOnlyRule` gets an exemption list that is exactly `{Host.Steam}`. A fact proves that `Host.Sdk`, `Host.Contracts`, `Host.Launcher` and `Host.FakeGame` don't reference it, even transitively. `DllImport`/`LibraryImport` stay refused there too. |
| S-3 | what "validated" means | A connect is forwarded to a backend only after **`ValidateAuthTicketResponse_t` = OK** (or `GSClientApprove`) for that SteamID and those ticket bytes; `BeginAuthSession` returning OK isn't enough. Until then nothing past `getchallenge` reaches the backend, and resends of the held connect are dropped. The single exception is S-11's bounded readmission. |
| S-4 | one Steam identity for the cluster ✔ | The gateway **may rewrite packets**. It rewrites each backend `S2C_CHALLENGE`: the GS SteamID (offset 23) becomes its own, the secure byte (offset 31) becomes **0** (the client is always insecure and refuses a secure server), and `authProtocol` (offset 17) becomes 3 if S0a shows the engine sends another value. The client then sees one server and reuses its ticket across hops. The GS logs on in `Authentication` mode (no VAC). Each rewrite gets a fact that diffs the extracted fields before and after. |
| S-5 | the handshake shape | **Validate-then-forward**, with `IdentityFirst` on or off. (a) Off: the backend's challenge comes back rewritten (S-4); the gateway holds the client's `connect`, validates it, checks bans and presence, calls `Identify`, then forwards that same connect. (b) On: as today, except the first connect is validated before `Identify`, and the second connect is forwarded only if its ticket bytes match the validated ones (otherwise it is validated again). (a) doesn't depend on H0f open question 1. |
| S-6 | Steam on the instances ✔ | **B now**: `sv_lan 1`, and Interpose with the launcher presenting a per-player reserved address (127.x, from the Loopback allocator) instead of the real one. **D later**, if the game needs the engine's own SteamIDs (§1.1). Game-pod NetworkPolicy denies all egress except the service. The game server alone can't do it: client-connect auth is in the closed engine (§0), and the game DLL's only lever is `sv_lan`, which B sets. |
| S-7 | the player's identity inside the game | With Steam off, the engine's SteamID for a client is LAN/invalid (S0a confirms). **The authoritative SteamID is the gateway-validated one**: the relay's `Opened` carries it to the launcher, and `PeerInfo.Resolve(peer)` returns it. The SDK's `PlayerJoined` takes it from `IPeers`, and the service refuses a `PlayerJoined` whose SteamID disagrees with the session's. |
| S-8 | Steam unavailable ✔ | **Reject new connects** with `S2C_CONNREJECT` and a reason the client shows: `"Steam is having issues; try again in a few minutes"` (`Steam.UnavailableReason`). Live sessions continue. "Unavailable" means the GS is not logged on, or `Begin` times out. Neither is a strike. `Gateway.Steam = Off` exists only for the fake tier and dev. |
| S-9 | logon identity ✔ anonymous first | **Anonymous logon** (`LogOnAnonymous`); everything in this plan must work without a token (user, 2026-10-04). Each gateway start gets a new GS SteamID, which is harmless: it is stamped into every challenge (S-4), clients mint a fresh ticket for it on their next connect (which also sidesteps "already claimed", S0e), and live players re-auth in game (S-11). Cost: the server's browser identity and favourites change on each gateway start. `Steam.LoginToken` stays an optional Secret, unset by default, for when a GSLT exists; with one, the GS SteamID becomes stable and nothing else changes. If S0h shows browser listing **requires** a GSLT, S8's browser step waits for one, and auth, bans and the packet bridge go ahead without it. App id: `Steam.AppId`, defaulting to the mod's `steam_appid` (243750); S0d confirms an anonymous GS on it validates a client ticket. |
| S-10 | what persists | A `steam_auth` row per identified session: `session_id` PK, `steamid`, `ticket_sha256`, `gs_steamid`, `epoch` (the gateway logon that validated it), `state` (`valid` \| `reauthing`), `validated_at`. Written in the same transaction as `Identify`'s session upsert, and deleted when the session closes. **Ticket bytes are never stored** (they can't be replayed). |
| S-11 | recovery after a gateway restart (deploy or crash) ✔ "keep playing if seamless" | The restarted gateway logs on (new `epoch`), rebuilds its sessions from `SyncTable`, and marks every carried identified session `reauthing`. **Those players keep playing.** The service sends each instance a `Reauth` for its players (S-20). The client mints a fresh ticket in game, the game server returns it through the SDK, and the gateway `Begin`s it: OK → `valid`; deny → kick (S-12); no ticket within `Steam.ReauthTimeout` (asked `Steam.ReauthAttempts` times) → `Steam.ReauthUnanswered` (`Kick` with `"please reconnect"`, or `Trust` until the player leaves, for game builds without S-20). While Steam is unavailable, `reauthing` sessions wait without a deadline (S-8: live sessions continue). **Reconnect with a claimed ticket**: if a connect's ticket gets "already claimed" and its hash matches a `valid` row for that SteamID from an earlier epoch, it is admitted and immediately `Reauth`ed in game (the one exception to S-3: once per row, within `Steam.ClaimedTrust`). Any other "already claimed" is refused with `"please reconnect"` and no strike. A service restart doesn't touch Steam. |
| S-12 | kick | Triggers: a later non-OK `ValidateAuthTicketResponse` (ticket cancelled, VAC, logged in elsewhere, no license), `GSClientKick`, a failed or timed-out re-auth, and a ban. Steps: (1) the gateway stops forwarding client→backend at once; (2) `AuthRevoked {session, steamid, reason}` → the service → `Kick {steamid, reason}` down the instance's command stream, so the engine frees the slot and the message reaches the client; (3) the gateway closes the session after `Steam.KickGrace` or on `PlayerLeft`, whichever comes first. |
| S-13 | release on leave, as fast as possible | `EndAuthSession` is queued on the pump the moment any of these arrives: (1) the game's `PlayerLeft` → service → `GatewayControl.EndAuth {session_id}`; (2) `AuthTicketCanceled` (the client quit); (3) any gateway `Close` (expiry, admin, kick, presence timeout). It is keyed by **session id**, so a late `PlayerLeft` for an old session can never end a rejoin's auth. A hop that presents the **same ticket bytes** reuses the live auth, with no Steam round trip. |
| S-14 | bans, by address and by SteamID, enforced by the proxy ✔ | One table, `bans` (`kind` address\|steamid, `value`, `until` null = permanent, `source` admin\|auto, `reason`, `created`). A migration carries over the settings JSON key and `accounts.banned` (which stays as a view for the Accounts page). The gateway holds the whole list (from `SyncTable`, then pushed on change with `GatewayControl.SetBans`) and never waits on the service to refuse. **Address** (IPv4 or CIDR): checked at packet entry, before A2S and before a session exists, and dropped silently (no reply, so no amplification). **SteamID**: checked when the connect is read, **before** `Begin` (the ID is unverified, but a refusal refuses only that claimed ID and saves a Steam call), and again on the validated SteamID. The client gets `CONNREJECT "banned"` and the attempt is a strike. `Identify` checks again in its transaction. A new ban kicks matching live sessions (S-12). |
| S-15 | temp bans on repeated failures ✔ per IP and per SteamID | N failures in a window → a ban that doubles on each repeat within a day, up to a cap (S-21 has the defaults: 5 in 60 s → 10 min, capped at 24 h; separate thresholds for IP and SteamID). Failures: a Steam deny, an immediate `BeginAuthSession` error, an auth timeout, an unreadable ticket, a refused banned SteamID, a connect to an unknown or expired challenge, and (SteamID only) a revocation after validation. A SteamID strike from an **unvalidated** connect counts toward that SteamID only if the same IP has also failed, so nobody can get a stranger banned by forging their SteamID. Not counted: a presence refusal, a rate-limited `getchallenge`, "Steam unavailable", a claimed ticket after a restart. The gateway enforces at once and reports `TempBanned`; the service writes it to `bans`, so it survives restarts. NAT/CGNAT risk: conservative defaults, and the admin can lift. |
| S-16 | test seam | `ISteamAuthServer` in `Host.Gateway`: `Status → {logged_on, gs_steamid, epoch}`, `Begin(sessionId, steamid, ticket) → Task<AuthVerdict>`, `End(sessionId)`, event `Revoked(sessionId, reason)`, and the packet pair `HandleIncoming` / `DrainOutgoing` (S-19). Implementations: `SteamworksAuthServer` (over `Host.Steam`); `FakeSteamAuthServer` (`Host.Testing`), which scripts a verdict per ticket, latency and later revocation, and keeps a "Steam backend" of claimed tickets, so after a fake restart a replay is "already claimed" as in reality; and `Off`. `Gateway.Steam = Steamworks \| Fake \| Off`. The fake image uses `Fake`. |
| S-17 | one connection per SteamID ✔ "oldest wins until it times out" | **Enforced by the gateway itself** (an index `steamid → session`), on every path that links a session: validate-then-forward, `SetRoute`, a hop (cut before open, as today), and carried routes after a restart (newest `opened` closed). While a SteamID's session is live, a new connect for it gets `CONNREJECT "already connected; try again in N s"` (N = the time until the old one would time out), and that is **not a strike**. The old session ends by leaving (`PlayerLeft`, ticket cancelled: S-13, immediate) or by timing out: no client packet for `Gateway.PresenceTimeout` (today's `SilentAfter` constant, moved into the gateway as an option). Then the next connect wins. Newest-wins was rejected because it can loop kicks between two clients (✔). |
| S-18 | server browser, from `mods.yaml` ✔ (block shape and first keys ✔) | The **GS advertises** (`SetAdvertiseServerActive`). What it shows comes from the mod: a `browser` block in `mods.yaml` (resolved into `mod.dedicated.yaml` like the rest), reported by the game server's SDK at first connect alongside the rules-module hash (D-H9), and stored per mod-image digest. Static fields: `name`, `description` (product / game description), `tags`, `region`, `max_players`. Templated fields: `map` and `rules` (key → template), filled from the **world-status keys** the host publishes (`{hub_map}`, `{players}`, `{levels_open}`, `{deepest}`, `{players_by_level}`, `{pool_ready}`, …; the list is part of the mod-image contract, `docs/mod-image.md`). The service renders `ServerInfo` from it and pushes it on change (debounced by `Steam.BrowserPushInterval`) to the gateway's `A2sResponder` and the GS (`SetServerName`/`SetMapName`/`SetMaxPlayerCount`/`SetKeyValue`/`SetGameTags`/`SetGameData`, `BUpdateUserData` per player across all instances). With no `browser` block, today's `ServerInfo` is used. |
| S-19 | Steam's game-server UDP goes through the proxy socket ✔ | The GS runs with `QueryPort = SharedQueryPort` and **opens no game-facing UDP socket**. Its game port is reported as the public port. In-process in the gateway: datagrams from the public socket that are meant for Steam go to `HandleIncomingPacket(packet, srcIp, srcPort)`, and `GetNextOutgoingPacket` is drained after every inbound packet and on a tick (`Steam.OutgoingTick`; heartbeats are unprompted), each one `SendTo`'d from the public socket. Meant for Steam means connectionless (`FF FF FF FF`), not the handshake (`q`/`k`), and not A2S unless S0h shows the library must answer A2S itself (`Gateway.Steam.A2s = Gateway \| Library`; `A2sResponder` stays the fallback). These packets pass the same address-ban check and rate limit as everything else. Not covered: steamclient's own connection to Steam's backend (logon, validation), which is direct pod egress (S0g), not game traffic. |
| S-20 | in-game re-auth ✔ "can make game client changes" | A new SDK exchange so the host can ask a connected player for a fresh ticket without a reconnect. **Host → game**: the instance command `Reauth {steamid, gs_steamid, nonce, deadline}`. The SDK raises `IAuth.ReauthRequested`; the game server sends its client a game message. **Client**: `ISteamUser::GetAuthSessionTicket` (cancelling its previous handle), returning the ticket in a game message. **Game → host**: the SDK's `IAuth.SubmitTicket(peer, nonce, ticket)` → the service → `GatewayControl.Reauth {session_id, nonce, ticket}` → `Begin`. The nonce binds the answer to the request, and the SteamID comes from the session, never from the game. Used by S-11; also available for a periodic check later (not in v1). |
| S-21 | every timing and threshold is configurable ✔ (defaults ✔, re-auth unanswered → `Kick`) | Every value below is an option bound from appsettings/environment, as today. The ones an operator tunes live (marked ●) are also on the admin **Config** page, like `Instances.ModImage`: stored in `settings`, pushed to the gateway with `SyncTable` and on change, and applied without a restart. Validation refuses nonsense at load and on edit (`ValidateOnStart`, and the same validator behind the page): positive durations, thresholds ≥ 1, cap ≥ base. Defaults are in the table that follows this one. |
| S-22 | client traffic over Steam networking (FakeIP / relay) ✔ deferred | **Not in this plan** (user, 2026-10-04: skip for now). The engine and the Steamworks API support it (§0), and the design is kept under "Later" for when it is picked up. |

| option | default | used by |
|---|---|---|
| ● `Steam.AuthTimeout` | 10 s | S-3: a `Begin` with no verdict counts as a timeout (a strike, or S-8 if Steam is down) |
| ● `Steam.ReauthTimeout` / `Steam.ReauthAttempts` | 30 s / 2 | S-11 |
| ● `Steam.ReauthUnanswered` | `Kick` | S-11 (`Kick` \| `Trust`) |
| ● `Steam.ClaimedTrust` | 6 h | S-11 readmission window |
| ● `Steam.KickGrace` | 2 s | S-12 |
| ● `Steam.UnavailableReason` | "Steam is having issues; try again in a few minutes" | S-8 |
| ● `Gateway.PresenceTimeout` | 15 s | S-17 |
| ● `Gateway.TempBan.Ip.{Failures, Window, Base, Cap, RepeatWindow}` | 5, 60 s, 10 min, 24 h, 24 h | S-15 |
| ● `Gateway.TempBan.SteamId.{…}` | as IP | S-15 |
| ● `Steam.BrowserPushInterval` | 1 s | S-18 |
| `Steam.OutgoingTick` | 50 ms | S-19 |
| `Gateway.Steam`, `Gateway.Steam.A2s`, `Steam.AppId`, `Steam.LoginToken` | `Steamworks`, `Gateway`, 243750, none (anonymous) | S-16, S-19, S-9 (deploy-time: a change restarts the gateway) |

### 1.1 S-6: the options for an engine without Steam

| | how | engine sees | SteamID inside the engine | work | risk |
|---|---|---|---|---|---|
| A | `sv_lan 1`; relay `Loopback` mode | 127.x per player | LAN/invalid → the game uses `IPeers` | smallest | low; an extra loopback UDP hop |
| **B ✔** | `sv_lan 1`; Interpose presenting a per-player 127.x | as A | as A | the launcher maps session ↔ synthetic address (the Loopback allocator, reused) | low; in-process, no loopback sockets |
| C | `sv_lan 0`, Steam unreachable | the real address | — | none | expected to refuse every client; not pursued |
| D (later) | `sv_lan 0`, Interpose; the launcher (NativeAOT C#) **stands in for `libsteam_api`** and approves exactly the SteamIDs the gateway validated (via `PeerInfo`) | the real address | the **real** SteamID | large: the 2013 engine uses Steam's C++ interfaces (vtables) and callback registration, and each call it makes must be found and answered | high: tied to one engine build, hard to test, and a missed call is a crash in a pod |

Side effects of B (D has none of these):

- **SteamID in the game.** `GetClientSteamID`, `GetPlayerNetworkIDString`, `status` and `banid` return LAN/invalid, so the game takes identity from `IPeers` (S-7, §5). If S0a shows the 2013 engine still reads the SteamID from the ticket prefix under `sv_lan 1`, this goes away.
- **Addresses in the engine.** Logs, `status` and `GetAddress` show 127.x; the real address comes from `PeerInfo`. Engine IP bans don't matter: the gateway enforces every ban.
- **`sv_lan 1` treats clients as LAN**, which can lift rate caps. The pod args pin `sv_maxrate`, `sv_minrate` and `sv_maxupdaterate`, and S0a records the effective values.
- **No Steam features inside instances** (stats, achievements, workshop). Nothing uses them today.

## 2. The connect, end to end (S-5a, the default)

```
client                gateway (+ Steam GS in-process)                         service      instance
getchallenge ───────► address ban? rate? ─ forward ───────────────────────────────────────► engine
             ◄─────── S2C_CHALLENGE, GS id → ours, secure → 0 ◄──────────────────────────── engine
connect(ticket) ────► hold; SteamID ban? presence (S-17)?
                      BeginAuthSession ──► Steam ──► ValidateAuthTicketResponse OK
                      (resends of the connect dropped)
                      Identify{sid, ticket hash, epoch} ─────────────────────► bans, presence,
                                                                              steam_auth row
                      ◄────────────────────────────────────────────────────── allowed, backend
                      forward the held connect ─────────────────────────────────────────────► engine (sv_lan 1, 127.x)
             ◄─────── S2C_CONNECTION …  relay as today
deny / timeout ─────► S2C_CONNREJECT, strike (S-15), End

gateway restart:  sessions carried (reauthing, still relaying) → service: Reauth → instance → client
                  → new ticket → SDK SubmitTicket → service → gateway Begin → valid | kick
```

## 3. Phases (one commit per step; the build stays green and warning-free)

### S0: measure before building (no product code; facts go in `docs/net-protocol.md`)

Each row needs a command, and its numbers are re-checked by a second one. Rows marked
**(client)** need a real Steam client and account: a **user checkpoint**.

| # | question | decides |
|---|---|---|
| S0a | The engine with `sv_lan 1` behind B (or Loopback until B exists): the bytes of its `S2C_CHALLENGE`. Does it accept a connect carrying a Steam ticket, and what SteamID do `status` and the game DLL report? Effective rate caps. | S-4, S-6, S-7, the rate pins |
| S0c | **(client)** With the backend challenge rewritten: does the client send the same ticket on the next connect, and on a hop? | S-4, S-13 |
| S0d | **(client)** Does an anonymous GS on app 243750 log on and validate the mod client's ticket? Is its GS SteamID new on each logon? | S-9 |
| S0e | **(client)** After a disconnect, a reject or a kick: does the next connect carry new ticket bytes or the cached ones? The result code for "already claimed". Same with the GS SteamID changed (anonymous restart). | S-11's readmission |
| S0f | **(client)** Time from client quit to `AuthTicketCanceled`; time from `EndAuthSession` to a rejoin's OK. | S-13, S-17 timings |
| S0g | `libsteam_api.so` (SDK redistributable) plus steamcmd's `steamclient.so` in the gateway image: does `SteamGameServer_Init` succeed and log on in a pod? What egress does it need? | images, NetworkPolicy |
| S0h | **(client)** Shared-socket mode behind the gateway: does the library open any UDP socket of its own (`ss -uap`)? Is the server listed, and does it show our `ServerInfo` with the library fed only non-A2S packets, or must it answer A2S too? Is an anonymous GS listed, or does listing need a GSLT? | S-18, S-19 |
| S0i | **(client)** A ticket from a client's in-game `GetAuthSessionTicket` (not the connect path), validated by our GS while the player is connected: OK? | S-20 |

### S1: `Host.Steam` (lane G)

1. Copy `~/Downloads/sdk/dotnet/src/SteamSdk.GameServer/*` to `src/Host.Steam`, fix the namespace, and adopt `Directory.Build.props`. Add the `ManagedOnlyRule` exemption, with facts that the rule still refuses `AllowUnsafeBlocks` in every other project, refuses `DllImport` in `Host.Steam`, and that the exemption list is exactly `{Host.Steam}`. *Commit.*
2. The no-transitive-reference fact (S-2). *Commit.*
3. `Host.Steam.Tests`: `CallbackDecoder` against hand-built buffers for `ValidateAuthTicketResponse_t`, `GSClientApprove/Deny/Kick_t` and `SteamAPICallCompleted_t`; `SteamId` parsing. Mutation proof: shift one offset and see red. *Commit.*
4. Fix the warnings the copy brings (CS1591 and friends) at the source. *Commit if needed.*

### S2: the seam, the fake, Steamworks, codecs, options (lane G)

1. `ISteamAuthServer`, `AuthVerdict` and `FakeSteamAuthServer` (S-16). Facts for the fake: ordered verdicts, revocation, latency on a fake clock, and "already claimed" for a replay after a restart. *Commit.*
2. `SteamworksAuthServer` over `SteamGameServerHost`: session-id ↔ SteamID bookkeeping on the pump thread; `End` in FIFO order before a following `Begin`; `Revoked` from a non-OK verdict after OK, `GSClientKick` and `AuthTicketCanceled`; a new `epoch` per logon; `Authentication` mode, `SharedQueryPort`. Facts run over an internal native-call seam. *Commit.*
3. Codecs: `TryReadConnect` also returns the ticket span (offset T+8, L-8 bytes); `RewriteChallenge(Span<byte>, gsId, secure, authProtocol)`. `ToyCodec` gains a ticket and a GS id. Facts: offsets against a captured-shape connect; out-of-range L refused; the rewrite changes exactly the fields named (diff extracted values). The "never rewrites" doc comments are updated in the same commit. *Commit.*
4. The S-21 options with their validator. Facts: each default as tabled; each invalid value refused with a message naming the option; a live change (fake clock) takes effect on the next decision without a restart (a presence timeout shortened mid-session frees the SteamID sooner). *Commit.*

### S3: gateway admission (lane G; proto serialised with D)

1. Proto: `RouteTable.bans` (`Ban {kind, value, until_unix, reason}`, replacing `banned_addresses`) and `RouteTable.options` (the ● values); `GatewayControl.SetBans`, `SetOptions`, `EndAuth {session_id}` and `Reauth {session_id, nonce, ticket}`; `IdentifyRequest.ticket_sha256` and `epoch`; `GatewayEvents.AuthRevoked`, `ReauthNeeded {session_id, steamid, gs_steamid, nonce}` and `TempBanned`. Contract versions bumped together. *Commit.*
2. `BanList` (address with CIDR, SteamID, expiry on the relay's clock): address first in `OnClientPacket`, SteamID at connect before `Begin` and after the verdict. Facts: a banned IP's `getchallenge`, `connect` and A2S get zero replies and zero sessions; a banned SteamID gets `CONNREJECT "banned"`, **zero `Begin`s** and a strike; the unbanned controls are answered; expiry; CIDR edges; `SetBans` kicks a matching live session. *Commit.*
3. `StrikeTable` (S-15), per IP and per SteamID. Facts at the defaults and at a changed threshold: one under the threshold doesn't ban and the threshold does; window, doubling and cap; uncounted refusals don't count; a forged SteamID from a clean IP doesn't strike that SteamID; a temp ban is reported once. *Commit.*
4. `PresenceIndex` (S-17). Facts: a property fact over random sequences of connects, hops, `SetRoute`s, closes, silences and restarts asserts that no two sessions for one SteamID are ever both linked; while the old one is live the new connect gets `CONNREJECT` with N seconds and no strike; after `PresenceTimeout` of silence, or after `PlayerLeft`, the next connect is admitted; the old session's auth is `End`ed before the new `Begin`. *Commit.*
5. Validate-then-forward (S-5a/b) behind `Gateway.Steam`. Facts with the fake Steam and `FakeGameHost`: no byte past `getchallenge` reaches the backend before the verdict (on the fake backend's receive log, with non-zero required in the OK arm); a deny gives `CONNREJECT`, a strike and zero backend connects; a timeout behaves like a deny; resends are dropped; an equal second connect makes no second `Begin`, and a different one makes one; the backend challenge is rewritten; Steam unavailable gives the S-8 reason and no strike; `Off` behaves exactly as today (the existing relay facts stay green, unchanged). *Commit.*
6. Release (S-13) and kick (S-12). Facts: `Close` for each reason calls `End`; a late `EndAuth` for an old session leaves the new auth valid; `AuthTicketCanceled` ends and closes; a hop with the same ticket makes no Steam call; a revocation freezes the backend's receive count at once, emits `AuthRevoked`, and closes after `KickGrace`. *Commit.*

### S4: persistence, the service, restart recovery (lane D, then G)

1. Migration: tables `steam_auth` (no ticket column) and `bans`, carrying over the old `BansKey` JSON and `accounts.banned`; store interfaces. Facts on SQLite `:memory:`: both sources carried (counted before and after); deleting a session deletes its row. *Commit.*
2. Service wiring, a commit and a fact each: `Identify` checks bans and writes `steam_auth`; `SessionClosed` deletes the row; `PlayerLeft` (a no-op today) → `EndAuth`, with the fact showing the call reaching the gateway fake (non-zero); `AuthRevoked` → `Kick` + `CloseSession`; `TempBanned` → `bans`; admin ban or unban (address or SteamID) → `bans` → `SetBans`; `SyncTable` carries bans and the ● options, and a Config-page edit → `SetOptions`; the presence timeout moves out of `GatewayRegistry` (one source of truth).
3. Restart re-auth (S-11) end to end on fakes: the gateway restarts with three carried sessions; all keep relaying (their packet counts rise during `reauthing`); `ReauthNeeded` → service → `Reauth` instance command → fake game → fake client → `SubmitTicket` → `Reauth` → `Begin`. The fake Steam approves two and denies one, so two become `valid` and one is kicked. A control session whose client never answers is kicked at the timeout with `ReauthUnanswered = Kick`, and keeps relaying with `Trust`. With Steam unavailable, none is kicked. A differential with a negative control: a session that did not exist before the restart is not carried. *Commit.*
4. Claimed-ticket readmission (S-11). Facts: a claimed ticket matching a `valid` row from an earlier epoch is admitted once and re-authed at once; a second time it is refused; a non-matching claimed ticket is refused with `"please reconnect"` and no strike; past `ClaimedTrust` it is refused. *Commit.*

### S5: Steam off on instances, B (lane I)

1. The launcher in Interpose mode presents a per-session synthetic 127.x address (the Loopback allocator, reused): `recvfrom` writes the synthetic address, `sendto` to it goes down that session's stream, and `PeerInfo.Resolve` maps it back to the real address and the validated SteamID (carried on `Opened`). Facts in `Host.Launcher.Tests` over `ISocketCalls`: the engine side never sees a non-reserved address; a reply to the synthetic address reaches the right stream; two sessions get two addresses; a reconnecting SteamID gets its address back while the instance lives. *Commit.*
2. Pod args: `+sv_lan 1`, rate pins per S0a. NetworkPolicy: game pods egress only to the service (and DNS); the gateway gets egress to Steam (S0g's list). Facts on `GamePodBuilder` and the rendered policy. *Commit.*
3. Engine image: drop `steamclient.so` if the engine starts without it under `sv_lan 1`. *Commit.*

### S6: the SDK (lane D)

1. `PlayerJoined` takes the SteamID from `IPeers.Resolve(peer)`; the service refuses a disagreeing one. *Commit.*
2. `IAuth`: `ReauthRequested` (raised from the `Reauth` command) and `SubmitTicket(peer, nonce, ticket)`. The fake game and `Host.FakeClient` implement the in-game exchange **through the SDK only** (the fake game's own toy message to its client), which is the SDK's proof. *Commit.*
3. The `browser` block: the SDK reports it at first connect with the rules-module hash; the service stores it per mod-image digest and renders `ServerInfo` from it and the world-status keys. Facts: a template with every key renders; an unknown key is refused when reported (not rendered blank); no block falls back to today's `ServerInfo`. *Commit.*
4. SDK and contract minor version bump; `docs/sdk-integration.md` describes the three game-side duties (§5). *Commit.*

### S7: images, admin, docs (lanes G, A)

1. Gateway image: a steamcmd stage for `steamclient.so` (as the engine image does), and `libsteam_api.so` from `make images STEAMWORKS_SDK=<dir>`, a build context that is never committed; a missing directory fails the build with a message. `Steam.LoginToken` comes from an optional Secret, absent by default (anonymous logon, S-9). *Commit.*
2. Admin: a Bans page (kind, value, until, source, reason; add and lift, by address or SteamID; the Accounts page's ban button writes here); the ● options on the Config page with the validator; sessions show auth state, epoch and the ticket hash prefix, and `reauthing` ones are marked; the Dashboard shows the GS logon state, GS SteamID and epoch. bUnit facts. *Commit.*
3. `docs/net-protocol.md` (the rewrite, the S0 results), `docs/mod-image.md` (the `browser` block and world-status keys), `docs/ops.md` (anonymous logon and the optional GSLT, the SDK context, the options, and what a gateway rollout now costs players: a re-auth, not a reconnect). *Commit.*

### S8: Steam's UDP through the proxy, and the server browser (lane G, then D; after S0h)

1. The packet bridge (S-19): the filter, `HandleIncoming`, and the drain after each inbound packet plus the tick. Facts with the fake: a Steam-bound packet reaches it with the sender's real address and port in the SDK's host byte order; an outgoing packet leaves the public socket to exactly the named address; a heartbeat with no inbound packet still goes out within one tick; handshake packets and (with `A2s = Gateway`) A2S never reach it; a banned address's packets never do; nothing goes in while the GS is logged off. A fact over the native-call seam checks the byte order. *Commit.*
2. The GS applies the rendered `ServerInfo` (S-18) and advertises. Facts: a level opening changes the rendered map and rules within one push; players on every instance are counted; logged-off means no advertising. *Commit.*
3. Live (real, a **user checkpoint**): the server appears in the Steam browser with the `mods.yaml` name and the world status, and they change when a level opens.

### S9: live gates

- `make live-s1 TIER=fake` on the local k3s: join; leave then rejoin within 1 s (accepted; `End` before `Begin` in the fake's log); the same SteamID from a second address while the first is live (refused with N s, no strike, then admitted once the first goes silent past the presence timeout); gateway pod delete mid-session (players keep relaying, re-auth through the fake game and fake client, and one scripted deny is kicked); Steam unavailable → the S-8 reject and no strike; 5 bad tickets → temp ban → gateway restart → still banned; a Config-page change to the temp-ban threshold applies without a restart; a banned address → zero replies; a banned SteamID → `CONNREJECT` and zero `Begin`s.
- `TIER=real`, a **user checkpoint** (needs a Steam client and the game changes of §5): join with the engine on `sv_lan 1`; quit the client and rejoin; roll the gateway mid-game (per memory: ask peer sessions first) and stay connected through the re-auth; join the same account from a second machine (refused until the first leaves or times out); ban the account mid-game; the server browser entry.

### Later: a Steam networking ingress (S-22, deferred)

A second client ingress in the gateway beside the public UDP socket. The gateway's GS requests a
FakeIP before logon, and a pump feeds `FakeUDPPort.ReceiveMessages` into the same `OnClientPacket`
path; replies go out with `SendMessageToFakeIP`. Instances don't change. What it changes:

- **Identity:** `GetRealIdentityForFakeIP` gives a Steam-authenticated SteamID at the first packet, so SteamID bans apply before `getchallenge`. A ticket SteamID that differs is rejected.
- **No real IP:** address bans and IP strikes can't apply on that path.
- **The FakeIP replaces the public IP** in the browser.
- **Possible upside:** the public UDP port could close (`Gateway.Ingress = Steam`), hiding the server's IP from DDoS.

It starts with two measurements:

- Does Valve grant our GS a FakeIP for app 243750, and does the mod client connect by FakeIP?
- Does a relay client's flow survive a gateway restart?

### Later: D (a Steam stand-in under the engine)

Not in this plan. It becomes worth it if the game needs the engine's own SteamIDs or real addresses
in the engine. It starts with a measurement: every Steam call the 2013 engine makes on a dedicated
connect, from `ltrace`/symbol hooks on `libsteam_api.so` in a pod.

## 4. Rulings recorded

Ruled 2026-10-04: the gateway may rewrite packets (S-4); `unsafe` in `Host.Steam` (S-2); B now, D later (S-6); reject new connects while Steam is down, with a reason (S-8); seamless re-auth through the game instead of a separate process (S-1, S-11, S-20); bans and temp bans by SteamID as well as by address (S-14, S-15); the oldest connection wins until it times out (S-17); a server browser entry from `mods.yaml` with the world status (S-18); Steam's UDP through the proxy socket (S-19); every timing and threshold configurable, the operational ones live from the Config page (S-21); the Steam networking ingress deferred (S-22); anonymous logon first, a GSLT optional later (S-9); the `browser` block with the world-status keys of S-18 published first (S-18); the S-21 defaults, with an unanswered re-auth kicking (S-21).

No open questions: every row above is ruled. The rest is settled by S0's measurements, with the user checkpoints marked **(client)**.

## 5. What the game (`source-sdk-sharp`) has to do

Handed over, not done from this repo:

1. Take a player's SteamID from `IPeers` (the SDK), never from the engine, since `sv_lan 1` makes the engine's LAN/invalid (S-7).
2. Carry `Reauth` to the client and the ticket back: a server→client message with the GS SteamID and nonce; the client calls `ISteamUser::GetAuthSessionTicket` (cancelling its previous handle) and answers; the server calls `IAuth.SubmitTicket` (S-20).
3. Add a `browser` block to each hosted mod in `mods.yaml`, and have `source# -resolve` write it into `mod.dedicated.yaml` (S-18).
