#!/usr/bin/env bash
# Foreground server (systemd ExecStart). Reads DATABASE_URL, JWT_SECRET, HOST, PORT, EXTRA_PORT,
# and optional IOXIDE_REACTORS, DATABASE_MAX_CONN.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_gcServer="${DOTNET_gcServer:-1}"
# Keep the whole live set (up to 1M idle connections) inside the box's RAM.
export DOTNET_GCHeapHardLimitPercent="${DOTNET_GCHeapHardLimitPercent:-0x50}"

# .NET 11 thread-pool parking heuristic (dotnet/runtime#135855 / #132765).
if [ -n "${THREADPOOL_SPIN_THRESHOLD:-}" ]; then
  export DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold="$THREADPOOL_SPIN_THRESHOLD"
fi
if [ -n "${THREADPOOL_KEEPALIVE:-}" ]; then
  export DOTNET_ThreadPool_ThreadsToKeepAlive="$THREADPOOL_KEEPALIVE"
fi

cd "$HERE/out"
if [ -x FeedApi ]; then
  exec ./FeedApi
fi
exec dotnet FeedApi.dll
