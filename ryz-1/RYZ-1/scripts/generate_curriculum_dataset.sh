#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

OUT="${RYZ1_RUN_DIR:-Library/RYZ1/runs/curriculum}"
SEED="${RYZ1_SEED:-100}"
REPETITIONS="${RYZ1_CURRICULUM_REPETITIONS:-3}"
BEAM="${RYZ1_BEAM:-16}"
DEPTH="${RYZ1_DEPTH:-32}"
MAX_SEARCH_TICKS="${RYZ1_MAX_SEARCH_TICKS:-500000}"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "error: dotnet SDK is required to generate SimCore curriculum datasets." >&2
  exit 10
fi

dotnet run --project src/Ryz1.Runner/Ryz1.Runner.csproj -- \
  generate-curriculum \
  --seed "$SEED" \
  --repetitions "$REPETITIONS" \
  --beam "$BEAM" \
  --depth "$DEPTH" \
  --max-search-ticks "$MAX_SEARCH_TICKS" \
  --out "$OUT"
echo "dataset=$OUT/dataset.json"
