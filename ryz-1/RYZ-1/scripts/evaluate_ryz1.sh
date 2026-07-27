#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VENV="${RYZ1_VENV:-.venv-ryz1}"
DATASET="${RYZ1_DATASET:-Library/RYZ1/runs/smoke/dataset.json}"
CHECKPOINT="${RYZ1_CHECKPOINT:-Library/RYZ1/models/smoke/checkpoint.pt}"
OUT="${RYZ1_EVAL_REPORT:-Library/RYZ1/reports/eval.json}"

source "$VENV/bin/activate"
ryz1-evaluate --dataset "$DATASET" --checkpoint "$CHECKPOINT" --out "$OUT"
