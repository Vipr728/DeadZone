#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "RYZ-1 GB10 native runtime verification"
echo "arch=$(uname -m)"
echo "os=$(uname -s)"

if [[ "$(uname -m)" != "aarch64" && "$(uname -m)" != "arm64" ]]; then
  echo "warning: this host is not ARM64; GB10 publish can still be cross-published from a supported SDK."
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet SDK is required for Ryz1.SimCore native runtime verification." >&2
  echo "Install a .NET 8+ SDK for linux-arm64 on GB10, then rerun this script." >&2
  exit 10
fi

dotnet --info
dotnet test tests/Ryz1.SimCore.Tests/Ryz1.SimCore.Tests.csproj
dotnet run --project src/Ryz1.Runner/Ryz1.Runner.csproj -- --out Library/RYZ1/runs/verify

if command -v python3 >/dev/null 2>&1; then
  python3 --version
else
  echo "warning: python3 not found; training verification skipped."
fi
