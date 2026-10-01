# Integrating Host.Sdk into the game (source-sdk-sharp)

What the Descent server side must do so the hosting service can run it. Until the game calls
`Booting` and `MapReady`, a real game pod boots the engine and is then killed by the service's
boot timeout (120 s, `Instances.BootTimeout`). That is the first milestone; the rest makes
players, characters and items real.

**The working reference is `src/Host.FakeGame/FakeGameServer.cs`** in this repo: a game server
written only against `Host.Sdk` that passes every live gate (h4, h7, the one-hour h9 soak).
When this guide and the fake disagree, the fake is right — copy its shape.

Every API below is in `src/Host.Sdk` (namespace `SourceSharp.Host.Sdk`); every call returns a
`HostResult` / `HostResult<T>`: check `r.Ok`, then use `r.Value`, else `r.Refusal` (`Error`,
`Reason`, `Detail`). Reading `r.Value` on a refusal throws, so always branch on `Ok` first.

---

## 1. Reference the packages

```
cd source-sdk-sharp-host && make pack        # → bin/nupkg/SourceSharp.Host.Sdk.*.nupkg, SourceSharp.Host.Contracts.*.nupkg
```

Add `bin/nupkg` as a local NuGet source in the sharp repo, then:

| assembly in the game | references |
|---|---|
| the Descent **server** assembly (the code that runs per frame on the dedicated server) | `SourceSharp.Host.Sdk` |
| the Descent **rules** assembly (`Descent.Rules`, the `IGameRules` implementation) | `SourceSharp.Host.Contracts` only |

Rules that are checked or will bite:

- Managed only, as the game already is (TF2 plan R15): the SDK has no native code.
- The rules assembly must not reference the SDK, the server assembly, or any engine
  assembly: the service loads it alone, in its own process, to replay rolls.
- `Host.Contracts` is never shipped as part of the module; the host's copy is the one that
  binds. Version today: `HostContract.Version = "1.0.0"`; a mismatch is refused at Booting
  with `HostError.ContractVersion`.

## 2. Mark the rules module

In the rules assembly:

```csharp
[assembly: SourceSharp.Host.Contracts.HostRulesModule(typeof(DescentRules))]

public sealed class DescentRules : IGameRules { … }   // src/Host.Contracts/IGameRules.cs
```

At `Booting` the SDK finds the loaded assembly carrying that attribute
(`LoadedAssemblyModuleSource`), announces its SHA-256 plus the hashes of the DLLs it
references that sit beside it, and uploads them only if the service has never seen that
hash. So: **the rules assembly must already be loaded in the process when `Booting` runs**
(touch a type from it first), and its dependencies must be next to it on disk.

The service replays item rolls with this module, so it must be deterministic: same inputs,
same output, no clock, no `Random` without the given seed, no I/O.

## 3. Start, pump, stop

```csharp
HostSdk sdk = await HostSdk.StartAsync();     // once, at server load; reads args + env (§8)
…
sdk.Pump();                                   // once per server frame, on the main thread
…
await sdk.DisposeAsync();                     // at shutdown
```

**The threading rule — the one that matters most.** Every `Task` the SDK returns completes
*inside `Pump()`*, on the thread that calls it, and every command event (§6) is raised there
too. So:

- Call SDK methods and `await` them on the main thread. The continuation runs during a later
  `Pump()`, i.e. on a later frame, back on the main thread — safe to touch engine state.
- **Never block on an SDK task** (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`) on the
  main thread: the completion can only be delivered by the `Pump()` you are blocking. Deadlock.
- If the main thread stops calling `Pump()` for 15 s (`MainThreadStallLimit`), the SDK stops
  heartbeating on purpose and the service treats the instance as hung (suspect, then reaped).
  A long map load *before the first* `Pump()` is fine; after that, keep pumping during loads.

`HostSdk.StartAsync` throws `HostConfigException` only at start, with the missing setting
named (§8). It never throws from a game call.

## 4. Boot: Booting, then MapReady

```csharp
var boot = await sdk.Session.Booting(mapName);          // as soon as the map name is known
if (!boot.Ok) { /* see below */ }
BootInfo info = boot.Value;   // InstanceKind "hub" | "level", Depth, LevelHash, InstanceSeed, Module (Known/Send/Pending)

// … the map loads, the server socket is bound …

var ready = await sdk.Session.MapReady(boundPort, mapName);   // the port the socket REPORTS
```

- `mapName` is the `+map` argument the pod passed.
- `boundPort` must be what the socket actually bound, not the `-port` that was asked for:
  an engine that silently walks to a higher port is refused with `HostError.PortMismatch`
  (that has cost a day before).
- Send `MapReady` only when a client could connect. The instance goes `Live` on it and the
  gateway starts routing players.
- `BootInfo.InstanceSeed` seeds this instance's kill rolls; `Depth` / `LevelHash` say which
  level this is (the map file is already in `maps/pool/`).

On a refusal: retry `HostError.Unreachable` (the host restarted, or a new pod's network is
not admitted yet — about 2 s on k3s) with a short delay; **log every other refusal loudly
and exit non-zero.** The fake gives up silently on other errors, and a silent stall is
indistinguishable from a hang: it cost two investigations here.

## 5. Players

On a client fully connecting (the engine's "client active" point):

```csharp
var j = await sdk.Session.PlayerJoined(peer, steamId);   // peer = the address string the engine has for this client, unchanged
if (!j.Ok) { kick(j.Refusal!.Reason); return; }
if (!j.Value.Allowed) { kick(j.Value.Reason); return; }      // e.g. single_presence

var list = await sdk.Characters.List(steamId);
HostCharacter ch = list.Value.FirstOrDefault()
                   ?? (isHub ? (await sdk.Characters.Create(steamId, className, name)).Value : null);  // only the hub creates
var lease = (await sdk.Characters.Lease(ch.Id)).Value;
lease.LeaseLost += (l, why, detail) => { /* stop writing for this player; kick or freeze them */ };

if (!isHub)
{
    reserve = sdk.Items.OpenReserve(lease);                         // the kill-drop cache (§7)
    if (info.Depth > ch.ReachedDepth) await lease.SetReachedDepth(info.Depth);
}
sdk.Session.Players = playerCount;                                  // reported on every heartbeat
```

On disconnect, and before a hop (§6):

```csharp
await lease.Checkpoint(sheetPayload);   // the character sheet, opaque bytes + schema version
await lease.Release();                  // the final call; the lease refuses use after this
await sdk.Session.PlayerLeft(steamId, peer);
```

- The lease is the fencing token: every item and character write carries it. After
  `LeaseLost` every call for that player is refused locally (`HostError.LeaseLost`) — stop the
  player from acting rather than letting their actions silently fail.
- Checkpoint on a timer as well (every minute or so) and at notable moments (level-up,
  quest): a crash loses only what came after the last checkpoint.
- `sdk.Peers.Resolve(peer)` returns who is behind an address (real client address, session,
  SteamID) for logs and bans.

## 6. Commands from the service

Raised inside `Pump()` (`sdk.Commands.*`):

| event | the game does |
|---|---|
| `PrepareHop(h)` | the player is moving to `h.TargetInstance`: Checkpoint, Release, then `await h.Ready()`. **Never call `Ready()` before the release** — the target can't lease the character until this instance lets go. |
| `Retry(r)` | tell that client to reconnect (the engine's `retry` on the client). Its route now points at the new instance; it lands there. Treat the player as left (§5 leave, without a second Release). |
| `Kick(k)` | kick `k.SteamId` with `k.Reason` |
| `Drain(d)` | accept no new players; tell current ones the server is closing. The service shuts the instance down when it is empty. |
| `Shutdown(s)` | checkpoint and release everyone still here, `DisposeAsync` the SDK, exit 0 |
| `Say(s)` | print `s.Text` to all players |
| `Exec(e)` | run `e.Command` as a server console command; `await e.Reply(output)` |

Order of a hop, for reference: travel call (§7) → service picks/boots the target → gateway
route flips → `PrepareHop` here → checkpoint + release + `Ready()` → `Retry` here → the client
reconnects and `PlayerJoined` fires on the target.

## 7. Items, kills, travel

- **Kill drop (the hot path, no I/O):** roll with your rules (`KillRoll(info.InstanceSeed,
  killSeq, robot, depth, partySize)`); if it drops, `reserve.NextForTier(tier)` gives an
  item the service already minted. Spawn it, then `await reserve.Reveal(item, killSeq, robot)`.
  If the tier is empty, fall back to `sdk.Items.MintDrops(lease, killSeq, robot, tier)`.
  Bosses and champions: `MintDrops(..., tier: -1)`. `killSeq` increments per kill on this
  instance.
- **Pickup:** `Items.Claim(lease, itemId)`. **Drop:** `Items.Drop`. Inventory moves, stash,
  vendors, crafting: the other `IItems` methods, all with the lease.
- **Death:** `Items.RecordDeath(lease, position)` — everything carried becomes the corpse's in
  that one call. A corpse left behind goes to Lost & Found when the level ends;
  `LostAndFound.List` / `Reclaim` (the fee is charged in Australium).
- **Travel:** from the hub `Travel.RequestDescent(lease, partyId, depth)`; on a level
  `Travel.StairsDown(...)`, `TownPortal(lease)`, `ReturnThroughPortal`, `ReturnToCorpse`.
  `Started` means a `PrepareHop` follows; `Waiting` means a level is being prepared (show a
  message, try again on `Eta`); `Refused` carries the reason.
- **Parties and trades:** `sdk.Parties` (create / invite / accept), `sdk.Trades` (offer, lock,
  confirm).

## 8. What the pod gives the process

The SDK reads these itself; the game only needs `HostSdk.StartAsync()`.

| input | value |
|---|---|
| args | `-game <modDir> -console -port 27015 -ip 0.0.0.0 +maxplayers N +map <map> +descent_instance <id>` |
| `DESCENT_SERVICE` | the game API (gRPC h2c) |
| `DESCENT_INSTANCE_ID`, `DESCENT_INSTANCE_TOKEN` | who this instance is (the token comes from a per-pod Secret) |
| `DESCENT_SIDECAR_INFO` | the launcher's peer-info endpoint on loopback (default `127.0.0.1:5011`) |
| `DESCENT_MOD` | the mod name (`descent`) |

The mod image layout (where the server assembly and the SDK DLLs go) is `docs/mod-image.md`.

## 9. Testing without a cluster

| mode | how |
|---|---|
| a real local service | `make local` in this repo, then start the game with `-hostlocal bin/local/local-host.json` (or `DESCENT_LOCAL=<file>`) |
| the service inside the game process (dev builds only) | ship `SourceSharp.Host.Local` beside the game in a dev build and start with `-hostlocal inproc` (data in `./descent-local/`). A release build omits that DLL; the SDK then says so instead of starting. |

Both connect exactly as a pod does, so everything above runs unchanged.

## 10. Done when

1. **Boots:** with the real tier deployed (`make deploy TIER=real MOD_IMAGE=…`), the hub's
   audit shows `instance.boot_reported` → `instance.map_ready` → `instance.live`, and the pod
   stays up past 120 s.
2. **Joins:** a Steam client connecting to the LoadBalancer address appears on the hub, with
   a lease (`/ops/leases`) and a session bound to its SteamID (`/ops/sessions`).
3. **Hops:** a descent moves the player to a level pod and back to town, with the character
   leased on the instance they stand on after each hop and the inventory unchanged.
4. **Survives a restart:** `kubectl rollout restart statefulset/descent-service` while
   playing; the instance is adopted (`instance.adopted`), the player stays connected.

Then the `TIER=real` live gates and the packet captures (`docs/net-protocol.md`) can run.
Note the current gates drive players through the fake's control port; the real tier needs a
small driver of its own (bots or console commands) to automate 2–4.
