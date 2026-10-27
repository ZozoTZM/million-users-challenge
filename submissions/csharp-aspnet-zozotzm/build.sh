#!/usr/bin/env bash
# Framework-dependent Release publish into ./out (idempotent).
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet publish FeedApi.csproj -c Release -o out
