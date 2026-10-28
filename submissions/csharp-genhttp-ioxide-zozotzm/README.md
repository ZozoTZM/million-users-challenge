# csharp-genhttp-ioxide-zozotzm

The challenge feed API on **GenHTTP 11** running on the **ioxide** engine: GenHTTP's HTTP/1.1
pipeline and routing on top of an io_uring runtime with one reactor per CPU core. Postgres goes
through **ioxide.pg**, whose ring-native pool is opened on each reactor's own thread. No hand-written
HTTP parser, no unsafe code, no response cache.

This is the deliberately extreme C# entry. The normal-tools version is `csharp-aspnet-zozotzm`; the
middle experiment is `csharp-aspnet-multiprocess-zozotzm`.

## Stack

| Piece | Version |
|---|---|
| Runtime | .NET 10 (`net10.0`); GenHTTP 11.1.0 ships net10 assets |
| Web framework | GenHTTP 11.1.0 (`GenHTTP.Engine.Ioxide`, `GenHTTP.Modules.IO`, `GenHTTP.Modules.Compression`) |
| Engine | ioxide 0.15.251, one io_uring reactor per usable CPU |
| Postgres | `ioxide.pg` 0.15.251, per-reactor ring-native pool |
| JSON | `System.Text.Json` `Utf8JsonWriter` |
| Compression | GenHTTP `CompressedContent` (gzip/Brotli/Zstd negotiation) |

Linux 6.1+ with `io_uring` is required; Ubuntu 24.04's 6.8 kernel qualifies. There is no Windows or
macOS fallback.

## Design

- **Routing**: a GenHTTP `IHandler` (`Handler.From(App.HandleAsync)`) dispatches the five endpoints.
  GenHTTP's engine does the HTTP parsing, keep-alive, chunking and response writing.
- **Postgres**: `Postgres.Configure` parses `DATABASE_URL`; `PgPool.Start` runs from GenHTTP's
  `onReactorStart` hook, so every reactor owns its own ring-native pool. Requests are handled with
  `IoxideReactor.Current.GetService<PgPool>()`. `DATABASE_MAX_CONN` defaults to 256, split across
  reactors and capped at 8 per reactor like the HttpArena reference (128 connections at 16
  reactors).
- **JSON**: `Utf8JsonWriter` to an `ArrayBufferWriter`, then GenHTTP's `StringContent` with
  `application/json`. Property order is written explicitly to match the spec's golden bytes.
- **Auth**: HS256 is checked with BCL `HMACSHA256` and a constant-time compare; `alg`, `exp`, `sub`
  and `username` are validated on every authenticated request. Auth runs before the body or id check.
- **SQL**: exactly the SPEC statements, one per request. Auto-prepare is not exposed by ioxide.pg;
  the driver is a ring-native text/binary protocol implementation, and the query text is the SPEC's.
- **Writes** wait for the insert/commit before the response. No caching.
- **Two ports**: both `PORT` and `EXTRA_PORT` are bound with `HttpProtocols.Http1`, HTTP/1.1 only.

## Local validation

- Exact CI path on Ubuntu 24.04 WSL, .NET SDK 10.0.403, PostgreSQL 16 with benchmark-like settings:
  `PORT=3700 EXTRA_PORT=3701 bash test/run.sh submissions/csharp-genhttp-ioxide-zozotzm`
  → **passed=42 failed=0**.
- The golden `/feed` and `/posts/:id` bytes match exactly; `created_at` truncates to milliseconds.
- Like-on-missing-post maps PostgreSQL `23503` to 404; `INSERT ... ON CONFLICT DO NOTHING` uses the
  command tag's affected-row count for 201 vs 200.
- `ioxide` logs one listening line per reactor for both ports at startup.
- `HOST=0.0.0.0` with 4 reactors: a keep-alive connection idle for 80 s served its next request on
  the same socket.

### Local benchmark (not the benchmark box)

20,000 users, `-gzip -think-scale 0.1`, 30 s ramp, 60 s hold, tuned WSL PostgreSQL 16, mload and
Postgres sharing the same 16-core / 16 GB box:

| Variant | req/s | p95 | p99 |
|---|---:|---:|---:|
| baseline ASP.NET example | 14,153 | 355 ms | 442 ms |
| ASP.NET normal | 14,280 | 370 ms | 494 ms |
| ASP.NET multiprocess 4 workers | 15,432 | 352 ms | 479 ms |
| **GenHTTP/ioxide 16 reactors** | **17,536** | **28 ms** | **54 ms** |
| GenHTTP/ioxide 4 reactors | **17,627** | **18 ms** | **44 ms** |

The local machine is also running mload and Postgres on the same cores, so this is a stress
comparison; the 16-core app box with a separate DB is the real test. The 4-reactor run is slightly
faster here because it leaves more of the shared box for mload and Postgres; 16 reactors is the
natural default for a 16-core app box.

A `pidstat` window during the 16-reactor run showed server ~3.35 cores, Postgres ~7.4 cores and
mload ~2.0 cores. The server is not the local bottleneck; the shared Postgres is.

**Pinned 4-core server, .NET 10** (server CPUs 0-3, Postgres 4-11, mload 12-15; 20k users,
`-think-scale 0.1`, 60 s hold): **17,324 req/s, p95 81.5 ms, p99 231.7 ms, 0 errors**. Server used
~2.96 of 4 cores, Postgres ~6.4 of 8, mload ~2.2 of 4. This is the most relevant local comparison for
the 4-core score; the database is on separate cores and is not the server's bottleneck.

### Idle-connection memory

20,000 idle keep-alive connections after one `/health` each, 16 reactors: server RSS went from
72 MB to 453 MB, i.e. **~19.0 KB per idle connection**. At 1M that is ~19 GB of server RSS before
kernel socket memory, inside the 32 GB box but tight.

## Build and run

```bash
sudo bash install.sh                 # .NET 11 SDK if missing
bash build.sh                        # dotnet publish -c Release -o out
DATABASE_URL=postgres://user:pass@db:5432/feed \
JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=80 EXTRA_PORT=81 \
bash start.sh
```

Optional:

| Env | Default | Meaning |
|---|---|---|
| `IOXIDE_REACTORS` | `Environment.ProcessorCount` | io_uring reactors (one per core by default) |
| `DATABASE_MAX_CONN` | `256` | total Postgres connections across reactors, max 8 per reactor |
| `DOTNET_GCHeapHardLimitPercent` | `0x50` (80%) | GC heap hard limit |
| `THREADPOOL_SPIN_THRESHOLD` | unset | maps to `DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold` |
| `THREADPOOL_KEEPALIVE` | unset | maps to `DOTNET_ThreadPool_ThreadsToKeepAlive` |

This is the .NET 10 build. The HttpArena reference uses .NET 11 with `runtime-async=on`; the .NET 11
variant is kept on a separate branch for the later release, where runtime-async (#135738) can be A/B'd.

## Attribution

The engine setup and ioxide.pg wiring follow the public HttpArena `genhttp-ioxide` reference
(`MDA2AV/HttpArena`), adapted to this challenge's API and single-statement SQL contract.
