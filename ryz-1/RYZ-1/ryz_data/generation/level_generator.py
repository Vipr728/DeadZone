from __future__ import annotations
import random
from typing import Any

def generate_level(rng: random.Random, mechanics: dict[str, Any], scenario_id: str = "precision_dash_gate") -> dict[str, Any]:
    """Reachable stepped route; optional hazards add branch diversity without blocking it."""
    direction = rng.choice([-1, 1]); step = direction * rng.uniform(2.4, 3.6)
    count = rng.randint(3, 5)
    platforms = [[-3.0, 3.0, 0.0]]
    x, y = 0.0, 0.0
    for _ in range(count):
        x += step; y += rng.uniform(0.0, 0.55)
        width = rng.uniform(2.2, 3.5)
        platforms.append([x - width / 2, x + width / 2, y])
    goal = [x, y + 1.0]
    # hazards are below route, never overlap a platform top.
    spikes = [[min(0.8, step) * direction, max(0.8, step) * direction, -1.0, -0.7]]
    entities: list[dict[str, Any]] = []
    # Entity representation is intentionally data-only: it maps directly to native SimCore DTOs.
    if scenario_id in {"timed_moving_platform", "platform_chain", "missed_platform_recovery", "retracting_bridge"}:
        entities.append({"id": "moving-0", "kind": "moving_platform", "position": [step / 2, 0.45],
                         "size": [2.0, 0.25], "path": [[step / 2, 0.45], [step * 1.4, 0.45]], "period_frames": 90})
    if (any(token in scenario_id for token in ("key", "door", "artifact", "collect_all"))
            and "switch" not in scenario_id and "generator" not in scenario_id):
        entities += [{"id": "key-0", "kind": "key", "position": [step, 1.1], "key_id": "amber"},
                     {"id": "door-0", "kind": "door", "position": goal, "key_id": "amber", "locked": True}]
    if "switch" in scenario_id or "generator" in scenario_id:
        entities += [{"id": "switch-0", "kind": "switch", "position": [step, 1.0], "target_id": "door-0", "timed_frames": 120},
                     {"id": "door-0", "kind": "door", "position": goal, "locked": True}]
    if any(token in scenario_id for token in ("resource", "stamina", "dash_charge", "powerup")):
        entities.append({"id":"resource-0", "kind":"resource_pickup", "position":[step,1.1], "resource":"stamina", "amount":50})
    if any(token in scenario_id for token in ("checkpoint", "recovery")):
        entities.append({"id":"checkpoint-0", "kind":"checkpoint", "position":[step,1.1]})
    return {"start": [0.0, 1.0], "goal": goal, "platforms": platforms, "spikes": spikes, "entities": entities,
            "kill_y": -7.0, "difficulty": count, "route_metadata": {"direction": direction,
            "required_mechanics": [], "optional_routes": 0, "scenario_id": scenario_id}}

def validate_level(level: dict[str, Any]) -> None:
    start, goal = level["start"], level["goal"]
    if start == goal or not level["platforms"]: raise ValueError("invalid start/goal or empty level")
    for left, right, _top in level["platforms"]:
        if left >= right: raise ValueError("invalid platform geometry")
    if not any(left <= start[0] <= right for left, right, _ in level["platforms"]):
        raise ValueError("start is not supported")
    entity_ids = [entity["id"] for entity in level.get("entities", [])]
    if len(entity_ids) != len(set(entity_ids)): raise ValueError("duplicate entity IDs")
