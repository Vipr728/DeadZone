"""Versioned normalized dataset records and stable JSON conversion."""
from __future__ import annotations
import dataclasses
import time
from dataclasses import dataclass, field
from typing import Any

SCHEMA_VERSION = "1.0.0"

@dataclass
class TransitionRecord:
    schema_version: str = SCHEMA_VERSION; task_id: str = ""; trial_id: str = ""; trajectory_id: str = ""; transition_id: str = ""
    source_type: str = "unknown"; branch_status: str = "unknown"; task_seed: int = 0; trial_index: int = 0; step_index: int = 0
    ground_truth_manifest: dict[str, Any] = field(default_factory=dict); inferred_manifest: dict[str, Any] = field(default_factory=dict)
    manifest_visibility_mask: dict[str, bool] = field(default_factory=dict); player_state: dict[str, Any] = field(default_factory=dict)
    local_geometry: dict[str, Any] = field(default_factory=dict); nearby_entities: list[dict[str, Any]] = field(default_factory=list)
    global_task_features: dict[str, Any] = field(default_factory=dict); memory_context: dict[str, Any] = field(default_factory=dict)
    previous_action: dict[str, Any] | None = None; previous_reward: float = 0.; previous_events: list[dict[str, Any]] = field(default_factory=list)
    candidate_action: dict[str, Any] = field(default_factory=dict); candidate_action_index: int = 0; candidate_validity_mask: list[bool] = field(default_factory=list)
    resulting_player_state: dict[str, Any] = field(default_factory=dict); resulting_local_geometry: dict[str, Any] = field(default_factory=dict)
    resulting_entities: list[dict[str, Any]] = field(default_factory=list); immediate_reward: float = 0.; progress_delta: float = 0.
    events: list[dict[str, Any]] = field(default_factory=list); died: bool = False; completed: bool = False; terminal: bool = False
    search_score: float = 0.; cumulative_search_score: float = 0.; prune_reason: str | None = None
    branch_eventually_solved: bool | None = None; steps_to_verified_completion: int | None = None; best_verified_descendant_score: float | None = None
    teacher_policy: list[float] | None = None; teacher_value: dict[str, float | bool | None] = field(default_factory=dict)
    dynamics_target: dict[str, Any] = field(default_factory=dict); solver_budget: dict[str, int | float] = field(default_factory=dict)
    calibration_confidence: dict[str, float] = field(default_factory=dict); parent_transition_id: str | None = None; timestamp_ns: int = field(default_factory=time.time_ns)
    def to_dict(self) -> dict[str, Any]: return dataclasses.asdict(self)

def task_split(seed: int, split_seed: int = 1337) -> str:
    # deterministic split by task seed; no transition-level leakage.
    bucket = (seed * 1103515245 + split_seed) % 100
    return "train" if bucket < 90 else "validation" if bucket < 95 else "test"

def validate_transition(record: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    for key in ("task_id", "trial_id", "trajectory_id", "transition_id", "candidate_action", "player_state"):
        if key not in record or record[key] in (None, ""): errors.append(f"missing {key}")
    if record.get("completed") and not record.get("terminal"): errors.append("completion must be terminal")
    policy = record.get("teacher_policy")
    if policy is not None and policy and abs(sum(policy) - 1) > 1e-4: errors.append("policy does not sum to one")
    if record.get("branch_status") == "perturbed" and "perturbation" not in record.get("dynamics_target", {}): errors.append("missing perturbation metadata")
    return errors
