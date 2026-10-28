# csharp-ioxide-lean-zozotzm

The comparison entry: the challenge API on the **raw ioxide io_uring API**, with no HTTP
framework and no GenHTTP layer. It exists to measure what the ASP.NET/Kestrel and GenHTTP costs
actually are; the conventional entry is `csharp-aspnet-zozotzm` and the framework-on-ioxide entry
is `csharp-genhttp-ioxide-zozotzm`.

This is the same architecture as the fast Rust entry: one reactor thread per core, one ring per
reactor, `SO_REUSEPORT` listeners on both ports, a hand-written HTTP/1.1 parser, and ring-native
Postgres queries. There is no per-connection receive buffer: an idle keep-alive connection holds
only the unconsumed tail of its last receive.

## Stack

| Piece | Version |
|---|---|
| Runtime | .NET 10 (`net10.0`) |
| Engine | ioxide 0.18.374, one io_uring reactor per usable CPU |
| Postgres | ioxide.pg 0.18.374, per-reactor ring-native pool |
| JSON | hand-written UTF-8 framing and escaping; `System.Text.Json` only for request bodies |
| Compression | `GZipStream` when the client sends `Accept-Encoding: gzip` |
| HTTP | own HTTP/1.1 parser in `Http.cs` |

Linux 6.1+ with `io_uring` is required. There is no Windows or macOS fallback.

## Design

- **Transport**: `Reactor` with `TcpHandle`; both `PORT` and `EXTRA_PORT` are bound by every
  reactor. `WriteSlabSize` defaults to 4 KB. Larger responses spill into pooled slabs and leave
  through ioxide's segmented vectored send instead of growing every connection's slab.
- **HTTP**: request line, headers, case-insensitive header names, `Content-Length`, chunked
  request bodies, keep-alive, pipelining and fragmented reads. The parser returns a small value
  type; route validation happens before any await so no span crosses the DB wait.
- **Memory**: the only per-connection state is a tiny carry array (starts empty and grows to the
  actual request size), a reference to the reactor's pool, and ioxide's connection object. Response
  JSON is built in an `ArrayPool<byte>` buffer that is returned after the write slab copies it;
  a connection sitting idle after a request owns no response buffer.
- **Postgres**: `PgPool.Start` runs from `Reactor.OnStart`, so each pool belongs to its reactor
  thread. All four statements are prepared and parameterized. The feed is prepared too; sending
  `LIMIT 20` as a simple query was the single largest local latency regression.
- **Auth**: HS256 via BCL `HMACSHA256`, constant-time compare, `alg`/`exp`/`sub`/`username`
  validation. Auth runs before the id check on like requests.
- **SQL**: exactly the statements in `SPEC.md`, one per request, prepared but not replaced with
  extra server-side work.
- **Compression**: optional per the spec. The default is on; `IOXIDE_LEAN_GZIP=0` turns it off for
  the latency A/B.

## Local validation

CI path on Ubuntu 24.04 WSL, .NET SDK 10.0.403, PostgreSQL 16:

```bash
PORT=3700 EXTRA_PORT=3701 bash test/run.sh submissions/csharp-ioxide-lean-zozotzm
```

Result on a fresh seed database: **passed=42 failed=0**. Both ports answer `/health`; the golden
feed/post bytes, auth failures, create/like/read-back paths, timestamps and escaping all pass.

## Pinned 4-core comparison

Server CPUs 0-3, Postgres 4-11, mload 12-15; 20,000 users, `-gzip -think-scale 0.1`, 60 s hold.
This is the same harness used for the earlier Rust, GenHTTP and Kestrel numbers.

| Implementation | req/s | p95 | p99 | Server CPU | PG CPU | Idle/conn |
|---|---:|---:|---:|---:|---:|---:|
| Rust PR #1 | 17,716 | **3.4 ms** | 20 ms | 2.70 / 4 | 4.58 / 8 | ~1.2 KB |
| **Lean ioxide, pool 64, gzip off** | **17,583** | **32.3 ms** | 78 ms | 2.56 / 4 | 7.10 / 8 | ~9 KB |
| Lean ioxide, pool 128, gzip off | 17,574 | 37.5 ms | 81 ms | 2.58 / 4 | 7.27 / 8 | ~9 KB |
| Lean ioxide, pool 128, gzip on | 17,464 | 55.3 ms | 105 ms | 2.71 / 4 | 7.05 / 8 | ~9 KB |
| GenHTTP/ioxide, pool 32 | 17,435 | 57.5 ms | 99 ms | 3.01 / 4 | 6.88 / 8 | ~19 KB |
| Kestrel normal | 13,707 | 405 ms | 451 ms | 3.58 / 4 | 6.27 / 8 | ~13.2 KB |

The raw layer closes the HTTP-side gap: throughput is within ~1% of Rust, server CPU is slightly
lower, p95 is an order of magnitude better than Kestrel and better than GenHTTP. The remaining gap
to Rust is almost entirely Postgres: ioxide.pg is text-format and one pool, while the Rust entry
uses binary rows and separate read/write pools. That is also why the raw C# server uses ~7 of the
8 Postgres cores at this local load. On the real box the database is separate, so this local
Postgres contention will not look the same.

Idle-connection RSS after 20,000 keep-alive connections that had each completed one `/health`:
**~9 KB/connection** with the 4 KB slab, **~6 KB/connection** with a 1 KB slab. The 1 KB slab cost
about 15 ms of p95 in the same 4-core test, so 4 KB is the default. For comparison, GenHTTP/ioxide
was ~19 KB and Kestrel ~13.2 KB; Rust's no-buffer design is ~1.2 KB.

## Build and run

```bash
sudo bash install.sh
bash build.sh                 # JIT; AOT=1 bash build.sh for the NativeAOT A/B
DATABASE_URL=postgres://user:pass@db:5432/feed \
JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=80 EXTRA_PORT=81 \
bash start.sh
```

| Env | Default | Meaning |
|---|---|---|
| `IOXIDE_REACTORS` | `Environment.ProcessorCount` | reactor threads / rings |
| `DATABASE_MAX_CONN` | `256` | total Postgres connections, split across reactors |
| `DATABASE_POOL_PER_REACTOR_MAX` | `32` | per-reactor cap |
| `IOXIDE_WRITE_SLAB_KB` | `4` | primary per-connection write slab; overflow is segmented |
| `IOXIDE_LEAN_GZIP` | `1` | `0` disables response compression |
| `DOTNET_GCHeapHardLimitPercent` | `0x50` | managed heap limit |

## Attribution

The transport shape and raw ioxide usage follow the public HttpArena `ioxide` / `edixoi` reference
entries (`MDA2AV/HttpArena`), adapted to this challenge's five endpoints and single-statement SQL
contract.
