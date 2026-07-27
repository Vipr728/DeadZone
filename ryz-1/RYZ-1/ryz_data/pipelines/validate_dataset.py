from __future__ import annotations
import argparse
from pathlib import Path
from ..dataset.validation import validate_dataset
def main()->None:
 p=argparse.ArgumentParser();p.add_argument("--dataset",type=Path,required=True);a=p.parse_args();r=validate_dataset(a.dataset);print(r.to_json());raise SystemExit(0 if r.valid else 1)
if __name__=="__main__":main()
