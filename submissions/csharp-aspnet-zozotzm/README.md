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

Run on Linux (Ubuntu 24.04 WSL, .NET SDK 10.0.403, PostgreSQL 16), not on the benchmark boxes:

- Exact CI path:
  `PORT=3400 EXTRA_PORT=3401 bash test/run.sh submissions/csharp-aspnet-zozotzm`
  → **passed=42 failed=0**.
- The golden feed/post bytes match exactly, so the source-generated property order and snake_case
  naming are correct.
- The Aspire dev host builds against this project; `test/run.sh` is the stronger check.

No benchmark result is claimed. On a local box this conventional variant is expected to be close to the
example; the larger structural changes live in the multiprocess variant and, later, the GenHTTP/ioxide
entry.

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

Knobs from the .NET performance playbook that are **not** enabled here: DATAS off,
`DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS`, thread-pool hill-climbing changes, NativeAOT. They are
worth an A/B on the real box, but they change behavior and were not measured on the target hardware.

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
