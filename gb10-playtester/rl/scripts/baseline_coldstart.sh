#!/usr/bin/env bash
# Cold-start baseline for Gate 2 comparison — prd-ml.md §5.
# Locked CLI contract: --level-id --checkpoint-out --num-envs --output-manifest
# Must be launched against the SAME level and (ideally) same seed as the
# corresponding finetune_stage2.sh run so the comparison is apples-to-apples
# (spec §7 Gate 2) — pass --seed explicitly to both if you want a controlled
# comparison rather than the CLI's default seed.
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
uv run --project "$PROJECT_DIR" python -m playtester_rl.cli coldstart "$@"
