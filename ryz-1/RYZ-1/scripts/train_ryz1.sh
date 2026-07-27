#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VENV="${RYZ1_VENV:-.venv-ryz1}"
DATASET="${RYZ1_DATASET:-Library/RYZ1/runs/smoke/dataset.json}"
CONFIG="${RYZ1_TRAIN_CONFIG:-python/ryz1/configs/smoke.json}"
OUT="${RYZ1_MODEL_DIR:-Library/RYZ1/models/smoke}"

if [[ ! -f "$VENV/bin/activate" ]]; then
  echo "error: virtualenv $VENV not found. Run scripts/setup_gb10.sh first." >&2
  exit 10
fi
if [[ ! -f "$DATASET" ]]; then
  echo "error: dataset not found at $DATASET. Run scripts/generate_dataset.sh first." >&2
  exit 11
fi

source "$VENV/bin/activate"
ryz1-validate-dataset "$DATASET"
ryz1-train --dataset "$DATASET" --config "$CONFIG" --out "$OUT"
