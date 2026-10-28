#!/usr/bin/env bash
# Foreground server (systemd ExecStart). Reads DATABASE_URL, JWT_SECRET, HOST, PORT, EXTRA_PORT.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# GC heap hard limit = 80% of RAM (documented GC setting; the example used 70%).
# DOTNET_GC* values are hex: 0x50 = 80%.
export DOTNET_GCHeapHardLimitPercent="${DOTNET_GCHeapHardLimitPercent:-0x50}"

# .NET 11 thread-pool parking heuristic. The runtime default is 2/3 * CPU count.
# dotnet/runtime#135855 shows large differences between 0 and 32767; leave the
# runtime default unless the VM benchmark says otherwise. These env aliases make
# the A/B easy without editing the unit file.
if [ -n "${THREADPOOL_SPIN_THRESHOLD:-}" ]; then
  export DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold="$THREADPOOL_SPIN_THRESHOLD"
fi
if [ -n "${THREADPOOL_KEEPALIVE:-}" ]; then
  export DOTNET_ThreadPool_ThreadsToKeepAlive="$THREADPOOL_KEEPALIVE"
fi

cd "$HERE/out"
exec dotnet FeedApi.dll
