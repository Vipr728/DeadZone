"""Action types remain independent of Unity and physical device IDs."""
from __future__ import annotations

from dataclasses import asdict, dataclass


@dataclass(frozen=True)
class PrimitiveAction:
    buttons: tuple[int, ...] = ()
    horizontal_axis: float = 0.0
    vertical_axis: float = 0.0

    def to_dict(self) -> dict[str, object]:
        return asdict(self)


@dataclass(frozen=True)
class ActionMacro:
    action: PrimitiveAction
    duration_frames: int
    release_frames: int = 0
    macro_type: str = "hold"
    semantic_name: str = "noop"

    def to_dict(self) -> dict[str, object]:
        return {"action": self.action.to_dict(), "duration_frames": self.duration_frames,
                "release_frames": self.release_frames, "macro_type": self.macro_type,
                "semantic_name": self.semantic_name}
