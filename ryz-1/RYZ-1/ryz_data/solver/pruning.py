from __future__ import annotations
from ..sim.state import Observation

def heuristic_prune(observation: Observation, depth: int, max_depth: int) -> str | None:
    if observation.player.y < -6.5: return "below_kill_zone"
    if depth >= max_depth: return "depth_limit"
    return None
