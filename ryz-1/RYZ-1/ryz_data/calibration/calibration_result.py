from __future__ import annotations
from dataclasses import asdict, dataclass
from typing import Any

@dataclass(frozen=True)
class ProbeRecord:
    probe_id: str; initial_state: dict[str, Any]; action_sequence: list[dict[str, Any]]
    state_sequence: list[dict[str, Any]]; events: list[dict[str, Any]]; estimated_mechanic: str
    estimated_value: Any; ground_truth_value: Any; estimation_error: float | None; confidence: float
@dataclass(frozen=True)
class CalibrationResult:
    inferred_manifest: dict[str, Any]; probes: list[ProbeRecord]
    def to_dict(self) -> dict[str, Any]: return asdict(self)
