# plan_host.md — the hosting service for Descent on SourceSharp: gateway, instances on Kubernetes, item ledger, map pool, admin

Companion to `plan_tf2_rpg.md` (the game plan, "TF2 plan" below; to be carried into
`source-sdk-sharp/docs/plans/`).
This repository, `source-sdk-sharp-host`, is the standalone home of everything that runs
**beside** the game: the UDP gateway, the manager that launches dedicated servers as
Kubernetes pods, the character / item / progress database, the map generator with its
pre-generated pool, the gRPC API game servers talk to, and the admin UI.
Written 2026-09-30. Untracked until reviewed (the sharp repo's convention for plans).

**The game being hosted is `source-sdk-sharp`** (`../source-sdk-sharp`, tip `f9f9e0f`):
the standalone SourceSharp repo, which builds against the Valve fork `../source-sdk-2013`
and compiles maps with `../source-sdk-map-tools` (`ssmap`). Facts of that repo this plan
rests on, each read this session:

| fact | where |
|---|---|
| the game directory is `bin/<config>/`: `source#` (NativeAOT launcher), `mods.yaml`, `bin/{linux64,managed,dotnet}`, `mods/<id>/` per mod | `README.md` "The game directory" |
| a mod is a `mods.yaml` entry: `game_dir`, `content`, `steam_appid`, `engine {appid, exe, restart_exit_code, fd_limit}`, `assemblies {server[], client[]}`, `args`, `env`; `source#` picks it with `-mod <id>` | `game/mods.yaml`, `src/mods/ModsConfig.cs` |
| `source#` finds the engine **through the Steam client API** (`SteamAPI_ISteamApps_GetAppInstallDir`) | `src/launcher/Native.cs:54-63`, `Launcher.cs:62` |
| **there is no dedicated-server target.** `make headless` is a *client* run without dialogs (`./source# … -novid -windowed -nohltv +tv_enable 0 -assertlog +map <map>`), and `source#` always starts `hl2_linux64`. The SourceSharp flags that matter to a server (`-devapi -devapisocket -condebug -nomessagebox -nocrashdialog -assertlog -nohltv +tv_enable 0`) are in `RUN_ARGS` / `HEADLESS_ARGS`; the windowing ones are not for a server. A dedicated run is this plan's to define: `srcds_linux64 -game <mod> -console -port <p> -ip <ip> +maxplayers <n> +map <map>` plus those flags (§7.1, §14.3) | `Makefile:122-127, 349-354`, `game/mods.yaml` (`engine.exe: hl2_linux64`) |
| the engine install (app 243750) ships `srcds_linux64` beside `hl2_linux64` | `~/.steam/steam/steamapps/common/Source SDK Base 2013 Multiplayer/` |
| `make map` runs `ssmap` from `../source-sdk-map-tools` by `dotnet run --project` (`MAPTOOLS_DIR`, `SSMAP`) | `Makefile:378-383` |
| the devapi web UI is React 19 + esbuild + vitest, embedded in `SourceSharp.DevApi` | `src/webui/package.json` |
| plans live in `docs/plans/` and stay uncommitted until reviewed | `CLAUDE.md` |

**The rooms feature already exists** in `source-sdk-map-tools` (tip `83bbf57`,
`origin https://github.com/muflub/hl2sdk_tools_sharp.git`), and it is what §9 builds on:
one **library VMF** with `info_room` markers (name, cell, door kit, sockets, `room_role`),
`ssmap room <library.vmf>` → `<library>.roompack` with an incremental SQLite store,
`level.yaml` (`library:` / `libraries:`, `rows`, `columns`, `grid` of `room@rotation`),
`ssmap link <level.yaml>` → `<map>.bsp` + `<map>.nav3d` + `<map>.map2d`, transition rooms
(`room_role` up / down, `spawn` / `arrival` / `exit_up` / `exit_down` markers),
`ssmap layout` (grid generator, `-sequence K`), and the in-process API
`RoomLibraryCompiler.CompileAsync`, `RoomCompiler.CompileAsync(VmfDocument, RoomDefinition,
VbspContext)`, `LevelLinker.LinkAsync(LevelLayout, RoomLibrary, VbspContext, LevelLinkOptions)`
→ `LinkedLevel`, `LevelYaml`, `LevelGenerator`. The mod-side contract is
`SourceSharp.RoomContracts` (`logic_room`, `cxry_` names, `LevelMap`, `LevelTransition`).
**Room compile needs game content; link does not** (`docs/rooms-full-features.md` D1).
Where the TF2 plan cites `plan_maptools.md` §10b, read this repo instead.

Nothing in this plan changes a ruling of the TF2 plan. One rule the TF2 plan does not
have is added here and is the spine of §5:

> **R-H1 — the service mints every item.** No item exists that the service did not
> create. Drops, vendor stock, crate openings, crafts and admin gifts are all minted by
> the service, tracked in one ledger with exactly one owner at a time, and handed to the
> game server by id. An unclaimed item is swept when the level that holds it shuts down.
> The game server keeps a cache, never the truth. (Ruled 2026-09-30.)

---

## 0. Rulings and defaults — overrule any default by editing this table

`Q` rows are the questions asked 2026-09-30; a row marked **ruled** is your answer, the
rest are defaults I took.

| # | question | ruling / default |
|---|---|---|
| Q1 | repo boundary | **ruled: standalone repo with a `make setup` that uses a side-by-side checkout or clones.** One build-time root: `MAPTOOLS_ROOT` (`../source-sdk-map-tools`), consumed by `ProjectReference` through `$(MapToolsRoot)` written to `roots.props` (§3 H1a). Nothing of the sharp repo is referenced at build time any more: the game's built files arrive as a Docker image (D-H6) and the rules module the host needs arrives from the game server itself at first connect (D-H9). `SOURCE_SHARP_ROOT` survives only as the dev convenience `make mod-image-dev` reads (§14.1) |
| Q2 | game-agnostic or Descent-specific | **ruled: split, but never at the RPG's expense.** `Host.*` projects know nothing about heroes or loot; `Descent.Service` holds every game rule. When keeping a thing generic would cost the RPG an abstraction, a second interface or a round-trip, it goes in `Descent.Service`. No plugin system; one game |
| Q3 | scope of the "progress DB" | **ruled: yes** — all of TF2 plan 3c, because it all moves items through the one ledger of §5 |
| Q4 | gateway unmeasured | **ruled: measure first.** Phase H0; `redirect` (TF2 plan 0f) stays behind `ITravelStrategy` until then |
| — | **ruled** | items minted and tracked by the service, swept at level shutdown; game-server API over gRPC (R-H1) |
| — | **ruled** | MapForge uses `ssmap` with the rooms feature (§9) |
| — | **ruled** | admin UI is localhost only (Q20) |
| — | **ruled** | deployment is Docker images (Q23) |
| — | **ruled** | **game instances launch in a Kubernetes cluster** (§7): `KubernetesInstanceHost` is the production `IInstanceHost`; one pod per instance, no Deployment, `restartPolicy: Never`. `LocalProcessInstanceHost` remains for development and unit tests only |
| — | **ruled** | the game to host is `source-sdk-sharp`, not the old codex checkout. It is not a build root of this repo (Q1, D-H9); `SOURCE_SHARP_ROOT` names its checkout for dev conveniences only |
| — | **ruled** | **`ssmap` is used as a library, never as a process.** Bake, link, lint, flatten, the oracle compile and the diff all call `SourceSharp.MapTools` in-process (§9). No image carries the `ssmap` CLI |
| — | **ruled** | **room packs are managed at run time by the admin**: a `.roompack` can be uploaded per mod and library, every upload is a kept version, and the admin controls which version each depth links from — activate, pin, roll back — with a policy for the levels already linked from the old one (§9.4). The bake Job is just another producer of versions |
| — | **ruled** | **the host ships a C# SDK for the game** (`Host.Sdk`, §6.6): interfaces that carry all communication and client-side logic to the host — the stream, leases and fencing tokens, idempotency, retries, the item reserve cache, command dispatch onto the game's main thread. The game's server assembly (the mod) is the host's client and uses nothing but the SDK; the player's game client never talks to the host (R11). The fake game server is built on the SDK, which is how the SDK is proven complete |
| — | **ruled** | **a fake game server in C#** (`Host.FakeGame`, §7.6) stands in for the engine in the unit tier and in the default live tier, so the full lifecycle, ledger and travel paths are tested without booting a game server; the engine tier runs the same scripts against the real image |
| Q5 | service ↔ gateway control | **ruled: one gRPC stream for health checks and pings, everything else unary.** Both processes host gRPC: the gateway calls the service for session events, the service calls the gateway for routes and snapshots; `Health` is the only stream, and its loss is what triggers the full route-table resync (§8.2). The game-server API follows the same shape as far as a game process allows (§6.1) |
| Q6 | multi-host, and how a server learns a player's real address | is now the normal case: every backend is a pod `IP:port`. No per-session source address exists (there is no `127.x.y.z` in a pod); `MAX_REUSE_PER_IP` is handled by handshake admission (§8.1). **Asked: can a player id be seeded into the forwarded packets?** Not in the payload — the engine is Valve's closed binary and parses each datagram itself, so a prefixed header is rejected before any managed code runs. The id rides in the **source port** instead: every session has its own backend-facing socket, so the `gateway_ip:port` a server sees for a client is unique to that session; the server reads it with `INetChannelInfo::GetAddress` (available to the game DLL) and resolves it with one unary call, `ResolvePeer` (§6.1, §8.1). **Asked: can one player be pinned to one local IP, so the same SteamID is always the same address?** Yes: the peer is **pinned per SteamID** and persisted — as a stable source **port** everywhere (`PortPerPlayer`, the default), and as a stable **IP** where the CNI can give the gateway pod a pool of addresses (`AddressPerPlayer`, Calico multi-IP or Multus ipvlan; §8.1a). The pin can only apply once the SteamID is known, which today is after the hub's `PlayerJoined` — from the next hop on — unless the identity-first handshake of §8.1b holds: the gateway answers the first challenge itself, reads the SteamID from `connect`, sets the pin up, and re-challenges the client with the backend's challenge (H0f decides whether the 2013 client accepts that) |
| Q7 | server browser | gateway answers `A2S_INFO` / `A2S_PLAYER` for the hub; no master-server heartbeat |
| Q8 | one town or many hubs | **one hub** in v1; `Hubs.Count` in config, same code path for N |
| Q9 | sizes and caps | hub `maxplayers 32`; level `maxplayers 4`; `Instances.MaxLevelPods` starts at 16; pod requests / limits in config (start: 1 CPU, 1.5 GiB level; 2 CPU, 3 GiB hub) — measured in H0 |
| Q10 | empty-level grace | 5 min empty (Town Portal return, D9), 15 min with a corpse (D6), then drain → reap |
| Q11 | warm spares | none in v1; `Instances.WarmSpares` exists and starts at 0 |
| Q12 | instance crash | sessions fall back to the hub route; leases release; corpses go to Lost & Found at reap; the party may re-enter the depth on a fresh map; a level pod is never restarted, the hub pod is |
| Q13 | data access | EF Core + SQLite (WAL) behind the store interfaces of §4; the ORM is not the swap seam, the interface is |
| Q14 | backups | `VACUUM INTO` hourly, keep 24 hourly + 7 daily, on the service's volume; admin triggers and downloads |
| Q15 | where MapForge lives | `Descent.MapForge` is built here: library manifest + lint, level-file emitter, bake and link drivers, packager |
| Q16 | compiler | `ssmap` rooms, in-process through `SourceSharp.MapTools`; **bake** (`RoomLibraryCompiler`, needs content) runs as a Kubernetes Job from the **service image** with the content volume mounted, as the `Descent.Service bake` subcommand; **link** (`LevelLinker`, needs no content) runs inside the service pod. No Wine, no monolithic path in production; the oracle (`LevelFlattener` → the tools' full compile → the tools' diff) is the admin's on-demand check |
| Q17 | pool key | `(pack version, depth, difficulty)` where the pack version is the one **assigned to that depth** (§9.4), K = 3 per depth, 15 depths → 45 ready levels |
| Q18 | map delivery | **ruled: two endpoints, and only the bz2 is public.** The client endpoint is `/maps/<mod name>-<level>-<hash>.bsp.bz2` on a **fastdl listener** (`:5004`, the only HTTP the cluster exposes, via an Ingress or LoadBalancer) that serves nothing else; a level's map name is `<mod>-<depth>-<hash>` (`descent-7-<hash>`; the town map keeps its own name). The `.bsp`, `.nav3d` and `.map2d` go to game pods' **init container** from an **internal** endpoint, `/internal/maps/descent-<depth>-<hash>.{bsp,nav3d,map2d}` on `:5000`, reachable inside the namespace only (NetworkPolicy, never published). `.map2d` is the automap and is revealed to clients room by room by the server (TF2 plan 2m); `.nav3d` is the robots' navigation. Neither is ever downloadable, and a fact asserts the fastdl listener returns 404 for any extension but `.bsp.bz2`. Retired levels: keep the newest 10 per depth |
| Q19 | admin UI tech | Blazor Server in the service process at `/admin` (C# and ASP.NET as asked). The instance console page links to that pod's devapi web UI through a service-side proxy (§11) rather than re-implementing it |
| Q20 | admin auth | **ruled: localhost only.** The admin endpoint binds `127.0.0.1` inside the service pod and refuses any other address; reached with `kubectl port-forward` (wrapped as `make admin`). No login; audit actor `admin@localhost` |
| Q21 | admin edits on leased data | refused while leased; "kick and edit" releases the lease through the gateway first; every admin mutation is an `audit_log` row |
| Q22 | admin actions on instances | pod log tail; console via the gRPC `Exec` command with `sv_cheats` shown; link to the pod's devapi UI |
| Q23 | deployment | **ruled: Docker** — three images built here (`service`, `gateway`, `engine`) by `make images`, plus the **mod image** the sharp repo publishes, named by runtime config (D-H6); **run on Kubernetes** (ruled above) from manifests under `deploy/k8s/` (kustomize; a local `k3d` cluster for development and the live gates) |
| Q24 | public or LAN | LAN / friends: plain gRPC inside the cluster, gateway rate-limits handshakes per source IP, nothing else. TLS, NetworkPolicies beyond the basic ones, and ticket validation are §12 items |
| Q25 | metrics | Serilog + prometheus-net `/metrics` on every process; a `ServiceMonitor` is optional |
| Q26 | live gates | unit and in-process facts in CI, including end-to-end facts with the fake game server hosted in the test process; live gates are scripts under `live/` against a `k3d` cluster in two tiers, `TIER=fake` (default, the fake image as the game image) and `TIER=real` (the engine image) |
| D-H1 | drop latency (from R-H1) | **ruled: a pool of pre-generated items per player, rolled around the character's current level.** When a level instance leases a character, the service mints a **reserve** for that character on that instance: N items already rolled through `Descent.Rules` for `(character level, depth's ilvl and loot table)`, stratified by rarity tier, owned `Reserve`. A kill costs no round trip: the server's kill roll (drop? which tier?) is a deterministic rules function of `(instance seed, kill_seq)`, it takes the next reserve item of that tier from its cache, spawns it at once, and reports `Reveal` afterwards; the service replays the kill roll and moves the item `Reserve → World`. Refill in the background below a low-water mark; re-roll on level-up (§5.2a). Bosses and champions (D13, rare by design) keep the synchronous `MintDrops`. Unrevealed reserve items are swept with the level. Cluster RTT is still measured in H0e, now to size the reserve rather than to gate the design |
| D-H2 | who rolls | the service, through the mod's rules module loaded per D-H9; the server never rolls an item |
| D-H3 | Australium and metal | Australium is a per-character **currency ledger**; metals are items with `count` |
| D-H4 | vendor stock | rolled hourly per hub into `Vendor`-owned items; unsold swept at the next roll and at hub reap; buy-back keeps the last 12 per character per vendor |
| D-H5 | per-instance token | each pod gets its own token in its environment; nothing else can register (R11's shared secret becomes one token per pod the service created) |
| D-H6 | engine, mod and content in the cluster | **ruled: the mod files are provided as a Docker image, and its registry and version are runtime config** (`Instances.ModImage = <registry>/<name>:<tag>`, editable on the admin Config page; built by the sharp repo with `bin/{linux64,managed,dotnet}`, `mods.yaml`, `mods/descent/` under `/game`). Because it can change without a rebuild here, it is **not baked into any image**: a game pod's init container runs the mod image and copies `/game` into an `emptyDir`, and the game container runs this repo's **engine image** (Steam app 244310, *Source SDK Base 2013 Dedicated Server*, anonymous `steamcmd`, the libraries `srcds_linux64` needs, the entrypoint) with that volume mounted (§7.1). The service records the digest each instance actually ran. A change of `ModImage` applies to new instances; the hub is drained and re-created so it picks it up. `srcds_linux64`, never `source#`, which needs a Steam client (§14.3). TF2 content is a read-only volume filled once by a `steamcmd` Job from app 232250 (*TF2 Dedicated Server*), never in an image built here, never in a repo (TF2 plan §16) |
| D-H7 | layout generation | the mod's rules module (`Descent.Rules`, via `IGameRules`) generates the RPG layout (critical path, branches, treasure, boss arena — TF2 plan 6b) and MapForge writes it as a `level.yaml`; `ssmap layout` is used only to exercise the pipeline before 6b exists (§9.1) |
| D-H8 | Descent's home | **ruled: Descent will live in `source-sdk-sharp`** once work starts on it (the mod as a second `mods.yaml` entry, `Descent.Rules/Server/Client/Ai` as modules). This repo never references it: it defines the contract the rules module implements and loads the module at run time (D-H9) |
| D-H9 | how the host gets the mod's rules module | **ruled: the module the host needs from the mod comes out of the mod's own build, at run time — and (asked "extract it from the docker image, or have the SDK send it on first connect?") the SDK sends it on first connect.** **Hash first, bytes on request:** the SDK announces only the module's identity (assembly name, version, content hash, contract version) in `Booting`; the host answers `known` or `send`, and only on `send` does the SDK stream the bytes — content-addressed and deduplicated, so only the first pod of a build uploads and every later pod sends a hash and nothing more. The host loads it in a collectible `AssemblyLoadContext` per hash and uses, for every instance, the module *that instance's* pod runs — replay parity by construction. Cold start works because the hub pod is created at boot and is the first to connect; the pool waits for a module and says so. Registry extraction was the alternative: stronger trust root, but a registry client in the service, credentials, and no guarantee the pulled image is the one a pod ran. Its trust gap is closed with an admin page of module hashes and quarantine (§1.2, §11) |
| D-H10 | how the gateway reaches a game pod, and how the server learns a player's real address | **ruled 2026-09-30: a gRPC sidecar in every game pod.** The gateway keeps the public UDP socket; toward a pod it opens **one gRPC stream per client session** to the pod's sidecar (`Host.Sidecar`, `proto/relay.proto` `PeerRelay.Relay`), which turns the stream into a UDP flow on loopback to the engine from a **unique 127.x.y.z per client**, unique **within that game instance** only (ruled 2026-09-30: no cross-pod stability needed): the sidecar allocates from its own pool, a SteamID reconnecting to the same instance gets its previous address back while the instance lives, and a released address is quarantined 60 s before another player gets it. The game server asks the sidecar over loopback gRPC (`PeerInfo.Resolve`) for a peer's real address, session and SteamID — no file; the SDK's `IPeers` does this. `ResolvePeer` at the service stays for history (disconnected peers, admin, old log lines). Supersedes §8.1's per-session backend UDP sockets, §8.1's per-backend handshake admission (each client has its own source IP at the engine, so `MAX_REUSE_PER_IP` no longer bites; only per-source-IP rate limiting stays) and §8.1a's allocators in the gateway (the pin is the sidecar's per-instance loopback address choice). A route flip is still a hard cut: the stream to A ends before the stream to B opens, with the client's packets held until B's stream is open. Game pods accept the sidecar's gRPC port from the gateway only; the engine's UDP never leaves the pod. **Amended 2026-09-30 (with D-H11): the engine sees each client's real ip:port.** Game pods run the relay in **Interpose** mode (`Instances.RelayMode`, default): the launcher hooks the engine's `recvfrom`/`sendto`, so relayed datagrams arrive from, and replies go to, the client's real address — bans, logs and Steam auth need no lookup, and a public server keeps `sv_lan 0`. The per-instance loopback address remains only as the **Loopback** mode of the fake tier, whose managed sockets cannot be hooked |
| D-H11 | the dedicated launcher, and where the relay runs | **ruled 2026-09-30: our own launcher, with the sidecar merged into it.** Anonymous app 244310 ships the 64-bit engine libraries but no `srcds_linux64` and not the 64-bit `libsteam_api.so` / `filesystem_stdio.so` it needs (docs/ops.md). `Host.Launcher` is a **NativeAOT C#** executable in this repo: it loads `bin/linux64/libtier0_srv.so`, `libvstdlib_srv.so`, `dedicated_srv.so` with `NativeLibrary` and calls `DedicatedMain(argc, argv)` on the main thread through a function pointer (no `DllImport`, `extern` or `AllowUnsafeBlocks`; NativeAOT brings no second CoreCLR, so the game's `libmanaged_host` runtime is the only CLR in the process), and serves the D-H10 relay (`PeerRelay`, `PeerInfo`, health) on background threads in the same process — so a game pod is **one container**. The engine image is 244310 (64-bit depots) plus the missing libraries taken from the local Steam install or the sharp repo's build (`make image-engine ENGINE_FILES=…`), plus `steamclient.so` from steamcmd, plus the launcher. A pod's clean shutdown is the service's `Shutdown` command down the stream; the launcher does not let SIGTERM tear the engine down under it |
| D-H12 | are a player's levels the same when they return to a depth? | **ruled 2026-09-30: a character's level seeds are sticky and persistent for its session.** Each character has a persisted `depth → level` claim (`character_levels`): returning to depth N in the same session gives the same level (same hash, same seed, same map). A party entering a depth uses the leader's claim for it; without one a level is handed out as before (§9.3) and claimed for every member. The session ends when the character has held no lease anywhere for `Travel.SessionIdle` (30 min): its claims are dropped, and a level is retired only when no claim and no running instance refer to it — which amends §9.3's "reap → retired" and the pool's GC (a claimed level's files are never deleted). A disconnect, a hop, a Town Portal round trip and a service restart all keep the session |
| D-H13 | the SDK's local mode for rapid game testing | **ruled 2026-09-30: the real service, run locally.** `make local` runs `Descent.Service` as one local process in a Local profile: SQLite file under `bin/local/`, no Kubernetes, no gateway (clients connect straight to the game), `Instances.Host = Local`. At start it registers a hub instance `local-hub` with a fresh dev token and writes `bin/local/local-host.json` (service address, instance id, token). The game, started by hand (the sharp repo's run targets) with the SDK's local flag (`-hostlocal <file>` or `DESCENT_LOCAL=<file>`), reads that file and connects exactly as a pod would — same SDK code path, same ledger, rolls and replays, so nothing drifts. Level travel in local mode is served by the same game process (the hop's target is the requesting instance and the SDK tells the game to change map), unless `LocalProcessInstanceHost` is configured to start separate processes (each with its own TMPDIR, devapi dir and port). **Amended the same day: the host can also run inside the game process.** A dev-only assembly `SourceSharp.Host.Local` (references `Descent.Service`) exposes `LocalHost.StartAsync(dataDir)`; the SDK with `-hostlocal inproc` loads it by name at run time (no compile reference: `Host.Sdk` stays R15-clean), starts the real service on loopback and connects to it over gRPC as to a pod. The game's dev runtime must then carry the ASP.NET Core shared framework and SQLite's native library (the sharp repo's dev build); `make local` remains the out-of-process variant |

---

## 1. The shape of the thing

```
 game clients ──UDP──▶ LoadBalancer :27015/udp (externalTrafficPolicy: Local → real source IP)
                                    │
                        ┌───────────▼──────────────┐   gRPC GatewayControl   ┌───────────────────────────────────────────┐
                        │ gateway (Deployment, 1)  │◀────────────────────────│ service (StatefulSet, 1; PVC: db, maps)    │
                        │ session ↔ backend pod IP │                         │ gRPC :5001 game API   gRPC :5002 gateway  │
                        │                          │                         │ HTTP :5004 fastdl (public: *.bsp.bz2 only)│
                        │ A2S for the hub          │                         │ HTTP :5000 /admin(lo) /internal /metrics  │
                        └───┬──────────────┬───────┘                         │ workers: MapPool, Reaper, LeaseExpiry,    │
                            │ relayed UDP  │                                 │          VendorRoll, Backup, Reconcile     │
               ┌────────────▼───┐   ┌──────▼─────────┐  gRPC (pod token)     │ IInstanceHost = Kubernetes API            │
               │ hub pod        │   │ level pod × N  │──────────────────────▶│ (create / watch / delete pods, logs, Jobs) │
               │ srcds+server.so│   │ init: fetch map│                       └───────────────────┬───────────────────────┘
               │ descent_town   │   │ descent-<d>-<h>│                                           │ creates
               └────────────────┘   └────────────────┘                        ┌──────────────────▼──────────────────┐
                     both mount the TF2 content PV (read-only)                │ bake Job (service image + content PV)│
                                                                              │ RoomLibraryCompiler → .roompack → svc│
                                                                              └─────────────────────────────────────┘
```

Three images, two long-lived workloads, N short-lived pods. The **service** holds all
state and every worker and is a singleton (SQLite on its own volume). The **gateway**
holds only a route table it can rebuild from the service. **Game pods** are pets with one
life each: created for an instance, deleted at reap, never restarted in place.

### 1.1 Projects

| project | kind | knows the game? | holds |
|---|---|---|---|
| `Host.Abstractions` | lib | no | `IInstanceHost`, `IMapCompiler`, `IMapPool`, `IGameRules`, store interfaces, `TimeProvider`, shared DTO records |
| `Host.Proto` | lib | no | `.proto` files + `Grpc.Tools` output: game-server API (§6), gateway control (§8.2) |
| `Host.Sdk` | lib (NuGet `SourceSharp.Host.Sdk`) | no | the SDK the game uses (§6.6): `IHostSession`, `ICharacters`, `IItems` with the reserve cache, `IParties`, `ITrades`, `ITravel`, `IPeers`, `IHostCommands`; references `Host.Proto` and `Grpc.Net.Client` only — no engine, no `unsafe`, no native (R15) |
| `Host.Sdk.Tests` | xUnit | — | the SDK against the in-process service: every interface method, every failure path, the main-thread pump |
| `Host.Gateway` | exe | no | UDP relay, session table, handshake admission, A2S responder, gRPC client to the service |
| `Host.Instances` | lib | no | `KubernetesInstanceHost` (over the `KubernetesClient` package: pods, watches, logs, Jobs), `LocalProcessInstanceHost` (dev), the lifecycle state machine |
| `Host.MapPool` | lib | no | pool worker, job queue and priorities, level storage, the bake-Job driver, HTTP file endpoints, GC |
| `Host.Data` | lib | no | EF Core `HostDbContext`, migrations, SQLite stores, idempotency, audit, backup |
| `Host.Admin` | Razor class lib | no — binds to `IAdminActions`, whose game parts `Descent.Service` provides | Blazor Server pages (§11) |
| `Descent.MapForge` | lib | yes | library manifest + lint, `level.yaml` emitter, bake driver, link driver over `SourceSharp.MapTools`, packager. References `SourceSharp.MapTools`, `MapFormats`, `RoomContracts` from `$(MapToolsRoot)` |
| `Host.Contracts` | lib (NuGet `SourceSharp.Host.Contracts`) | no | `IGameRules` and its records (§1.2): the one assembly both the host and the mod's rules module reference; versioned on its own |
| `Host.Modules` | lib | no | receives, verifies and stores rules modules (content-addressed), loads each in a collectible `AssemblyLoadContext`, resolves `IGameRules` per instance digest, quarantine (§1.2) |
| `Descent.Service` | exe | yes | composition root; gRPC services; ledger policies; travel orchestration; the `bake` subcommand the bake Job runs. It references **no** Descent assembly: the rules arrive at run time (D-H9) |
| `Host.FakeGame` | exe + lib | no | the fake game server (§7.6), **written on `Host.Sdk` and nothing else** of the API: a toy UDP handshake the gateway can relay, a control API the tests drive; hostable in-process |
| `Host.FakeClient` | lib | no | a UDP client for the toy handshake: connects through the gateway, follows `retry`, reports which backend it landed on |
| `Host.Tests`, `Host.Gateway.Tests`, `Host.Instances.Tests`, `Host.MapPool.Tests`, `Descent.Service.Tests` | xUnit | — | `[Fact]`s, one behaviour each; fakes for the Kubernetes API (an in-memory pod store), backend UDP, compiler, rules, clock |
| `live/` | scripts | — | live gates against `k3d`, `TIER=fake` by default, `TIER=real` for the engine |

`Directory.Build.props` mirrors the sharp repo's: `net10.0`, nullable, `LangVersion
latest`, deterministic, CS0108 / CS0114 as errors and nothing else, intermediates under
`bin/obj/`. It imports `roots.props` (git-ignored, written by `make setup`) with
`$(MapToolsRoot)`; a missing file fails the build with "run `make setup`".

### 1.2 The `IGameRules` seam — the mod's rules module, loaded at run time (D-H9)

The service must roll, replay and validate with the same code as the game. That code
is the mod's rules assembly (`Descent.Rules`, TF2 plan §1), built in the sharp repo and
shipped in the mod image. The host never compiles against it. Instead:

- **`Host.Contracts`** (a NuGet package the sharp repo references) holds `IGameRules`
  and its records, nothing else, with its own semver. The mod's rules module implements
  it; a `[HostRulesModule(typeof(DescentRules))]` assembly attribute names the entry
  type.

```csharp
public interface IGameRules
{
    RolledItem RollItem(ItemRollContext ctx, ulong seed);
    bool Replays(RolledItem item);
    KillRoll KillRoll(ulong instanceSeed, uint killSeq, string robotTemplate, int depth, int partySize);
    int BackpackSlots(CharacterSheet s); int StashSlots(IReadOnlyList<CharacterSheet> living);
    ValidationResult ValidateCheckpoint(CharacterSheet before, CharacterSheet after, CheckpointEvidence e);
    TradeResult ApplyTrade(TradeState t);
    LevelPlan GenerateLayout(LayoutKey key);     // rooms, cells, rotations, roles, markers (D-H7)
    LevelRow LevelTable(int depth);
    ISheetCodec Sheets { get; } IItemCodec Items { get; }   // the opaque payloads of §6
}
```

- **Announce, then upload once.** The SDK's `Booting` carries `RulesModule {assembly,
  version, sha256, contract_version, deps[]}`. The host answers `known` or `send`; on
  `send` the SDK streams the assembly and its private dependencies (never the
  framework, never `Host.Contracts` — the host's own copy is the one that must match).
  Content-addressed under `modules/<sha256>/` on the `data` volume, verified against
  the declared hash and the declared `contract_version` (refused if the host does not
  support it, before anything else happens), recorded in `rules_modules`.
- **One load context per hash.** `Host.Modules` loads each module in a collectible
  `AssemblyLoadContext` and hands out `IGameRules` **per instance**: every instance row
  carries `rules_sha256`, and every replay, reserve mint, checkpoint validation and
  trade for that instance uses that module. Two pods on different builds coexist, each
  replayed by its own code. The pool and the vendor roll use the module of the
  **current mod image** (`Instances.ModImage`), learned from the last pod that booted
  from it; a context is unloaded when no live instance, no `ready` level and no
  assignment refers to its hash.
- **Cold start.** The pool worker and the vendor roll wait until a module is known
  and show "waiting for the hub's rules module" on the Dashboard; the hub pod is
  created at service boot and is the first to connect, so the wait is the hub's boot.
- **Trust.** Only an authenticated instance (its per-pod token) may upload; the upload
  must match the hash it announced; the admin Modules page lists every hash with the
  instances that run it and the levels linked by it, and can **quarantine** a hash
  (instances on it are drained, uploads of it refused). In `Development` new hashes
  are accepted silently; in production `Modules.RequireApproval` can make a new hash
  wait for the admin before its pods go `live`. The module runs in the service process
  with the service's rights: it is the mod's own code, and the same trust already
  applies to the pod it came from.
- **Tests** use `FakeGameRules : IGameRules` in-process; `Host.Modules.Tests` load a
  fixture module built from a tiny test project against `Host.Contracts`, including a
  wrong-contract-version and a wrong-hash fixture that are refused.

Gate facts: announce → `known` skips the upload; a second pod on the same hash uploads
nothing; a mismatched hash and a wrong contract version are refused and audited; two
instances on different hashes replay with different modules (a fixture pair whose
`KillRoll` differs proves which one ran); unload when unreferenced; quarantine drains.

---

## 2. Phase H0 — measure before building (no product code)

Each row lands in §2.1 with the command that produced it and a second that confirms it.

- **H0a The engine in a container.** Build the engine image (§3 H1d), pull a mod image, and run
  `srcds_linux64 -game mods/descent -console -port 27015 +map ss_sandbox` with no Steam
  client and no display. Does `server.so` + `libmanaged_host.so` boot, does Bootstrap
  load the mod's assemblies without `source#` (§14.3 is the fix if not), does devapi
  answer? Record boot → `map_ready`, RSS idle, RSS with four bots.
- **H0b The gateway hop in the cluster.** TF2 plan 0h, run on `k3d`: two game pods, a
  throwaway relay pod, a client outside the cluster joins A through the LoadBalancer,
  the route flips, A issues `retry`, the client lands on B at the same address. Record
  added RTT; whether six simultaneous clients from the gateway's single pod IP trip
  `MAX_REUSE_PER_IP` (5, hard-coded in the engine) on the hub, and whether
  admitting five handshakes at a time clears it; whether the LoadBalancer preserves the
  client source address (`externalTrafficPolicy: Local`), without which routing by source
  address is impossible. Gate: hop works. If it fails: D25 struck, `RedirectTravel` with
  a NodePort per instance (§14.5).
- **H0c Steam auth from a pod.** Memory `steam-ticket-fails-with-many-instances`: with
  `sv_lan 0` do four pods at once refuse clients, and what does a client relayed by the
  gateway look like to Steam auth? Decides `sv_lan` / `-insecure` for v1 (R11 accepts the
  spoofing risk).
- **H0d Bake and link budget.** `RoomLibraryCompiler.CompileAsync` on the
  `rooms-features` sample library inside the service image (content mounted), then
  `LevelLinker.LinkAsync` of a 20-room `level.yaml` in the same image: bake time per
  room, link wall time and CPU, output sizes. Sets K, link concurrency and the bake Job's
  resources.
- **H0e Service ↔ pod RTT.** A gRPC round trip pod → service on `k3d` and on the target
  cluster, p50 / p99. With D-H1's reserve it no longer gates a drop; it sizes the
  reserve's low-water mark (a refill must land before the reserve empties at the
  measured kill rate of TF2 plan 5d's spawn director) and bounds the synchronous boss
  `MintDrops`.

- **H0f The game's UDP protocol and handshake — what can be influenced.** Before the
  gateway is designed further, write down the engine's connection path from public
  documentation and observation (the shipped 2013 engine is closed), each
  claim then checked against a **packet capture** of a real client connecting to a real
  `srcds_linux64` directly and through the H0b relay (`tcpdump` on both sides). The
  note, `docs/net-protocol.md`, answers, with a citation and a capture per row:
  - the handshake as it is: `A2S_GETCHALLENGE` → `S2C_CHALLENGE` → `C2S_CONNECT` (with
    protocol version, challenge, name, password, Steam ticket) → `S2C_CONNECTION` →
    netchannel `SIGNONSTATE`s; where a NAT rebind or a changed source address breaks it;
  - **every field the client controls** in the connectionless packets (name, password,
    the ticket, convars sent at connect) — the candidates for carrying a session token
    end to end without the relay touching the payload, and which of them the managed
    server can read (`IVEngineServer::GetClientConVarValue`, `INetChannelInfo`);
  - **every place the engine uses the peer address**: challenge derivation
    (`GetChallengeNr`), `MAX_REUSE_PER_IP`, `sv_max_connects_sec`, the ban list, the
    `redirect` / `retry` client commands (`FCVAR_SERVER_CAN_EXECUTE`), A2S, the
    `sv_lan` / Steam auth path — each with what a relay's stable per-session peer does
    to it;
  - the netchannel's framing: sequence numbers, reliable / unreliable streams, split
    packets and MTU (a relay adds no bytes, but confirms `net_maxfragments` behaviour),
    any encryption or signing of the payload (which would rule out ever touching it),
    keepalive and timeout (`cl_timeout`, `sv_timeout`) — what the gateway's session
    expiry must be shorter or longer than;
  - what a server may send that moves a client: `retry`, `redirect`, `connect` as
    server-executable commands, and their exact behaviour across a map download;
  - two questions §8.1a depends on: is the **SteamID readable in the `C2S_CONNECT`
    packet** (the Steam auth ticket's header) before the server has answered, so the
    gateway can choose a per-player peer for the very first packet; and is the engine's
    `netadr_t` **IPv4-only**, which rules out an IPv6 prefix as the cheap way to one
    address per player;
  - the **lever table**: each thing the gateway or the managed server *can* influence
    (source port as session id, a pinned port or address per player, handshake
    queueing, challenge behaviour, A2S answers, a client-carried token, `retry` vs
    `redirect`), each thing it *cannot* (payload,
    challenge derivation inside the engine, the reuse limit), and the one chosen for
    each need of §8 with the alternative noted.
  Gate: `docs/net-protocol.md` exists with every row cited and captured; §8 is
  re-read against its lever table before H5 starts, and any §8 statement the note
  contradicts is corrected there, not here.

Exit: §2.1 filled; this file only.

### 2.1 Measured (to fill)

| fact | value | command |
|---|---|---|
| srcds boot → `map_ready` in a pod | **4.9 s** container start → `Server is hibernating` on `dm_lockdown`, SourceSharp test mod, NativeAOT launcher (4.887 s, 4.870 s; b50f9c7, image `h0a-b50f9c7`, podman on this box) | `h0a-measure.sh <image> <game dir>` twice (scratchpad; polls `podman logs` every 0.5 s) |
| pod RSS idle / four bots | **216.5 MB** idle 30 s after map load (216,508 / 216,548 kB); four bots: not measured | same runs, `VmRSS` of pid 1 |
| relay added RTT through the LoadBalancer | not yet through the LoadBalancer. **Local (loopback, podman): +0.1 ms p50** — challenge round trip 10.05 / 10.04 ms via gateway → gRPC relay → engine vs 9.94 / 9.94 ms direct (100 samples each, two runs; p99 within 0.2 ms; the ~10 ms floor is the engine answering once per frame); 758308d | `rtt.py 127.0.0.1 <port> 100` ×2 each (scratchpad), gateway `HandshakesPerSecond` raised for the run |
| `MAX_REUSE_PER_IP` tripped at 6 / cleared by 5-at-a-time | — | — |
| source IP preserved through the LoadBalancer | — | — |
| bake per room / link 20 rooms | — | — |
| gRPC RTT pod → service p50 / p99 | — | — |
| handshake packets, direct vs relayed (count, bytes, sequence) | identical at the client: `S2C_CHALLENGE` 39 bytes (magic 0x5A4F4933, auth 3, key length 0, client challenge echoed) and `S2C_CONNECTION` 20 bytes, both paths; one each per handshake; seen at the client, not a tcpdump capture | `h0b_client.py 27215` (direct) and `27115` (gateway), scratchpad |
| client-controlled connect fields readable by the managed server | — | — |
| payload signed / encrypted | — | — |
| `cl_timeout` / `sv_timeout` vs gateway session expiry | — | — |
| SteamID readable in `C2S_CONNECT` before the server answers | — | — |
| client re-sends `connect` on a second `S2C_CHALLENGE` (identity-first handshake, §8.1b) | — | — |
| challenge carried in `S2C_CONNECTION` / netchannel header | — | — |
| engine address type IPv4-only | — | — |

---

## 3. Phase H1 — skeleton, contracts, images, cluster

- **H1a Solution and Makefile.** `Host.slnx`, projects of §1.1, `Directory.Build.props`,
  `global.json` pinned to the sharp repo's SDK (10.0.112 today), `.editorconfig`. The
  `Makefile` is the front door; `setup` is what Q1 ruled:

  ```
  MAPTOOLS_ROOT ?= ../source-sdk-map-tools      MAPTOOLS_REPO ?= https://github.com/muflub/hl2sdk_tools_sharp.git
  SOURCE_SHARP_ROOT ?= ../source-sdk-sharp        (dev only: `make mod-image-dev` builds a mod image from its bin/release/; not a build root)
  setup:   MAPTOOLS_ROOT exists → use it; else git clone $(MAPTOOLS_REPO) $(MAPTOOLS_ROOT); write roots.props (absolute path);
           dotnet restore; print the root's commit; refuse if it is dirty (the sharp repo's own rule)
  build test run gateway fake | images image-service image-engine image-gateway image-fake | cluster-up cluster-down deploy undeploy admin logs
  lint-rooms bake link (the service's subcommands, in-process map tools) | live-h0 … live-h9 [TIER=fake|real] | clean
  ```

  This repo never builds the game and no Makefile variable names the mod image: it is
  runtime config (D-H6). `make cluster-up` seeds `Instances.ModImage` for the `k3d`
  overlay from `MOD_IMAGE` on the command line, and `make deploy` never touches it.
- **H1b Proto.** `proto/game.proto` (§6) and `proto/gateway.proto` (§8.2). Every mutating
  request carries `request_id`; character writes carry `lease_token`; sheets and items
  travel as `bytes` + `schema_version` so `Host.Proto` never learns a stat name.
- **H1c Config.** `HostOptions` from `appsettings.json` + `DESCENT_*` env, `ValidateOnStart`:
  namespace, the engine / fake image names and tags, `Instances.ModImage` (registry,
  name, tag, optional digest), pod resources, listen addresses (admin must be loopback),
  caps and timers of §0, pool K per depth, library path, content volume claim.
  Runtime-editable values (`ModImage`, K, timers, caps) persist to `settings` and
  override the file.
- **H1d Images.** `deploy/Dockerfile.service` (multi-stage publish of `Descent.Service`;
  runtime image; no game files), `deploy/Dockerfile.gateway`, and `deploy/Dockerfile.engine`:
  `steamcmd +login anonymous +app_update 244310` (the dedicated engine), the 32/64-bit
  libraries `srcds_linux64` needs, `deploy/game-entrypoint.sh` and `fetch-level.sh` — and
  **no mod files**. The mod image is the sharp repo's; what it must contain is a one-page
  contract in `docs/mod-image.md`: the game dir at `/game` (`bin/linux64`, `bin/managed`,
  `bin/dotnet`, `mods.yaml`, `mods/<mod>/`), a shell and `cp` so an init container can
  copy it out (a `busybox` base is enough), and a label `org.sourcesharp.mod=<mod>`. A
  fact in `Host.Instances.Tests` checks the contract's paths against a mod image tarball
  listing, and the service refuses to create a pod from a mod image whose label does not
  match `Mod.Name`. (When the cluster is on a Kubernetes with the `image` volume source,
  the copy step becomes a mount; the contract does not change.) `deploy/Dockerfile.fake`
  publishes `Host.FakeGame` on the runtime image and is what `TIER=fake` deploys as the
  engine image, with the mod init container skipped. TF2 content is not in any image (D-H6).
  The service image carries `SourceSharp.MapTools` as an assembly, and no image carries
  the `ssmap` executable.
- **H1e Cluster manifests.** `deploy/k8s/base/`: namespace; `service` StatefulSet (1
  replica, PVC `data` for `host.db` + backups, PVC `maps` for packs and levels) with a
  ClusterIP Service for :5000 / :5001 / :5002 and a separate Service for the fastdl
  listener :5004 published through an Ingress (or a second LoadBalancer) — the one HTTP
  port the outside can reach; `gateway` Deployment with a LoadBalancer
  Service `27015/udp`, `externalTrafficPolicy: Local`, and a ClusterIP Service for its
  control port :5003 reachable from the service only (an optional overlay
  `deploy/k8s/calico-peer-pool/` adds the dedicated IP pool and the pod annotation for
  `AddressPerPlayer`, §8.1a); RBAC: a ServiceAccount for the
  service with `pods` (create / get / list / watch / delete), `pods/log`, `batch/jobs`
  in its namespace and nothing cluster-wide; the `content` PVC (RWX, read-only to game
  pods) and the one-shot `content-fill` Job (`steamcmd` app 232250); NetworkPolicy: game
  pods accept UDP from the gateway only, talk gRPC and `/internal` HTTP to the service
  only; `:5000` accepts connections from the namespace only. `deploy/k8s/k3d/`
  overlay: local registry, `hostPath` content, small resources. `make cluster-up` = `k3d
  cluster create` + registry + `kubectl apply -k`.
- **H1f Service host.** Kestrel: HTTP admin on loopback, gRPC game API, gRPC gateway
  control; Serilog; prometheus-net; `/healthz` reporting DB, Kubernetes API reachability,
  pool, gateway link.

Gate: CI green on an empty test set (CI runs `make setup … && make test`); `make images`
produces three images; `make cluster-up MOD_IMAGE=… deploy` on this box brings service and gateway to
Ready, `make admin` port-forwards and `/admin` answers on `127.0.0.1:5000` and on nothing
else (checked with `ss -ltnp` in the pod and a connect from the pod IP, twice);
`make undeploy` deletes every pod within the grace period.

---

## 4. Phase H2 — data: stores, ledger, lease, audit

### 4.1 Schema (EF Core migrations, SQLite WAL, one file `host.db` on the `data` PVC)

| table | key columns | notes |
|---|---|---|
| `accounts` | `steamid` PK, `created`, `banned`, `note` | R11 |
| `characters` | `id`, `account`, `class`, `name`, `hardcore`, `fallen`, `level`, `xp`, `reached_depth`, `difficulty`, `sheet_json`, `sheet_version`, `australium`, `version`, `deleted` | sheet opaque to `Host.*` |
| `leases` | `character_id` PK, `instance_id`, `token`, `issued`, `expires`, `heartbeat` | §4.3 |
| `items` | `id` (ULID), `seed`, `base_type`, `rarity`, `ilvl`, `count`, `identified`, `instance_json`, `for_character`, `owner_kind`, `owner_id`, `slot`, `instance_id`, `minted_by`, `minted_at`, `version`, `state`, `admin_edited` | the ledger (§5); indexes on `(owner_kind, owner_id)`, `(instance_id, state)`, `base_type`, `rarity` |
| `item_events` | `id`, `item_id`, `at`, `kind`, `from_owner`, `to_owner`, `instance_id`, `request_id`, `actor` | append-only history |
| `australium_ledger` | `id`, `character_id`, `delta`, `reason`, `ref`, `at` | D-H3; `characters.australium` is the cached sum |
| `stash_slots` | `account`, `hardcore`, `slot` PK, `item_id` | D4 |
| `lost_and_found` | `id`, `character_id`, `item_id`, `fee`, `since`, `origin_instance` | R5 / D6 |
| `parties`, `party_members` | `id`, `leader`, `created`; `(party_id, character_id)` | max 4 |
| `trades` | `id`, `hub_instance`, `a`, `b`, `state`, `offer_json`, `locked_a`, `locked_b`, `committed` | 2d two-phase; escrow owner |
| `rules_modules` | `sha256` PK, `assembly`, `version`, `contract_version`, `deps_json`, `first_seen`, `first_instance`, `state` (pending / approved / quarantined), `bytes` | D-H9; files under `modules/<sha256>/` |
| `instances` | `id`, `kind`, `state`, `depth`, `party_id`, `level_hash`, `pod_name`, `pod_uid`, `pod_ip`, `port`, `token_hash`, `mod_image`, `mod_image_digest`, `rules_sha256`, `booted`, `live_at`, `drain_at`, `reaped_at`, `exit_code`, `reason` | §7; `pod_uid` guards a re-created name; the mod image digest is what the pod actually ran (D-H6) |
| `sessions` | `id`, `client_addr`, `steamid`, `instance_id`, `state`, `opened`, `last_seen`, `identified_at` | the gateway registry |
| `players` | `steamid` PK, `pinned_peer` (ip:port), `allocator`, `since`, `last_seen`, `previous_json` | the per-player pin of §8.1a; a row per SteamID the gateway has ever identified |
| `packs` | `id` PK, `mod`, `library` (tileset key), `version` (monotonic per mod+library), `pack_id` (the tools' own id, from the file), `library_version` (content hash), `source` (upload / bake), `uploaded_by`, `uploaded_at`, `bytes`, `sha256`, `lint_json`, `notes`, `state` (validating / ready / rejected / retired) | every room pack ever accepted; files on the `maps` volume as `packs/<mod>/<library>/<version>.roompack` |
| `pack_assignments` | `mod`, `depth` PK, `pack_id`, `assigned_by`, `assigned_at`, `old_levels_policy` (drain / retire) | which version each depth links from (§9.4); absent → the library's newest `ready` version |
| `levels` | `hash` PK, `pack_id`, `depth`, `tileset`, `seed`, `difficulty`, `state` (generating / ready / handed_out / retired / failed), `created`, `handed_to`, `bytes`, `error` | the pool |
| `map_jobs` | `id`, `kind` (bake / link), `key`, `priority`, `state`, `attempts`, `started`, `finished`, `log` | persisted queue |
| `vendor_stock` | `hub_instance`, `vendor`, `rolled_at`, `item_id` | D-H4 |
| `idempotency` | `request_id` PK, `instance_id`, `response_bytes`, `at` | 24 h |
| `audit_log` | `id`, `at`, `actor`, `action`, `target`, `before_json`, `after_json` | every admin mutation, sweep, forced lease |
| `settings` | `key`, `value_json`, `updated_by`, `updated_at` | runtime overrides |

### 4.2 Store interfaces (`Host.Abstractions`)

`ICharacterStore`, `IItemLedger`, `IStashStore`, `ILeaseStore`, `IPartyStore`,
`ITradeStore`, `IInstanceStore`, `ISessionStore`, `ILibraryStore`, `ILevelStore`,
`IAuditLog`, `IIdempotencyStore`, `ISettingsStore`. Async, cancellable, no EF types
exposed. The unit tier runs the real implementations on SQLite `:memory:`. Writes go
through one `Channel`-fed writer (Microsoft.Data.Sqlite's async is synchronous
underneath), so a burst of `Reveal`s from many pods never contends on the file lock.

### 4.3 The lease (TF2 plan 3b, made concrete)

- `Lease(character, instance)` inserts the row; a second `Lease` by another instance →
  `ALREADY_LEASED {instance}`; by the same instance → the same token (re-entrant).
- Token = 32 random bytes; every character write carries it; mismatch →
  `FAILED_PRECONDITION stale_lease`, audited.
- Heartbeat every 5 s on the instance stream renews that instance's leases;
  `expires = now + 30 s`; `LeaseExpiry` releases expired rows every 5 s, audited.
- `Release` on hop or logout after the final `Checkpoint`.
- Gateway-driven release: `SessionClosed` for an identified session releases at once.
- Admin force-release exists, audited; it is what "kick and edit" uses.

Gate facts: double-lease refused; re-entrant returns the same token; stale token refused
after re-lease; expiry with a fake clock; release on session close; crash recovery — a
checkpoint before a simulated pod death survives, an unclaimed mint is swept, not duplicated.

### 4.4 Idempotency and audit

Every mutating RPC: hit on `request_id` → replay stored bytes; miss → one transaction
that also inserts the row; 24 h retention. Gate: a duplicated `Craft` pays and mints
once; a duplicated `Claim` returns the same success; after retention it is a fresh call.

---

## 5. The item ledger (R-H1)

### 5.1 Owners and states

`owner_kind` ∈ `Reserve | World | Corpse | Vendor | Character | Stash | TradeEscrow |
LostAndFound | Admin`; `state` ∈ `Live | Swept | Destroyed | Consumed`. Exactly one owner at a time; a
move is one `UPDATE … WHERE id = @id AND version = @v AND owner_kind = @from` that must
affect one row, else `FAILED_PRECONDITION` and no event. Terminal rows are kept 30 days.

### 5.2 Mint sources, one RPC each

| source | RPC | owner at mint |
|---|---|---|
| robot kill (the normal path, D-H1) | `TakeReserve(character, lease, instance, tiers[] → counts)`: the service mints a reserve for the character on this instance, rolled for `(character level, depth)`, returns `[(item, tier)]`; the server caches them. On a kill, `Reveal(item, kill_seq, robot_template, nearby_members)`: the service replays the kill roll from `(instance seed, kill_seq)`, checks the item's tier matches, moves it `Reserve → World` with `for_character`, and books the shared Australium (R4). A `Reveal` the roll does not reproduce is refused: the server removes the entity and the item is `Destroyed (forged_reveal)`, audited | `Reserve` at mint (`instance_id`, `for_character`, `rolled_for_level` set); `World` at reveal |
| boss / champion kill | `MintDrops(instance, killer, robot_template, depth, nearby_members, kill_seq)`: synchronous, per-player rolls from the boss's own table (D13) | `World` |
| vendor stock | `VendorRoll` worker hourly per hub; `GetVendorStock` to display | `Vendor` |
| buy / sell / buy-back | `Buy`, `Sell`, `BuyBack` — Australium delta + owner change in one transaction | — |
| crate | `OpenCrate`: consume crate, mint result unidentified (D23) | `Character` |
| craft / re-roll / upgrade / combine | `Craft(recipe, inputs)`: consume inputs, mint output (D24) | `Character` |
| identify / repair | `Identify`, `Repair`: mutate `instance_json`, pay; not a mint | — |
| admin | `GiveItem` from the admin UI, audited | `Character` / `Stash` |
| floor drop | `Drop` → `World` | `World` |
| death | `RecordDeath(character, lease, instance, position)`: everything carried → `Corpse`; Australium held on the corpse | `Corpse` |
| corpse loot | `LootCorpse`: as many as fit → `Character`; the rest stay | — |

`Claim(item, character, lease, instance)` is the pickup: requires `World`, matching
`instance_id`, `for_character` null or equal, and capacity. The server spawns a world
entity only for ids the service returned, and removes it on a successful `Claim`.

### 5.2a The reserve (D-H1)

- **Size and shape.** `Reserve.Size` per tier from config (start: 12 Stock–Vintage,
  6 Strange, 2 Unusual, 0 Australium — uniques are boss-only), rolled at
  `ilvl = LevelTable(depth).ilvl` around `character.level`, unidentified where D22 says
  so. Nothing in a reserve depends on which robot dies; the robot only decides *whether*
  and *which tier*, in the kill roll the service replays.
- **Low-water refill.** The server calls `TakeReserve` for a tier when its cache falls
  below `Reserve.LowWater` (a third); the call is off the kill path. If a tier is empty
  when a kill rolls it, the server falls back to synchronous `MintDrops` for that one
  kill, counted in metrics — it is the signal to raise the size.
- **Level-up.** Reserve items carry `rolled_for_level`. When a `Checkpoint` raises the
  character's level by 2 or more over a tier's `rolled_for_level`, the service sweeps
  that tier (`Swept (reserve_stale)`) and the server takes a fresh one; the ledger
  never lets a level-30 hero reveal level-1 loot from a stale cache.
- **Scope.** A reserve belongs to `(character, instance)`: it is swept when the
  character's lease on that instance ends (hop, logout, death, crash) or the instance
  is reaped, so a reserve can never follow a player to town or to another level. A
  party member's reserve is theirs alone (drops are instanced, R4).
- **Replay.** `IGameRules.KillRoll(instanceSeed, killSeq, robotTemplate, depth,
  partySize) → (drop?, tier)` is pure and shared with the game; the service's `Reveal`
  check is that function, so a server cannot reveal more, or better, than the roll allows.

Gate facts: a reserve is minted at lease with the configured tier counts; `Reveal`
moves exactly one item and books Australium once; a `Reveal` whose kill roll gives no
drop, or another tier, is refused and the item destroyed with an audit row; refill
below low-water; stale-tier sweep on a 2-level jump; sweep on lease release and on reap;
the property test of §5.3 now includes `TakeReserve` / `Reveal` in its operation set.

### 5.3 The sweep

`SweepInstance(instance)` inside the reap transition, once, one transaction:
1. `World` and `Reserve` items with `instance_id = X` → `Swept` (`level_shutdown`).
2. `Corpse` items whose corpse is on X → `LostAndFound` at 25 % of vendor value (D6);
   a hardcore character's → `Destroyed` (D7).
3. If X is a hub: `Vendor` items of that hub → `Swept`; `TradeEscrow` of that hub's
   trades → back to offerers, trades cancelled.
4. One `audit_log` row with the counts.

Idempotent. Gate facts: each rule; count-in = count-out across mint → claim → drop →
death → reap → Lost & Found → reclaim; a property test over random RPC sequences with
mid-sequence reaps that no item ever has two owners and none vanishes without a
terminal event.

### 5.4 Reconcile

Nightly and on demand: cached Australium equals its ledger; every `Live` item's owner
exists; every `World` item's instance is not reaped; every lease's instance is live;
stash overflow flagged withdraw-only (2d). Findings on the Dashboard; nothing auto-fixed
but the cached sum.

---

## 6. Phase H3 — the game-server API (gRPC)

`proto/game.proto`, package `descent.host.v1`. Auth interceptor: `x-instance-id` +
`x-instance-token` metadata against `instances.token_hash`; anything else
`UNAUTHENTICATED`. A level instance acts only on characters it leases and items in its
own `instance_id`; a hub may also create / list / delete characters, trade, stash,
vendor and travel.

### 6.1 `InstanceService`

Same shape as Q5, as far as a game process allows: **one stream, everything else
unary.** `Connect(stream Heartbeat) returns (stream ServerCommand)` is opened once per
pod at boot and held for its life. Up it carries only `Heartbeat {players, tick_ms}`
every 5 s; down it carries the commands the service must push, because the game
process hosts no gRPC server: `Drain {reason}`, `Kick`, `Shutdown`, `Retry {steamid}`,
`PrepareHop {steamid, instance}`, `Say`, `Exec {cmd}` (admin console, `sv_cheats`-gated
server-side). Everything the server reports is a unary call with its own response and
idempotency key: `Booting`, `MapReady {port}`, `PlayerJoined {peer ip:port, steamid}`
(7e identity; the response carries the player's **real address and session id**, so a
server needs no second call), `ResolvePeer {peer ip:port} → {session, client_addr,
steamid?}` (for logs, bans and admin at any later time), `PlayerLeft`, `HopReady`,
`Log` (batched, throttled). The server logs and bans by the resolved address, never
by the peer it sees, and bans are stored in the service and enforced by the gateway. The stream is the
health check: three missed heartbeats → `suspect`, six → reap; its close is the crash
signal. (The fake game server of §7.6 could host a gRPC server and take commands as
unary calls; it does not, so it exercises the same path the engine will.)

### 6.2 `CharacterService`

`List(account)`, `Create`, `Delete`, `Lease`, `Release`, `Checkpoint(character, lease,
sheet_bytes, sheet_version, evidence)` — idempotent, replayed through
`ValidateCheckpoint`, refused with the first failing rule named — `GetSheet`,
`SetReachedDepth`.

### 6.3 `ItemService`

The §5.2 table (`TakeReserve`, `Reveal`, `MintDrops`, `Claim`, `Drop`, …), plus `Move` (backpack / equip / belt), `StashMove`, `GetBackpack`,
`GetStash`, `GetVendorStock`, `Salvage`, `ListWorldItems(instance)`.

### 6.4 `PartyService`, `TradeService`, `LostAndFoundService`

`Party.Create / Invite / Accept / Leave / Kick / Get` (D8). `Trade.Open / Offer / Lock /
Confirm / Cancel`, both leases held by the calling hub, second `Confirm` commits.
`LostAndFound.List / Reclaim`.

### 6.5 `TravelService`

- `RequestDescent(party, depth, leader_lease)` → D8 / R6 → takes a `ready` level (or
  queues a fallback link and answers `WAITING {eta}`) → creates the level pod → for each
  member: waits for `MapReady`, flips the route, sends the hub `PrepareHop` → `HopReady`
  → `Retry`.
- `StairsDown`, `TownPortal` (level stays live for the D9 return), `ReturnThroughPortal`,
  `ReturnToCorpse`.
- `ITravelStrategy`: `GatewayRetryTravel` (D25) or `RedirectTravel` (0f; instances get a
  NodePort each). Chosen by H0b.

Gate: in-process gRPC facts (`WebApplicationFactory` + `GrpcChannel`) per RPC including
auth refusal, wrong-instance refusal, stale lease, capacity refusal, and the forged-item
fact (an `instance_json` the seed does not reproduce is rejected on `Checkpoint`).

### 6.6 The SDK (`Host.Sdk`) — what the game uses instead of the API

The mod never sees a gRPC stub. It references `Host.Sdk` and talks to interfaces; the
SDK owns every rule about *how* to talk to the host, so `Descent.Server` holds only
game logic and the fake game server holds only test logic.

| interface | what it hides |
|---|---|
| `IHostSession` | reading the pod's environment (`DESCENT_*`), opening `Connect`, the heartbeat timer, reconnect with backoff, `Booting` / `MapReady` / `PlayerJoined` / `PlayerLeft` / `Log` as typed calls with the idempotency key generated inside; `PlayerJoined` returns the resolved real address; batching and throttling of `Log`; **the rules-module handshake of D-H9** — it finds the assembly marked `[HostRulesModule]` in the process, hashes it, announces it in `Booting`, and streams it when the host says `send`; the game never knows this happened |
| `IHostCommands` | the down-stream commands as an event the game subscribes to (`Drain`, `Kick`, `Shutdown`, `Retry`, `PrepareHop`, `Say`, `Exec`); `PrepareHop` is a request the handler completes with `HopReady` after its own checkpoint, and the SDK sends it |
| `ICharacters` | `Lease` returning an `ICharacterLease` that renews itself, carries the fencing token into every write, refuses use after `Release`, and raises `LeaseLost` when the service says so; `Checkpoint` with sheet versioning, the evidence record, and "stale lease" surfaced as a typed result, never an exception the game must parse |
| `IItems` | the **reserve cache** of §5.2a: `TakeReserve` on lease, per-tier queues, `NextForTier(tier)` on the kill path (synchronous, no I/O), `Reveal` fired afterwards, low-water refill off the kill path, the synchronous `MintDrops` fallback for an empty tier and for bosses, stale-tier replacement on level-up, and the cache's release when the lease ends; `Claim` / `Drop` / `Move` / `StashMove` / vendor / craft / identify / repair as typed results |
| `IParties`, `ITrades`, `ITravel`, `ILostAndFound` | the calls of §6.4–6.5 with their idempotency keys and the two-phase trade state machine enforced client-side (a `Confirm` before both `Lock`s is refused locally) |
| `IPeers` | `ResolvePeer` with a per-session cache, so logs and bans ask once |
| `IHostClock`, `IHostMetrics` | the SDK's own timers and counters (reserve fallbacks, RPC latency, reconnects), exposed so the game can show them in `descent_status` |

**Rules the SDK enforces, so the game cannot get them wrong:**

- **Main-thread delivery.** Every completion and every command is queued and delivered
  when the game calls `IHostSession.Pump()` from its main thread once per frame; no
  SDK callback ever runs on a gRPC thread. The kill path (`NextForTier`) is a
  dictionary pop, no await. (This is the same discipline as TF2 plan R18 / Appendix
  B.6 for the AI: engine-touching code runs only where the engine runs it.)
- **Fail closed.** No item is ever created locally; an unreachable host means no
  reserve, no mint, no checkpoint acknowledged — the game shows the wait, the SDK
  retries with backoff, and a lease that cannot heartbeat is reported `LeaseLost` so
  the game stops writing under it.
- **Idempotency and deadlines** are generated and attached inside; the game passes
  none. Retries re-send the same key.
- **Versioning.** `Booting` carries the SDK version; the service refuses an SDK it does
  not support with a message naming both versions, before anything else happens.
- **Opaque payloads.** Sheets and items cross the SDK as `byte[]` + `schema_version`;
  the codecs come from the rules module itself (`IGameRules.Sheets` / `.Items`), on
  both sides, so the host decodes with the very code that encoded.
  `Host.Sdk` never learns a stat name (Q2's tie-break applies: if a typed Descent
  wrapper ever pays for itself it goes in the sharp repo beside `Descent.Server`).
- **No engine, no native.** The SDK references `Host.Proto` and `Grpc.Net.Client`
  only; a fact in `Host.Sdk.Tests` reads its own project and refuses `SourceSharp.Abi`,
  `DllImport`, `LibraryImport`, `extern` and `AllowUnsafeBlocks` (TF2 plan R15, §16).

**Delivery.** Built here, packed by `make pack` as `SourceSharp.Host.Sdk` (with
`Host.Proto` inside it) into `bin/nupkg/`, and consumed by the sharp repo by
`PackageReference` from a local feed or by `ProjectReference` through a `HOST_ROOT`
property there; the sharp repo's choice (§14.9). The SDK's public surface is the
contract and is versioned with the proto: a breaking change bumps both.

Gate: `Host.Sdk.Tests` against the in-process service for every interface method,
including reconnect while a lease is held (renewed on reconnect, `LeaseLost` after the
service expired it), `PrepareHop` → `HopReady` ordering, reserve refill and fallback
counters, `Pump()` delivering nothing off-thread (a fact with a thread-id assertion),
and the R15 self-read fact; and the fact that `Host.FakeGame` references `Host.Sdk`
and not `Host.Proto` directly.

---

## 7. Phase H4 — the instance manager on Kubernetes

### 7.1 A game pod

```yaml
metadata: { name: descent-<kind>-<id>, labels: { app: descent-game, descent/instance: <id>, descent/kind: level|hub } }
spec:
  restartPolicy: Never
  terminationGracePeriodSeconds: 60
  initContainers:
    - name: mod                        # the mod image, from runtime config (D-H6)
      image: <Instances.ModImage>; command: [sh, -c, "cp -a /game/. /out/"]; volumeMounts: [game → /out]
    - name: fetch-level                # levels only; the hub's town map came with the mod
      image: <engine>; command: [/opt/descent/fetch-level.sh, descent-<depth>-<hash>]   # GET /internal/maps/descent-<depth>-<hash>.{bsp,nav3d,map2d} (cluster-only) → /game/maps-overlay
  containers:
    - name: game
      image: <engine>                  # this repo's engine image; the mod files arrive on the `game` volume
      command: [/opt/descent/game-entrypoint.sh]
      args: [-game, /game/mods/descent, -console, -port, "27015", -ip, "0.0.0.0", +maxplayers, "<n>", +map, "<map>", +descent_instance, "<id>"]
      env: DESCENT_SERVICE=descent-service:5001, DESCENT_INSTANCE_ID, DESCENT_INSTANCE_TOKEN (from a per-pod Secret), TMPDIR=/tmp/game, SOURCESHARP_DEVAPI_DIR=/tmp/devapi
      ports: [{ containerPort: 27015, protocol: UDP }]
      volumeMounts: [game → /game, content → /game/content (ro), maps-overlay → /game/mods/descent/maps/pool, tmp → /tmp]
      resources: { requests/limits from config }
      readinessProbe: exec devapi ping (the service also waits for MapReady)
  volumes: [game (emptyDir), content (PVC, ro), maps-overlay (emptyDir), tmp (emptyDir)]
```

- `game-entrypoint.sh` does what `source#` does for a dedicated run and no more: sets
  `LD_LIBRARY_PATH` to the engine's `bin/` and the mod's `bin/linux64`, the fd limit
  (`engine.fd_limit` from `mods.yaml`), the mod's `env`, then `exec srcds_linux64` with
  the mod's `args` from `mods.yaml` plus the pod's. Bootstrap reads `mods.yaml` beside
  `srcds_linux64`'s game dir to find the mod's assemblies (H0a measures whether it does
  so without `source#`; §14.3 if not).
- The flag set is a **dedicated-server** set, not the sharp Makefile's client
  `HEADLESS_ARGS`: `-console` (srcds is console-only, `-novid` / `-windowed` do not
  apply), `-nohltv +tv_enable 0` (the dropped-feature enforcement the sharp Makefile
  explains), and SourceSharp's own `-devapi -devapisocket -condebug -nomessagebox
  -nocrashdialog -assertlog`. It lives in `Instances.Args` config so the sharp repo can
  change it without a release here, and H0a is where it is first proven against
  `srcds_linux64`, which no target in the sharp repo runs today.
- Levels land in `maps/pool/` inside the mod's `maps/` as `descent-<depth>-<hash>.bsp`,
  a content-addressed name that never collides with the town map and says its depth in
  the server browser and logs; `sv_downloadurl` points at the public fastdl listener,
  where the client asks for `maps/descent-<depth>-<hash>.bsp.bz2` and can get nothing
  else. `.nav3d` and `.map2d` sit beside the BSP in `maps/pool/` for the server only
  and are outside every download path.
- Each pod has its own IP, so the port is `27015` and never walks; `MapReady {port}` is
  still recorded and compared, because the memory
  `live-gate-port-is-not-the-one-you-asked-for` cost a lane a day.
- Pod logs are kept by Kubernetes; the service tails them for the admin (§11) and copies
  the last 10k lines into `instances/<id>.log` on its volume at reap, 7-day retention.

### 7.2 `KubernetesInstanceHost : IInstanceHost`

Over `KubernetesClient`, in-cluster config. `Create(spec) → pod uid`, `Watch()` (an
informer over `app=descent-game`, reconnecting), `Delete(name, grace)`, `Logs(name,
tail)`, `RunJob(spec)` for the bake (§9.2). Every pod carries the instance id as a label;
the Secret holding its token has an `ownerReference` to the pod so deletion cascades.
`LocalProcessInstanceHost` implements the same interface with `srcds_linux64` child
processes for `make run` on this box, and `FakeInstanceHost` (an in-memory pod store
with scripted phase changes) is what the facts run against.

### 7.3 State machine (TF2 plan 7b + crash)

`requested → level_ready → creating → booting → live → draining → reaped`, plus `crashed`
(pod phase `Failed` / `Succeeded` outside `draining`, or the container restarted — it
cannot, `restartPolicy: Never`) and `failed` (no `MapReady` within `Instances.BootTimeout`,
120 s; the pod is deleted). Each transition is persisted before its side effect so a
service restart resumes from the row.

- `live` = `MapReady` on the stream **and** pod `Ready`.
- `draining` = empty past its grace (Q10), or admin Drain, or hub replacement. Refuses
  new routes; reaped when empty.
- `reaped` = `Delete(pod, grace 60 s)` (SIGTERM → the game's own shutdown; SIGKILL by
  Kubernetes at the deadline), then §5.3 sweep, then attached sessions fall back to the
  hub route.
- `crashed` = the watch reports the pod gone or failed: sweep, release leases, hub
  fallback; for a hub: re-create with backoff (1 s … 30 s, five in five minutes → stop
  and alert).

### 7.4 Adoption after a service restart

On start: list pods by label; for each `instances` row not terminal, a pod with the same
name **and uid** → mark `live` pending the stream reconnect (pods retry `Connect` for
60 s); no pod, or a different uid → `crashed` and run its transitions; a pod with no row
→ delete it (it is nobody's).

### 7.5 Hubs and capacity

One hub (Q8). `Instances.MaxLevelPods` enforced at `RequestDescent`; at cap the request
queues with a visible wait. Pod requests / limits from config, node selectors and
tolerations pass through so game pods can be pinned to a node pool.

### 7.6 The fake game server (`Host.FakeGame`)

A C# stand-in for a game pod, so nothing in this repo needs the engine to be tested:

- **Same contract, same shape — through the SDK.** It is written on `Host.Sdk` (§6.6)
  and nothing else of the API: it reads the same environment a game pod gets, lets
  `IHostSession` open the stream and heartbeat, reports `MapReady` and players through
  it, takes commands from `IHostCommands`, and drives `ICharacters` / `IItems` /
  `ITravel` exactly as `Descent.Server` will. So the SDK's facts and the API's facts
  are written once, against the fake, and the real server is held to the same calls —
  and a feature the fake cannot express through the SDK is a gap in the SDK, found
  before the game needs it.
- **A toy netchannel.** It binds its UDP port and answers a minimal handshake
  (`getchallenge` → challenge, `connect` → accept, then keepalives that echo the
  instance id) which the gateway relays unchanged, because the gateway is
  payload-agnostic. `retry` is a real message in the toy protocol, so a route flip is
  exercised end to end. `Host.FakeClient` speaks the client side and reports which
  instance it is on.
- **Driven, not scripted.** A small `FakeGameControl` gRPC service on the fake
  (`Join(steamid)`, `Leave`, `Kill(robot)`, `Pickup(item)`, `Die`, `Descend`, `Crash`,
  `Hang` — stop heartbeats — `Slow(ms)`) mirrors the `descent_*` dev commands of the TF2
  plan, so a live script drives the fake the way it will drive the engine.
- **Two hosts.** As an image (`deploy/Dockerfile.fake`) it is the engine image of
  `TIER=fake` (the mod init container is skipped), created by the real
  `KubernetesInstanceHost` in a real pod with the real level init container, gateway and
  LoadBalancer in the path. In-process (`FakeGameHost`) it is
  started by `FakeInstanceHost` when the service "creates a pod", so `Descent.Service.Tests`
  runs the whole travel gate of §10 as facts in seconds, with a fake clock.
- **Never in production.** The engine image name is config; `base/` names the real
  one, only the `k3d` overlay with `TIER=fake` substitutes the fake. A start-up fact
  refuses `Instances.EngineImage` containing `fake` outside `Development`.

Gate: facts against `FakeInstanceHost` for every transition, boot timeout, hub re-create
backoff, adoption (same uid, different uid, orphan pod), sweep-on-reap ordering, and that
reap is a pod delete with the configured grace; in-process end-to-end facts with
`FakeGameHost`: create → live → drain → reap with the sweep observed, `Hang` → suspect →
reap, `Crash` → crashed → leases released. Live (`live/h4.sh`, `TIER=fake` then `real`):
1 hub + 3 levels, each joinable (fake: through `Host.FakeClient`; real: through devapi),
`kubectl delete pod` one level and see `crashed` → sweep → audit row; `kubectl rollout
restart` the service and see all three adopted.

---

## 8. Phase H5 — the gateway (`Host.Gateway`)

### 8.1 Data path

- One public UDP socket behind a LoadBalancer Service with `externalTrafficPolicy:
  Local`, so the source address the gateway sees is the client's (H0b confirms). Per
  client `ip:port` a **session** with its own backend-facing socket (a distinct source
  port, one pod IP), so each backend sees a stable peer.
- **The source port is the player id.** Nothing can be added to the datagram (the
  engine parses it), so the session's identity is carried by the only field the relay
  controls: the backend-facing socket's `ip:port`. It is unique per session for the
  socket's life, reported to the service in `SessionOpened {addr, backend, peer}`, and
  a server resolves it with `ResolvePeer` (§6.1). A session's peer changes only on a
  route flip, when a new backend socket is opened; the old one is closed first (R21),
  so a peer is never reused while a server could still name it, and closed peers are
  quarantined for 60 s before the port may be reused.
- Payload-agnostic relay, one `SocketAsyncEventArgs` pool, no per-packet allocation.
- Default route for a new source is the hub. Unidentified sessions can reach only the hub.
- A route flip is a hard cut (R21): the socket to A closes before the first packet goes
  to B; `SessionMoved` is reported.
- Sessions expire after 60 s of silence → `SessionClosed`.
- **Handshake admission.** All clients reach a backend from the gateway's one pod IP,
  so `MAX_REUSE_PER_IP` (5 half-connected per IP, hard-coded) applies per backend: the
  gateway forwards at most 5 in-flight handshakes per backend and **holds** the rest (§8.1b; a
  handshake is in flight from its first `getchallenge` until the first netchannel packet
  after `connect`, or 5 s). A level holds 4 players so it never queues; the hub queues
  briefly on a burst. `sv_max_connects_sec` is a convar and is raised on every instance.
  Per source IP: `Gateway.HandshakesPerSecond` (2). H0b measures both.
- More than one gateway replica is not in v1 (routing is per session, in memory, and
  the LoadBalancer does not hash by source consistently across replicas); §12 lists it.

### 8.1a A pinned peer per player

The peer a server sees should be **the same for the same player**, every session, every
hop, every instance: then bans, logs, rate limits and the engine's own per-address
state all key on the player rather than on a session. `IPeerAllocator` owns it, with
the pin persisted in `players (steamid → pinned_peer, since, last_seen)`:

| implementation | what is pinned | needs | what it buys |
|---|---|---|---|
| `PortPerPlayer` (default) | a backend source **port** per SteamID on the gateway's one IP, from a hash of the SteamID into the allocator's range with collision fallback; the socket lives as long as the player has any session | nothing from the cluster | stable identity per player; `ResolvePeer` answers from the pin; up to ~60k concurrent pinned players per gateway |
| `AddressPerPlayer` | an **IP** per SteamID from a pool the gateway pod owns (Calico: `cni.projectcalico.org/ipAddrs` on the pod from a dedicated IP pool; or Multus + ipvlan with a routed range; a /22 = 1,000 players), port fixed | a CNI that can attach a pool of IPv4 addresses to one pod, and routes from the game pods to it | everything above **and** the engine's per-address limits become per-player: `MAX_REUSE_PER_IP` and `sv_max_connects_sec` stop mattering, so §8.1's handshake admission is switched off; real per-player bans at the engine level |

- IPv6 would make the second trivial (one /64 per pod is ordinary), but the 2013
  engine's `netadr_t` is IPv4-only — H0f confirms with a capture.
- **When the pin can apply.** The gateway learns a SteamID from the hub's `PlayerJoined`,
  after the client is already connected on an ephemeral peer. So the pin is used **from
  the next hop on** (church → level, portal, corpse run), which is where identity matters
  most, and the hub sees the ephemeral peer for a first-time player. H0f asks whether the
  SteamID is readable from the `C2S_CONNECT` packet's Steam ticket (its header carries the
  `CSteamID` in the clear in every Source build inspected so far — to be confirmed, not
  assumed); if it is, the gateway reads it (reads, never rewrites — the relay stays
  payload-agnostic on the way through) and picks the pinned peer before forwarding the
  first packet, so even the hub sees the pin — that is §8.1b's identity-first handshake,
  which holds the connect packet while the pinned peer is set up. Under R11 that SteamID
  is unverified, which is why the pin is keyed on it only after the single-presence rule
  (7e) has admitted the session.
- **Reuse and release.** A pinned port or address is held while the player has a live
  session and for `Gateway.PinRetention` (24 h) after, then returned to the pool; a
  returning player gets the same pin if it is still held, a new one otherwise, and the
  `players` row records both so `ResolvePeer` on an old log line still answers.
- **What does not change.** A session still owns one backend-facing socket; the pin
  decides which port or address that socket binds. A route flip is still a hard cut.

Gate facts (`Host.Gateway.Tests`, both allocators): the same SteamID gets the same peer
across two sessions and across a hop; two SteamIDs never share a pin; a pin survives a
gateway restart (from `players`); release after retention; with `AddressPerPlayer` the
admission queue is bypassed and the sixth simultaneous handshake to one backend is
forwarded at once (fake backends assert distinct source IPs). Live (`TIER=fake`): a
`ResolvePeer` from the level for a player who hopped returns the same pin the hub
resolved.

### 8.1b Buffering: hold, never drop, and the identity-first handshake

The relay may **hold a client's packets** for a bounded time instead of forwarding or
dropping them. It does so only in three situations, all before or between netchannels;
in steady state every packet is forwarded at once, because a held netchannel packet is
added latency the player feels.

1. **Admission** (§8.1). A handshake that would be the sixth in flight to one backend
   is held, not refused, and released in order when one completes or times out.
2. **A route flip.** Between the hard cut from A and the moment B reports `MapReady`
   and `Ready`, the client's `retry` handshake packets are held and replayed to B when
   it is there, so a hop never depends on the client's resend timer and a slow pod boot
   does not show as a failed connect. Cold start uses the same hold: an unidentified
   session's packets wait for the hub.
3. **Identity first.** The gateway learns who a client is **before** any backend sees
   the client, and then chooses the pinned peer (§8.1a) and the route for the very
   first forwarded packet — the answer to "when can the pin apply". Because the
   engine's challenge is bound to the backend that issued it and is carried into the
   netchannel that follows, the gateway cannot read the SteamID and then swap peers
   under a challenge it already relayed. So the handshake is done in two steps:
   - the client's `A2S_GETCHALLENGE` is **answered by the gateway** with a challenge it
     generates (it already answers A2S; the connectionless formats are H0f's);
   - the client's `C2S_CONNECT` arrives with the Steam ticket; the gateway reads the
     SteamID from it (reads, never rewrites), runs single presence (7e), allocates the
     pinned peer, picks the route (hub for a new arrival; the instance the service
     already routed for a returning one), and **holds** the packet;
   - from the pinned peer it sends `A2S_GETCHALLENGE` to the chosen backend and gets
     the backend's real challenge;
   - it replies to the client with a fresh `S2C_CHALLENGE` carrying the backend's
     challenge; the client re-sends `C2S_CONNECT` with it, which is forwarded as is,
     and from `S2C_CONNECTION` on the gateway touches nothing again.
   If the 2013 client does **not** re-connect on a second challenge (H0f decides), the
   fallback is the plan as it was: the first handshake goes to the hub on an ephemeral
   peer and the pin applies from the next hop. Nothing else in this section depends on
   the outcome.

Rules: buffers are per session, bounded (`Gateway.HoldPackets` 64, `Gateway.HoldBytes`
64 KiB, `Gateway.HoldMs` 15 s), oldest dropped first, and released in arrival order;
a hold that expires closes the session with a logged reason; the hold state is in the
session, so a gateway restart loses only what was in flight (a `retry`'s worth). What
this changes about "payload-agnostic": the gateway **parses and answers the
connectionless handshake** and reads the SteamID from the connect packet; it never
reads, buffers or alters a netchannel packet, and the two are told apart by the
connectionless header, which is the first thing H0f documents.

Gate facts (two fake backends, `Host.FakeClient`): a held handshake is forwarded in
order when admission frees; packets sent during a flip arrive at B once and in order
after `MapReady`, none at A; identity-first — the first packet a backend ever receives
for a client already comes from that client's pinned peer, single presence is applied
before any backend is touched, a `connect` with an unreadable ticket falls back to the
hub on an ephemeral peer; bounds — the 65th held packet evicts the oldest, a hold past
15 s closes the session with the reason. H0f rows: the client's behaviour on a second
`S2C_CHALLENGE`; whether `S2C_CONNECTION` or the netchannel's first packets carry the
challenge; whether the ticket's SteamID is readable in `C2S_CONNECT`.

### 8.2 Control (`proto/gateway.proto`)

One stream, everything else unary (Q5). Both sides host gRPC on loopback-free cluster
addresses: the service on `:5002`, the gateway on `:5003`, each reachable only by the
other (NetworkPolicy).

- **`GatewayHealth.Attach(stream Ping) returns (stream Pong)`**, gateway → service, held
  for the gateway's life: a ping every 2 s carrying the gateway's table version and
  session count; a pong carrying the service's table version. A version mismatch, or the
  stream's loss and re-attach, triggers `SyncTable`. Three missed pongs → the gateway
  keeps relaying on its last table and logs; three missed pings → the service marks the
  gateway `unreachable` on the Dashboard and stops issuing hops until it is back.
- **`GatewayEvents`** (gateway → service, unary): `SessionOpened {addr, backend}`,
  `SessionClosed`, `SessionMoved`, `Stats` (every 10 s). Each carries `request_id` and is
  retried on failure.
- **`GatewayControl`** (service → gateway, unary): `SyncTable {version, sessions[],
  default_backend}`, `SetRoute {session, backend ip:port, version}`, `CloseSession`,
  `SetDefault`, `SetServerInfo` (A2S snapshot). Every call carries the table version it
  produces; the gateway refuses a stale one with `FAILED_PRECONDITION`, which is the
  service's cue to `SyncTable`.

The service owns the registry (`sessions` table); a gateway restart rebuilds from
`SyncTable`; a service restart leaves the gateway relaying on its last table until
`Attach` succeeds again.

Identity: the hub's `PlayerJoined {gateway_port, steamid}` binds session ↔ SteamID; single
presence (7e: oldest live wins, silent taken over) is decided in the service and executed
by `CloseSession`.

### 8.3 A2S

`A2S_INFO` / `A2S_PLAYER` / `A2S_RULES` for the hub from the pushed snapshot, with
challenge numbers. No master-server heartbeat.

Gate: facts against two fake UDP backends — session create / expire, route flip affects
only new handshakes, hard cut (no packet delivered to both, asserted on the fakes'
receive logs), admission (a sixth handshake to one backend waits, and is forwarded when
one completes or times out), `SyncTable` after a simulated restart and after a stale
`SetRoute` version is refused, relaying continues through a lost health stream, unidentified session
refused a level route, second live session for one SteamID refused and a silent one
taken over, session close releases the lease — the fake backends of these facts are
`Host.FakeGame` in-process and the clients `Host.FakeClient`. Live (`live/h5.sh`): H0b's
hop through the real gateway on `k3d` (`TIER=fake`, then `real`); eight clients joining
the hub at once, none refused; a server-side `ResolvePeer` for each returns the
address the client really connected from.

---

## 9. Phase H6 — MapForge on `ssmap` rooms, and the pool

### 9.1 `Descent.MapForge`

- **Library.** One `rooms.vmf` per tileset with `info_room` markers as the map tools
  define them, plus `library.json`: the TF2 plan 6a metadata the tools do not know — per
  room tags (`start`, `stairs`, `boss_arena`, `treasure`, `corridor`, `junction`,
  `shrine`), weight, depth range, `jump` / `low` (R19), and the spawn / furniture marker
  names the game reads. `mapforge lint` runs `RoomLinter` (the tools' own: model, sealed,
  sockets) and then 6a's game rules (R19 jump volume measured from brushes, tag
  coverage, nav fragment covers every marker) with a refusing fixture each. Library
  version = hash of `rooms.vmf` + `library.json`; the pack id is the tools' own. A pack
  reaches the service in one of two ways — the bake Job (§9.2) or an **admin upload**
  (§9.4) — and both land as a version in `packs` through the same validation.
- **Layout.** `IGameRules.GenerateLayout` (D-H7) returns a `LevelPlan`: grid size,
  placements `(room, row, column, rotation)`, the up room (arrival from the church or the
  level above) and the down room (stairs), boss arena on 5 / 10 / 15, treasure off-path,
  capped sockets. MapForge writes it as a `level.yaml` (`library:`, `rows`, `columns`,
  `grid` with `room@rotation`, the transition rooms' `-up-map` / `-down-map` equivalents
  as level keys) — the format the tools' `LevelYaml` reads, checked in here as fixtures.
  Until 6b exists, the tools' `LevelGenerator` (what `ssmap layout -seed` calls), used
  in-process, fills the pool so every other phase can run.
- **Link.** `LevelLinker.LinkAsync(layout, library, ctx, options)` → `LinkedLevel`;
  MapForge saves `descent-<depth>-<hash>.bsp`, `.nav3d`, `.map2d`, bz2s the BSP, keeps
  `level.yaml` beside them. Hash = `hash(pack_id, tileset, depth, seed, difficulty,
  linker identity)`. `.nav3d` replaces the TF2 plan's `.dnav` and `.map2d` its 2m
  outlines (Appendix B.3 / 2m read these files through `SourceSharp.MapFormats`).
- **Oracle.** `LevelFlattener.Flatten` → VMF → the tools' full compile (`Vbsp` /
  `Vvis` / `Vrad` `*Async`) → the tools' diff against the linked BSP, all in-process,
  from the admin Pool page only.

### 9.2 `IMapCompiler` and the bake Job

`Bake(library) → pack` and `Link(LevelPlan) → LevelFiles`. Both are library calls into
`SourceSharp.MapTools`; nothing shells out. `LinkedMapCompiler` links in-process in the
service pod (no content needed). A **bake** runs as a Kubernetes Job from the **service
image** with the content PVC mounted, command `Descent.Service bake --library <tileset>
--cache /cache --out /out`: it builds a `VbspContext` over the mounted content, calls
`RoomLibraryCompiler.CompileAsync` with the tools' incremental store
(`SourceSharp.MapTools.Cache.Sqlite` on a small `bake-cache` PVC, so a one-room edit
re-bakes one room and the log says `reused`), then `POST /internal/packs/<id>` to the
service, which validates it exactly as an upload (§9.4) and stores it as the next
version of that mod + library on the `maps` volume; whether that version is used is the
admin's call unless `MapPool.AutoActivateBakes` is on. The Job's resources come from
H0d. `FakeMapCompiler` returns fixture bytes for the unit tier.

### 9.3 The pool worker (TF2 plan 7c)

- Target K = `MapPool.PerDepth[depth]` (3) `ready` levels per depth **of the pack
  version assigned to that depth** (§9.4). Deficits become `map_jobs` with priority: (0)
  fallback for an empty pool at `RequestDescent`, (1) depth N+1 for every party on N,
  (2) depths selectable from the church, (3) shallowest first. A `bake` job outranks
  every link and runs when the library source changes; a change of assignment re-targets
  the deficit at once and applies the old-levels policy of §9.4.
- Link concurrency `MapPool.ConcurrentLinks` (2; H0d decides). Failure → `failed` with
  the log, retried 3× with a fresh seed, then on the admin Pool page.
- Hand-out: `ready → handed_out` in the same transaction as the instance's `requested`
  row — once, ever. Reap → `retired`. GC: retired and unreferenced beyond the newest 10
  per depth → files deleted; `failed` older than 7 days deleted.
- Two listeners (Q18): the public fastdl `:5004` serves `/maps/descent-<depth>-<hash>.bsp.bz2`
  (the ruled form `/maps/<mod name>-<level>-<hash>.bsp.bz2`) and 404s every other path
  and extension; the internal `:5000` serves `/internal/maps/descent-<depth>-<hash>.{bsp,nav3d,map2d}`
  to pods' init containers and is never published. `ETag` = hash; the mod name is
  `Mod.Name` in config, the level is the depth number.

### 9.4 Room packs at run time: upload, versions, assignment

- **Upload.** The admin Room packs page accepts a `.roompack` (multipart on the
  loopback admin endpoint, size limit `Admin.MaxUploadBytes`, 2 GiB) for a **mod** and
  a **library** key, with an optional `library.json` and a note. The service streams it
  to `packs/<mod>/<library>/<version>.roompack.uploading`, then **validates**: the
  file opens with the tools' `RoomPack` reader; its manifest names the library key and
  the mod (the tools' `-namespace`, written by `ssmap room`, must equal the library key,
  and `mods_name_keys` / `RoomContracts` names must belong to `Mod.Name`); every room
  passes `mapforge lint` from the pack's own room metadata; the pack's entity budget
  fits the engine limits (`LevelEntityBudget`); and a **trial link** of one small fixed
  `level.yaml` per library succeeds in-process. Any failure → `rejected` with the
  report kept, file deleted. Success → `ready`, version = previous + 1, sha256 and
  lint report stored, audit row. The bake Job's `POST /internal/packs` runs the same
  validation, so a bake and an upload are indistinguishable afterwards but for
  `source`.
- **Assignment.** `pack_assignments` maps `(mod, depth)` → pack version. The page shows
  a matrix of depths × versions with the assigned one marked, and offers per depth or
  per depth range: **activate** a version (new links use it), **pin** (the same, but
  a newer upload or bake does not move it — the default is pinned; `MapPool.AutoActivateBakes`
  only affects unpinned depths), **roll back** (activate an older `ready` version), and
  **retire** a version (refused while assigned anywhere). Every change is audited with
  the before/after matrix.
- **Old levels policy**, chosen at assignment: *drain* (default) — levels already
  linked from the old version stay `ready` and are handed out until the new version's
  levels exist, then retired as K is met; *retire now* — they go `retired` at once and
  the pool links the new version at priority 0 (the church may show the
  "doors are stuck" wait). A level that is `handed_out` is never touched: **a running
  instance keeps the level it booted with**, whatever the assignment says, and the
  Instances page shows each instance's pack version.
- **Levels and packs are immutable.** A level hash includes the pack version; a level
  never re-links; a `ready` version never changes on disk. GC deletes a `retired`
  version's file only when no `ready` or `handed_out` level references it and no
  assignment names it.
- **What an admin sees.** Per version: `pack_id`, library version, source, uploader,
  size, sha256, room count and per-room entity counts (from `ssmap rooms`'s reader),
  lint report, trial-link time, which depths use it, how many levels are `ready` /
  `handed_out` from it, and the diff of room names against the previous version.

Gate: facts for hand-out-once (two concurrent requests get different hashes), priority
order, bake-outranks-link and old-pack continuity, fallback, GC keep-10, retry with a new
seed; lint per rule with a refusing fixture; byte-identical `level.yaml` for the same
plan; §9.4 — a corrupt, wrong-mod, wrong-library and lint-failing pack are each
`rejected` with the file gone; a valid upload becomes version N+1 and is not used until
assigned; activate re-targets the pool and *drain* keeps old levels handing out until
the new ones exist while *retire now* retires them at once; a `handed_out` level keeps
its version across an assignment change; retire of an assigned version is refused;
roll back re-links from the older version; GC never deletes a referenced pack. Live (`live/h6.sh`): a bake Job on `k3d` produces a pack the service accepts; a
linked level loads in a game pod; stairs-to-spawn with a warm pool is dominated by fetch
+ load, not link (both timed); a one-room edit re-bakes one room (`reused` count in the
Job log) and relinks.

---

## 10. Phase H7 — travel end to end

Wires §6.5 through §7, §8 and §9. The whole path first runs as in-process facts with
`FakeGameHost` and `Host.FakeClient` (§7.6). Gate (live, `live/h7.sh`, the TF2 plan's
7d gate on `k3d`, `TIER=fake` then `real`): two clients in a party descend 1 → 2 → town
→ back, inventories intact and the
`Live` item count unchanged across every hop; a third solo client asking for depth 1
gets a different hash; one hero dies, items are `Corpse`-owned within one RPC of death,
the level pod is deleted, Lost & Found holds exactly the corpse's contents, reclaim pays
the fee; every client only ever connected to the LoadBalancer address.

---

## 11. Phase H8 — the admin UI (`Host.Admin`, Blazor Server at `/admin`, loopback only)

Shell: left nav, one page per feature, filters, paging, CSV export; Edit and Delete with
a confirm dialog showing the diff; every mutation through `IAdminActions` (audited,
refuses leased targets with "kick and edit"). Live updates by SignalR push from the
workers. Reached by `make admin` (`kubectl port-forward`).

| page | shows | edits / actions |
|---|---|---|
| Dashboard | instances by state, sessions, pool readiness per depth, library bake state, DB size, last backup, reconcile findings, worker health, cluster facts (namespace, node pool, API reachability) | run reconcile, run backup |
| Gateway | sessions: address, backend pod, SteamID, state, age, bytes; route table; admission queue; A2S snapshot; per-player pins (allocator, peer, since, last seen, pool usage) | close session, force to hub, ban address, release a pin |
| Instances | list by kind / state, players, level and its pack version, pod name / IP / node / phase, uptime, last heartbeat | create hub, drain, kick, delete pod, log tail (live, from the pods/log API), console (`Exec`, `sv_cheats` shown), open the pod's devapi UI (proxied at `/admin/instances/<id>/devapi/`) |
| Accounts | SteamID, characters, ban, notes | ban / unban, delete |
| Characters | sheet decoded via `IGameRules`, level / xp / attrs / skills, Australium + ledger, reached depth, lease, party | edit (rules-validated), grant Australium, release lease / kick, fallen / unfallen, delete |
| Items | search by owner / base / rarity / ilvl / id; detail with `instance_json` and history | edit (flagged `admin_edited` if it no longer replays), move, give (minted), destroy |
| Stash | per account, overflow flags | move, remove |
| Parties | members, leader, live instance | disband, kick |
| Trades | open / committed / cancelled, escrow | cancel (escrow returned) |
| Lost & Found | rows, fee, origin, age | waive fee, release to stash, delete |
| Map pool | per-depth counts, job queue with priorities and logs, per-level: seed, layout as SVG from `.map2d`, sizes, link time | regenerate depth, retire, delete, download `.bsp` / `level.yaml` / `.nav3d` / `.map2d`, requeue, run the oracle diff, set K (runtime) |
| Room packs | per mod and library: versions with source, uploader, size, sha256, lint report, trial-link time, room-name diff vs previous; the depth × version assignment matrix; per version the `ready` / `handed_out` level counts; the bake Job's state and log | **upload a `.roompack`** (+ `library.json`, note), activate / pin / roll back per depth or range with the old-levels policy, retire a version, re-bake from source, cache gc (§9.4) |
| Modules | every rules module hash: assembly, version, contract version, first seen and from which instance, the instances running it, the levels linked with it, state | approve, quarantine (drains its instances, refuses its uploads), delete when unreferenced (§1.2) |
| Audit log | actor, action, target, before / after | read only |
| Config | every option; runtime ones editable | edit runtime settings |
| Backups | list, size, age | run now, download, restore-to-file |

Gate: `bunit` facts per page (render with fake stores; each action calls `IAdminActions`
with the shown values and refuses a leased target); a `WebApplicationFactory` fact that
`/admin` is served on the loopback endpoint only; a start-up fact that a non-loopback
`Admin.Listen` fails `ValidateOnStart`; an end-to-end fact for `admin_edited` + audit row.

---

## 12. Phase H9 — operations and soak

- Backups (Q14) on the `data` PVC; restore procedure; the upgrade procedure (`make images
  deploy` rolls the service, which drains → reaps → the new pod adopts; players see one
  `retry`) in `docs/ops.md`.
- Metrics: sessions, instances by state, pod create-to-live, RPC latency histograms,
  mint / claim / sweep counts, pool readiness, link and bake durations, DB write-queue
  depth.
- Soak (`live/h9.sh`): four bot-driven parties cycling church → 1 → 2 → town for an
  hour on `k3d`; no dangling lease, no `World` item older than the reap grace, `Live`
  count = mints − terminal events, no `crashed` without an audit row, service RSS flat,
  no orphan pods.
- Later, not v1: TLS on gRPC (or a mesh), Steam ticket validation at the hub join path
  (TF2 plan §17.2), gateway replicas with a consistent-hash LoadBalancer or a DaemonSet
  with `hostNetwork`, game pods across node pools by depth, object-store backups.

---

## 13. Lanes and order

Lanes by project: **G** gateway (H0b, H0f, H5), **I** instances (H0a, H0c, H4), **M** MapForge
+ pool (H0d, H6), **D** data + ledger + API + **SDK** (H2, H3, §6.6 — the SDK lands with
each API surface, never after it), **A** admin (H8, after D's stores).
H1 first, then D, G, I, M in parallel, H7 when all four are in, then A, then H9. First
useful milestone = H1 + H2 + H3 + H4 with `RedirectTravel` and `ssmap layout`-filled
pool: a character persists through a hop between two pods.

**External dependencies.** D needs no Descent code: the rules module is loaded at run
time (D-H9) and every fact uses `FakeGameRules`; the real module is TF2 plan lane A's
work in the sharp repo, built against `Host.Contracts`. M needs nothing new from the map
tools; D-H7's RPG layout needs 6b. I needs the dedicated-run fix of §14.3 if H0a says so — and with the fake
game server, only H0 and the `TIER=real` runs wait on it; every lane's facts and the
`TIER=fake` gates run without the engine. Everything else builds against the map-tools
repo as it is today.

Standing rules, inherited from the sharp repo's `CLAUDE.md`:
- One commit per sub-step with its test; stage explicit paths only; never `git add -A`.
- `[Fact]` per behaviour; every bug fix lands with a test proven red first; zero warnings
  before a phase is called done.
- Every number in this file is backed by a command run in that session and a second one.
- Processes are never addressed by `pkill` patterns; pods by name **and uid**.
- Tool output kept small; intermediate findings in a scratch file in the worktree.
- After any build, `git -C $(MapToolsRoot) status --porcelain` is empty: this repo never
  writes into its root, never builds the game (the mod image is runtime config, and the
  digest each instance ran is recorded) and never compiles against the game (the rules
  module arrives by hash at run time, and the hash each instance ran is recorded).

---

## 14. Still open

1. **The mod image's build.** Its registry and version are runtime config here (D-H6)
   and its contents are the contract in `docs/mod-image.md`; the Dockerfile that
   produces it belongs in the sharp repo and does not exist yet. Until it does, the
   `k3d` tier uses a mod image built by hand from a sibling `bin/release/` with
   `make mod-image-dev` (a dev-only target that reads the contract and nothing else).
2. **`SOURCE_SHARP_ROOT`** is now dev-only (`make mod-image-dev`), so the missing remote
   blocks nothing here. The sharp repo will need `Host.Contracts` and `Host.Sdk` as
   packages (§14.9).
3. **A dedicated run, which the sharp repo does not have.** Its `make headless` is a
   client run, `mods.yaml` names `engine.exe: hl2_linux64`, and `source#` locates the
   engine through the Steam client API, which a pod does not have. The game image
   launches `srcds_linux64` directly (§7.1). Whether Bootstrap finds `mods.yaml` and the
   mod's assemblies on that path is H0a's question. Either way the sharp repo wants two
   small additions: a `make dedicated` target and a `mods.yaml` engine entry for
   `srcds_linux64` (so the flag set is owned there), and an `SOURCESHARP_ENGINE_DIR` /
   `-engine <dir>` override in `source#` so the launcher, not this repo's entrypoint
   script, stays the one place launch logic lives.
4. **The game side of R-H1.** `Descent.Server`'s service client must treat the reserve
   (`TakeReserve` / `Reveal`) and boss `MintDrops` as the only sources of world items and
   `Claim` as the only pickup; TF2 plan 5e and 2d change accordingly ("the server is
   authoritative for every roll" becomes "the service mints, the kill roll is a shared
   pure function the service replays"). And the TF2 plan's `.dnav` / 2m outlines become
   `.nav3d` / `.map2d`.
5. **If the gateway fails H0b**, `RedirectTravel` needs a reachable address per
   instance: a NodePort Service per pod (simple, port range permitting) or `hostPort`.
   Not designed further until needed.
6. **Reserve sizing.** D-H1's tier counts and low-water mark are guesses until H0e's
   RTT and the spawn director's kill rate (TF2 plan 5d) are measured together; the
   metric for synchronous fallbacks per hour is what tunes them.
7. **TF2 content for pods.** D-H6 uses the TF2 dedicated-server depot (app 232250) on a
   RWX volume. Whether the mod's `gameinfo.txt` content mount works from that depot
   rather than a full client install is unmeasured and belongs to the TF2 plan's 0c.
8. **Admin identity** beyond loopback: Steam OpenID for an allowlist once hubs are public.
9. **How the sharp repo consumes `Host.Sdk` and `Host.Contracts`** — `PackageReference` from a local NuGet
   feed (`bin/nupkg/` here, `make pack`) or `ProjectReference` through a `HOST_ROOT`
   property in the sharp repo's `Directory.Build.props`. The package is the cleaner
   boundary and what CI would use; the project reference is faster while both move.
   The SDK is what TF2 plan §1's "service client" in `Descent.Server` becomes, so that
   plan's row should say "uses `Host.Sdk`" rather than describe HTTP calls; and
   `Descent.Rules` gains "implements `SourceSharp.Host.Contracts.IGameRules`, marked
   `[HostRulesModule]`" — the module the host loads at run time (D-H9).
