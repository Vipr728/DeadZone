#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PYTHON="${PYTHON:-python3}"
VENV="${RYZ1_VENV:-.venv-ryz1}"

if ! command -v "$PYTHON" >/dev/null 2>&1; then
  echo "error: python3 is required." >&2
  exit 10
fi

"$PYTHON" -m venv "$VENV"
source "$VENV/bin/activate"
python -m pip install --upgrade pip
python -m pip install -e "python/ryz1[test]"
echo "RYZ-1 Python environment ready at $VENV"
