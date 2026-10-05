#!/bin/sh
# What source# does for a dedicated run, and no more (plan §7.1): the library path, then the
# mod's resolved dedicated settings (fd limit, env, args), then exec Host.Launcher (plan D-H11)
# with the mod's args plus the pod's. The launcher serves the relay and runs srcds's
# DedicatedMain on its main thread.
# The mod files are on the `game` volume at /game (D-H6). REQUIRE_MOD=0 runs the bare engine
# (a stock game such as hl2mp, H0a) without a mod.
set -eu
GAME=${GAME_DIR:-/game}
ENGINE=${ENGINE_DIR:-/opt/srcds}
MOD=${SOURCESHARP_MOD:-tf2_descent}
# The mod's settings as `source# -resolve <id> -dedicated` writes them (docs/mod-image.md):
# defaults and engine.dedicated already merged, so nothing here re-implements the inheritance.
# The writer emits only plain scalars, in this shape:
#   fd_limit: <int>   restart_exit_code: <int>   args: [] | args: then "  - <arg>" lines
#   env: {} | env: then "  KEY: <value>" lines
#   browser: {} | browser: then indented lines (the game's server-browser entry; skipped here)
# restart_exit_code is source#'s own (local restarts); pods are restartPolicy Never.
SETTINGS="$GAME/mods/$MOD/mod.dedicated.yaml"
if [ -f "$SETTINGS" ]; then
  pod_args=$#
  section=
  while IFS= read -r line || [ -n "$line" ]; do
    # browser: (plan_steam_auth S-18) is the game's server-browser entry, read by the game from mods.yaml,
    # never by this script: everything indented under it is skipped until the next top-level key.
    if [ "$section" = browser ]; then
      case "$line" in ' '*) continue ;; esac
    fi
    case "$line" in
      ''|'#'*) continue ;;
      '  - '*)
        [ "$section" = args ] || { echo "entrypoint: $SETTINGS: list item outside args: '$line'" >&2; exit 65; }
        set -- "$@" "${line#  - }" ;;
      '  '*': '*|'  '*:)
        [ "$section" = env ] || { echo "entrypoint: $SETTINGS: map entry outside env: '$line'" >&2; exit 65; }
        entry=${line#  }
        key=${entry%%:*}
        case "$entry" in *': '*) value=${entry#*: } ;; *) value= ;; esac
        case "$key" in
          ''|[0-9]*|*[!A-Za-z0-9_]*) echo "entrypoint: $SETTINGS: '$key' is not an environment name" >&2; exit 65 ;;
        esac
        export "$key=$value" ;;
      'fd_limit: '*) section=; ulimit -n "${line#fd_limit: }" 2>/dev/null || echo "entrypoint: could not raise the fd limit to ${line#fd_limit: }" >&2 ;;
      'restart_exit_code: '*) section= ;;
      'args: []') section= ;;
      'args:') section=args ;;
      'env: {}') section= ;;
      'env:') section=env ;;
      'browser: {}') section= ;;
      'browser:') section=browser ;;
      *) echo "entrypoint: $SETTINGS: unexpected line '$line' (is the mod image's source# newer than this entrypoint?)" >&2; exit 65 ;;
    esac
  done < "$SETTINGS"
  # The mod's args were appended after the pod's; rotate the pod's to the end so they win.
  i=0
  while [ "$i" -lt "$pod_args" ]; do a=$1; shift; set -- "$@" "$a"; i=$((i + 1)); done
elif [ "${REQUIRE_MOD:-1}" != 0 ]; then
  echo "entrypoint: $SETTINGS missing: did the mod init container run, and does the mod image carry source#'s resolved settings? (REQUIRE_MOD=0 runs without a mod)" >&2
  exit 64
fi

# The dynamic loader reads LD_LIBRARY_PATH once, at exec: it must be set here, before the launcher
# starts, for the engine's own dlopen calls to find bin/linux64 and the mod's libraries.
export LD_LIBRARY_PATH="$ENGINE/bin/linux64:$ENGINE/bin:$GAME/bin/linux64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export TMPDIR=${TMPDIR:-/tmp/game} DEBUG_API_DIR=${DEBUG_API_DIR:-/tmp/debug-api}
export LAUNCHER_EngineDir=${LAUNCHER_EngineDir:-$ENGINE}
# What source# tells SourceSharp's Bootstrap (src/launcher): which mod, and where mods.yaml is.
# Without it Bootstrap looks two levels above bin/managed, i.e. inside the mod folder (H0a).
export SOURCESHARP_MOD=${SOURCESHARP_MOD:-$MOD} SOURCESHARP_MODS_YAML=${SOURCESHARP_MODS_YAML:-$GAME/mods.yaml}
mkdir -p "$TMPDIR" "$DEBUG_API_DIR"
cd "$ENGINE"
# ENTRYPOINT_LAUNCHER: a fact replaces the launcher to see what it would be given.
exec "${ENTRYPOINT_LAUNCHER:-/opt/sourcesharp/launcher}" "$@"
