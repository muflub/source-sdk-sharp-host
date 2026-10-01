#!/bin/bash
# Host.Launcher out of process (plan D-H11): (1) the NativeAOT publish produces a native exe (size
# recorded); (2) SIGTERM does not stop the launcher while its engine runs. The "engine" is libc's
# sleep(unsigned) called as DedicatedMain(argc, argv): with argv0 + 3 args it sleeps 4 s. Needs clang
# and zlib (NativeAOT's link step). Finds the repo from its own location.
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}
OUT=${OUT:-$(mktemp -d)}
mkdir -p "$OUT"
exe=$OUT/SourceSharp.Host.Launcher

start=$(date +%s)
MSBUILDDISABLENODEREUSE=1 "$DOTNET" publish "$ROOT/src/Host.Launcher/Host.Launcher.csproj" -c Release -r linux-x64 \
  -o "$OUT" --nologo > "$OUT/publish.log" 2>&1; rc=$?
echo "publish rc=$rc in $(( $(date +%s) - start )) s; warnings: $(grep -cE ': warning ' "$OUT/publish.log")"
[ $rc -eq 0 ] || { grep -E ': error ' "$OUT/publish.log" | head; exit 1; }
file "$exe" | grep -q 'ELF 64-bit' || { echo "not a native ELF: $(file "$exe")"; exit 1; }
echo "native exe: $(stat -c %s "$exe") bytes ($(du -h "$exe" | cut -f1))"
# Interpose mode needs recvfrom/sendto exported with the version the engine's references carry.
exports=$(objdump -T "$exe" | grep -cE 'GLIBC_2\.2\.5 +(recvfrom|sendto)$')
echo "interpose exports versioned GLIBC_2.2.5: $exports of 2"
[ "$exports" -eq 2 ] || exit 1

# One arm: start the launcher with sleep(4) as the engine, SIGTERM at 1 s, report alive / code / time.
arm() { # $1 = LAUNCHER_BlockSigtermOnEngineThread
  local port=$(( 20000 + RANDOM % 20000 )) pid t0 t1 code alive
  RELAY_Listen=127.0.0.1:$port RELAY_InfoListen=127.0.0.1:$((port+1)) RELAY_Health=127.0.0.1:$((port+2)) \
  LAUNCHER_EngineDir=/ LAUNCHER_ChangeDirectory=false LAUNCHER_Libraries__0=libc.so.6 LAUNCHER_EntryPoint=sleep \
  LAUNCHER_BlockSigtermOnEngineThread=$1 "$exe" a b c > "$OUT/sigterm-$1.log" 2>&1 &
  pid=$!
  t0=$(date +%s%N)
  sleep 1
  kill -TERM "$pid"
  sleep 1.5
  if kill -0 "$pid" 2>/dev/null; then alive=yes; else alive=no; fi
  wait "$pid"; code=$?
  t1=$(date +%s%N)
  echo "block=$1: SIGTERM at 1 s; alive 1.5 s later: $alive; exit $code after $(( (t1 - t0) / 1000000 )) ms; handler log lines: $(grep -c 'SIGTERM received' "$OUT/sigterm-$1.log")"
  ARM_ALIVE=$alive ARM_CODE=$code
}
arm true;  blocked_alive=$ARM_ALIVE blocked_code=$ARM_CODE
arm false; open_code=$ARM_CODE
# Blocked: the engine's sleep(4) completes (exit 0) and the process outlives the signal. The control
# (not blocked) shows the signal did reach the process: sleep returns early with the seconds left.
[ "$blocked_alive" = yes ] && [ "$blocked_code" -eq 0 ] && [ "$open_code" -ne 0 ]
