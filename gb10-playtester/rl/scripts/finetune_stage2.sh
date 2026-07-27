#!/usr/bin/env bash
# Stage 2 fine-tune from a Stage 1 checkpoint — prd-ml.md §5.
# Locked CLI contract: --level-id --checkpoint-in --checkpoint-out --num-envs --output-manifest
# This is also the exact command the Unity Editor tool's TrainingControlPanel
# subprocess-invokes (prd-unity.md §4) — do not rename these flags independently.
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
uv run --project "$PROJECT_DIR" python -m playtester_rl.cli stage2 "$@"
