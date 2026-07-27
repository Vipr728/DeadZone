"""Telemetry writing/validation — prd-ml.md §3, contract at
contracts/telemetry.schema.json (PRD.md §3.1).

The real telemetry document for a live Unity run is written by the C#
TelemetryRecorder (prd-unity.md §6). This module exists so:
  1. RL-side analysis/gate-evaluation code can validate telemetry it reads.
  2. Fixture telemetry can be generated/validated without a live Unity run
     (infra's report-pipeline tests, this package's own pipeline tests).
  3. The `seen_in_stage1_range` heuristic (spec §4.1) has one canonical
     implementation, importable by both the fake-env test harness and any
     real analysis tooling.

Field convention note: the schema's piece_result.params only has `width` and
`height` (no generic `distance` field). move_to_goal pieces store their
traversal distance in `width` — this is a deliberate reuse, not a bug; encode
that same convention on the Unity side if authoring the C# writer independently.
"""

from __future__ import annotations

import json
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import jsonschema

from playtester_rl.config_loader import PieceConfig

CONTRACTS_DIR = Path(__file__).resolve().parents[3] / "contracts"
TELEMETRY_SCHEMA_PATH = CONTRACTS_DIR / "telemetry.schema.json"


class TelemetryValidationError(ValueError):
    """Raised when a telemetry document does not conform to
    contracts/telemetry.schema.json."""


def _load_schema() -> dict[str, Any]:
    with open(TELEMETRY_SCHEMA_PATH, encoding="utf-8") as f:
        return json.load(f)


def validate_telemetry(doc: dict[str, Any]) -> None:
    """Raises TelemetryValidationError on schema violation. Both /rl analysis
    code and /infra tests call this — it is the single point of truth for
    'is this telemetry document well-formed', so a shape drift on either side
    of the /rl <-> /infra boundary is caught immediately rather than surfacing
    as a confusing downstream KeyError."""
    schema = _load_schema()
    try:
        jsonschema.validate(instance=doc, schema=schema)
    except jsonschema.ValidationError as e:
        raise TelemetryValidationError(str(e)) from e


def compute_seen_in_stage1_range(piece_type: str, params: dict[str, float | None], piece_config: PieceConfig) -> bool:
    """The §4.1 'no prior teaching instance' heuristic, structural half:
    does this piece's parameter fall within the range Stage 1 was trained on
    for this piece type? (The *narrative* half — whether the LLM should also
    consider level-local novelty, e.g. 'first time this wide a gap appears in
    THIS level' — is the report pipeline's job, not this function's; this
    function only answers the Stage-1-training-range question.)

    Returns False (not an error) if the piece type is disabled in piece_config
    — an untrained piece type was, by definition, never 'seen' in Stage 1
    range, since it has no range at all.
    """
    piece_type_config = {
        "gap_jump": piece_config.gap_jump,
        "move_to_goal": piece_config.move_to_goal,
        "elevation": piece_config.elevation,
    }.get(piece_type)

    if piece_type_config is None:
        raise ValueError(f"Unknown piece_type: {piece_type!r}")

    if not piece_type_config.enabled:
        return False

    # gap_jump and move_to_goal key off 'width' (move_to_goal reuses width for
    # its traversal distance, per the field-convention note above); elevation
    # keys off 'height'.
    value = params.get("height") if piece_type == "elevation" else params.get("width")
    if value is None:
        return False

    lo, hi = piece_type_config.param_range
    return lo <= value <= hi


@dataclass
class TelemetryBuilder:
    """Accumulates one playtest run's telemetry incrementally (used by the
    fake-env test harness and, structurally, mirrors what the Unity-side
    TelemetryRecorder does episode-by-episode) and emits a schema-conformant
    document via `build()`."""

    level_id: str
    stage: str
    checkpoint_path: str
    timestamp_start: str
    run_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    episode_summaries: list[dict[str, Any]] = field(default_factory=list)

    def add_episode(
        self,
        episode_index: int,
        outcome: str,
        total_reward: float,
        time_to_clear_seconds: float | None,
        path_trace: list[dict[str, float]],
        piece_results: list[dict[str, Any]],
    ) -> None:
        if outcome not in ("success", "death", "timeout"):
            raise ValueError(f"Invalid outcome: {outcome!r}")
        self.episode_summaries.append(
            {
                "episode_index": episode_index,
                "outcome": outcome,
                "total_reward": total_reward,
                "time_to_clear_seconds": time_to_clear_seconds,
                "path_trace": path_trace,
                "piece_results": piece_results,
            }
        )

    def build(self) -> dict[str, Any]:
        doc = {
            "run_id": self.run_id,
            "level_id": self.level_id,
            "stage": self.stage,
            "checkpoint_path": self.checkpoint_path,
            "timestamp_start": self.timestamp_start,
            "episode_summaries": self.episode_summaries,
        }
        validate_telemetry(doc)
        return doc


def write_telemetry(doc: dict[str, Any], path: Path) -> None:
    """Validates before writing — a malformed telemetry file must never
    silently land on disk for the report pipeline to trip over later."""
    validate_telemetry(doc)
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2)


def read_telemetry(path: Path) -> dict[str, Any]:
    """Validates after reading — never hand back a document to a caller
    that doesn't conform, even if it was written by an external process
    (e.g. the Unity-side TelemetryRecorder)."""
    with open(path, encoding="utf-8") as f:
        doc = json.load(f)
    validate_telemetry(doc)
    return doc
