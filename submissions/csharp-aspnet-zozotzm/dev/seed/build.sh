#!/usr/bin/env bash
# Build the one-shot seed image used by the optional Aspire dev host.
# Run from anywhere; the Docker build context is the repository root.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SUBMISSION="$(cd "$HERE/../.." && pwd)"
REPO="$(cd "$SUBMISSION/../.." && pwd)"
SUBPATH="${SUBMISSION#"$REPO"/}"
docker build --build-arg "SUBPATH=$SUBPATH" -f "$HERE/Dockerfile" -t feed-seed:local "$REPO"
