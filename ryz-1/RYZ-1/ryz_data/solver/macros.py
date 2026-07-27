from __future__ import annotations
from ..sim.actions import ActionMacro, PrimitiveAction

def macro_vocabulary(controls: dict[str, int]) -> list[ActionMacro]:
    b = lambda name: (controls[name],) if name in controls else ()
    out = [ActionMacro(PrimitiveAction(), 2, semantic_name="noop"),
           ActionMacro(PrimitiveAction(horizontal_axis=-1), 4, semantic_name="move_left"),
           ActionMacro(PrimitiveAction(horizontal_axis=1), 4, semantic_name="move_right"),
           ActionMacro(PrimitiveAction(buttons=b("jump")), 1, 2, "tap", "short_jump"),
           ActionMacro(PrimitiveAction(buttons=b("jump")), 5, 1, "hold", "long_jump"),
           ActionMacro(PrimitiveAction(buttons=b("jump"), horizontal_axis=-1), 5, 1, "hold", "running_jump_left"),
           ActionMacro(PrimitiveAction(buttons=b("jump"), horizontal_axis=1), 5, 1, "hold", "running_jump_right")]
    if "interact" in controls:
        out.append(ActionMacro(PrimitiveAction(buttons=b("interact")), 1, semantic_name="interact"))
    if "dash" in controls:
        out += [ActionMacro(PrimitiveAction(buttons=b("dash"), horizontal_axis=-1), 2, semantic_name="dash_left"),
                ActionMacro(PrimitiveAction(buttons=b("dash"), horizontal_axis=1), 2, semantic_name="dash_right"),
                ActionMacro(PrimitiveAction(buttons=b("jump") + b("dash"), horizontal_axis=1), 3, semantic_name="jump_dash")]
    return out
