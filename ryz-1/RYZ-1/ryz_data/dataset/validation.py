from __future__ import annotations
import json, math
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from .reader import DatasetReader
from .schema import SCHEMA_VERSION, validate_transition

@dataclass
class ValidationReport:
    valid: bool; errors: list[str]; checked: int
    def to_json(self) -> str: return json.dumps({"valid": self.valid, "checked": self.checked, "errors": self.errors}, indent=2)

def validate_dataset(root: Path) -> ValidationReport:
    errors=[]; ids=set(); tasks=set(); trials={}; trajectories={}; parents=[]; checked=0
    reader=DatasetReader(root)
    for row in reader.rows("tasks"):
        if row.get("schema_version") != SCHEMA_VERSION: errors.append("task schema version mismatch")
        tasks.add(row.get("task_id"))
    for row in reader.rows("trials"):
        trials[row.get("trial_id")]=row.get("task_id")
    for row in reader.rows("trajectories"):
        trajectories[row.get("trajectory_id")]=row.get("task_id")
    for row in reader.rows():
        checked += 1; errors.extend(f"{row.get('transition_id')}: {x}" for x in validate_transition(row))
        tid=row.get("transition_id")
        if tid in ids: errors.append(f"duplicate transition {tid}")
        ids.add(tid)
        if row.get("schema_version") != SCHEMA_VERSION: errors.append(f"{tid}: schema mismatch")
        if row.get("task_id") not in tasks: errors.append(f"{tid}: unknown task")
        if row.get("source_type") != "calibration" and row.get("trial_id") not in trials: errors.append(f"{tid}: unknown trial")
        if row.get("source_type") != "calibration" and row.get("trajectory_id") not in trajectories: errors.append(f"{tid}: unknown trajectory")
        parents.append((tid,row.get("parent_transition_id")))
        for value in (row.get("immediate_reward"), row.get("search_score"), row.get("progress_delta")):
            if value is not None and not math.isfinite(float(value)): errors.append(f"{tid}: nonfinite numeric")
    for tid, parent in parents:
        if parent is not None and parent not in ids: errors.append(f"{tid}: missing parent {parent}")
    report=ValidationReport(not errors, errors, checked); target=root/"statistics"; target.mkdir(exist_ok=True)
    (target/"validation_report.json").write_text(report.to_json()); return report
