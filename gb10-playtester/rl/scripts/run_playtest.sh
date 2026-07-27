#!/usr/bin/env bash
# Locked OpenClaw -> Unity playtest contract. Real checkpoint markers are
# replayed through ML-Agents inference; fake markers run a structural smoke.
set -euo pipefail

level_id=""
checkpoint_in=""
episodes=""
telemetry_out=""
execution_mode="${PLAYTESTER_RL_PLAYBACK_MODE:-local}"
remote_config="${PLAYTESTER_REMOTE_CONFIG:-}"
remote_port="${PLAYTESTER_REMOTE_PORT:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --level-id) level_id="$2"; shift 2 ;;
    --checkpoint-in) checkpoint_in="$2"; shift 2 ;;
    --episodes) episodes="$2"; shift 2 ;;
    --telemetry-out) telemetry_out="$2"; shift 2 ;;
    --execution-mode) execution_mode="$2"; shift 2 ;;
    --remote-config) remote_config="$2"; shift 2 ;;
    --remote-port) remote_port="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$level_id" || -z "$checkpoint_in" || -z "$episodes" || -z "$telemetry_out" ]]; then
  echo "Usage: $0 --level-id ID --checkpoint-in PATH --episodes N --telemetry-out PATH" >&2
  exit 2
fi
if [[ ! "$level_id" =~ ^[A-Za-z0-9_-]+$ ]] || [[ ! "$episodes" =~ ^[1-9][0-9]*$ ]]; then
  echo "level-id must be safe and episodes must be a positive integer" >&2
  exit 2
fi
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
playback_args=(
  --level-id "$level_id"
  --checkpoint-in "$checkpoint_in"
  --episodes "$episodes"
  --telemetry-out "$telemetry_out"
  --execution-mode "$execution_mode"
)
if [[ -n "$remote_config" ]]; then
  playback_args+=(--remote-config "$remote_config")
fi
if [[ -n "$remote_port" ]]; then
  playback_args+=(--remote-port "$remote_port")
fi
uv run --project "$PROJECT_DIR" python -m playtester_rl.playback "${playback_args[@]}"
