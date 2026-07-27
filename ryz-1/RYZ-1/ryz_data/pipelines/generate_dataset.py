from __future__ import annotations
import argparse, json, logging, multiprocessing as mp, os, random, time
from pathlib import Path
from typing import Any
from ..collection.trial_manager import TrialManager
from ..dataset.statistics import compute_statistics
from ..dataset.writer import DatasetWriter
from ..generation.task_generator import generate_task
from ..sim.adapter_example import ExampleSimCoreAdapter
from ..solver.beam_solver import SolverBudget

LOG=logging.getLogger(__name__)
def load_config(path: Path) -> dict[str, Any]:
    try:
        import yaml
    except ImportError as exc: raise RuntimeError("PyYAML is required; install ryz-data dependencies") from exc
    return yaml.safe_load(path.read_text())

def _collect_worker(args: tuple[int, int, dict[str, Any]]) -> object:
    """Top-level picklable worker: one adapter instance and deterministic task-local RNG."""
    task_seed, trials, config = args; generated = generate_task(task_seed); sim = ExampleSimCoreAdapter()
    try:
        return TrialManager(sim, SolverBudget(**config["solver"]["high_budget"]), SolverBudget(**config["solver"]["low_budget"]),
            random.Random(task_seed)).collect(generated, trials, include_perturbations=bool(config.get("sources",{}).get("perturbations",True)),
            include_random=bool(config.get("sources",{}).get("random_exploration",True)), include_low_budget=bool(config.get("sources",{}).get("low_budget_solver",True)))
    finally: sim.close()

def generate(config: dict[str, Any], output: Path) -> dict[str, Any]:
    data=config["dataset"]; generation=config["generation"]; sources=config.get("sources",{})
    writer=DatasetWriter(output, int(data["shard_size"])); start=time.monotonic(); total=writer.counts.get("transitions",0)
    target_tasks=int(data["target_tasks"]); target_transitions=int(data["target_transitions"]); policy=data.get("stopping_policy","either")
    seed=int(generation["global_seed"]); rng=random.Random(seed)
    done_tasks=writer.counts.get("tasks",0)
    jobs=[]
    for ordinal in range(done_tasks, target_tasks):
        task_seed=seed+ordinal; trials=rng.randint(int(data["trials_per_task"]["min"]),int(data["trials_per_task"]["max"]))
        jobs.append((task_seed,trials,config))
    requested=generation.get("workers",1); workers=max(1,(os.cpu_count() or 1)-1) if requested=="auto" else int(requested)
    # imap preserves source task ordering. The process pool transfers records only, never snapshots;
    # a real factory substitutes the example adapter above to retain the same isolation property.
    context=mp.get_context("spawn")
    pool = context.Pool(workers) if workers > 1 else None
    iterator = (_collect_worker(job) for job in jobs) if pool is None else pool.imap(_collect_worker,jobs,chunksize=1)
    try:
      for collected in iterator:
        if (policy=="either" and total>=target_transitions) or (policy=="transitions" and total>=target_transitions): break
        collected.task["solved"]=any(t.completed for t in collected.transitions)
        writer.add("tasks",collected.task)
        for row in collected.trials: writer.add("trials",row)
        for row in collected.trajectories: writer.add("trajectories",row)
        for row in collected.calibrations: writer.add("calibrations",row)
        for row in collected.transitions:
            writer.add("transitions",row)
            writer.add("candidate_actions", {"schema_version": row.schema_version, "transition_id": row.transition_id,
                "task_id": row.task_id, "candidate_action_index": row.candidate_action_index,
                "action": row.candidate_action, "validity_mask": row.candidate_validity_mask,
                "teacher_policy": row.teacher_policy, "source_type": row.source_type})
            total += 1
            if policy=="transitions" and total>=target_transitions: break
        writer.flush(); LOG.info("task=%s transitions=%d",collected.task["task_id"],total)
        if policy=="transitions" and total>=target_transitions: break
    finally:
      if pool is not None: pool.terminate(); pool.join()
    elapsed=max(time.monotonic()-start,1e-9); writer.close({"config":config,"elapsed_seconds":elapsed,"transitions_per_second":total/elapsed})
    summary=compute_statistics(output); summary.update({"elapsed_seconds":elapsed,"transitions_per_second":total/elapsed}); return summary

def main() -> None:
    parser=argparse.ArgumentParser(); parser.add_argument("--config",type=Path,required=True); parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args(); logging.basicConfig(level=logging.INFO,format="%(levelname)s %(message)s")
    print(json.dumps(generate(load_config(args.config),args.output),indent=2))
if __name__=="__main__": main()
