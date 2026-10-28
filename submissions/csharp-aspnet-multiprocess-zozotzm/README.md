# csharp-aspnet-multiprocess-zozotzm

The challenge feed API on the standard C# stack: **ASP.NET Core / Kestrel on .NET 11, Npgsql,
System.Text.Json**. No hand-written HTTP, no custom Postgres protocol, no unsafe code, no caching.

This is deliberately **not** the usual C# deployment shape. Microsoft's eShop, Jellyfin and Bitwarden
all run one ASP.NET Core host per box or container. The conventional submission is
`csharp-aspnet-zozotzm`; this folder exists to test whether process-level heap isolation and fewer
per-request allocations move the 1M-connection ceiling.

This is the `examples/csharp` server with the deployment shape and the allocation discipline changed.
The benchmark holds ~1,000,000 keep-alive sockets while serving only ~42,000 requests/s, so the score is
dominated by per-connection memory, GC pauses, and CPU per request — not by raw request throughput.

## What is different from `examples/csharp`

| Area | Example | Here |
|---|---|---|
| Process model | one Kestrel process | 4 Kestrel processes by default (`WORKERS` overrides), `SO_REUSEPORT` on both ports |
| Heap | one Server GC heap, 70% hard limit | a bounded heap budget per worker (4 GC heaps each at 16 cores / 4 workers), 80% of RAM total |
| Responses | `Results.Json` + `ResponseCompression` (gzip on everything) | pooled buffer + `Utf8JsonWriter` + one async `Content-Length` write; gzip only for `/feed` |
| JSON | reflection serializer, default encoder | `Utf8JsonWriter`, `UnsafeRelaxedJsonEscaping`, `SkipValidation` |
| Auth | `JwtBearer` / IdentityModel | small `AuthenticationHandler` using `HMACSHA256` + `Base64Url` + `JsonDocument` |
| Postgres | one pool | separate read and write pools, so a commit never queues reads |
| Kestrel buffers | defaults (64 KB response, 1 MB request) | 16 KB request/response/transport buffers |
| Body | `JsonDocument.ParseAsync(Request.Body)` | `BodyReader` + `JsonDocument.Parse(ReadOnlySequence<byte>)` |

Nothing here changes the SQL, the schema, the JSON shape, or the error contract.

## Why this shape

At 1M users the load generator sends about **0.0425 requests per user per second** (a user's
feed → post → like/write loop averages ~2.17 requests per ~51 s), i.e. ~42k req/s. That is a modest
request rate for 16 cores. The hard part is:

1. **One million open sockets.** They are idle for up to ~40 s between requests. Any per-connection
   buffer that stays allocated while idle is multiplied by 1M.
2. **Tail latency while the live set is huge.** A stop-the-world or long GC pause delays every request
   in flight. A single big heap over 1M connections is the wrong shape.
3. **CPU per request.** The server has to parse HTTP, verify JWT, run one Postgres statement, render
   JSON, and gzip the feed, ~42k times a second.

The changes above attack those three in order:

- many smaller Kestrel processes instead of one large GC heap;
- Kestrel buffers capped, and no response compression for tiny bodies;
- no `Results.Json`, no claims pipeline, one buffer write instead of chunked response writes;
- reads and writes on separate Postgres pools.

## Kestrel settings

| Setting | Value | Why |
|---|---|---|
| `SocketTransportOptions.Backlog` | 65535 | 1M clients connecting; kernel caps at `net.core.somaxconn` |
| `SocketTransportOptions.WaitForDataBeforeAllocatingBuffer` | true | no read buffer while a keep-alive connection is idle |
| `SocketTransportOptions.MaxReadBufferSize` / `MaxWriteBufferSize` | 16 KB | requests are < 1 KB; responses are 2–12 KB |
| `SocketTransportOptions.IOQueueCount` | `cores / workers` | one or two socket queues per process, not 16 in every process |
| `SocketTransportOptions.CreateBoundListenSocket` | `SO_REUSEPORT` | lets every worker bind `PORT` and `EXTRA_PORT` |
| `Limits.MaxRequestBufferSize` / `MaxResponseBufferSize` | 16 KB | bound the pipe buffers; not a response-size cap |
| `Limits.MaxRequestHeadersTotalSize` / `MaxRequestLineSize` | 16 KB / 4 KB | normal API requests; rejects absurd headers early |
| `Limits.MaxConcurrentConnections` | unlimited | ~1M connections |
| `Limits.KeepAliveTimeout` | 120 s | clients idle up to ~40 s; spec requires ≥ 75 s |
| `Limits.RequestHeadersTimeout` | 30 s | slow-header guard; does not cut idle keep-alives |

## Process / GC / pool model

- `WORKERS` defaults to `min(nproc, 4)`: 4 at 16 or 8 cores, 4 at 4, 1 at 1. `nproc` follows the
  affinity mask, so this also works when the benchmark runs the app on 8 or 4 cores. The local A/B
  found 4 workers faster than 8 at high request rate; `WORKERS=8` is still available.
- `start.sh` computes one hard heap limit per worker: `MemTotal × 80% / workers`. At 16 cores / 8
  workers on a 31 GB box that is about 3.1 GB per worker, 24.8 GB total. `TOTAL_HEAP_PCT` overrides 80.
- `DOTNET_GCHeapCount = cores / workers` (4 at 16 cores / 4 workers, 1 at 4/4), and
  `DOTNET_GCNoAffinitize=1`. `DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS=0` by default; turning it
  on was slower locally.
- `DB_POOL` is the **total** across workers, default `min(256, 16 × cores)`. Each worker gets a
  `DB_POOL / workers` slice, split 3:1 into read and write pools. At 16 cores: 256 total = 192 read +
  64 write. `DB_POOL_READ` / `DB_POOL_WRITE` override the per-process split.
- Npgsql: `MaxAutoPrepare=10`, `AutoPrepareMinUsages=2`, `NoResetOnClose=true`, `Timeout=5`,
  `CommandTimeout=5`.

## Rule alignment

- **One SQL statement per request**, exactly the statements in `SPEC.md`, prepared by Npgsql's normal
  auto-prepare. No SQL that does more work inside Postgres.
- **No caching across requests**: no response/query/JWT-result caches, no in-memory table copies.
  Connections, prepared statements and the HMAC key are the only retained state.
- **Writes are committed before responding**: each insert is autocommit and the response is written
  after the insert/commit completes.
- **Every token checked**: HS256 signature, `alg`, `exp` (and `nbf` if present) on every authenticated
  request; then `sub` and `username` are validated.
- **At most 512 Postgres connections**: default 256, `DB_POOL` capped at 512.
- **Two ports**: every worker binds both `PORT` and `EXTRA_PORT`; no Nginx.
- **Standard stack**: Kestrel, ASP.NET Core authentication middleware, Npgsql, `System.Text.Json`,
  `GZipStream`, `ArrayPool`. No custom HTTP parser, no unsafe code, no custom compression.

## Local validation

All of this was run locally, not on the 16-core benchmark boxes.

- Exact CI path in Linux (Ubuntu 24.04 WSL, .NET SDK 11.0.100-rc.3, PostgreSQL 16):
  `WORKERS=4 PORT=3600 EXTRA_PORT=3601 bash test/run.sh submissions/csharp-aspnet-multiprocess-zozotzm`
  → **passed=42 failed=0**, with the default 4 workers.
- Same suite against the Aspire dev host (single worker, PostgreSQL 18 container): **42/42**.
- Same suite against a 2-worker Linux run: **42/42**; `ss -ltnp` shows two Kestrel processes listening
  on each of the two ports (SO_REUSEPORT working).
- `HOST=0.0.0.0` with 2 workers binds dual-stack listeners on both ports; a keep-alive connection
  idle for 80 s served its next request on the same socket.
- `/feed` with `Accept-Encoding: gzip` decompresses to the exact golden JSON; `Content-Length` is the
  compressed length; port 81 serves the same bytes as port 80.
- **Idle memory:** 20,000 idle keep-alive connections after one `/health` each, 2 workers:
  server RSS went from 171 MB to 520 MB, i.e. **~17.8 KB per idle connection**. At 1M that is ~18 GB
  of server RSS, before kernel socket memory. This is the number the design is optimizing.

### .NET 11 thread-pool setting

This branch now targets **.NET 11**. `DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold` (new in the
#132765 parking heuristic; issue #135855 shows `0` vs `32767` changing results substantially) and
`DOTNET_ThreadPool_ThreadsToKeepAlive` are exposed as `THREADPOOL_SPIN_THRESHOLD` /
`THREADPOOL_KEEPALIVE` in `start.sh`. The runtime default is left in place until the VM A/B. Runtime-async
is not enabled (see dotnet/runtime#135738).

### Local A/B (not the benchmark box; .NET 10 build)

The numbers below are from the previous .NET 10 build and are kept as the .NET 10 reference; the .NET 11
run waits for the VMs. 20,000 users, `-gzip -think-scale 0.1`, 30 s ramp, 60 s hold, tuned local
PostgreSQL 16, everything on one 16-core / 16 GB WSL box. The load generator, server and Postgres share
the same cores, so this is a stress comparison, not a capacity result.

| Variant | req/s | p95 | p99 |
|---|---:|---:|---:|
| baseline example | 14,153 | 355 ms | 442 ms |
| single-process submission | 14,280 | 370 ms | 494 ms |
| 8 workers, inline completions on | 12,676 | 862 ms | 1116 ms |
| 8 workers, inline completions off | 14,328 | 509 ms | 686 ms |
| 4 workers, inline completions on | 13,779 | 627 ms | 897 ms |
| **4 workers, inline completions off (default)** | **15,432** | **352 ms** | **479 ms** |

The 4-worker default is why this folder can beat the baseline locally; 8 workers made `SO_REUSEPORT`
connection distribution uneven (one worker at 100%, others ~70%) and inline completions added latency.
At the scoring think-time (`5,000` users, `-think-scale 2.5`) all variants are underloaded and tie at
~210 req/s.

CPU profile at 20k users: server ~4.6 cores total (4 workers), Postgres ~6.0 cores, mload ~1.6
cores, GC pause ~0.7% of a core per worker (~2.8% total). The local bottleneck is the database and
server CPU, not GC.

The remaining uncertainty is real: the local boxes are not the benchmark boxes, and no one has run this
at 660k–1M yet. The 1M estimate above is an extrapolation.

## Build and run

```bash
sudo bash install.sh                 # .NET 11 SDK if missing
bash build.sh                        # dotnet publish -c Release -o out
DATABASE_URL=postgres://user:pass@db:5432/feed \
JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=80 EXTRA_PORT=81 \
bash start.sh
```

Optional knobs:

| Env | Default | Meaning |
|---|---|---|
| `WORKERS` | `min(nproc, 4)` | number of Kestrel processes |
| `TOTAL_HEAP_PCT` | `80` | total GC heap budget as % of RAM, split across workers |
| `DB_POOL` | `min(256, 16 × cores)` | total Postgres connections across all workers |
| `DB_POOL_READ` / `DB_POOL_WRITE` | 3:1 split | per-worker pool split; keep the app total at ≤ 512 |
| `THREADPOOL_SPIN_THRESHOLD` | unset | maps to `DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold` |
| `THREADPOOL_KEEPALIVE` | unset | maps to `DOTNET_ThreadPool_ThreadsToKeepAlive` |
| `GC_DATAS` / `GC_TARGET_TCP` / `GC_GEN0_GROWTH` | unset | DATAS A/B knobs |

If memory is tight, lower `TOTAL_HEAP_PCT` or `WORKERS`; if the app is CPU/latency bound at 16 cores,
try more workers or a larger `DB_POOL`.

## Files

| File | Purpose |
|---|---|
| `Program.cs` | Kestrel setup, routes, response rendering |
| `AppJson.cs` | pooled response writer, gzip, timestamps, errors |
| `JwtAuth.cs` | HS256 authentication handler and token validation |
| `Db.cs` | connection string, SQL, id parsing |
| `BodyReader.cs` | bounded JSON body reader |
| `ListenSocket.cs` | `SO_REUSEPORT` listening socket |
| `start.sh` | worker processes, per-worker GC limits, signal handling |
| `install.sh`, `build.sh` | the normal .NET install/publish scripts |

`dev/` is an optional local Aspire AppHost (see [dev/README.md](dev/README.md)). It is not used by
`install.sh`, `build.sh` or `start.sh` and is not part of the benchmark binary.
