# Plan: MVP Vertical Slice

Status (2026-07-23): T1–T8 done. Full suite green — EditMode 62/62, PlayMode 20/20. T8 (execution-tolerance
perturbation + counterfactual tunables) landed: ADR-008 overrides applied/restored in the adapter, Perturbation
tolerance sweep, CounterfactualRunner paired experiments, editor DemoBatchRunner wired (edit-mode verdict:
arena scenes are Play-Mode-only, so the button reports a clear "requires Play Mode / run via PlayMode tests"
fallback — never fabricated data).

Scenario: existing SampleScene level, section from spawn (x≈−8) to Finish flag (x≈107). Contains basic movement, forgiving jump (first gap, wide landing), precise jumps (pillar sequence x 30–50 over spikes), hazards (3 spike rows), dash, springs, 5 checkpoints, goal added at flag. No new scene needed.

Fixed decisions: fixedDeltaTime 0.02; manual `PhysicsScene2D.Simulate`; arenas = additive scenes with LocalPhysicsMode.Physics2D; data under `Library/PlatformerPlaytest/`; asmdefs under `Assets/PlatformerPlaytest/` (ADR-010).

## Tasks

Legend: impl = implementing agent, rev = reviewer. Reviewer always different model, read-only.

### T1 — Simulator seam (impl: sonnet-implementer, rev: independent-reviewer on opus)
Files: `Assets/Scripts/CelesteBenchmark/CelesteBenchmarkPlayer.cs` only.
- Add `IVirtualInput` (MoveX/MoveY/JumpHeld/JumpPressedEdge/JumpReleasedEdge/DashPressedEdge/ClimbHeld) + `SetVirtualInput`, `event Action Died` (fired in Respawn), `SetTickDriven(bool)` mode: edge buffers consumed from virtual source in FixedUpdate; respawn freeze + drop-through become tick counters; drop-through uses per-collider IgnoreCollision.
- Acceptance: keyboard play unchanged (manual QA + no behavior diff when source null); no global IgnoreLayerCollision remains; compiles.
Risk: subtle behavior change to live play. Mitigation: null-source path must be byte-identical logic.

### T2 — Core runtime: actions/observations/scenario/arena/episode runner (impl: sonnet-implementer, rev: opus)
Files: `Assets/PlatformerPlaytest/Runtime/**` (asmdefs, Core/, Adapter/CelesteBenchmarkAdapter.cs), `Tests/EditMode`, `Tests/PlayMode`.
Interfaces: per specs game-adapter.md. ScenarioConfig = ScriptableObject {spawn, goalRect, sectionBoundsX[], stepBudget, seed, overrides[]}. ArenaManager: create/unload additive physics scenes, clone level root into arena. EpisodeRunner: tick loop per data-flow.md, cancellation token, EpisodeResult.
Acceptance (PlayMode tests): scripted agent (hold right + periodic jump) runs an episode without keyboard; reset twice from same seed → identical first-200-tick position trace; death detected on spikes; completion detected in goal rect; progress monotone-nondecreasing per checkpoint.
Depends: T1.

### T3 — Telemetry + replay (impl: gpt-systems-engineer, rev: sonnet)
Files: `Runtime/Telemetry/**`, `Runtime/Agents/ReplayAgent.cs`, tests.
Per specs telemetry.md/replay.md: recorder, writers, keyframes+stateHash, ReplayAgent, DesyncReport.
Acceptance: record→replay zero desync; tampered action → desync detected; serialization round-trip; output path under Library/; hash stability test.
Depends: T2.

### T4 — Multi-arena + benchmark (impl: gpt-systems-engineer, rev: opus)
Files: `Runtime/Core/ArenaManager` extensions, `Tests/PlayMode/ArenaIsolationTests.cs`, benchmark script + `docs/benchmarks/`.
Acceptance: 2 arenas run different action streams with no cross-contamination (drop-through in arena A doesn't affect B; identical-seed arenas produce identical traces while a differing arena runs beside them); benchmark 1/2/4/8/16/32 arenas records sim-frames/sec, episodes/min, memory, reset latency → committed results table; default arena count chosen from data. Strict-isolation fallback documented if isolation test fails.
Depends: T2 (parallel with T3).

### T5 — Beam-search solver (impl: gpt-systems-engineer, rev: opus)
Files: `Runtime/Agents/Solver/**`, tests.
Macros per ADR-004; state hash (quantized pos/vel/flags/dashes); beam width configurable; re-simulation from action prefixes; targets stable states then goal; outputs action stream.
Acceptance: solver completes the slice scenario start→goal within budget; duplicate-elimination unit tests; deterministic given seed; solution replayable with zero desync; invariants+complexity documented in code header.
Depends: T2, T3 (replay validation).

### T6 — Synthetic profiles (impl: sonnet-implementer, rev: gpt-systems-engineer agent def (opus))
Files: `Runtime/Agents/ProfiledAgent.cs`, profile assets, tests.
Per player-profiles.md. Beginner/intermediate/expert.
Acceptance: parameter-application unit tests; 20-episode batch per profile completes; beginner deaths ≥ expert deaths at precise section; seeded determinism.
Depends: T5.

### T7 — Analysis + editor reporting (impl: sonnet-implementer, rev: opus)
Files: `Runtime/Analysis/**`, `Editor/**` (window: Run/Results/Replay tabs, UI Toolkit), scene overlay (death-position gizmos).
Metrics: completion rate/time/deaths by profile+section, death heatmap data, furthest progress; difficulty flag = section whose death-rate exceeds neighbors by threshold, evidence attached (episode ids).
Acceptance: EditMode tests on metric math with fixture telemetry; window runs a batch and shows results; precise section flagged harder than forgiving section in demo run; every finding lists episode/replay ids.
Depends: T3, T6.

### T8 — Tolerance perturbation + counterfactual (impl: gpt-systems-engineer, rev: sonnet)
Files: `Runtime/Analysis/Perturbation.cs`, `Runtime/Core/TunableOverride` application in adapter, editor Counterfactual tab, tests.
Perturb solver solution: shift jump/dash tick ±N, measure success window. Counterfactual: one geometry tunable (moving-platform speed or a named platform position — pick moving-platform speed: pure component field, restoration trivial) with ≥3 variants, paired seeds, comparison report.
Acceptance: timing-window numbers produced for one obstacle; 3 variants × N seeds report; override restoration test (values identical after teardown); no auto-apply.
Depends: T5, T7.

### T9 — Docs + final review (Fable)
CLAUDE.md updates, limitations doc, final architecture review checklist from Step 9.

## Merge order
T1 → T2 → (T3 ∥ T4) → T5 → T6 → (T7 ∥ T8) → T9. No overlapping-file tasks run concurrently (T3/T4 touch disjoint files; T7/T8 disjoint).

## Test commands
Unity CLI: `Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results-edit.xml` (and PlayMode). In-session: UnityMCP `run_tests`.

## Risk points
1. Coroutine-clock nondeterminism → Phase-1 gate seed-repeatability test; escalate to opus-debugger if red.
2. Cloning level into arena scene: crumble/refill/moving-platform state reset — adapter must fully re-init; isolation tests cover.
3. Manual Simulate vs FixedUpdate callbacks: `Physics2D.simulationMode = Script` still invokes FixedUpdate via player? No — FixedUpdate runs on Unity's fixed clock regardless; with Script mode, we call Simulate manually but MonoBehaviour.FixedUpdate cadence is driven by Time. Resolution: arena ticking calls a public `TickSimulation(dt)` path — player logic refactored minimally so FixedUpdate body is callable as `Tick(dt)` (seam task T1) — flagged as the highest-risk integration detail; T2 owner must verify callback ordering empirically before building on it.
4. Solver runtime cost — re-simulation is O(depth) per node; if too slow, snapshot cache (deferred).
