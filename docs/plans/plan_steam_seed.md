# Steam files seeded per node: engine runtime and TF2 content from one anonymous app

Status: **draft for review** (2026-09-30). Amends plan_host D-H6, D-H11, H1d, H1e, §7.1 and
open question 7 once ruled. Lane I (instances) owns it; it touches `Host.Abstractions`
(`PodSpec`), so that change is serialised like the proto.

## 0. Findings (measured 2026-09-30, before any design)

Manifests listed anonymously with DepotDownloader 3.4.0 (`-manifest-only`, nothing downloaded;
zip sha256 `a999dec6…7a53`), for each of the three dedicated-server apps, Linux, 64-bit:

```
DepotDownloader -app <app> -os linux -osarch 64 -manifest-only -dir m<app>
```

| app | depot (manifest date) | files | bytes on disk |
|---|---|---|---|
| 232250 TF2 Dedicated Server | 232250 content (2026-07-16) | 1056 | 14,752,673,735 |
| | 232256 Linux binaries (2026-07-03) | 47 | 156,284,679 |
| 232370 HL2DM Dedicated Server | 232370 content (2026-08-06) | 1067 | 811,762,229 |
| | 232376 Linux binaries (2026-07-09) | 42 | 123,216,102 |
| 244310 SDK Base 2013 DS | 244313 Linux binaries (2025-02-18) | 37 | 75,244,858 |

What each Linux binary depot has from the list docs/ops.md says 244310 lacks:

| file | 244313 (SDK) | 232376 (HL2DM) | 232256 (TF2) |
|---|---|---|---|
| `srcds_linux64`, `srcds_run_64` | no | no | **yes** |
| `bin/linux64/libsteam_api.so` | no | no | **yes** |
| `bin/linux64/filesystem_stdio.so`, `libtier0.so`, `libvstdlib.so` | no | no | no |
| `bin/linux64/*_srv.so` (13 engine libraries) | yes | yes | yes |

The 64-bit engine libraries are **three different builds**. By SHA1 from the manifests:
`dedicated_srv.so` is `156c69b4…` in 244313 and `d91aaa47…` in both 232256 and 232376.
TF2 and HL2DM share 34 files but differ in 7, among them `bin/linux64/engine_srv.so` and
`libtier0_srv.so`. 244313 shares **0 of 13** `bin/linux64` files with either.

Conclusions:

1. **HL2DM (232370) adds no engine files.** It lacks the same files 244310 lacks. Its content
   adds only `hl2mp/` (§0.1); its `hl2/` is already inside TF2's.
2. **TF2's binaries depot (232256, 156 MB, anonymous) is a complete 64-bit dedicated server:**
   launcher, engine and `libsteam_api.so`. TF2 64-bit servers run from it in production. So
   the libraries the ops note listed beyond `libsteam_api.so` (`filesystem_stdio.so`,
   `libtier0.so`, `libvstdlib.so`) are not needed by *this* engine build. For the 244310
   build they were only ever inferred from 243750's listing; the measured failure was
   `libsteam_api.so` alone.
3. **One anonymous `app_update 232250` gives both the engine runtime and the TF2 content,**
   as one consistent build with one `buildid`. That is what makes a per-node seed attractive:
   one download, one version, no licensed overlay (the D-H11 `ENGINE_FILES` from 243750
   goes away in production).
4. **The open risk moves.** We would run SourceSharp's `server_srv.so`, built against the
   SDK 2013 tree, on the TF2 engine build (2026-07) instead of 244310's (2025-02). Clients run
   243750's client engine, so the network protocol must also match. Neither is measured, and
   both are gates in §2.

### 0.1 HL2 content: HL2DM's download against TF2's

Measured by downloading only the VPK directory files (`DepotDownloader -depot <d> -filelist`,
regex `^hl2/.*_dir\.vpk$`, about 1 MB) and comparing entries by path, CRC and size (scratch
`vpk.py`, VPK v2 tree parser):

| VPK set | TF2 DS entries | HL2DM DS entries | same CRC | differ | only TF2 | only HL2DM |
|---|---|---|---|---|---|---|
| `hl2_misc` | 18,946 | 18,574 | 18,574 | 0 | 372 | 0 |
| `hl2_textures` | 5,032 | 5,032 | 5,032 | 0 | 0 | 0 |
| `hl2_sound_misc` | 2,970 | 2,970 | 2,970 | 0 | 0 | 0 |
| `hl2_sound_vo_english` | 2,553 | 2,553 | 2,553 | 0 | 0 | 0 |

TF2's 372 extra entries are exactly the 372 files HL2DM ships loose under `hl2/` (`resource/`,
`scripts/` for weapons, vehicles and talker). So **TF2's `hl2/` is a superset of HL2DM's,
with identical bytes**. It includes the HL2 models: 2,201 `.mdl`, among them combine soldier,
classic zombie, antlion, headcrab, vortigaunt, strider, citizens, `props_c17` (239) and
`props_combine` (170).

What only HL2DM (232370) adds:

| folder | contents | bytes |
|---|---|---|
| `hl2mp/hl2mp_pak` | 344 entries: 179 materials, 81 models (player and human animation and gesture models), 42 scripts | 11.3 MB |
| `hl2mp/maps/` | 8 DM maps (`dm_lockdown` … `halls3`) | 172.7 MB |
| `hl2_complete/` | a stub: HUD `.res`, fonts, a 50 KB VPK | 0.6 MB |

Neither anonymous server carries the full HL2 campaign or the Episodes: `hl2_complete` is a
stub there, and there is no `episodic/` or `ep2/`. Combining is a matter of search paths: the
mod's `gameinfo.txt` lists `tf/`, `hl2/` and, if wanted, `hl2mp/` by explicit path
(`|all_source_engine_paths|` needs the Steam client, docs/mod-image.md). HL2DM's engine
binaries (232376) are never used.

**Ruling proposed:** a build is the **set** of apps the mod asks for, keyed by each app's
buildid, e.g. `232250-<b1>+232370-<b2>`. The default set is `{232250}`. `232370` is added only
when the mod needs `hl2mp` player animations or DM maps; it costs about 1 GB more per node.

## 1. Design

### 1.1 Layout on each node

```
/var/lib/descent/steam/                 hostPath, written only by the seeder
  builds/232250-<b1>[+232370-<b2>]/     a complete, never-modified install (engine, hl2/, tf/[, hl2mp/])
    .sdk64/steamclient.so               from the seeder's own steamcmd (64-bit)
    .seed.json                          { apps: [{ app, buildid, depots+manifest ids }], sha of the file list, finished }
  .staging-<ts>/                        work in progress; never mounted, never reported
```

- **A build is a set of apps** (`Seed.Apps`, default `[232250]`; §0.1). A second app is
  installed into the same directory after the first, so `hl2mp/` sits beside `tf/` and
  `hl2/`; the two apps' shared `hl2/` and `platform/` files must agree, and the seeder checks
  that 232250 wins wherever they overlap. A change in any app's buildid is a new build.
- **A build directory is immutable.** An update copies the newest build to `.staging-<ts>`
  (`cp -a --reflink=auto`), runs `steamcmd app_update 232250 validate` there, and reads
  `buildid` from `steamapps/appmanifest_232250.acf`. If the buildid is new, it renames
  staging to `builds/232250-<buildid>`; if not, it discards staging. A running server never
  sees a file change under it: `steamcmd` rewriting a mapped `.so` or an open VPK is the
  failure this layout exists to prevent.
- Disk per node: about 15 GB per build, 30 GB while an update runs, and two builds kept by
  default (§1.4). A node without about 50 GB free under `/var/lib` refuses to seed and
  reports why.

### 1.2 The seeder (DaemonSet `steam-seed`, namespace `descent-seed`)

- `Host.Seeder`, a small C# exe (self-contained, on the `steamcmd/steamcmd:debian` base),
  runs `steamcmd` as a child process with the existing retry rule for "Missing
  configuration". Steps: start → seed if no build exists → poll every `Seed.Interval` (1 h)
  with `app_info_update` / `app_info_print` and update when the public buildid moves.
- It reports to a namespaced ConfigMap `seed-<nodeName>` (node name from the downward API):
  builds present, the one in progress, bytes, last error, free disk. It never touches Node
  objects, so it needs **no cluster-wide RBAC** (plan_host H1e's rule for the service holds
  for the seeder as well).
- The namespace is separate because the seeder mounts `hostPath` read-write, which Pod
  Security `baseline` forbids. `descent-seed` is labelled `privileged`; the game namespace is
  not.
- `SEED_IMPORT=<dir>` mode (dev and the fake tier): import an existing install, or a
  synthetic tree with a hand-written `appmanifest`, as a build instead of downloading. This
  lets the local k3s box reuse a TF2 DS it already has, and lets `TIER=fake` exercise update,
  pin and GC with no Steam at all.

### 1.3 Game pods read it through the existing `content` claim

- One cluster-wide **hostPath PersistentVolume** (`/var/lib/descent/steam`, `ReadOnlyMany`)
  bound to the `content` PVC. Each node resolves the path locally, so game pods keep a
  `persistentVolumeClaim` volume (allowed under `restricted`) and `GamePodBuilder` keeps its
  claim. Kubernetes documents hostPath PVs as single-node only; on k3s they behave per node,
  and gate S3 proves this on a two-node k3d cluster before we rely on it. The fallback is a
  direct `hostPath` volume in the pod, with the game namespace relaxed to `privileged`.
- `MountSpec` gains `SubPath`. The game container mounts:
  - `builds/232250-<b>` → `/opt/srcds` (ro): the engine root, replacing the image's copy;
  - `builds/232250-<b>/tf` → `/game/content/tf` (ro): content where the mod's `gameinfo.txt`
    expects it today.
- `game-entrypoint.sh` links `~/.steam/sdk64/steamclient.so` to the build's `.sdk64` copy.
  It **refuses to start (exit 65)** when the build's `.seed.json` buildid differs from
  `DESCENT_STEAM_BUILD`, so a wrong mount fails loudly, never silently.
- The **engine image becomes thin**: Ubuntu 26.04, the system libraries, `Host.Launcher`,
  and the scripts. No steamcmd stage, no overlay, no `ENGINE_FILES`. The launcher stays: it
  is the D-H10 relay and the Interpose hooks, not only a replacement for `srcds_linux64`.

### 1.4 Which build a pod gets (the service)

- `Instances.SteamBuild` is runtime config, like `ModImage`: a pinned buildid, edited on the
  admin Config page. **Default: pinned, never `auto`.** Valve updates 232250 for TF2's
  reasons, and an engine update can break our `server_srv.so`. The seeder downloads new
  builds ahead of time; the admin promotes one after a canary instance boots on it (the
  room-pack activate / roll-back model, §9.4).
- The service reads the `seed-*` ConfigMaps (a namespaced Role in `descent-seed`) and gives
  each game pod required node affinity `kubernetes.io/hostname In [nodes reporting
  <pinned> ready]`. `PodSpec` gains that field. If no node has the build, the create is
  refused with a reason (Dashboard and audit), rather than a pod left Pending.
- **The build is recorded on the instance row** (`steam_build`, EF migration) next to
  `ModImageDigest`, so a crash is attributed by mod digest **and** engine build.
- GC: the seeder keeps the pinned build, the newest build, and any build a pod on its node
  still mounts (pods carry label `descent/steam-build`; the seeder lists pods by
  `spec.nodeName` through a namespaced Role). It deletes nothing else. A build in use is
  never deleted: that's a fact and a gate.
- Admin: a **Steam seed** panel showing per node the builds present, which is pinned,
  progress, disk and last error, with actions: promote a build to pinned, roll back, and
  re-seed a node.

## 2. Gates before anything is built (measure first; checkpoint after)

Each gate runs on this box with podman, once no other worktree's heavy run is active (memory:
one heavy run per box). Each has a negative control.

| gate | question | how | negative control |
|---|---|---|---|
| **S0a** | Does the test mod reach its map on 232256's engine with **no overlay**? | download depot 232256 only (`-depot 232256`, 156 MB); the H0a run with `ENGINE_DIR` there; record the libraries loaded (`/proc/<pid>/maps`) | remove `bin/linux64/libsteam_api.so` → the old `cannot open shared object file` |
| **S0b** | Fallback: does 244310 run with only 232256's `libsteam_api.so` added? | the same run, 244313 + that one file | none added → fails as in ops.md |
| **S0c** | Plan_host open question 7: does `gameinfo.txt` mount TF2 content from the DS depot? | full 232250 (15 GB, once); a map using a TF2 asset; `path` in console lists the `tf/` VPKs | content unmounted → asset missing |
| **S0d** | Does the engine write into its own directory? | run with `/opt/srcds` read-only; any `EROFS` in the log or strace | read-write run shows which file it writes |
| **S0e** | Can a real 243750 client join a server running the TF2 engine build? | waits on H0c (a real client through the gateway); until then, compare `protocol`/version strings from `version` on both | the 244310 build as baseline |

**Checkpoint (user):** choose the engine source from the results:
(A) 232250 for both engine and content (S0a, S0c, S0e pass);
(B) 244310 engine + 232256's `libsteam_api.so` + 232250 content, all seeded (S0a fails,
S0b passes);
(C) keep the overlay image and seed content only.
§1 holds for all three; only what goes into a build directory changes.

## 3. Steps (one commit each; facts first)

1. **Record the findings.** Add §0's tables to `docs/ops.md`; correct the "only in 243750"
   line; update the memory note.
2. **Abstractions.** `MountSpec.SubPath`; `PodSpec` required hostname affinity. Facts for
   `KubernetesInstanceHost`'s mapping of both, plus `FakeInstanceHost`.
3. **Seed model (`Host.Instances`).** Parse `appmanifest_*.acf` (VDF) and `.seed.json`;
   pick a build; GC choice. Facts: the keep set (pinned ∪ newest ∪ in-use), a build in use
   never chosen for deletion, a half-written staging dir never reported, an unreadable
   `.seed.json` is not ready (never "pass").
4. **`Host.Seeder`.** steamcmd runner with retries; staging → rename; report ConfigMap;
   import mode. Facts with a fake steamcmd (a script that writes a tree and an appmanifest),
   including a kill mid-update leaving only staging, which the next start discards.
5. **Service.** `Instances.SteamBuild` config, the ConfigMap reader, node affinity in
   `GamePodBuilder`, refusal when no node is ready, the `steam_build` column + migration,
   `DESCENT_STEAM_BUILD` env and pod label. Facts: pinned build → affinity lists exactly the
   ready nodes; none ready → refused with a reason; row records the build.
6. **Entrypoint.** The buildid check (exit 65) and the `sdk64` link. A fact reads the
   script; S1 proves it.
7. **Manifests.** `deploy/k8s/base/steam-seed.yaml` (namespace, DaemonSet, RBAC, hostPath
   PV, `content` PVC re-pointed); the local overlay imports an existing install. **Delete**
   `content-fill` in a separate commit.
8. **Admin.** The Steam seed panel and the promote / roll-back actions, with audit rows.
9. **Thin engine image.** New `Dockerfile.engine` without steamcmd or overlay, and the
   `image-engine` target without `ENGINE_FILES`. Then, in a separate commit, delete the overlay
   stage, the variable and its ops text (for option A or B; option C keeps them).
10. **Plan_host amendments** (D-H6, D-H11, H1d, H1e, §7.1, Q7) and the CLAUDE.md "the
    engine" row: the engine comes from the seeded 232250 build, not from an image.

## 4. Live gates

- **S1 (`TIER=fake`, k3d, 2 nodes).** Seeder in import mode with synthetic builds `b1`, `b2`.
  Pin `b1` → pods land only on nodes reporting `b1`; seed `b2` on one node and promote → new
  pods go only there, old pods keep running on `b1`; GC never removes `b1` while a pod mounts
  it (the seeder's audit counts > 0 in the positive arm); after the last `b1` pod is reaped,
  `b1` is collected. Kill the seeder mid-import → no `.staging-*` reported, and the next
  start cleans it.
- **S2 (`TIER=real`, local k3s).** A real `app_update 232250` on one node; the test mod's
  map runs from the seeded build with the thin engine image; `/healthz` and the row show the
  buildid.
- **S3 (k3d, 2 nodes).** The hostPath PV resolves per node: a marker file that differs per
  node is read back from a pod on each. If this fails, switch to the direct hostPath fallback
  of §1.3.

## 5. Not decided here

- Whether to pre-seed a new node before it takes game pods (a taint the seeder removes).
  That needs cluster-wide RBAC for Node, so it stays off until a multi-node production
  cluster exists. The affinity already keeps pods off unseeded nodes.
- Steam's rate on N nodes downloading 15 GB at once. If it bites, one node seeds and the
  others pull from it over the cluster network.
