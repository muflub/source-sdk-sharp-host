# Operations

Notes the plan's gates produce (plan §12, H9). Each entry names the commit it was measured at.

## Local cluster (k3s on this box)

- The live gates run on the local k3s (`local-k3s.yaml` in the main checkout, used by
  `make … KUBECONFIG_FILE=`), not k3d. `deploy/cluster-up.sh` refuses any API server that is
  not `127.0.0.1` / `localhost`.
- Images: rootless podman builds and pushes to the in-cluster registry at
  `localhost:30500` (NodePort); k3s's containerd pulls `localhost:30500/*` over plain HTTP
  without a `registries.yaml` (pulled `descent-service:dev` in 1.4 s, 470dafc).
- **NetworkPolicy lag for new pods.** k3s's policy controller admits a new pod's IP into a
  `from:` selector about 2 s after the pod starts: a probe at t+0 s times out, every probe
  from t+2 s passes (470dafc, `service` policy on :5000). Probe pods sleep first; the SDK's
  first `Connect` / `Booting` retries with backoff. Never loosen a policy to fix such a timeout.

## H1 gate (470dafc)

| check | result | command |
|---|---|---|
| images | service, gateway, fake built and pushed at 470dafc | `make image-service image-gateway image-fake` |
| deploy | service and gateway Ready, all PVCs bound (content ROX from the local TF2 install) | `make deploy TIER=fake` |
| admin listeners | `127.0.0.1:5000` admin, `<podIP>:5000` internal, `0.0.0.0` 5001 / 5002 / 5004 | `/proc/net/tcp` in the pod (no `ss` in the image) |
| admin via port-forward | 200 twice, body names 470dafc | `make admin` + `curl 127.0.0.1:5000/admin` |
| admin via pod IP | 404 twice (`/healthz` 200 on the same socket) | a pod in the namespace, `sleep 5; curl <podIP>:5000/admin` |
| undeploy | every pod and the namespace gone in 25 s (grace 30 s) | `make undeploy` |

## The engine image (measured 2026-09-30)

| fact | command |
|---|---|
| anonymous `app_update 244310` fails intermittently with "Missing configuration"; a retry succeeds | 5 runs in `docker.io/steamcmd/steamcmd:debian`, successes and failures interleaved |
| without `+@sSteamCmdForcePlatformBitness 64` only the 32-bit engine arrives | `ls /opt/srcds/bin` |
| with it, `bin/linux64/` holds the `*_srv.so` engine (13 files, 770 MB install) | `ls bin/linux64 \| wc -l`, `du -sh` |
| **no `srcds_linux64` in 244310**, public or `prerelease` branch: only `srcds_linux` (32-bit) and `srcds_run` | `find /opt/srcds -name 'srcds*'` |
| `srcds_linux64` ships in 243750 (the client *Source SDK Base 2013 Multiplayer*), which anonymous login refuses: "No subscription" (3 of 4 tries; the 4th "Missing configuration") | `app_update 243750` ×4 |

| `srcds_linux64` itself is a ~17 KB launcher: `dlopen(RTLD_NOW)` of `bin/linux64/libtier0_srv.so`, `libvstdlib_srv.so`, `dedicated_srv.so`, `dlsym("DedicatedMain")`, an optional `-wait_for_debugger` (polls `TracerPid:` up to 30 s), `DedicatedMain(argc, argv)`, `dlclose` ×3; 244310's 64-bit `dedicated_srv.so` exports `DedicatedMain` | `nm -D`, `objdump -d` of the client's launcher (read, never copied) |
| a clean-room 25-line C launcher with that contract builds and runs, but `dedicated_srv.so` then fails: **`libsteam_api.so: cannot open shared object file`** — 244310's 64-bit depot has 13 files, all `*_srv.so`; the 64-bit `libsteam_api.so`, `filesystem_stdio.so`, `libtier0.so`, `libvstdlib.so` it needs are only in 243750 (59 files in the client's `bin/linux64`) | scratch image `srcds-exp` (deleted), `ls bin/linux64` on both |

So the real-engine tier needs 243750's files (a logged-in download, or a dev copy of a local
install); a launcher alone does not close the gap, and a 32-bit engine cannot load SourceSharp's
64-bit `server.so`. The fake tier is unaffected.

## Backups and restore (Q14)

- The service's `Maintenance` worker writes `VACUUM INTO` copies to `Data.BackupPath`
  (`/data/backups` on the `data` PVC): `hourly-yyyyMMdd-HHmm.db` every `Maintenance.Backup`
  (1 h), the first of each day also as `daily-yyyyMMdd.db`; the newest `Data.HourlyBackups`
  (24) and `Data.DailyBackups` (7) are kept. The copy is taken on the single writer, so it
  is never half a transaction. Last backup and last error are on the Dashboard.
- **Restore** (the service must not be running, or it will keep writing the old file):
  ```
  kubectl -n descent scale statefulset/descent-service --replicas=0
  kubectl -n descent run restore --rm -it --image=busybox:1.37 --overrides='{"spec":{"volumes":[{"name":"data","persistentVolumeClaim":{"claimName":"data-descent-service-0"}}],"containers":[{"name":"restore","image":"busybox:1.37","stdin":true,"tty":true,"volumeMounts":[{"name":"data","mountPath":"/data"}]}]}}'
  # inside: cp /data/backups/<chosen>.db /data/host.db && rm -f /data/host.db-wal /data/host.db-shm
  kubectl -n descent scale statefulset/descent-service --replicas=1
  ```
  Game pods keep running meanwhile; when the service returns it adopts them (§7.4) and the
  gateway re-syncs its table on the next health attach. Unverified live so far.

## Upgrades (§12)

- `make images TAG=<sha> && make deploy TIER=…` rolls the service StatefulSet. On start the
  service applies EF Core migrations to `host.db` in place (`Database.Migrate`), then adopts
  every game pod whose name **and uid** match a non-terminal row (§7.4); a pod with no row is
  deleted. The gateway keeps relaying on its last route table while the service is away and
  syncs when its health stream re-attaches. Players see at most one `retry`.
- A model change without a migration fails `MigrationFacts` (build-time guard). Add one with
  `dotnet tool run dotnet-ef migrations add <Name> --project src/Host.Data --output-dir Migrations`.
- Confirm what runs by the version on `/healthz` (the image's commit), never by a green test run.

## Local mode (D-H13)

- `make local [LOCAL_DIR=bin/local]`: the real service as one process, loopback only, no
  Kubernetes and no gateway; it registers a local hub and writes `$(LOCAL_DIR)/local-host.json`
  (`service`, `instanceId`, `token`). Start the game by hand with the SDK's
  `-hostlocal <that file>`. A level instance writes `local-level-<id>.json` for a second
  game process. Stop the service by its PID (`ps -o args=` first).
- In-process: the SDK's `-hostlocal inproc` loads `SourceSharp.Host.Local` (dev builds only) and
  starts the same service inside the game process.
### The engine image with Host.Launcher (D-H11, measured 2026-09-30 at 24f84b5)

`make image-engine ENGINE_FILES=<dir>` builds 244310 (64-bit) + the overlay + steamcmd's
`steamclient.so` (at `~srcds/.steam/sdk64/`) + the NativeAOT launcher at `/opt/descent/launcher`.
`ENGINE_FILES` is a directory whose `bin/linux64/` holds the libraries 244310 lacks; it defaults to
the local Steam install of app 243750 (*Source SDK Base 2013 Multiplayer*). **For production that
directory must come from a licensed source** (the owner's Steam account); it is a build context,
never committed, never pushed anywhere but our own registry. Only `bin/linux64/*.so` is read from it,
copied no-clobber: 244310's own files win.

| fact | value | command |
|---|---|---|
| image build, cold 244310 download (805 MB, first steamcmd attempt succeeded) | 68 s, rc 0 | `make image-engine TAG=g2-b306441` |
| rebuild after a launcher change (244310 layer cached) | 39 s | `make image-engine TAG=g2-24f84b5` |
| image size | 1.04 GB | `podman images localhost:30500/descent-engine` |
| libraries the overlay added to 244310's 13 | 43 (incl. `libsteam_api.so`, `filesystem_stdio.so`, `libtier0.so`, `libvstdlib.so`) | build log `overlay: 43 libraries added` |
| NativeAOT launcher | 12,893,600 bytes, 0 publish warnings | `deploy/launcher-check.sh` |
| the launcher loads the engine: `dedicated_srv.so` runs, shader API, filesystem, VPKs | yes | engine log |
| **244310 ships no server library** (`find /opt/srcds -name 'server*.so'` is empty; `hl2mp/` has no `bin/`) | `-game hl2mp +map dm_lockdown`: VPK hashes load, then SIGSEGV (exit 139) ~3 s in | podman run, TTY |
| `/healthz` on :5012 while the engine runs (image g2-8adc75d, polled every 0.2 s through a port-forward) | `{"ok":true,"relay":{"up":true,"peers":0},"engine":{"state":"running"}}` from 11 ms until the SIGSEGV at ~3.6 s | `/tmp/lane-g/engine-health.sh` |
| the sharp repo's test mod (debug build mounted at `/game`, `-game /game/mods/test`) | exit 1 right after `Using shader api`, no message (with or without its `steam.inf`) | podman run, TTY |

The engine's base directory is the directory of `argv[0]` (observed), so the
launcher passes `<EngineDir>/srcds_linux64` as argv[0] (the file need not exist). A running map needs
a mod with a 64-bit server library: that is the mod image's job, and the test mod's exit 1 is the next
thing to chase (H0a, lane I).

## H0a: SourceSharp as a dedicated server in a container (b50f9c7)

- Result: the sharp repo's test mod runs `dm_lockdown` under Host.Launcher in the engine image;
  the C# server initialises, the Steam game server activates (insecure), the server hibernates
  with no players. **4.9 s** start → map (4.887 / 4.870 s), **216.5 MB** RSS idle (two runs).
- Found on the way (each with its evidence in `docs/mod-image.md`): `|appid_N|` search paths
  need the Steam client; the dedicated engine loads `server_srv.so`; the mod's natives need
  glibc 2.43 (runtime base now Ubuntu 26.04); the game's CoreCLR needs ICU (`libicu78`
  installed); Bootstrap needs `SOURCESHARP_MOD` / `SOURCESHARP_MODS_YAML` (the entrypoint sets them).
- Bare `hl2mp` still crashes: 244310 ships no server library, so the game DLL factory is null
  (`CModAppSystemGroup::Create` → address 0, gdb). Expected, not a defect of the image.
- The launcher ignored SIGTERM with the engine running, as designed; the container was
  SIGKILLed at podman's stop timeout.
- Not yet measured: RSS with four bots; a client join through the gateway (needs `TIER=real`
  on k3s and a real client — H0b/H0c).

## H0b, local: a real handshake through gateway → gRPC relay → engine (758308d)

- Setup (podman on this box, no cluster): the engine image with the dev mod image's files
  (`+sv_lan 1`, `-insecure`), relay published on loopback; the gateway as a local process with
  its route set by hand through GatewayControl (no service); a scripted client sending the real
  `A2S_GETCHALLENGE` and `C2S_CONNECT` bytes of `docs/net-protocol.md` §2.
- Result: `S2C_CHALLENGE` (39 bytes) and `S2C_CONNECTION` (20 bytes) come back identical on the
  direct and relayed paths; the engine logs `Client "h0b-probe" connected (127.1.0.1:29005)` —
  the per-client loopback address of D-H10 — versus the podman NAT address when direct.
- Relay cost: **+0.1 ms p50** (10.05 vs 9.94 ms; ~10 ms is the engine's frame), p99 within 0.2 ms.
- The gateway's per-source-IP limit (`HandshakesPerSecond` 2) answered 33–34 of 50 challenges
  sent at 50/s: working as designed; raise it only for measurements.
- Not yet: through the k3s LoadBalancer (needs the service's lifecycle, which needs the game on
  Host.Sdk — see below), a real Steam client (H0c: Steam auth with `sv_lan 0`), tcpdump captures
  (need root).
- **The real tier's lifecycle waits on the game:** the sharp repo's game does not use Host.Sdk
  yet, so a real game pod never calls Booting / MapReady and would be failed at the boot
  timeout. That integration is the game plan's work (plan §13, §14.9).

## The real tier on k3s, first run (f938975)

- `make deploy TIER=real MOD_IMAGE=localhost:30500/descent-mod:dev-test MOD_NAME=test` with
  `Mod.TownMap=dm_lockdown`: the service creates the hub pod through KubernetesInstanceHost
  (token Secret owned by the pod; mod init container; one game container = launcher + relay +
  engine), the pod goes **Ready** on `/healthz :5012`, the engine loads `dm_lockdown` through the
  C# server and hibernates (RSS 214 MB).
- Then, as expected, the boot timeout (120 s) fails it: the game does not call Booting / MapReady
  because it does not use Host.Sdk yet. The manager deletes the pod, re-creates the hub with
  backoff and stops after five failures in five minutes (`hub.recreate_stopped` audited).
- Found and fixed on the way (each reproduced with podman from the pod's exact arguments):
  the first hub pod had an empty mod image (the env arrived with the second rollout: adopted,
  then boot-timed-out — the failure path worked); every later pod exited 1 at managed LoadSide
  because the pod did not set `DESCENT_MOD` (`unknown mod 'descent' (known: test)`), fixed in
  08c48d8; `Instances.ModImage` is an object, now set by `make deploy MOD_IMAGE=…`.
- Undeployed afterwards (`make undeploy`): nothing left cycling.

## Live gates on k3s, TIER=fake (2026-09-30)

Run as `KUBECONFIG=… TIER=fake live/hN.sh` after `make images deploy TIER=fake` and `make build`
(the gates drive clients with `bin/Debug/Host.LiveDriver`). Each writes
`live/out/<gate>-<tier>-<tip>-<time>.log` ending in `gate=… tip=… rc=… fails=…`.

| gate | tip | result |
|---|---|---|
| `live/h4.sh` (§7) | 8fa8e42 | rc=0: three clients through the LoadBalancer on three levels with three hashes; a deleted level pod is Crashed with its sweep audited and no lease left; `rollout restart` of the service adopts every live instance with the same pod uid |
| `live/h7.sh` (§10) | bcfc604 | rc=0: a party goes 1 → 2 → town → back with inventories and the Live count (live − Reserve) unchanged at each hop, back on the same level hash (D-H12); a solo client gets another hash; a death leaves every carried item Corpse-owned right after the RPC; the deleted level's sweep puts exactly those items in Lost & Found; reclaim returns the item and takes the fee; each client keeps one address |
| `live/h9.sh` (§12, one hour, four parties) | 353cd12 | rc=0: 1,156 party cycles (hub → 1 → 2 → town) with none failed; minted 316,614 − terminal 314,078 = live 2,536; reconcile finds nothing; every Crashed row audited; no orphan pod. Service RSS 260 → 298 MiB over the hour: within the gate's 25 %, but a steady climb, not flat — watch it in a longer soak |

Defects the gates found, each fixed with a fact that failed first:

- A stopping service kept judging heartbeats after its Connect streams ended and reaped the
  levels the next service was about to adopt (c82bd65).
- Every read took SQLite's write lock (EF's `BeginTransaction` is `BEGIN IMMEDIATE`): reads held
  open stalled the writer for the 30 s busy timeout, "database is locked" (0a77b5e).
- A game's PlayerJoined could arrive before the gateway's SessionOpened for the same peer; the
  session stayed unbound, so a party trip could not flip that member's route (bcfc604).
- A route SyncTable carries to a new gateway for a client that never comes back never expired,
  so its session row stayed open (0e85d39).
- Pods booting together with a new rules module were all told Send; the first commit dropped the
  announcement and the others' uploads were refused `not_announced`, so they never sent MapReady
  and failed at the boot timeout (d2fe4c9).
- SyncTable rebuilt routes from the registry, but a trip's SetRoute reached the session row only
  when the gateway later reported the move; a sync in between (stale versions from concurrent
  calls) sent in-flight members back to their old level. Routes are written first (78f2912), and
  a route to another backend clears the old backend's peer (353cd12: keeping it made a party
  member's join match the other member's row and be refused single_presence).
