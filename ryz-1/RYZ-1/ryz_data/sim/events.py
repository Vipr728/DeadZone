from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Any


@dataclass(frozen=True)
class SimEvent:
    kind: str
    frame: int
    details: dict[str, Any]

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)
