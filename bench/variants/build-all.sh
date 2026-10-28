#!/usr/bin/env bash
# Build every in-repo submission variant for the VM matrix.
#
#   bash build-all.sh jit
#   bash build-all.sh aot
#   bash build-all.sh all
#
# The external PR baselines are separate clones; see README.md.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
MODE="${1:-all}"

KESTREL="$ROOT/submissions/csharp-aspnet-zozotzm"
CUSTOM="$ROOT/submissions/csharp-aspnet-customauth-zozotzm"
GENHTTP="$ROOT/submissions/csharp-genhttp-ioxide-zozotzm"
LEAN="$ROOT/submissions/csharp-ioxide-lean-zozotzm"
EPOLL="$ROOT/submissions/csharp-epoll-zozotzm"

build() {
  local aot="$1" dir="$2"
  echo
  echo "== $(basename "$dir") AOT=$aot =="
  (cd "$dir" && AOT="$aot" bash build.sh)
}

case "$MODE" in
  jit)
    build 0 "$KESTREL"
    build 0 "$CUSTOM"
    build 0 "$GENHTTP"
    build 0 "$LEAN"
    build 0 "$EPOLL"
    ;;
  aot)
    build 1 "$KESTREL"
    build 1 "$CUSTOM"
    build 1 "$GENHTTP"
    build 1 "$LEAN"
    build 1 "$EPOLL"
    ;;
  all)
    build 0 "$KESTREL"
    build 1 "$KESTREL"
    build 0 "$CUSTOM"
    build 1 "$CUSTOM"
    build 0 "$GENHTTP"
    build 1 "$GENHTTP"
    build 0 "$LEAN"
    build 1 "$LEAN"
    build 0 "$EPOLL"
    build 1 "$EPOLL"
    ;;
  *)
    echo "usage: $0 jit|aot|all" >&2
    exit 2
    ;;
esac

echo
echo "done: $MODE"
