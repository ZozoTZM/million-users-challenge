#!/usr/bin/env bash
# Release publish into ./out.
#
#   bash build.sh          framework-dependent JIT build (the submission default)
#   AOT=1 bash build.sh    Linux x64 NativeAOT experiment
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
rm -rf out

if [ "${AOT:-0}" = 1 ]; then
  ISA_ARG="${AOT_ISA:+-p:IlcInstructionSet=$AOT_ISA}"
  dotnet publish FeedApi.csproj -c Release -r linux-x64 \
    -p:PublishAot=true \
    -p:OptimizationPreference=Speed \
    $ISA_ARG \
    -o out
else
  dotnet publish FeedApi.csproj -c Release -o out
fi
