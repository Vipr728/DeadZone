#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

scripts/verify_gb10_runtime.sh

if [[ -f ".venv-ryz1/bin/activate" ]]; then
  source .venv-ryz1/bin/activate
  python - <<'PY'
import platform
try:
    import torch
    print("torch", torch.__version__)
    print("cuda_available", torch.cuda.is_available())
    if torch.cuda.is_available():
        print("cuda_device", torch.cuda.get_device_name(0))
except Exception as exc:
    print("torch check failed:", exc)
print("python", platform.python_version(), platform.machine())
PY
else
  echo "Python venv not present; run scripts/setup_gb10.sh for training checks."
fi
