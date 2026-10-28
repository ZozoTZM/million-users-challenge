# csharp-epoll-zozotzm

The max-C# experiment: no web framework, no database driver, no HTTP library. One epoll event loop
per core, a hand-written HTTP/1.1 front end, and a binary Postgres v3 client on the same loops.

This is our own implementation. The Java and Rust entries were used as benchmarks, not copied.

## Design

- One `epoll` set per loop, its own `SO_REUSEPORT` listeners on both ports, its own clients and its
  own Postgres connections. No locks between loops.
- A client is an entry in a shared fd-indexed `Conn` array: a generation, a few flags, and nullable
  `In`/`Out` buffers only while a request is fragmented or a send is backpressured. Idle connections
  hold no buffer.
- One 64 KB receive buffer and one 1 MB response buffer per loop. Requests are parsed in place and
  answered with one `send`.
- Prepared statements on every Postgres connection. Parameters and result rows are binary; ids,
  counts and `created_at` need no text conversion. `created_at` is microseconds since 2000, formatted
  directly to the spec's millisecond UTC form.
- Multiple read connections per loop plus one write connection per loop. `DB_POOL` controls the
  total; the default split is 4 readers per loop at 16 loops and 16 at 4 loops, always under the
  512-connection limit.
- `TCP_DEFER_ACCEPT` on the listeners, `TCP_NODELAY` on accepted sockets.
- Optional gzip for JSON bodies at least `GZIP_MIN` bytes (feed in practice), with
  `Content-Encoding: gzip` and `Vary: Accept-Encoding`.
- JWT HS256 verification on every authenticated request, checked before the body/id work.

## Not implemented yet

The current version deliberately starts with independent queries: each read connection has one
request in flight, and writes are one commit at a time. Java's top entry batches reads and groups
writes; those are the next optimizations, not correctness dependencies. Database reconnect after a
runtime loss is also not implemented; startup failure is reported as degraded and requests fail 500
(503 for health).

## Build and run

```bash
sudo bash install.sh
bash build.sh                 # JIT
AOT=1 bash build.sh           # NativeAOT
AOT=1 AOT_ISA=x86-64-v3 bash build.sh
DATABASE_URL=postgres://user:pass@db:5432/feed \
JWT_SECRET=million-users-challenge HOST=0.0.0.0 PORT=80 EXTRA_PORT=81 \
bash start.sh
```

Environment:

| Variable | Default | Meaning |
|---|---|---|
| `LOOPS` / `IOXIDE_REACTORS` | `ProcessorCount` | event-loop threads |
| `DB_POOL` | `64` | total Postgres connections |
| `GZIP` | `1` | `0` disables response gzip |
| `GZIP_MIN` | `1024` | smallest body to gzip |
| `DOTNET_gcServer` | `0` | workstation GC, no background GC |
| `DOTNET_GCHeapHardLimitPercent` | `0x50` | managed heap cap |

## Validation

The official suite passes in both builds on a fresh seed database:

- JIT: **42/42**
- NativeAOT (`AOT_ISA=x86-64-v3`): **42/42**

No performance result is claimed from the local desktop. The workload comparison belongs on the
separate VPS and Postgres boxes.
