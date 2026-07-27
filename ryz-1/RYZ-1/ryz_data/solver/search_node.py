from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any
from ..sim.actions import ActionMacro
from ..sim.state import Observation, SimSnapshot

@dataclass
class SearchNode:
    node_id: int; parent_id: int | None; depth: int; snapshot: SimSnapshot; state_hash: str
    observation: Observation; action_from_parent: ActionMacro | None; immediate_reward: float
    cumulative_reward: float; heuristic_score: float; search_score: float; terminal: bool; completed: bool; died: bool
    prune_reason: str | None = None; branch_status: str = "unknown"; candidate_scores: list[float] = field(default_factory=list)
    candidate_validity: list[bool] = field(default_factory=list); candidate_actions: list[ActionMacro] = field(default_factory=list)
    events: list[dict[str, Any]] = field(default_factory=list); progress_delta: float = 0.0
    source_type: str = "high_budget_solver"
    metadata: dict[str, Any] = field(default_factory=dict)
