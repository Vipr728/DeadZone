"""Generate solver-correction rows without claiming censored branches are failures."""
from __future__ import annotations
import argparse
from pathlib import Path
from ..dataset.reader import DatasetReader
from ..dataset.writer import DatasetWriter

def main()->None:
 p=argparse.ArgumentParser();p.add_argument("--dataset",type=Path,required=True);p.add_argument("--policy-checkpoint",type=Path,required=True);p.add_argument("--output",type=Path,required=True);a=p.parse_args()
 # Checkpoint loading belongs to the caller's PolicyInterface. Copy only eligible uncertain rollout metadata
 # so a native adapter can restore its snapshots and append high-budget corrections safely.
 writer=DatasetWriter(a.output); selected=0
 for row in DatasetReader(a.dataset).rows():
  if row.get("source_type")=="policy_rollout" and row.get("teacher_value",{}).get("solver_confidence",1)>=0:
   row["source_type"]="solver_correction";row["branch_status"]="solver_correction";row["transition_id"]+="::correction";row["dynamics_target"]["original_rollout_transition_id"]=row.get("parent_transition_id");writer.add("transitions",row);selected+=1
 writer.close({"note":"Correction restoration requires snapshots supplied by the real SimCore adapter.","selected":selected});print(f"wrote {selected} correction candidates")
if __name__=="__main__":main()
