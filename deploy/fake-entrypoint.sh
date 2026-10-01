#!/bin/sh
# TIER=fake (plan §7.6): the pod runs /opt/sourcesharp/game-entrypoint.sh with the engine's arguments
# (GamePodBuilder), so the fake image puts this at that path. Host.FakeGame reads the same
# arguments (-port, +map) and environment (SOURCESHARP_HOST_*, RELAY_*, FAKEGAME_*).
# No mod files, no engine: nothing to set up but the process.
set -eu
exec dotnet /app/SourceSharp.Host.FakeGame.dll "$@"
