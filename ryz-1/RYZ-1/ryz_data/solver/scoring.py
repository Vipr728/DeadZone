from __future__ import annotations
import math
from typing import Any
from ..sim.state import Observation

def progress(observation: Observation, start: tuple[float, float]) -> float:
    gx, gy = observation.goal; sx, sy = start; px, py = observation.player.x, observation.player.y
    denom = max(1e-6, math.hypot(gx - sx, gy - sy))
    return ((px - sx) * (gx - sx) + (py - sy) * (gy - sy)) / (denom * denom)

def score(observation: Observation, *, start: tuple[float, float], cumulative_reward: float,
          completed: bool, died: bool, depth: int, prior_hashes: int = 0,
          weights: dict[str, float] | None = None) -> float:
    w = {"progress": 10., "height": 0.3, "distance": 1., "survival": 1., "completion": 100.,
         "death": 50., "loop": 1., "action_cost": .02, **(weights or {})}
    p = progress(observation, start); gx, gy = observation.goal
    distance = math.hypot(gx - observation.player.x, gy - observation.player.y)
    return (w["progress"] * p + w["height"] * observation.player.y - w["distance"] * distance +
            w["survival"] + cumulative_reward - w["action_cost"] * depth - w["loop"] * prior_hashes +
            (w["completion"] if completed else 0) - (w["death"] if died else 0))
