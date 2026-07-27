#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

MODE="${RYZ1_DEMO_MODE:-live-smoke}"
RUN_DIR="${RYZ1_RUN_DIR:-Library/RYZ1/runs/hackathon-demo}"
MODEL_DIR="${RYZ1_MODEL_DIR:-Library/RYZ1/models/hackathon-demo}"

echo "RYZ-1 hackathon demo mode: $MODE"
RYZ1_RUN_DIR="$RUN_DIR" scripts/generate_dataset.sh

if [[ "$MODE" == "pretrained" ]]; then
  if [[ ! -f "$MODEL_DIR/checkpoint.pt" ]]; then
    echo "error: pretrained mode selected but $MODEL_DIR/checkpoint.pt is missing." >&2
    exit 20
  fi
else
  RYZ1_MODEL_DIR="$MODEL_DIR" RYZ1_DATASET="$RUN_DIR/dataset.json" scripts/train_ryz1.sh
fi

RYZ1_MODEL_DIR="$MODEL_DIR" RYZ1_CHECKPOINT="$MODEL_DIR/checkpoint.pt" RYZ1_DATASET="$RUN_DIR/dataset.json" scripts/evaluate_ryz1.sh
echo "Demo artifacts: $RUN_DIR and $MODEL_DIR"
