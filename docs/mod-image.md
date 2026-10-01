# The mod image: contract

The mod image carries the game's built files into a game pod (plan D-H6). It is
**built by the sharp repo** (`source-sdk-sharp`), never here, and named by runtime
config: `Instances.ModImage = <registry>/<name>:<tag>` (optionally `@<digest>`),
editable on the admin Config page. Changing it applies to new instances; the hub is
drained and re-created to pick it up. The digest each instance actually ran is
recorded on its `instances` row.

## What the image must contain

| path / property | what | why |
|---|---|---|
| `/game/mods.yaml` | the sharp repo's mod table, with an entry whose key is `Mod.Name` (`descent`) | the entrypoint reads `engine.fd_limit` and `env` from it; SourceSharp's Bootstrap reads it to find the mod's assemblies |
| `/game/bin/linux64/` | `server.so`, `libmanaged_host.so` and the other native pieces of the game directory | on `LD_LIBRARY_PATH` after the engine's own `bin/` |
| `/game/bin/managed/` | the managed assemblies, including the mod's server assemblies and `SourceSharp.Host.Sdk` | the game's server side talks to the host only through the SDK |
| `/game/bin/dotnet/` | the private .NET runtime the game hosts | the engine image carries no .NET |
| `/game/mods/<Mod.Name>/` | the mod's game directory (`gameinfo.txt`, `maps/`, …) | `-game /game/mods/<Mod.Name>`; levels are overlaid at `maps/pool/` |
| `/bin/sh` and `cp` | a shell and coreutils | the init container runs `sh -c "cp -a /game/. /out/"`; a `busybox` base is enough |
| label `org.sourcesharp.mod=<Mod.Name>` | the mod the image is for | the service refuses to create a pod from an image whose label differs |

The rules module the host replays with is **not** read from this image: the game
server's SDK announces it at first connect and uploads it only if the host asks
(D-H9).

## What a dedicated run needs from it (H0a, measured 2026-09-30)

The SourceSharp test mod from the sharp repo's `bin/release/` booted to a running map in the
engine image only after these, found one at a time (docs/ops.md "H0a"):

| requirement | why | seen |
|---|---|---|
| `gameinfo.txt` mounts content with `\|all_source_engine_paths\|`, never `\|appid_243750\|` | `\|appid_N\|` is resolved through the Steam client, which a pod does not have; the engine exits 1 right after "Using shader api" with no message | exit 1 → VPKs mounted after the rewrite |
| `bin/linux64/server_srv.so` beside `server.so` (a copy or a symlink) | the dedicated engine (`engine_srv.so`) loads `server_srv.so`; without it the game DLL factory is null and `CModAppSystemGroup::Create` calls address 0 | SIGSEGV (gdb backtrace) → "server_srv.so loaded" |
| native libraries built for glibc ≤ the engine image's (Ubuntu 26.04, glibc 2.43) | the engine image's base must be at least the build host's | "GLIBC_2.43 not found" on bookworm |

The engine image supplies the rest: ICU for the game's CoreCLR, and `SOURCESHARP_MOD` /
`SOURCESHARP_MODS_YAML` (what `source#` tells Bootstrap) from the entrypoint.

## What it must not contain

TF2 content (a read-only volume filled by a `steamcmd` Job), the engine
(`srcds_linux64` is in this repo's engine image), secrets or tokens (each pod gets its
own token from a per-pod Secret).

## How a pod uses it

```
initContainers:
  - name: mod
    image: <Instances.ModImage>
    command: [sh, -c, "cp -a /game/. /out/"]
    volumeMounts: [{ name: game, mountPath: /out }]
```

The game container (this repo's engine image) mounts the same `game` volume at `/game`.
When the cluster supports the `image` volume source, the copy becomes a mount; this
contract does not change.

## A dev image by hand

Until the sharp repo publishes one (plan §14.1), `make mod-image-dev
SOURCE_SHARP_ROOT=../source-sdk-sharp` packs `bin/release/` of that checkout into
`<registry>/descent-mod:dev` on a `busybox` base with the label above, and reads nothing
but this contract.
