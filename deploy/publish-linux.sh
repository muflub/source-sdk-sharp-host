#!/usr/bin/env bash
# The Linux build: every executable published for linux-x64 into bin/publish/<project>/, then one
# tarball per executable in bin/dist/. `make publish`; CI runs the same script.
#   Descent.Service, Host.Gateway, Host.FakeGame   framework-dependent (need the ASP.NET Core 10 runtime)
#   Host.Launcher                                  NativeAOT (needs clang and zlib1g-dev to build)
# BUILD_VERSION lands in InformationalVersion and the tarball names. Never pass it as VERSION:
# MSBuild reads that environment variable as $(Version) and restore fails without logging why.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}
BUILD_VERSION=${BUILD_VERSION:-$(git -C "$ROOT" rev-parse --short HEAD)}
RID=linux-x64
PUBLISH="$ROOT/bin/publish"
DIST="$ROOT/bin/dist"
[ -f "$ROOT/roots.props" ] || { echo "publish-linux: roots.props is missing: run \`make setup\`"; exit 1; }
rm -rf "$PUBLISH" "$DIST"
mkdir -p "$PUBLISH" "$DIST"

publish() { # <project> <expected executable> [extra msbuild args...]
  local proj=$1 exe=$2; shift 2
  MSBUILDDISABLENODEREUSE=1 "$DOTNET" publish "$ROOT/src/$proj/$proj.csproj" -c Release -r "$RID" \
    -o "$PUBLISH/$proj" --nologo -v q -p:InformationalVersion="$BUILD_VERSION" "$@"
  # A publish that "succeeded" without its executable is a failure, not a pass.
  [ -x "$PUBLISH/$proj/$exe" ] || { echo "publish-linux: $proj produced no $exe"; exit 1; }
  tar -C "$PUBLISH" -czf "$DIST/$proj-$BUILD_VERSION-$RID.tar.gz" "$proj"
}

publish Descent.Service Descent.Service --self-contained false
publish Host.Gateway    SourceSharp.Host.Gateway --self-contained false
publish Host.FakeGame   SourceSharp.Host.FakeGame --self-contained false
publish Host.Launcher   SourceSharp.Host.Launcher

(cd "$DIST" && sha256sum ./*.tar.gz > SHA256SUMS)
ls -1 "$DIST"
