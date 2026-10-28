#!/usr/bin/env bash
# Fetch and build the two external C# reference entries used in the VM matrix.
#
#   bash prepare-external.sh
#
# This is benchmark scaffolding, not a submission. The clones live under
# bench/variants/external/ and are git-ignored.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/external"
mkdir -p "$OUT"

# PR #31, C# epoll + NativeAOT + binary/pipelined Postgres.
if [ ! -d "$OUT/xeonfx-aot" ]; then
  git clone --depth 1 --branch feat/csharp-epoll-xeonfx \
    https://github.com/XeonFX/million-users-challenge.git "$OUT/xeonfx-aot"
fi
git -C "$OUT/xeonfx-aot" fetch --depth 1 origin 2386e5ad118d9f4fa8df3e939cb8c4b30c39dca9
git -C "$OUT/xeonfx-aot" checkout --detach 2386e5ad118d9f4fa8df3e939cb8c4b30c39dca9
(
  cd "$OUT/xeonfx-aot/submissions/csharp-epoll-xeonfx"
  sed -i 's/\r$//' build.sh start.sh
  bash build.sh
)

# Same source, JIT fallback, so AOT vs JIT can be measured with identical code.
if [ ! -d "$OUT/xeonfx-jit" ]; then
  cp -a "$OUT/xeonfx-aot" "$OUT/xeonfx-jit"
  rm -rf "$OUT/xeonfx-jit/submissions/csharp-epoll-xeonfx/bin/app"
  (
    cd "$OUT/xeonfx-jit/submissions/csharp-epoll-xeonfx"
    FEED_JIT=1 bash build.sh
  )
fi

# PR #28, conventional Kestrel/Npgsql NativeAOT, for the ASP.NET comparison.
if [ ! -d "$OUT/wylie" ]; then
  git clone --depth 1 --branch csharp-kestrel-wylieagab \
    https://github.com/wylieagab/million-users-challenge.git "$OUT/wylie"
fi
(
  cd "$OUT/wylie/submissions/csharp-kestrel-wylieagab"
  sed -i 's/\r$//' build.sh start.sh
  bash build.sh
)

echo "external references ready under $OUT"
