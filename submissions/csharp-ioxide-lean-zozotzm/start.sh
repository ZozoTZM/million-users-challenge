#!/usr/bin/env bash
# Foreground server. DATABASE_URL, JWT_SECRET, PORT and EXTRA_PORT are required; IOXIDE_REACTORS
# defaults to the available CPU count.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_gcServer="${DOTNET_gcServer:-1}"
# The live set is up to 1M idle connections; keep the managed heap inside the box.
export DOTNET_GCHeapHardLimitPercent="${DOTNET_GCHeapHardLimitPercent:-0x50}"

cd "$HERE/out"
if [ -x LeanApi ]; then
  exec ./LeanApi
fi
exec dotnet LeanApi.dll
