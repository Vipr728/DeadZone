# Spec: Telemetry

Location: `Library/PlatformerPlaytest/runs/<runId>/`. JSON-lines (ADR-005).

## run.json (per run)

runId, createdUtc, scenarioId, scenarioVersion, unityVersion, packageVersion, agentVersions, gitCommit (if available), buildHash (hash of player-controller tunables + fixed timestep + physics settings), fixedDeltaTime, profileIds, seeds.

## episodes.jsonl (one line per episode)

episodeId, scenarioId, agentId, profileId, seed, outcome (Completed/DiedOut/StepBudget/Cancelled), steps, deaths, furthestProgress, completionTimeTicks, checkpointsReached, sectionsReached, hasFullTrajectory.

## ep_<id>.actions.jsonl (always, every episode)

One line per tick: `{"t":123,"mx":1,"my":0,"jp":true,"jh":true,"dp":false,"ch":false}` (omit false/zero fields). This is the replay source of truth.

## ep_<id>.frames.jsonl (failures + representative successes only)

One line per tick: t, px, py, vx, vy, flags bitfield (grounded, wallL, wallR, dashing, climbing), dashes, stamina, progress, section. Events inline: `{"t":214,"ev":"Death"}`.

## Keyframes

Every 30 ticks + on every event: t, px, py, vx, vy, flags, stateHash (FNV-1a over quantized px,py,vx,vy,flags,dashes). Stored in frames file (`"kf":true`) or separate array in a small `ep_<id>.keyframes.json` for episodes without full trajectory.

## Rules

- Writer buffers per episode, flushes on episode end; bounded queue; no per-tick allocations beyond builder reuse.
- Full trajectory recorded for: all failures, first success per (agent, profile), any episode flagged by analysis request.
- Tests: round-trip serialize/parse; path stays under Library/; hash stability across two identical runs.
