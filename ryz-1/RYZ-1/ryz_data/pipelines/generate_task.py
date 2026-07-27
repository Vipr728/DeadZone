from __future__ import annotations
import argparse,json
from pathlib import Path
from ..generation.task_generator import generate_task
def main() -> None:
 p=argparse.ArgumentParser();p.add_argument("--seed",type=int,required=True);p.add_argument("--output",type=Path,required=True);p.add_argument("--visualize",action="store_true");a=p.parse_args()
 task=generate_task(a.seed);a.output.mkdir(parents=True,exist_ok=True);(a.output/"task.json").write_text(json.dumps({"config":task.config.__dict__,"manifest":task.ground_truth_manifest},default=str,indent=2));print(a.output/"task.json")
if __name__=="__main__":main()
