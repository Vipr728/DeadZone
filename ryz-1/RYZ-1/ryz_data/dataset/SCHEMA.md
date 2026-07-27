# RYZ-data schema v1.0.0

Storage is normalized into `tasks`, `trials`, `trajectories`, `transitions`, `calibrations`, and `candidate_actions` Parquet tables. Each row has `schema_version`; structured values are canonical JSON strings in Parquet and are decoded by `DatasetReader`.

`tasks` owns the procedural seed, split, level, and ground-truth manifest. `trials` owns the memory snapshot after each attempt. `trajectories` owns source and linkage metadata. `transitions` uses `TransitionRecord` in `schema.py` and references all three by ID. `candidate_actions` stores candidate-specific policy metadata without multiplying full observations. Calibration probes retain initial/actions/state/events/estimate/error/confidence in `calibrations` and are also represented as transitions.

`branch_eventually_solved=None` means censored/unknown; `False` is used only for evaluated evidence. `parent_transition_id` reconstructs search branches. Task split is derived solely from `task_seed` using `task_split`, so all descendants stay in the same split.
