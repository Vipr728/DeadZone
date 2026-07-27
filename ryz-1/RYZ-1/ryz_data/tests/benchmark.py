from __future__ import annotations
import argparse,json,time
from pathlib import Path
from ryz_data.generation.task_generator import generate_task
from ryz_data.sim.adapter_example import ExampleSimCoreAdapter
from ryz_data.sim.actions import PrimitiveAction
from ryz_data.solver.beam_solver import BeamSolver,SolverBudget

def main()->None:
 parser=argparse.ArgumentParser();parser.add_argument("--output",type=Path);args=parser.parse_args()
 task=generate_task(1337);sim=ExampleSimCoreAdapter();h=sim.create_task(task.config);sim.reset_task(h);n=10_000
 start=time.perf_counter()
 for _ in range(n):
  if sim.is_terminal():sim.reset_task(h)
  sim.step(PrimitiveAction(horizontal_axis=1))
 step_rate=n/(time.perf_counter()-start);snap=sim.clone_state();start=time.perf_counter()
 for _ in range(n):sim.clone_state()
 clone_rate=n/(time.perf_counter()-start);start=time.perf_counter()
 for _ in range(n):sim.restore_state(snap)
 restore_rate=n/(time.perf_counter()-start);sim.reset_task(h);start=time.perf_counter();r=BeamSolver(SolverBudget(16,30)).solve(sim,task.config.controls);elapsed=time.perf_counter()-start
 report={"adapter":"ExampleSimCoreAdapter","raw_sim_steps_per_second":step_rate,"snapshot_clone_per_second":clone_rate,"snapshot_restore_per_second":restore_rate,"beam_expansions_per_second":r.expansions/elapsed,"dataset_serialization":"measure with native sample; not included in physics microbenchmark","recommended_workers":"min(cpu_count-1, 8); benchmark native SimCore first"}
 if args.output:
  args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(report,indent=2))
 print(json.dumps(report,indent=2))
if __name__=="__main__":main()
