#!/usr/bin/env bash
# Restores and builds a throwaway consumer of the packed SDK from bin/nupkg alone (plus nuget.org for
# third-party packages): a package that names a dependency nobody packed fails here, not in the game.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}
FEED="$ROOT/bin/nupkg"
VERSION=$(ls "$FEED"/SourceSharp.Host.Sdk.*.nupkg | sed -E 's#.*/SourceSharp\.Host\.Sdk\.(.*)\.nupkg#\1#' | sort -V | tail -1)
[ -n "$VERSION" ] || { echo "pack-check: no SourceSharp.Host.Sdk package in $FEED"; exit 1; }
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML
cat > "$WORK/Consumer.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><PackageReference Include="SourceSharp.Host.Sdk" Version="$VERSION" /></ItemGroup>
</Project>
XML
# Touches the SDK, the contracts and a proto type, so every assembly the package needs must resolve.
cat > "$WORK/Use.cs" <<'CS'
public static class Use
{
    public static string Touch() =>
        SourceSharp.Host.Sdk.HostSdk.Version + SourceSharp.Host.Contracts.HostContract.Version
        + new SourceSharp.Host.Proto.Heartbeat { Seq = 1 }.Seq;
}
CS
cd "$WORK"
if ! "$DOTNET" build --nologo -v q -p:NuGetAudit=false --packages "$WORK/packages" > build.log 2>&1; then
  grep -E 'error' build.log | head -5
  echo "pack-check: FAILED for SourceSharp.Host.Sdk $VERSION"
  exit 1
fi
echo "pack-check: a consumer of SourceSharp.Host.Sdk $VERSION restores and builds from $FEED"
