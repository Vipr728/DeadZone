from __future__ import annotations
from typing import Any
from .calibration_result import CalibrationResult, ProbeRecord
from .estimator import estimate_from_probe
from .probe_library import standard_probes
from ..generation.manifest_builder import build_ground_truth_manifest
from ..sim.interface import SimCoreInterface, TaskHandle

class ProbeRunner:
    def run(self, sim: SimCoreInterface, task: TaskHandle, controls: dict[str, int]) -> CalibrationResult:
        ground_truth = sim.get_ground_truth_mechanics()
        records: list[ProbeRecord] = []
        inferred: dict[str, Any] = {}
        confidence: dict[str, float] = {}
        for probe in standard_probes(controls):
            sim.reset_task(task); before = sim.get_observation().to_dict(); states = [before]; events: list[dict[str, Any]] = []
            result = sim.step(probe.action, probe.frames); states.append(result.observation.to_dict()); events.extend(result.events)
            value, conf = estimate_from_probe(probe.mechanic, states, ground_truth)
            inferred[probe.mechanic], confidence[probe.mechanic] = value, conf
            error = None if value is None or not isinstance(value, (int, float)) else abs(float(value) - float(ground_truth[probe.mechanic]))
            records.append(ProbeRecord(probe.probe_id, before, [{"action": probe.action.to_dict(), "frames": probe.frames}],
                states, events, probe.mechanic, value, ground_truth.get(probe.mechanic), error, conf))
        # Retain unknown fields as explicitly hidden rather than manufacturing labels.
        base = build_ground_truth_manifest(ground_truth, controls).to_dict()
        base["continuous_mechanics"] = {k: v for k, v in base["continuous_mechanics"].items() if k in inferred and inferred[k] is not None}
        base["discrete_mechanics"] = {k: inferred.get(k) for k in base["discrete_mechanics"] if k in inferred and inferred[k] is not None}
        base["confidence"] = {k: confidence.get(k, 0.0) for k in ground_truth}
        base["visibility_mask"] = {k: inferred.get(k) is not None for k in ground_truth}
        return CalibrationResult(base, records)
