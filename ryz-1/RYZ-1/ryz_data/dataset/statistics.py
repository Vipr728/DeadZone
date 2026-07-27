from __future__ import annotations
import json
from collections import Counter
from pathlib import Path
from .reader import DatasetReader

def compute_statistics(root: Path) -> dict:
    reader=DatasetReader(root); rows=list(reader.rows()); tasks=list(reader.rows("tasks"))
    summary={"tasks_generated":len(tasks), "transitions_generated":len(rows),
      "tasks_solved":sum(bool(t.get("solved",False)) for t in tasks),
      "transition_count_by_source":dict(Counter(r.get("source_type") for r in rows)),
      "transition_count_by_branch_status":dict(Counter(r.get("branch_status") for r in rows)),
      "completion_count":sum(bool(r.get("completed")) for r in rows), "death_count":sum(bool(r.get("died")) for r in rows),
      "near_success_count":sum(r.get("branch_status")=="near_success" for r in rows)}
    target=root/"statistics"; target.mkdir(exist_ok=True); (target/"summary.json").write_text(json.dumps(summary,indent=2)); return summary
