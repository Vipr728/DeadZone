from __future__ import annotations
import random

SEMANTIC_ACTIONS = ("move_left", "move_right", "jump", "dash", "climb", "drop")

def randomize_controls(rng: random.Random, mechanics: dict[str, object]) -> dict[str, int]:
    actions = ["move_left", "move_right", "jump", "drop", "interact"]
    if mechanics.get("dash_enabled"): actions.append("dash")
    if mechanics.get("climbing"): actions.append("climb")
    ids = list(range(1, len(actions) + 1)); rng.shuffle(ids)
    return dict(zip(actions, ids, strict=True))
