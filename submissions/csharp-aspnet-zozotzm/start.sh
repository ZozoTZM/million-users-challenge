#!/usr/bin/env bash
# Foreground server (systemd ExecStart). Reads DATABASE_URL, JWT_SECRET, HOST, PORT, EXTRA_PORT.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# GC heap hard limit = 80% of RAM (documented GC setting; the example used 70%).
# DOTNET_GC* values are hex: 0x50 = 80%.
export DOTNET_GCHeapHardLimitPercent="${DOTNET_GCHeapHardLimitPercent:-0x50}"

cd "$HERE/out"
if [ -x FeedApi ]; then
  exec ./FeedApi
fi
exec dotnet FeedApi.dll
