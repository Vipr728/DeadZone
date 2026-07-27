#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

RID="${RYZ1_RUNTIME_ID:-linux-arm64}"
CONFIG="${RYZ1_DOTNET_CONFIG:-Release}"
OUT="${RYZ1_PUBLISH_DIR:-artifacts/gb10}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet SDK 8+ is required to publish RYZ-1 native runtime." >&2
  exit 10
fi

mkdir -p "$OUT"
dotnet publish src/Ryz1.Runner/Ryz1.Runner.csproj -c "$CONFIG" -r "$RID" --self-contained false -o "$OUT/runner"
dotnet publish src/Ryz1.ScanCli/Ryz1.ScanCli.csproj -c "$CONFIG" -r "$RID" --self-contained false -o "$OUT/scancli"
echo "Published RYZ-1 GB10 runtime to $OUT"
