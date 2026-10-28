# csharp-aspnet-zozotzm

The challenge feed API on the conventional C# stack: **ASP.NET Core minimal APIs / Kestrel, JwtBearer,
source-generated System.Text.Json, Npgsql**. Single process. No custom HTTP, no custom Postgres
protocol, no unsafe code, no response caching.

This is the "normal tools" entry. The separate `csharp-aspnet-multiprocess-zozotzm` folder is the more
aggressive experiment (multiple Kestrel processes, custom auth handler, pooled JSON writer).

A single Kestrel process is also how Microsoft's eShop reference app, Jellyfin and Bitwarden run: one
ASP.NET Core host per box or container. Source-generated JSON is a standard STJ feature, not a
benchmark-only path; `Results.Json` and `ResponseCompression` are the same APIs these apps use.

## What is different from `examples/csharp`

| Area | Example | Here |
|---|---|---|
| JSON | reflection-based STJ | **source-generated** `JsonSerializerContext`, `Results.Json(..., jsonTypeInfo)` |
| Builder | `WebApplication.CreateBuilder` | `CreateSlimBuilder` |
| GC heap | 70% hard limit | 80% hard limit |
| Postgres pool | 128 at 16 cores | 256 at 16 cores (`DB_POOL` overrides, capped at 512) |
| Kestrel buffers | defaults (64 KB response / 1 MB request) | 16 KB request/response/transport buffers |
| Auth | JwtBearer + `SignatureValidator` | unchanged |
| Compression | ResponseCompression | unchanged |
| Process model | one Kestrel process | unchanged |

The SQL, schema, JSON wire shape and error contract are unchanged.

## Why these changes

- **Source generation** removes reflection metadata lookup from every response and is the modern STJ
  path. `JsonPropertyOrder` pins the key order required by the spec; the golden tests verify it.
- **`CreateSlimBuilder`** starts only the ASP.NET Core pieces a minimal API needs; no IIS, no HTTPS
  redirection, no hosting startup assemblies.
- **A 256-connection pool** is the level the example's own tests found better than 128 at 16 cores.
  Each request is one round trip, and 1M users is ~42k req/s; 256 connections leave plenty of headroom
  without approaching the 512 rule or Postgres's 600 `max_connections`.
- **80% GC heap limit** gives the large live set (1M idle connections) more headroom than the example's
  70% cap while still leaving several GB for the runtime and kernel socket memory.
- **16 KB Kestrel buffers** bound the per-connection pipe buffers. The response is a few KB; the request
  is under 1 KB.
- `NoResetOnClose`, auto-prepare and a 5 s pool/command timeout are Npgsql settings, not SQL changes.

## Rule alignment

- **One SQL statement per request**, exactly the SPEC statements, prepared by Npgsql's normal
  auto-prepare.
- **No caching across requests**: no response/query/JWT-result caches, no table copies.
- **Writes are committed before responding**: each insert is autocommit; the response waits for it.
- **Every token checked**: JwtBearer validates HS256, `exp`, and the signature on every authenticated
  request; `sub` and `username` are validated after signature validation.
- **At most 512 Postgres connections**: default 256, `DB_POOL` capped at 512.
- **Two ports**, one process, no Nginx.
- **Standard stack**: Kestrel, ASP.NET Core authentication/authorization, Npgsql, source-generated STJ,
  `ResponseCompression`. No custom auth, no manual JSON writer, no multiprocess.

## Local validation

Run on Linux (Ubuntu 24.04 WSL, .NET SDK 11.0.100-rc.3, PostgreSQL 16), not on the benchmark boxes:

- Exact CI path:
  `PORT=3400 EXTRA_PORT=3401 bash test/run.sh submissions/csharp-aspnet-zozotzm`
  → **passed=42 failed=0**.
- The golden feed/post bytes match exactly, so the source-generated property order and snake_case
  naming are correct.
- The Aspire dev host builds against this project; `test/run.sh` is the stronger check.

### .NET 11 thread-pool setting

This branch now targets **.NET 11**. The new thread-pool parking heuristic from
dotnet/runtime#132765 / issue #135855 can materially change throughput in low-saturation,
many-connection workloads; `DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold` (`0` vs `32767` in the
issue) and `DOTNET_ThreadPool_ThreadsToKeepAlive` (`-1`) are the knobs. `start.sh` leaves the runtime
default in place and accepts `THREADPOOL_SPIN_THRESHOLD` / `THREADPOOL_KEEPALIVE` for A/B runs on the
VMs. Runtime-async is intentionally not enabled in this ASP.NET submission (see dotnet/runtime#135738).

### Local A/B (not the benchmark box; .NET 10 build)

The numbers below were collected on the previous .NET 10 build. They are kept as the .NET 10
reference; the .NET 11 run waits for the VMs. 20,000 users, `-gzip -think-scale 0.1`, 30 s ramp, 60 s
hold, tuned local PostgreSQL 16, everything on one 16-core / 16 GB WSL box. The load generator,
server and Postgres share the same cores, so this is a stress comparison, not a capacity result.

| Variant | req/s | p95 | p99 |
|---|---:|---:|---:|
| baseline example | 14,153 | 355 ms | 442 ms |
| this submission | 14,280 | 370 ms | 494 ms |

At the scoring think-time (`5,000` users, `-think-scale 2.5`) both are underloaded: baseline 209 req/s,
this submission 208 req/s, p95 ~2.1 ms.

The profile at 20k users is roughly: server 4.6 vs 4.9 cores, Postgres 5.0 vs 6.3 cores, mload 1.5 vs
1.7 cores, GC pause ~3.2% of one core in both. The local bottleneck is the database and server CPU, not
GC; source generation did not produce a clear throughput win here, but it removes reflection from the
hot path and is a low-risk modern default. The multiprocess variant (4 workers, inline completions off)
is faster locally at this stress load, but it is a separate submission.

These are local stress numbers, not a claim about the 16-core benchmark boxes or 1M connections.

## Build and run

```bash
sudo bash install.sh
bash build.sh
DATABASE_URL=postgres://user:pass@db:5432/feed \
JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=80 EXTRA_PORT=81 \
bash start.sh
```

Optional:

| Env | Default | Meaning |
|---|---|---|
| `DB_POOL` | `min(256, 16 × cores)` | total Postgres connections (max 512) |
| `DOTNET_GCHeapHardLimitPercent` | `0x50` (80%) | GC heap hard limit |
| `THREADPOOL_SPIN_THRESHOLD` | unset | maps to `DOTNET_ThreadPool_SpuriousDispatchNoSpinThreshold`; try `0` and `32767` |
| `THREADPOOL_KEEPALIVE` | unset | maps to `DOTNET_ThreadPool_ThreadsToKeepAlive`; `-1` keeps workers |
| `GC_DATAS` | unset | maps to `DOTNET_GCDynamicAdaptationMode` (`0`/`1`) |
| `GC_TARGET_TCP` | unset | maps to `DOTNET_GCDTargetTCP` (percent) |
| `GC_GEN0_GROWTH` | unset | maps to `DOTNET_GCDGen0GrowthPercent` |

Knobs from the .NET performance playbook that are **not** enabled here: DATAS off,
`DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS`, thread-pool hill-climbing changes, runtime-async,
NativeAOT. The .NET 11 thread-pool knobs are exposed through `start.sh` for the VM A/B. They change
behavior and were not measured on the target hardware.

## Files

| File | Purpose |
|---|---|
| `Program.cs` | Kestrel, auth, routes, Npgsql calls |
| `JsonModels.cs` | DTOs + `JsonSerializerContext` |
| `Auth.cs` | JwtBearer signature workaround and claim validation |
| `Db.cs` | connection string, SQL, timestamp/id helpers |
| `start.sh` | single foreground Kestrel process |
| `install.sh`, `build.sh` | normal .NET install/publish |

`dev/` is an optional local Aspire AppHost (see [dev/README.md](dev/README.md)). It is not used by
`install.sh`, `build.sh` or `start.sh`.
