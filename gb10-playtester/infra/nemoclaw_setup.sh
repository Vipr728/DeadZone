#!/usr/bin/env bash
set -euo pipefail

INFRA_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ -n "${NEMOCLAW_VERIFIED_INSTALLER_URL:-}" ]]; then
  echo "Using operator-supplied, verified NemoClaw installer URL."
  installer="$(mktemp)"
  trap 'rm -f "$installer"' EXIT
  curl --proto '=https' --tlsv1.2 -fsSL "$NEMOCLAW_VERIFIED_INSTALLER_URL" -o "$installer"
  bash "$installer"
  exit 0
fi

echo "NemoClaw sponsor integration is not verified; configuring the honest local Ollama fallback."

if ! command -v ollama >/dev/null 2>&1; then
  if [[ "${INSTALL_OLLAMA:-0}" != "1" ]]; then
    echo "Ollama is not installed. Re-run with INSTALL_OLLAMA=1 to install it, or install it manually."
    exit 1
  fi
  if command -v brew >/dev/null 2>&1; then
    brew install ollama
  elif [[ "$(uname -s)" == "Linux" ]]; then
    curl --proto '=https' --tlsv1.2 -fsSL https://ollama.com/install.sh | sh
  else
    echo "Automatic Ollama installation is unsupported on this OS."
    exit 1
  fi
fi

model="$(
  cd "$INFRA_DIR"
  uv run python -c 'from playtester_infra.config import load_config; print(load_config().llm.selected_model)'
)"

uv run --project "$INFRA_DIR" python -c '
from playtester_infra.config import load_config
config = load_config()
for path in (
    config.paths.watched_levels_dir,
    config.paths.telemetry_dir,
    config.paths.reports_dir,
    config.orchestration.checkpoint_out_dir,
):
    path.mkdir(parents=True, exist_ok=True)
    print(f"ready: {path}")
'

if ! ollama list >/dev/null 2>&1; then
  echo "Ollama is installed but not reachable. Start `ollama serve`, then rerun this script."
  exit 1
fi

ollama pull "$model"
echo "Local fallback ready with model: $model"
echo "Runtime label: Ollama local fallback (not NemoClaw)."
