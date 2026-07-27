from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
REGISTRY_PATH = ROOT / "models" / "registry.json"


def _expand(value: str) -> Path:
    replacements = {
        "${GB10_PROJECT_ROOT}": os.getenv("GB10_PROJECT_ROOT", "/home/dell/gb10-project"),
        "${RYZ1_PROJECT_ROOT}": os.getenv("RYZ1_PROJECT_ROOT", "/home/dell/Ryzi-labs/RYZ-1"),
    }
    for marker, replacement in replacements.items():
        value = value.replace(marker, replacement)
    return Path(value).expanduser()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_registry(*, verify: bool = False) -> dict[str, Any]:
    data = json.loads(REGISTRY_PATH.read_text(encoding="utf-8"))
    for model in data["models"]:
        for key in ("checkpoint", "onnx"):
            artifact = model.get(key)
            if not artifact:
                continue
            path = _expand(artifact["source"])
            artifact["resolved_path"] = str(path)
            artifact["available"] = path.is_file()
            artifact["verified"] = (
                artifact["available"] and sha256_file(path) == artifact["sha256"]
                if verify
                else None
            )
    for dataset in data.get("datasets", []):
        path = _expand(dataset["source"])
        dataset["resolved_path"] = str(path)
        dataset["available"] = path.is_file()
    return data


def model_by_id(model_id: str, *, verify: bool = False) -> dict[str, Any]:
    registry = load_registry(verify=verify)
    for model in registry["models"]:
        if model["id"] == model_id:
            return model
    raise KeyError(f"Unknown model id: {model_id}")

