from __future__ import annotations
import random
from dataclasses import dataclass
from ..sim.actions import ActionMacro, PrimitiveAction

@dataclass(frozen=True)
class Perturbation:
    type: str; magnitude: int

def perturb_macro(macro: ActionMacro, rng: random.Random) -> tuple[ActionMacro, Perturbation]:
    choice = rng.choice(["shorten_hold", "extend_hold", "reverse_briefly", "insert_noop", "delay_dash"])
    if choice == "shorten_hold": return ActionMacro(macro.action, max(1, macro.duration_frames-1), macro.release_frames, macro.macro_type, macro.semantic_name), Perturbation(choice, 1)
    if choice == "extend_hold": return ActionMacro(macro.action, macro.duration_frames+1, macro.release_frames, macro.macro_type, macro.semantic_name), Perturbation(choice, 1)
    if choice == "reverse_briefly": return ActionMacro(PrimitiveAction(macro.action.buttons, -macro.action.horizontal_axis, macro.action.vertical_axis), macro.duration_frames, macro.release_frames, macro.macro_type, macro.semantic_name), Perturbation(choice, 1)
    return ActionMacro(PrimitiveAction(), 2, semantic_name="noop"), Perturbation(choice, 2)
