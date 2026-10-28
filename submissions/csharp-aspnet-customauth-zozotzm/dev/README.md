# Optional Aspire dev host

This folder is for local development. It is not used by `install.sh`, `build.sh`, `start.sh`, or the
benchmark test suite.

It runs:

- `postgres:18` in a container, with a persistent volume;
- a one-shot `feed-seed:local` container that generates `feed_seed` with the repo's own
  `seed/make-seed.sh`, then creates `feed` as a copy of it;
- this submission as a normal Aspire project (single worker, ports 3000/3001).

## Prerequisites

- Docker running
- .NET 10 SDK
- Aspire CLI (`aspire --version`)

## Run

```bash
bash dev/seed/build.sh
aspire start --apphost dev/FeedDevHost/FeedDevHost.csproj --launch-profile http
aspire wait api --status healthy --timeout 420
aspire logs api
```

The API is on `http://localhost:3000` and `http://localhost:3001`. The Aspire dashboard URL is printed
by `aspire start`.

```bash
aspire stop --apphost dev/FeedDevHost/FeedDevHost.csproj
```

The seed job is idempotent: it checks for `feed_seed` and for the `posts` table in `feed` before doing
work. To start over, remove the `million-users-pg` Docker volume after `aspire stop`.

## Notes

- `dev/seed/build.sh` passes the submission path as a Docker build argument, so the Dockerfile does not
  hard-code this folder's name.
- The seed image normalizes CRLF in the copied repo scripts; the benchmark checkout is LF, but a Windows
  checkout is not.
- `ASPIRE_ALLOW_UNSECURED_TRANSPORT=true` is set in the `http` launch profile for the Aspire dashboard.
