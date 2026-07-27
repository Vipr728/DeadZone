# GB10 MVP Execution Plan — `rahul-infra`

## Summary

Deliver the PRD's two-level hackathon MVP using three parallel workstreams. The
current environment supports laptop development with Unity, `uv`, and Ollama,
but GB10-only training and validation remain a later mandatory gate.

Execution starts by creating `rahul-infra` from `main`, committing this plan,
and implementing the infra/reporting workstream there. The existing
`origin/unnat-rl` branch is reviewed and merged separately before final
integration.

```text
Shared contracts ─┬─> RL training pipeline ──────┐
                  ├─> Unity agent + telemetry ───┼─> Laptop integration
                  └─> Infra report pipeline ─────┘          │
                                                            v
                                                    GB10 validation
                                                            │
                                                            v
                                                     Demo hardening
```

## Implementation plan

### 1. Establish branches and shared contracts

- Create `rahul-infra` from the latest available `main` and commit this plan.
- Review and merge `origin/unnat-rl` separately, then rebase this branch.
- Keep the locked build layout, training flags, JSON schemas, and paths in
  `infra/config.yaml` immutable across workstreams.

### 2. Build infra/reporting on `rahul-infra`

- Create a typed, `uv`-managed `infra` package.
- Configure watched exports, telemetry, reports, checkpoints, local model
  backends, GB10 overrides, allowed paths, and blocked egress.
- Implement `ILLMClient` with Ollama, NIM, and NemoClaw-routed adapters.
- Validate telemetry and reports against the shared contracts, render a
  versioned Jinja prompt, and atomically create non-overwriting report files.
- Add deterministic Level A, planted-issue Level B, malformed, and
  out-of-range telemetry fixtures.
- Implement standalone idempotent orchestration plus duplicate-suppressing
  level watching.
- Implement path and egress policy interfaces, including a CLI proof which
  prints `PASS` only for a policy-blocked request.
- Add an honest `nemoclaw_setup.sh` local-fallback setup path.

### 3. Complete parallel Unity and ML/RL work

- Unity completion order — these are blocking requirements, not optional
  polish, and each must be implemented before a result is called a real Unity
  RL run:
  1. Resolve and lock the Unity-6-compatible ML-Agents and Sentis packages in
     `packages-lock.json`; reopen Unity and record a clean script compilation.
  2. Finish the player handoff: manually measure move speed, jump apex, and
     jump distance from `PlayerConfig`; commit the measured values and derived
     gap/elevation ranges to `rl/configs/piece_config.yaml`.
  3. Implement the config-sync boundary: `SyncConfigFromYaml.cs`,
     `PieceLibraryConfig`, and `RewardConfigAsset`. `rl/configs/*.yaml`
     remains the source of truth; generated ScriptableObjects are never edited
     by hand.
  4. Implement `IPieceType`, `PieceParams`, gap-jump and move-to-goal pieces,
     elevation behind the YAML/config feature flag, `PieceComposer`, start and
     goal markers, and `HazardTile`. Compose exactly three pieces and reset
     velocity at every piece boundary.
  5. Implement `PlaytestAgent : Agent`, `IObservationEncoder`,
     `GridObservationEncoder`, and the C# `IRewardStrategy` mirror. The
     behavior name must be exactly `PlaytestAgent`; it must drive the player
     only through `PlayerInputAdapter.SetMove(float)` and `SetJump(bool)`.
  6. Implement collider-driven completion, death, timeout, and episode-reset
     lifecycle behavior. Rewards must come from the synced reward config, not
     hardcoded values.
  7. Implement `TelemetryRecorder` and validate its output against
     `contracts/telemetry.schema.json`; write to
     `infra/config.yaml`'s telemetry directory in traversal order so infra can
     compute level-local teachability precedent.
  8. Author actual Tilemap Level A and Level B assets. Keep every parameter in
     the Stage-1 ranges; make Level B's planted in-range edge gap/hazard have
     no comparable earlier teaching instance.
  9. Complete the Editor tool: window, level selection, training subprocess
     controls using the locked five flags, checkpoint/manifest selector,
     playback control, and report display. Retain the build-then-marker export
     ordering in `ExportPanel`.
  10. Implement `SentisPlayback` using the same observation encoder as
      `PlaytestAgent`; load the ONNX path from the checkpoint manifest and
      visually verify a real checkpoint.
  11. Add Unity Edit Mode tests for observation-vector fixtures and
      config-bounded piece sampling, then run a real built-environment smoke
      test before relying on the RL pipeline.
- Evidence required before declaring Unity integrated: a resolved package
  lockfile, clean Unity compilation, the three builds at the locked paths,
  schema-valid telemetry from Unity, and one end-to-end run that does not use
  the RL fake-environment fallback.
- ML/RL owns real/fake mode separation, real ML-Agents integration and
  metrics, checkpoints, ONNX export, and YAML-source config synchronization.

### 4. Integration and validation gates

- Laptop: clean infra/RL environments, Unity Edit Mode tests, valid fixture
  reports, planted-issue detection, structural end-to-end flow, and honest
  labeling of fake training.
- GB10: Grace ARM64 build, compatible ML stack, real Gates 1 and 2, verified
  Sentis playback, concurrent wall-clock measurement, real local-model
  reports, and real sandbox-boundary egress proof.
- Demo hardening: known-good checkpoints, three consecutive Level B
  detections, rehearsed fallback/live/report/egress paths, and a final
  feature freeze.

## Public interfaces and minimum tests

- `ILLMClient.generate_structured(prompt: str, schema: dict) -> dict`
- `generate_report(telemetry_path: str, llm_client: ILLMClient) -> dict`
- `process_level_export(level_path: str, config_path: str) -> PipelineResult`
- `IEgressPolicy.is_read_allowed(path)`, `is_write_allowed(path)`, and
  `block_egress()`
- CLI commands for one-shot reporting, level watching, and egress proof.

Tests cover configuration, schemas, filenames, path policy, malformed model
output, duplicate events, fixtures, orchestration success and failure modes,
missing telemetry, malformed reports, and actual application-policy blocking.
Live Ollama tests skip only when the configured local service is unavailable.

## Definition of done and assumptions

The full MVP is complete only after the Unity workflows, real telemetry and
reports, Gates 1 and 2, Sentis playback, concurrent timing, real egress proof,
fallback checkpoints, and all automated tests pass. This branch contains the
infra implementation; separate Unity, RL, and GB10 deliverables are explicitly
deferred rather than simulated or claimed.
