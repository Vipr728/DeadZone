#!/usr/bin/env bash
# Stage 1 generalizer training — prd-ml.md §5.
# Locked CLI contract: --level-id --checkpoint-out --num-envs --output-manifest
# Falls back to the fake trainer automatically when no Unity build exists yet
# (see rl/src/playtester_rl/cli.py's module docstring).
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
uv run --project "$PROJECT_DIR" python -m playtester_rl.cli stage1 "$@"
