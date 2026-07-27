#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VENV="${RYZ1_VENV:-.venv-ryz1}"
CHECKPOINT="${RYZ1_CHECKPOINT:-Library/RYZ1/models/smoke/checkpoint.pt}"
OUT="${RYZ1_ONNX:-Library/RYZ1/models/ryz1.onnx}"

source "$VENV/bin/activate"
ryz1-export --checkpoint "$CHECKPOINT" --out "$OUT"
