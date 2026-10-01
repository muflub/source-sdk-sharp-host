#!/bin/sh
# What source# does for a dedicated run, and no more (plan §7.1): the library path, the fd limit
# and env from mods.yaml, then exec Host.Launcher (plan D-H11) with the mod's args plus the pod's.
# The launcher serves the relay and runs srcds's DedicatedMain on its main thread.
# The mod files are on the `game` volume at /game (D-H6). REQUIRE_MOD=0 runs the bare engine
# (a stock game such as hl2mp, H0a) without a mod.
set -eu
GAME=${GAME_DIR:-/game}
ENGINE=${ENGINE_DIR:-/opt/srcds}
MOD=${DESCENT_MOD:-descent}
if [ -f "$GAME/mods.yaml" ]; then
  # engine.fd_limit from the mod's mods.yaml entry, if present (a flat grep: mods.yaml is ours and simple).
  fd=$(awk -v m="$MOD:" '$1==m{f=1;next} f&&/^[^ ]/{f=0} f&&$1=="fd_limit:"{print $2; exit}' "$GAME/mods.yaml" || true)
  [ -n "${fd:-}" ] && ulimit -n "$fd" 2>/dev/null || true
elif [ "${REQUIRE_MOD:-1}" != 0 ]; then
  echo "entrypoint: $GAME/mods.yaml missing: did the mod init container run? (REQUIRE_MOD=0 runs without a mod)" >&2
  exit 64
fi

# The dynamic loader reads LD_LIBRARY_PATH once, at exec: it must be set here, before the launcher
# starts, for the engine's own dlopen calls to find bin/linux64 and the mod's libraries.
export LD_LIBRARY_PATH="$ENGINE/bin/linux64:$ENGINE/bin:$GAME/bin/linux64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export TMPDIR=${TMPDIR:-/tmp/game} SOURCESHARP_DEVAPI_DIR=${SOURCESHARP_DEVAPI_DIR:-/tmp/devapi}
export LAUNCHER_EngineDir=${LAUNCHER_EngineDir:-$ENGINE}
# What source# tells SourceSharp's Bootstrap (src/launcher): which mod, and where mods.yaml is.
# Without it Bootstrap looks two levels above bin/managed, i.e. inside the mod folder (H0a).
export SOURCESHARP_MOD=${SOURCESHARP_MOD:-$MOD} SOURCESHARP_MODS_YAML=${SOURCESHARP_MODS_YAML:-$GAME/mods.yaml}
mkdir -p "$TMPDIR" "$SOURCESHARP_DEVAPI_DIR"
cd "$ENGINE"
exec /opt/descent/launcher "$@"
