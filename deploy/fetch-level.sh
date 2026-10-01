#!/bin/sh
# Init container of a level pod (plan §7.1, Q18): fetch one linked level's files from
# the service's internal endpoint (namespace-only, never published) into maps-overlay.
# .bsp for the server, .nav3d and .map2d beside it; none of them is downloadable by a client.
set -eu
level=${1:?usage: fetch-level.sh <mod>-<depth>-<hash>}
base=${DESCENT_INTERNAL_MAPS:?DESCENT_INTERNAL_MAPS unset}
out=${OUT_DIR:-/maps-overlay}
mkdir -p "$out"
for ext in bsp nav3d map2d; do
  curl -fsS --retry 5 --retry-delay 1 -o "$out/$level.$ext.part" "$base/internal/maps/$level.$ext"
  mv "$out/$level.$ext.part" "$out/$level.$ext"
done
ls -l "$out"
