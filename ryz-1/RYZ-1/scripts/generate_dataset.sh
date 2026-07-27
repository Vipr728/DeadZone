#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

OUT="${RYZ1_RUN_DIR:-Library/RYZ1/runs/smoke}"
SEED="${RYZ1_SEED:-7}"
BEAM="${RYZ1_BEAM:-12}"
DEPTH="${RYZ1_DEPTH:-24}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet SDK is required to generate SimCore datasets." >&2
  exit 10
fi

dotnet run --project src/Ryz1.Runner/Ryz1.Runner.csproj -- --seed "$SEED" --beam "$BEAM" --depth "$DEPTH" --out "$OUT"
echo "dataset=$OUT/dataset.json"
