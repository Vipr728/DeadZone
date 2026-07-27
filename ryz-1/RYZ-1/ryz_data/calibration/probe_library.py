from __future__ import annotations
from dataclasses import dataclass
from ..sim.actions import PrimitiveAction

@dataclass(frozen=True)
class Probe:
    probe_id: str; mechanic: str; action: PrimitiveAction; frames: int

def standard_probes(controls: dict[str, int]) -> list[Probe]:
    button = lambda name: (controls[name],) if name in controls else ()
    return [Probe("no_action_fall", "gravity", PrimitiveAction(), 8),
      Probe("hold_left", "max_run_speed", PrimitiveAction(horizontal_axis=-1), 12),
      Probe("hold_right", "max_run_speed", PrimitiveAction(horizontal_axis=1), 12),
      Probe("short_jump", "jump_impulse", PrimitiveAction(buttons=button("jump")), 1),
      Probe("long_jump", "variable_jump_height", PrimitiveAction(buttons=button("jump")), 6),
      Probe("air_control_reversal", "air_acceleration", PrimitiveAction(horizontal_axis=-1), 5),
      Probe("dash_right", "dash_speed", PrimitiveAction(buttons=button("dash"), horizontal_axis=1), 2),
      Probe("dash_recharge", "dash_cooldown", PrimitiveAction(), 12),
      Probe("wall_contact", "wall_slide", PrimitiveAction(horizontal_axis=1), 20),
      Probe("climb", "climbing", PrimitiveAction(buttons=button("climb"), vertical_axis=1), 5),
      Probe("collision_boundary", "collision_width", PrimitiveAction(), 1)]
