#!/usr/bin/env bash
# Release publish into ./out.
#
#   bash build.sh          framework-dependent JIT build (the submission default)
#   AOT=1 bash build.sh    Linux x64 NativeAOT build for the VM A/B
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [ "${AOT:-0}" = 1 ]; then
  dotnet publish LeanApi.csproj -c Release -r linux-x64 \
    -p:PublishAot=true \
    -p:OptimizationPreference=Speed \
    -o out
else
  dotnet publish LeanApi.csproj -c Release -o out
fi
