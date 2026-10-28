#!/usr/bin/env bash
# Ubuntu 24.04, run as root, idempotent: make sure the .NET 11 SDK (which includes the ASP.NET Core 11
# runtime) is on PATH. This submission installs .NET 11 itself.
set -euo pipefail
if command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks | grep -q '^11\.' \
   && dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 11\.'; then
  echo ".NET 11 already installed:"; dotnet --list-sdks; dotnet --list-runtimes
  exit 0
fi
apt-get update -qq
apt-get install -y -qq curl ca-certificates libicu74 >/dev/null
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 11.0 --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
dotnet --list-sdks; dotnet --list-runtimes
