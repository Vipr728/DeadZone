"""The narrow, cloneable interface the native SimCore adapter must satisfy."""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Any

from .actions import PrimitiveAction
from .state import Observation, SimSnapshot, StepResult


@dataclass(frozen=True)
class TaskHandle:
    task_id: str
    opaque: Any = None


@dataclass(frozen=True)
class TaskConfig:
    task_id: str
    seed: int
    level: dict[str, Any]
    mechanics: dict[str, Any]
    controls: dict[str, int]


class SimCoreInterface(ABC):
    @abstractmethod
    def create_task(self, task_config: TaskConfig) -> TaskHandle: ...
    @abstractmethod
    def reset_task(self, task: TaskHandle, seed: int | None = None) -> Observation: ...
    @abstractmethod
    def step(self, action: PrimitiveAction, frames: int = 1) -> StepResult: ...
    @abstractmethod
    def clone_state(self) -> SimSnapshot: ...
    @abstractmethod
    def restore_state(self, snapshot: SimSnapshot) -> None: ...
    @abstractmethod
    def hash_state(self) -> str: ...
    @abstractmethod
    def get_observation(self) -> Observation: ...
    @abstractmethod
    def get_ground_truth_mechanics(self) -> dict[str, Any]: ...
    @abstractmethod
    def is_terminal(self) -> bool: ...
    @abstractmethod
    def close(self) -> None: ...
