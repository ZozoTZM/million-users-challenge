#!/usr/bin/env bash
# Foreground server. DATABASE_URL, JWT_SECRET, PORT and EXTRA_PORT are required.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_gcServer="${DOTNET_gcServer:-0}"
export DOTNET_GCHeapHardLimitPercent="${DOTNET_GCHeapHardLimitPercent:-0x50}"

cd "$HERE/out"
if [ -x feed ]; then
  exec ./feed
fi
exec dotnet feed.dll
