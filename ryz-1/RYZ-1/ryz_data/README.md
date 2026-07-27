# RYZ-1 training-data generation

`ryz_data` produces reproducible, normalized trajectory datasets from cloneable deterministic platformer simulators. It is local-only and uses no CUDA, cloud, or x86 emulation. Parquet/Zstandard is the physical format; task, trial, trajectory, transition, calibration, and candidate tables are separate.

## Quick start

```bash
python3.11 -m venv .venv && source .venv/bin/activate
pip install -e '.[dev]'
python -m pytest ryz_data/tests -q
python -m ryz_data.pipelines.generate_dataset --config ryz_data/config/smoke.yaml --output datasets/ryz_smoke
python -m ryz_data.pipelines.validate_dataset --dataset datasets/ryz_smoke
python -m ryz_data.tests.benchmark
```

For a five-task development run, create a small YAML override (`target_tasks: 5`, `target_transitions: 10000`, `beam_width: 8`, `max_depth: 20`) or run the end-to-end pytest. The generated tree is:

```text
dataset/{metadata.json,tasks/,trials/,trajectories/,transitions/,calibrations/,candidate_actions/,statistics/,generation_state/}
```

Every Parquet table has `schema_version`. Compound fields are canonical JSON columns to make schema migrations forward-compatible without exploding Parquet nested schemas. `DatasetReader` exposes decoded unified transition records and `RYZTransitionDataset` creates PyTorch-ready samples.

## Connecting native SimCore

1. Add `ryz_data/sim/simcore_adapter.py` implementing `SimCoreInterface`.
2. Map native `TaskBundle` input to `TaskConfig`, and convert native state into `Observation`/`PlayerState`.
3. Implement `clone_state` and `restore_state` with SimCore's compact native snapshot (do not serialize Unity objects).
4. Implement a deterministic `hash_state` over physics and state-machine fields. Quantize only deliberately and document it.
5. Replace `ExampleSimCoreAdapter()` in `pipelines/generate_dataset.py` with an adapter factory. Each worker must create its own instance.

The required native methods are exactly: `create_task`, `reset_task`, `step`, `clone_state`, `restore_state`, `hash_state`, `get_observation`, `get_ground_truth_mechanics`, `is_terminal`, and `close`. `step` accepts primitive physical controls; the pipeline translates semantic macros through the task action mapping.

`ExampleSimCoreAdapter` is an in-memory deterministic integration harness, not a physics substitute for the authoritative native runtime. Its snapshots are intentionally pure Python so clone/restore behavior is testable now.

## Labels and guarantees

Beam search stores evaluated branches, duplicate/heuristic/beam pruning reason, candidate rankings, budgets, and parent links. Only verified solution ancestors receive solved-descendant labels. Unexpanded or pruned branches remain censored (`None`), never forced to “impossible.” Calibration writes ground-truth and inferred manifests separately. Trial memory persists only within a task and is reset by constructing a new `TrialManager` for the next task.

Policy rollout collection uses `PolicyInterface`; correction export preserves linkage but true restore-and-correct requires optional native snapshot payloads from the real SimCore adapter. Snapshots are deliberately disabled in ordinary shards to prevent large datasets.

## Expanded platformer curriculum

`generation/scenario_catalog.py` contains 89 deterministic scenario contracts spanning movement timing, keys/doors/inventory, route planning, hazards/enemies, resources/abilities, physics interactions, mechanics discovery, multi-stage objectives, and recovery. Each task carries `scenario_id`, family, required entities, and expected event types through `global_task_features`; stateful entities are emitted in observations. Run the one-per-contract corpus with `all_use_cases_sample.yaml`, or the twenty-variant corpus with `all_use_cases_hackathon.yaml`.

## GB10 operation

Use a Linux ARM64 Python 3.11+ environment and ARM64 wheels for NumPy, PyTorch, PyArrow, and PyYAML. Start with one worker and run `benchmark_generation.sh` against the native adapter. Increase workers until aggregate simulator step/s plateaus while leaving memory headroom. The pipeline uses spawned workers with one adapter instance per worker and a single parent-owned shard writer; setting `workers: 1` is the deterministic debugger mode. It resumes from `generation_state/checkpoint.json`.

## Current integration assumptions

- The example adapter supports ground movement, jump, gravity, and horizontal dashes. Native SimCore supplies authoritative walls, climbing, moving platforms, resources, and richer events.
- A real adapter must provide geometry/entity observations and a task-specific progress function for non-linear goals.
- Model checkpoint loading and uncertainty are supplied via `PolicyInterface`; no training code or checkpoint format is assumed here.
- Multiprocess native execution is intentionally adapter-factory dependent. No simulator object or large snapshot crosses process boundaries.
