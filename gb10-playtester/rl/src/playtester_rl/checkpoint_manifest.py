"""Read/write helper for contracts/checkpoint_manifest.schema.json (PRD.md §3.2).

The manifest is a JSON file containing a top-level array of per-level entries.
`rl/scripts/*.sh` append/update one entry per level as each training stage
completes; `gate_eval.gate2_check` and the Unity Editor tool's checkpoint
dropdown (prd-unity.md §4) read from it.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import jsonschema
from filelock import FileLock

CONTRACTS_DIR = Path(__file__).resolve().parents[3] / "contracts"
MANIFEST_SCHEMA_PATH = CONTRACTS_DIR / "checkpoint_manifest.schema.json"


class ManifestValidationError(ValueError):
    """Raised when a checkpoint manifest entry does not conform to
    contracts/checkpoint_manifest.schema.json."""


def _load_schema() -> dict[str, Any]:
    with open(MANIFEST_SCHEMA_PATH, encoding="utf-8") as f:
        return json.load(f)


def validate_manifest_entry(entry: dict[str, Any]) -> None:
    schema = _load_schema()
    try:
        jsonschema.validate(instance=entry, schema=schema)
    except jsonschema.ValidationError as e:
        raise ManifestValidationError(str(e)) from e


def _empty_entry(level_id: str) -> dict[str, Any]:
    return {
        "level_id": level_id,
        "stage1_checkpoint": None,
        "stage2_checkpoint": None,
        "onnx_export_path": None,
        "stage1_metrics": None,
        "stage2_metrics": None,
        "coldstart_baseline_metrics": None,
    }


def load_manifest(path: Path) -> list[dict[str, Any]]:
    """Returns [] if the manifest file doesn't exist yet (first run of the
    hackathon has no manifest — that's a valid starting state, not an error)."""
    if not path.exists():
        return []
    with open(path, encoding="utf-8") as f:
        entries = json.load(f)
    for entry in entries:
        validate_manifest_entry(entry)
    return entries


def save_manifest(entries: list[dict[str, Any]], path: Path) -> None:
    for entry in entries:
        validate_manifest_entry(entry)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(entries, f, indent=2)


def upsert_entry_field(path: Path, level_id: str, field: str, value: Any) -> dict[str, Any]:
    """Updates (or creates) the entry for `level_id`, setting `field` to `value`.
    Used by training scripts to record e.g. stage1_checkpoint or stage1_metrics
    as each stage completes, without clobbering fields other stages already wrote.
    Returns the updated entry.

    Locked (via a sidecar .lock file) around the full read-modify-write —
    rl/scripts/run_concurrent_demo.sh deliberately runs two training
    processes in parallel against the SAME manifest path (PRD.md §7's
    concurrent-parallelism demo), and an unlocked read-modify-write here
    reliably corrupts the manifest under that exact usage pattern (verified:
    without the lock, one process's write raced the other's and the file
    round-tripped one process's data as invalid JSON). The lock is scoped to
    this one call, not held across unrelated manifest reads elsewhere.
    """
    lock_path = str(path) + ".lock"
    with FileLock(lock_path, timeout=30):
        entries = load_manifest(path)
        entry = next((e for e in entries if e["level_id"] == level_id), None)
        if entry is None:
            entry = _empty_entry(level_id)
            entries.append(entry)

        if field not in entry:
            raise ManifestValidationError(f"Unknown manifest field: {field!r}")

        entry[field] = value
        validate_manifest_entry(entry)
        save_manifest(entries, path)
        return entry


def get_entry(path: Path, level_id: str) -> dict[str, Any] | None:
    entries = load_manifest(path)
    return next((e for e in entries if e["level_id"] == level_id), None)
