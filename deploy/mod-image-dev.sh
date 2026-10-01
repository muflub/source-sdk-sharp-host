#!/bin/sh
# Dev only (plan §14.1): pack a sibling sharp checkout's bin/release/ into a mod image that meets
# docs/mod-image.md. Not a build root: it copies built files and nothing else, and never writes
# into the sharp checkout.
#
# Until the sharp repo ships a dedicated-ready layout itself, this applies the two fixups H0a found
# (docs/mod-image.md "What a dedicated run needs"), to its COPY only, and says so loudly:
#   - gameinfo.txt: |appid_N| → |all_source_engine_paths| (no Steam client in a pod)
#   - bin/linux64/server_srv.so → server.so (the dedicated engine loads *_srv)
set -eu
root=${1:?usage: mod-image-dev.sh <source-sdk-sharp root> <image ref>}
ref=${2:?usage: mod-image-dev.sh <source-sdk-sharp root> <image ref>}
mod=${DESCENT_MOD:-descent}
tool=${IMAGE_TOOL:-podman}
game="$root/bin/release"
for p in mods.yaml bin/linux64 bin/managed bin/dotnet "mods/$mod"; do
  [ -e "$game/$p" ] || { echo "mod-image-dev: $game/$p missing (build the sharp repo's release first)" >&2; exit 1; }
done
ctx=$(mktemp -d); trap 'rm -rf "$ctx"' EXIT
# -h: symlinks (mods/<mod>/bin -> ../../bin, gameinfo.txt -> the repo) are copied as files.
(cd "$game" && tar -chf - --exclude="mods/$mod/console.log" --exclude="mods/$mod/download*" --exclude="mods/$mod/demoheader.tmp" .) | (mkdir -p "$ctx/game" && tar -xf - -C "$ctx/game")

gi="$ctx/game/mods/$mod/gameinfo.txt"
if grep -q '|appid_' "$gi"; then
  sed -i 's/|appid_[0-9]*|/|all_source_engine_paths|/g' "$gi"
  echo "mod-image-dev: WARNING rewrote |appid_N| search paths in the image's gameinfo.txt; the sharp repo should ship this (docs/mod-image.md)" >&2
fi
for d in "$ctx/game/bin/linux64" "$ctx/game/mods/$mod/bin/linux64"; do
  if [ -f "$d/server.so" ] && [ ! -e "$d/server_srv.so" ]; then
    ln -s server.so "$d/server_srv.so"
    echo "mod-image-dev: WARNING added $d/server_srv.so -> server.so; the sharp repo should ship it" >&2
  fi
done

printf 'FROM docker.io/library/busybox:1.37\nLABEL org.sourcesharp.mod=%s\nCOPY game /game\n' "$mod" > "$ctx/Containerfile"
"$tool" build -t "$ref" "$ctx"
"$tool" push --tls-verify=false "$ref"
