# VM variant matrix

This branch is the experiment matrix, not a final submission branch. Every variant below passes the
official 42-test contract locally; the performance choice is deliberately deferred to the VMs.

The two submissions we ultimately want are:

1. the best **standard ASP.NET Core/Kestrel** build;
2. the best **C# within the contest rules**, regardless of whether the kernel path is epoll or
   io_uring.

The extra entries here exist to answer build/runtime questions, not to become extra PRs.

## Build variants

All five C# submissions now take `AOT=1` on `build.sh` and run the native executable from
`start.sh` when it is present.

```bash
AOT=0 bash build.sh     # framework-dependent JIT
AOT=1 bash build.sh     # Linux x64 NativeAOT, OptimizationPreference=Speed
AOT=1 AOT_ISA=x86-64-v3 bash build.sh   # AOT tuned for the benchmark host ISA
```

AOT is not assumed to be faster. These builds exist so the VM can compare AOT against JIT/PGO with
the same code and the same load. Run both the generic AOT and the `x86-64-v3` AOT if the host CPU
supports AVX2; the latter is the fair comparison to JIT on the same box.

Local correctness results for the AOT builds:

| Build | Tests |
|---|---|
| Kestrel, JwtBearer, JIT | 42/42 |
| Kestrel, JwtBearer, AOT | 42/42 |
| Kestrel, custom HS256, JIT | 42/42 |
| Kestrel, custom HS256, AOT | 42/42 |
| GenHTTP/ioxide, JIT | 42/42 |
| GenHTTP/ioxide, AOT | 42/42 (GenHTTP trim/AOT warnings at publish) |
| Lean raw ioxide, JIT | 42/42 |
| Lean raw ioxide, AOT | 42/42 |
| Own epoll, JIT | 42/42 |
| Own epoll, AOT | 42/42 |

AOT is not assumed to be faster. These builds exist so the VM can compare AOT against JIT/PGO with
the same code and the same load.

## Variants to run

### Kestrel / ASP.NET

| ID | Directory | Build | Runtime | Question |
|---|---|---|---|---|
| `kestrel-jit` | `submissions/csharp-aspnet-zozotzm` | JIT | defaults | JIT/PGO baseline |
| `kestrel-aot` | same | AOT | defaults | AOT vs JIT |
| `kestrel-jit-inline` | same | JIT | `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=1` | inline socket completions |
| `kestrel-aot-inline` | same | AOT | same | AOT + inline completions |
| `kestrel-jit-datas-off` | same | JIT | `DOTNET_GCDynamicAdaptationMode=0` | server GC without DATAS |
| `kestrel-aot-datas-off` | same | AOT | same | AOT + no DATAS |
| `kestrel-custom-jit` | `submissions/csharp-aspnet-customauth-zozotzm` | JIT | defaults | removes JwtBearer/IdentityModel |
| `kestrel-custom-aot` | same | AOT | defaults | custom auth + AOT |
| `kestrel-custom-jit-inline` | same | JIT | inline completions | custom auth + inline |

The custom-auth variant still uses Kestrel, Npgsql and minimal APIs. It replaces the JwtBearer
middleware with a direct BCL HS256 check; it is still a normal ASP.NET application.

### Framework-on-io_uring

| ID | Directory | Build | Runtime | Question |
|---|---|---|---|---|
| `genhttp-jit` | `submissions/csharp-genhttp-ioxide-zozotzm` | JIT | `DATABASE_POOL_PER_REACTOR_MAX=32` | full framework baseline |
| `genhttp-aot` | same | AOT | same | AOT effect on GenHTTP |
| `genhttp-jit-pool8` | same | JIT | pool cap 8 | the HttpArena reference cap |
| `genhttp-jit-pool64` | same | JIT | pool cap 64 | more DB connections |

GenHTTP is the full-framework io_uring comparison. It is not the max-C# candidate.

### Raw ioxide (io_uring)

| ID | Directory | Build | Runtime | Question |
|---|---|---|---|---|
| `lean-jit` | `submissions/csharp-ioxide-lean-zozotzm` | JIT | gzip on, slab 4, pool 256 | current raw-ioxide baseline |
| `lean-aot` | same | AOT | same | AOT effect |
| `lean-jit-gzip0` | same | JIT | `IOXIDE_LEAN_GZIP=0` | no gzip (PR #31's choice) |
| `lean-aot-gzip0` | same | AOT | `IOXIDE_LEAN_GZIP=0` | AOT + no gzip |
| `lean-jit-slab1` | same | JIT | `IOXIDE_WRITE_SLAB_KB=1`, gzip off | minimum idle state |
| `lean-jit-slab16` | same | JIT | `IOXIDE_WRITE_SLAB_KB=16`, gzip off | fewer segmented sends |
| `lean-jit-pool64` | same | JIT | `DATABASE_MAX_CONN=64` | DB connection count |
| `lean-aot-pool64` | same | AOT | `DATABASE_MAX_CONN=64` | AOT + pool tuning |

`IOXIDE_WRITE_SLAB_KB` defaults to 4. `IOXIDE_LEAN_GZIP` defaults to 1. The pool split is controlled
by `DATABASE_MAX_CONN` and `DATABASE_POOL_PER_REACTOR_MAX`.

### Our max-C# epoll

| ID | Directory | Build | Runtime | Question |
|---|---|---|---|---|
| `epoll-own-jit` | `submissions/csharp-epoll-zozotzm` | JIT | gzip on, pool 64 | our own epoll/binary-PG implementation |
| `epoll-own-aot` | same | AOT | gzip on, pool 64 | AOT effect |
| `epoll-own-jit-gzip0` | same | JIT | `GZIP=0` | gzip cost |
| `epoll-own-aot-gzip0` | same | AOT | `GZIP=0` | AOT + no gzip |
| `epoll-own-jit-pool32` | same | JIT | `DB_POOL=32` | fewer readers |
| `epoll-own-jit-pool128` | same | JIT | `DB_POOL=128` | more readers |

This is our hand-written epoll entry: own HTTP parser, own binary Postgres wire client, one reader
pool + one writer per loop. It currently uses one in-flight query per read connection; read batching
and write group commit are the next tuning step.

### Epoll reference baselines

These are external PRs, not our submissions. They establish the epoll/AOT bar:

```bash
mkdir -p bench/variants/external
git clone --depth 1 --branch feat/csharp-epoll-xeonfx \
  https://github.com/XeonFX/million-users-challenge.git \
  bench/variants/external/xeonfx
git clone --depth 1 --branch csharp-kestrel-wylieagab \
  https://github.com/wylieagab/million-users-challenge.git \
  bench/variants/external/wylie

# PR #31 epoll: NativeAOT and JIT
(cd bench/variants/external/xeonfx/submissions/csharp-epoll-xeonfx && bash build.sh)
(cd bench/variants/external/xeonfx/submissions/csharp-epoll-xeonfx && FEED_JIT=1 bash build.sh)
```

Their `start.sh` expects `bin/app/feed`; keep the two builds in separate copies if you want both
AOT and JIT side by side.

## VM test order

Do not run the whole matrix at 1M users. Stage it:

### Stage 1: architecture at 16 cores

Run at the real scoring load: `-gzip -think-scale 2.5`, 1M users, 5-minute hold, Postgres and mload
on separate machines/cores.

- `kestrel-jit`
- `kestrel-aot`
- `kestrel-custom-jit`
- `kestrel-custom-aot`
- `genhttp-jit`
- `genhttp-aot`
- `lean-jit`
- `lean-aot`
- `epoll-own-jit`
- `epoll-own-aot`
- PR #31 epoll AOT
- PR #31 epoll JIT

Keep only the entries that pass with p95 < 500 ms, p99 < 1 s, errors < 1%, and a five-minute hold.

### Stage 2: 8 and 4 cores, user ceiling

Take the top 3–4 from Stage 1 and search upward:

- 16 cores: 1M is the cap.
- 8 and 4 cores: 1M, 1.5M, 2M, 3M; stop on the first failure.
- Watch both server cores and Postgres cores. A server at 2.5/4 cores with Postgres at 7.5/8 is
  database-bound, not CPU-bound.

### Stage 3: runtime tuning on the two winners

Only after the architecture is chosen:

- gzip on/off
- `IOXIDE_WRITE_SLAB_KB=1/4/16`
- `DATABASE_MAX_CONN=64/128/256`
- Kestrel inline completions and DATAS off
- AOT vs JIT for the final winner
- .NET 11 after GA for Kestrel thread-pool knobs

## Command shape

Build:

```bash
cd submissions/<folder>
AOT=0 bash build.sh      # or AOT=1
```

Start both ports:

```bash
DATABASE_URL='postgres://feed:pass@db:5432/feed' \
JWT_SECRET='million-users-challenge' \
HOST=0.0.0.0 PORT=80 EXTRA_PORT=81 \
IOXIDE_REACTORS=16 \
bash start.sh
```

Load:

```bash
./bench/mload/mload \
  -target <server-ip> -ports 80,81 \
  -users 1000000 -ramp 60s -hold 5m \
  -gzip -think-scale 2.5 \
  -tokens seed/tokens.json -out result.json
```

For 16 cores `-users 1000000` is the cap; for 8/4 cores start at 1M and increase only after a clean
pass. Use separate machines for server, Postgres and mload; do not compare VMs sharing one host.

## Status of the max-C# entry

`submissions/csharp-epoll-zozotzm` now exists and passes 42/42 in JIT and NativeAOT builds. It has
the 40-byte connection table, own HTTP parser, binary Postgres, multiple readers + one writer per
loop, `TCP_DEFER_ACCEPT`, and optional gzip.

The remaining performance work is Java-style read batching and write group commit. The external
PR #31 build stays in the matrix as the architecture baseline to beat.
