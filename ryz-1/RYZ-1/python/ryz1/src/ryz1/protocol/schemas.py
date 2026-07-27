from __future__ import annotations

from dataclasses import dataclass
from typing import Any


TASK_BUNDLE_SCHEMA = "ryz-task-bundle/1.0"
DATASET_SCHEMA = "ryz-search-dataset/1.0"
REPLAY_SCHEMA = "ryz-replay/1.0"


@dataclass(frozen=True)
class DatasetShape:
    player_vector_size: int
    mechanics_vector_size: int
    macro_count: int


def require_keys(value: dict[str, Any], keys: list[str], label: str) -> None:
    missing = [key for key in keys if key not in value]
    if missing:
        raise ValueError(f"{label} missing required key(s): {', '.join(missing)}")
