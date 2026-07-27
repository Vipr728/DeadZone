from __future__ import annotations

import random
from dataclasses import dataclass
from typing import Any


DEFAULT_RANGES: dict[str, tuple[float, float]] = {
    "gravity": (0.18, 0.32), "max_fall_speed": (3.0, 6.0),
    "ground_acceleration": (0.18, 0.35), "ground_deceleration": (0.15, 0.35),
    "max_run_speed": (0.45, 0.9), "air_acceleration": (0.05, 0.18),
    "air_control": (0.3, 1.0), "jump_impulse": (1.25, 2.1),
    "coyote_time": (0, 8), "jump_buffering": (0, 8), "dash_speed": (1.0, 2.0),
    "dash_duration": (2, 8), "dash_cooldown": (8, 30), "climb_stamina": (0, 100),
    "resource_regeneration": (0, 1), "collision_width": (0.5, 1.0), "collision_height": (0.8, 1.6),
}


@dataclass(frozen=True)
class MechanicsConfig:
    values: dict[str, Any]


def validate_mechanics(values: dict[str, Any]) -> None:
    positive = ("gravity", "max_fall_speed", "ground_acceleration", "ground_deceleration",
                "max_run_speed", "air_acceleration", "jump_impulse", "collision_width", "collision_height")
    if any(float(values[x]) <= 0 for x in positive):
        raise ValueError("physics values and collision dimensions must be positive")
    if values["air_acceleration"] > values["ground_acceleration"] * 2:
        raise ValueError("air acceleration unreasonable")
    if values["dash_enabled"] is False and values["dash_charges"] != 0:
        raise ValueError("disabled dash cannot have charges")
    if values["wall_jump"] and not values["wall_slide"]:
        raise ValueError("wall jump requires wall-slide contact semantics")
    if values["climbing"] and values["climb_stamina"] <= 0:
        raise ValueError("climbing requires stamina")


def sample_mechanics(rng: random.Random, ranges: dict[str, tuple[float, float]] | None = None) -> MechanicsConfig:
    ranges = ranges or DEFAULT_RANGES
    result = {key: rng.uniform(low, high) for key, (low, high) in ranges.items()}
    for key in ("coyote_time", "jump_buffering", "dash_duration", "dash_cooldown"):
        result[key] = int(round(result[key]))
    result.update({"variable_jump_height": rng.choice([True, False]), "air_jumps": rng.choice([0, 1, 2]),
                   "dash_enabled": rng.choice([True, False]), "dash_charges": 0,
                   "dash_directions": rng.choice(["horizontal", "eight_way"]),
                   "wall_slide": rng.choice([True, False]), "wall_jump": False,
                   "wall_jump_impulse": rng.uniform(1.0, 2.0), "wall_jump_lockout": rng.randint(0, 8),
                   "climbing": False, "gliding": rng.choice([False, False, True]), "grappling": False,
                   "moving_platforms": rng.choice([False, True]), "custom_ability_slots": 0})
    if result["dash_enabled"]:
        result["dash_charges"] = rng.randint(1, 2)
    if result["wall_slide"]:
        result["wall_jump"] = rng.choice([True, False])
    result["climbing"] = rng.choice([False, False, True])
    if not result["climbing"]:
        result["climb_stamina"] = 0.0
    validate_mechanics(result)
    return MechanicsConfig(result)
