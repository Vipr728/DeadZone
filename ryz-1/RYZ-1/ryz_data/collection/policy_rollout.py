from __future__ import annotations
from dataclasses import dataclass
from typing import Protocol
from ..generation.manifest_builder import MechanicsManifest
from ..sim.actions import ActionMacro
from ..sim.state import Observation

@dataclass(frozen=True)
class PolicyOutput:
    action_index: int; probabilities: list[float] | None = None; uncertainty: float | None = None; hidden_state_id: str | None = None
class PolicyInterface(Protocol):
    def reset_memory(self) -> None: ...
    def begin_task(self, manifest: MechanicsManifest) -> None: ...
    def begin_trial(self, memory: object) -> None: ...
    def select_action(self, observation: Observation, candidate_actions: list[ActionMacro]) -> PolicyOutput: ...
