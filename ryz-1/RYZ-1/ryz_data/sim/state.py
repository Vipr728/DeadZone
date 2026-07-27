"""Portable observation and snapshot protocol types."""
from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any


@dataclass(frozen=True)
class PlayerState:
    x: float
    y: float
    vx: float
    vy: float
    grounded: bool = False
    wall_left: bool = False
    wall_right: bool = False
    ceiling: bool = False
    air_jumps_left: int = 0
    dash_charges: int = 0
    dash_cooldown: int = 0
    resources: dict[str, float] = field(default_factory=dict)


@dataclass(frozen=True)
class Observation:
    player: PlayerState
    goal: tuple[float, float]
    local_geometry: dict[str, Any] = field(default_factory=dict)
    nearby_entities: tuple[dict[str, Any], ...] = ()
    frame: int = 0
    state_machine: str = "airborne"

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


@dataclass(frozen=True)
class SimSnapshot:
    """Opaque simulation state. Native adapters may use a compact bytes payload."""
    payload: Any


@dataclass(frozen=True)
class StepResult:
    observation: Observation
    reward: float
    events: tuple[dict[str, Any], ...]
    terminal: bool
    completed: bool
    died: bool
    elapsed_frames: int
