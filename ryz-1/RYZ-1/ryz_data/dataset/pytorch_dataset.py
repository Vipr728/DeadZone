from __future__ import annotations
from pathlib import Path
from typing import Any
import numpy as np
from .reader import DatasetReader

class RYZTransitionDataset:
    def __init__(self, root: str | Path) -> None:
        self.rows=list(DatasetReader(Path(root)).rows())
    def __len__(self) -> int: return len(self.rows)
    def __getitem__(self, index: int) -> dict[str, Any]:
        import torch
        row=self.rows[index]; player=row["player_state"]; result=row["resulting_player_state"]
        obs=np.asarray([player.get(x,0.) for x in ("x","y","vx","vy","grounded","wall_left","wall_right","dash_cooldown")],dtype=np.float32)
        mechanics=np.asarray(list(row.get("inferred_manifest",{}).get("continuous_mechanics",{}).values()),dtype=np.float32)
        return {"observation":torch.from_numpy(obs), "mechanics":torch.from_numpy(mechanics), "memory_context":row["memory_context"],
          "candidate_actions":[row["candidate_action"]], "candidate_mask":torch.tensor(row["candidate_validity_mask"],dtype=torch.bool),
          "policy_target":None if row["teacher_policy"] is None else torch.tensor(row["teacher_policy"],dtype=torch.float32),
          "value_target":row["teacher_value"], "dynamics_target":row["dynamics_target"], "metadata":row}

class RYZTrialSequenceDataset:
    def __init__(self, root: str | Path) -> None: self.rows=sorted(DatasetReader(Path(root)).rows(),key=lambda x:(x["task_id"],x["trial_id"],x["step_index"]))
    def __iter__(self):
        current=None; buffer=[]
        for row in self.rows:
            key=(row["task_id"],row["trial_id"])
            if current is not None and key!=current: yield buffer; buffer=[]
            current=key; buffer.append(row)
        if buffer: yield buffer

def collate_transitions(batch: list[dict[str, Any]]) -> dict[str, Any]:
    import torch
    max_mechanics=max((x["mechanics"].numel() for x in batch),default=0)
    mechanics=torch.zeros((len(batch),max_mechanics))
    for i,x in enumerate(batch): mechanics[i,:x["mechanics"].numel()]=x["mechanics"]
    return {"observation":torch.stack([x["observation"] for x in batch]),"mechanics":mechanics,
            "candidate_actions":[x["candidate_actions"] for x in batch],"metadata":[x["metadata"] for x in batch]}
