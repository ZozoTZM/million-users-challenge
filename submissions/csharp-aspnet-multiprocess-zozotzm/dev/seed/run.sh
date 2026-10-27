#!/usr/bin/env bash
set -euo pipefail

until pg_isready -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" >/dev/null 2>&1; do
  sleep 1
done

if psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = 'feed_seed'" | grep -q 1; then
  echo "feed_seed already exists; not regenerating"
else
  bash /app/seed/make-seed.sh
fi

# Aspire creates the empty "feed" database before this job runs, so check for the schema,
# not just the database name.
if ! psql -d feed -tAc "SELECT to_regclass('public.posts')" 2>/dev/null | grep -q posts; then
  psql -v ON_ERROR_STOP=1 -d postgres \
    -c "DROP DATABASE IF EXISTS feed WITH (FORCE)" \
    -c "CREATE DATABASE feed TEMPLATE feed_seed"
fi

echo "seed container done"
