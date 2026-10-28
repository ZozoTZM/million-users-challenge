#!/usr/bin/env bash
# Start one external epoll reference build on ports 3000/3001.
#
#   bash run-external-epoll.sh aot|jit
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
KIND="${1:-aot}"
case "$KIND" in
  aot) DIR="$HERE/external/xeonfx-aot/submissions/csharp-epoll-xeonfx" ;;
  jit) DIR="$HERE/external/xeonfx-jit/submissions/csharp-epoll-xeonfx" ;;
  *) echo "usage: $0 aot|jit" >&2; exit 2 ;;
esac
[ -d "$DIR" ] || { echo "missing $DIR; run prepare-external.sh" >&2; exit 1; }
cd "$DIR"
exec env \
  DATABASE_URL="${DATABASE_URL:?DATABASE_URL is required}" \
  JWT_SECRET="${JWT_SECRET:-million-users-challenge}" \
  HOST="${HOST:-0.0.0.0}" PORT="${PORT:-3000}" EXTRA_PORT="${EXTRA_PORT:-3001}" \
  bash start.sh
