#!/usr/bin/env bash
# Foreground server (systemd ExecStart). Reads DATABASE_URL, JWT_SECRET, HOST, PORT, EXTRA_PORT,
# and the optional knobs: WORKERS, DB_POOL, DB_POOL_READ, DB_POOL_WRITE, TOTAL_HEAP_PCT.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_gcServer="${DOTNET_gcServer:-1}"
export DOTNET_TieredPGO="${DOTNET_TieredPGO:-1}"
export DOTNET_GCNoAffinitize="${DOTNET_GCNoAffinitize:-1}"
# Local A/B: inline completions made this workload slower; default off.
export DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS="${DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS:-0}"

# .NET 11 thread-pool parking heuristic. Leave the runtime default unless the VM
# benchmark says otherwise (dotnet/runtime#135855); these aliases make the A/B easy.
if [ -n "${THREADPOOL_SPIN_THRESHOLD:-}" ]; then
  export DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold="$THREADPOOL_SPIN_THRESHOLD"
fi
if [ -n "${THREADPOOL_KEEPALIVE:-}" ]; then
  export DOTNET_ThreadPool_ThreadsToKeepAlive="$THREADPOOL_KEEPALIVE"
fi

cores="$(nproc 2>/dev/null || getconf _NPROCESSORS_ONLN 2>/dev/null || echo 1)"
# Local A/B: 4 workers beat 8 (8 workers unevenly pinned one core and added ~20% CPU/request).
workers="${WORKERS:-$(( cores < 4 ? cores : 4 ))}"
[ "$workers" -lt 1 ] && workers=1
[ "$workers" -gt "$cores" ] && workers="$cores"

# One bounded heap budget per worker. Keeping the sum below the box's RAM is what lets many idle
# keep-alive connections coexist with request garbage without a global GC death spiral.
mem_kb="$(awk '/^MemTotal:/ { print $2 }' /proc/meminfo)"
total_heap_pct="${TOTAL_HEAP_PCT:-80}"
heap_limit_bytes=$(( mem_kb * 1024 / 100 * total_heap_pct / workers ))
heap_limit_hex="$(printf '0x%x' "$heap_limit_bytes")"
heap_count=$(( cores / workers ))
[ "$heap_count" -lt 1 ] && heap_count=1

export WORKERS="$workers"
export DOTNET_GCHeapHardLimit="${DOTNET_GCHeapHardLimit:-$heap_limit_hex}"
export DOTNET_GCHeapCount="${DOTNET_GCHeapCount:-$(printf '0x%x' "$heap_count")}"

cd "$HERE/out"

if [ "$workers" -le 1 ]; then
  export WORKER_INDEX=0
  exec dotnet FeedApi.dll
fi

pids=()
cleanup() {
  trap - TERM INT
  kill -TERM "${pids[@]}" 2>/dev/null || true
  wait 2>/dev/null || true
  exit 0
}
trap cleanup TERM INT

for i in $(seq 0 $((workers - 1))); do
  WORKER_INDEX="$i" dotnet FeedApi.dll &
  pids+=("$!")
done

# Foreground: if any worker exits, stop the rest and let systemd see the failure.
set +e
wait -n
status=$?
set -e
# A worker exiting cleanly is still a dead worker: fail the unit instead of reporting success.
if [ "$status" -eq 0 ]; then status=1; fi
for pid in "${pids[@]}"; do
  kill -TERM "$pid" 2>/dev/null || true
done
wait 2>/dev/null || true
exit "$status"
