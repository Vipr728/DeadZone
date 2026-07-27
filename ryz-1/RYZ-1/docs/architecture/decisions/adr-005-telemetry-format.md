# ADR-005: Telemetry as JSON-lines, binary only if measured need

Status: Accepted

Context: Episodes need reproducible action streams, keyframes, events, and summaries; thousands of episodes eventually.

Decision: JSON-lines files per run: `run.json` header (episode/scenario/build/settings hashes, seed, timestep), `episodes.jsonl` summaries, per-episode action stream + frame records. Full trajectories only for failures and representative successes. Switch to binary only after profiling shows JSON is a bottleneck.

Alternatives: Binary from day one — rejected: premature, kills debuggability. SQLite — rejected: dependency for no MVP query need.

Consequences: Human-readable, diffable, trivially parsed by later Python. Larger files; acceptable at MVP scale.
