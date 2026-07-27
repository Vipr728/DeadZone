from __future__ import annotations
from dataclasses import dataclass, field
from ..dataset.schema import TransitionRecord
from ..sim.actions import ActionMacro
from ..sim.state import Observation, StepResult

@dataclass
class TrajectoryCollector:
    records: list[TransitionRecord] = field(default_factory=list)
    def add_step(self, **kwargs: object) -> None:
        self.records.append(TransitionRecord(**kwargs))  # type: ignore[arg-type]
