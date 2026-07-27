# Data Flow

## Simulation tick (per arena, per fixed step)

```
Scenario (seed, tunables)
  → Adapter.BuildObservation (player state + local geometry + dynamic objects)
  → IAgent.Act(observation) → PlayerAction {moveX, moveY, jumpPressed, jumpHeld, dashPressed, climbHeld}
  → Adapter.ApplyAction → VirtualInput on player controller (replaces Keyboard polling)
  → PhysicsScene2D.Simulate(fixedDeltaTime)  // player FixedUpdate logic runs via manual tick
  → Adapter.DrainEvents (Death, CheckpointReached, DashRefill, Spring, GoalReached)
  → TelemetryRecorder.RecordStep(frame, pos, vel, action, flags, events, progress)
```

## Episode lifecycle

```
EpisodeRunner.Run(scenario, agent, seed)
  → Arena.Reset (teleport player to spawn, zero velocity, refill, reset dynamic objects, clear events)
  → tick loop until GoalReached | step budget | cancel
  → EpisodeResult {outcome, steps, deaths, progress, telemetry file refs}
```

## Storage

```
Library/PlatformerPlaytest/runs/<runId>/
  run.json            — run config, build/settings hashes, scenario version
  episodes.jsonl      — one summary line per episode
  ep_<n>.actions.bin? — exact action stream (JSON-lines first; binary only if measured need)
  ep_<n>.frames.jsonl — full trajectory (failures + representative successes; summaries otherwise)
```

## Replay

action stream + seed + scenario → re-run through same tick loop with ReplayAgent (feeds recorded actions) → compare against keyframes (every N frames: pos, vel, state hash) → desync report at first divergent frame.

## Analysis

telemetry files → metrics (completion by profile, deaths by section, tolerance windows from perturbed replays) → findings (each finding = metric evidence + episode/replay IDs) → Editor window / Scene overlay (death positions gizmos).

## Counterfactual

scenario + tunable override (e.g. platform X position) → paired runs same seeds/agents → per-variant metric comparison → report; apply-to-scene only via explicit user action.

## Commercial Onboarding Flow

```
Tools > Ryzi authoring path
  → scan active scene (read-only)
  → ranked candidates + evidence + issues
  → MechanicsManifest 1.0 in memory
  → exported RYZ Task Bundle
  → Ryz1.SimCore on GB10 validates bundle and runs authoritative hackathon simulation
  → selected integration provider
  → isolated calibration probes
  → runtime evidence/confidence reconciliation
  → preserved deterministic solver
  → Library/Ryzi/runs/<run-id>/{run.json,replay.json}
  → result summary + Scene path/failure overlay + replay controls
  → isolated three-candidate counterfactual
  → finally restore original values and clear recovery marker
```

The UI snapshots `Scene.isDirty` before scanning/calibration and treats an unexpected dirty-state change as an
actionable error. Long operations carry cancellation. No source or scene content is uploaded.
