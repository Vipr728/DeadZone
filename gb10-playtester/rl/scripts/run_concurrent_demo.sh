#!/usr/bin/env bash
# Concurrent-parallelism GB10 demo — prd-ml.md §5 / PRD.md §7.
# Launches two independent training processes (level_a and level_b) truly in
# parallel — separate subprocesses, NOT a --num-envs split across levels, per
# the locked design decision — and captures the real wall-clock time for
# both to finish. Do not inflate this number; report exactly what is measured
# (spec §6).
#
# Usage:
#   run_concurrent_demo.sh \
#     --level-a-id level_a --checkpoint-in-a <path> --checkpoint-out-a <path> \
#     --level-b-id level_b --checkpoint-in-b <path> --checkpoint-out-b <path> \
#     --output-manifest <path> --results-file <path> \
#     [--execution-mode remote] [--remote-config <path>] [--episodes N] [--seed N]
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

LEVEL_A_ID=""
LEVEL_B_ID=""
CHECKPOINT_IN_A=""
CHECKPOINT_IN_B=""
CHECKPOINT_OUT_A=""
CHECKPOINT_OUT_B=""
OUTPUT_MANIFEST=""
RESULTS_FILE=""
EPISODES=150
SEED=0
EXECUTION_MODE="${PLAYTESTER_RL_EXECUTION_MODE:-remote}"
REMOTE_CONFIG="${PLAYTESTER_REMOTE_CONFIG:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --level-a-id) LEVEL_A_ID="$2"; shift 2 ;;
    --level-b-id) LEVEL_B_ID="$2"; shift 2 ;;
    --checkpoint-in-a) CHECKPOINT_IN_A="$2"; shift 2 ;;
    --checkpoint-in-b) CHECKPOINT_IN_B="$2"; shift 2 ;;
    --checkpoint-out-a) CHECKPOINT_OUT_A="$2"; shift 2 ;;
    --checkpoint-out-b) CHECKPOINT_OUT_B="$2"; shift 2 ;;
    --output-manifest) OUTPUT_MANIFEST="$2"; shift 2 ;;
    --results-file) RESULTS_FILE="$2"; shift 2 ;;
    --episodes) EPISODES="$2"; shift 2 ;;
    --seed) SEED="$2"; shift 2 ;;
    --execution-mode) EXECUTION_MODE="$2"; shift 2 ;;
    --remote-config) REMOTE_CONFIG="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

for required in LEVEL_A_ID LEVEL_B_ID CHECKPOINT_IN_A CHECKPOINT_IN_B CHECKPOINT_OUT_A CHECKPOINT_OUT_B OUTPUT_MANIFEST RESULTS_FILE; do
  if [[ -z "${!required}" ]]; then
    echo "Missing required argument for: $required" >&2
    exit 1
  fi
done

START_EPOCH=$(date +%s)

COMMON_ARGS=(--execution-mode "$EXECUTION_MODE")
if [[ -n "$REMOTE_CONFIG" ]]; then
  COMMON_ARGS+=(--remote-config "$REMOTE_CONFIG")
fi

uv run --project "$PROJECT_DIR" python -m playtester_rl.cli stage2 \
  --level-id "$LEVEL_A_ID" --checkpoint-in "$CHECKPOINT_IN_A" --checkpoint-out "$CHECKPOINT_OUT_A" \
  --output-manifest "$OUTPUT_MANIFEST" --episodes "$EPISODES" --seed "$SEED" \
  "${COMMON_ARGS[@]}" &
PID_A=$!

uv run --project "$PROJECT_DIR" python -m playtester_rl.cli stage2 \
  --level-id "$LEVEL_B_ID" --checkpoint-in "$CHECKPOINT_IN_B" --checkpoint-out "$CHECKPOINT_OUT_B" \
  --output-manifest "$OUTPUT_MANIFEST" --episodes "$EPISODES" --seed "$SEED" \
  "${COMMON_ARGS[@]}" &
PID_B=$!

wait "$PID_A"
wait "$PID_B"

END_EPOCH=$(date +%s)
WALL_CLOCK_SECONDS=$((END_EPOCH - START_EPOCH))

mkdir -p "$(dirname "$RESULTS_FILE")"
cat > "$RESULTS_FILE" <<EOF
{
  "level_a_id": "$LEVEL_A_ID",
  "level_b_id": "$LEVEL_B_ID",
  "wall_clock_seconds": $WALL_CLOCK_SECONDS,
  "episodes_per_level": $EPISODES
}
EOF

echo "[run_concurrent_demo] Wall clock: ${WALL_CLOCK_SECONDS}s for both levels. Results written to $RESULTS_FILE"
