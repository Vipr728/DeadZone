# Known Limitations & Remaining Risks (post-vertical-slice, 2026-07-23; T11 update)

## Honest scope notes

1. ~~**Slice ran on a minimal in-code test level, not SampleScene.**~~ **CLOSED by T11.** The real
   `Assets/Scenes/SampleScene.unity` now loads as an isolated arena (`ArenaManager.LoadSceneArena`, additive +
   `LocalPhysicsMode.Physics2D`, Play Mode only) with its own scenario (`SampleSceneScenario` /
   `Assets/PlatformerPlaytest/Scenarios/SampleScene.asset`). PlayMode tests drive it, record/replay it, and search
   it. Duplicate additive loads of the same scene work — two copies are independent physics worlds. Remaining
   SampleScene gap is the solver's reach, tracked as its own item below, not the scene plumbing.
2. **Events**: adapter emits Death and GoalReached only. Checkpoint/spring/refill events deferred (needs small simulator hooks). Checkpoint metrics therefore absent from reports.
3. **Editor Run tab requires Play Mode** — `SceneManager.CreateScene(LocalPhysicsMode)` is Play-Mode-only (proven via CLI probe). Batch runs work in Play Mode or via PlayMode tests; the button says so honestly in Edit Mode.
4. **Replay playback UI** is metadata + copyable command only; visual scene playback is post-MVP. Replay execution/desync detection is API/tests-level (green).
5. **Synthetic profiles are not human models** (labeled in code + UI). `knowsWallJumpChains` currently only caps
   solver depth; macro filtering needs a solver hook. `explorationTendency` / `riskTolerance` from the spec are
   still unimplemented. ~~Profiles were open-loop and unusable on the real level.~~ **CLOSED by T13** — see item 13.
6. ~~**Profiles' persistence/retry dimensions** are exposed as helpers but not yet consumed.~~ **CLOSED by T13.**
   `ProfiledAgent` counts respawns itself (single-tick position teleport > 3 units — `IAgent` sees only
   `Observation`, so there is no death callback), rolls `ShouldRepeatFailedPlan` on each death to choose
   repeat-the-plan vs improvise-differently, and sets `Abandoned` once `persistenceRetries` is exceeded;
   `EpisodeRunner` turns that into the new `Outcome.Abandoned` instead of burning the whole step budget.
   `ShouldRetry` remains a batch-orchestrator helper with no in-episode consumer.
7. **Determinism scope**: **same PROCESS**, same machine, same build, fixedDeltaTime 0.02, tick-driven mode.
   Within one Unity process the simulated path is bit-exact — verified across episode resets *and* across two
   independently loaded copies of the same scene (T14). Across processes it is NOT bit-exact; see item 14 for the
   measured bound. Cross-machine replay untested. Normal keyboard mode (Update-clock coroutines, render-frame
   input) remains nondeterministic by design — only the simulated path is deterministic.
8. ~~**Coroutine clocks under simulation** (crumble, refill respawn)~~ **CLOSED by T11.**
   `BenchmarkCrumblingPlatform` and `BenchmarkDashRefill` now carry `SetTickDriven(bool)` + `Tick(float dt)` float
   countdowns, the same seam `CelesteBenchmarkPlayer` and `BenchmarkMovingPlatform` already used. Keyboard mode
   still runs the original `WaitForSeconds` coroutines byte-for-byte. Audit of everything else that fires in
   SampleScene: `BenchmarkSpring`, `BenchmarkSpike` and `BenchmarkCheckpoint` are pure trigger callbacks with no
   clock; player respawn and one-way drop-through were already tick-driven; `BenchmarkCameraFollow` is
   `LateUpdate` visual-only and its camera is disabled on load. No Update-clock dependency remains on the
   simulated path.

8b. **Reset was not state-exact for moving platforms** (found and fixed in T11, worth remembering as a class of
   bug): `BenchmarkMovingPlatform.ResetState` assigned `rb.position`, which queues a teleport that the next
   physics step applies *instead of* a `MovePosition` issued in that same step. Every reset therefore silently
   dropped the platform's first step, so episode 1 on a freshly loaded scene ran one tick ahead of every later
   episode — player traces diverged at tick 596 of SampleScene, and record/replay desynced at tick 600. Fixed by
   writing the position directly on the reset tick. Lesson: "identical after reset" must be tested against a
   COLD scene, not just reset-vs-reset.
9. **Worker processes / batch mode** (headless orchestration, phases beyond in-editor batching) not started — by plan. Benchmark shows in-process batching at ~36k ticks/s (~720× real-time), single-threaded; process parallelism is the next scaling step.
10. **Camera/rendering** untouched; headless workers will use -batchmode -nographics.

11. **Solver reach on SampleScene** (T11 result). The single-shot `BeamSearchSolver` cannot clear the real level —
   re-simulation is quadratic in depth. `SegmentedSolver` splits the traverse at the 5 authored checkpoints and
   DOES clear it: a validated 628-tick stream from spawn to the Finish column, 2.67M simulated ticks in 116 s at
   BeamWidth 20 / TickMenu {4,8,16,32} / MaxMacrosDepth 50. The narrower first attempt (BeamWidth 12, TickMenu
   {8,16,32}) stalled on segment 4 (x 66.5 → 98.5, furthest x = 71.72, "no new states to expand") — that section
   (vertical moving platform, crumble chain at x 82/87/92, diagonal spring at x 90, pillar at x 96) is the
   level's hardest for the planner and is the useful product signal from this run.
   Cost model is honest, not hidden: segment N+1 replays the whole accumulated prefix for every node it expands,
   so late segments dominate. `MaxTicksSimulated` is applied per segment.
   Difficulty signal off the solved plan (Beginner + Expert x 3 seeds, profile noise applied to the 628-tick
   stream): **all deaths land in section 1** (x -6 .. 19.5 — the spiked gap at x 14.5 with the pillar at x 15),
   none anywhere else. First real evidence that the death heatmap concentrates on an authored obstacle rather
   than smearing across the level.

12. ~~**SampleScene completion was a hard-coded coordinate column.**~~ **CLOSED by scenario discovery.**
   `CelesteBenchmarkScenarioProvider` now prepares procedural content first, then discovers the player,
   checkpoints, and highest-priority `BenchmarkGoal` from the loaded arena. The generated route owns its goal
   volume and layout seed. Missing or ambiguous objectives fail setup instead of silently selecting coordinates.

13. **Profiles are closed-loop on SampleScene (T13 result).** `ProfiledAgent.Act` now uses the observation:
   deviation from a caller-supplied reference trajectory (`ProfiledAgent.RecordTrajectory`, one extra plan replay
   per plan) triggers a re-anchor to the nearest point on that trajectory, and a search-free reactive controller
   improvises back onto the route when nothing is within tolerance. No solver re-entry and no scratch arena — a
   local beam search on a second arena was evaluated and deliberately not built, because the reactive controller
   clears the level. Measured over 6 seeds on the 754-tick solved plan: Beginner 0–1/6, Intermediate 3–5/6,
   Expert 6/6 (previously Expert hit StepBudgetExceeded, i.e. the comparison was degenerate).
   Honest gaps: the reactive controller is a heuristic with no completeness claim — it can walk a stalled agent
   into a wall forever, which is what Beginner's `StepBudgetExceeded at x≈14–44` runs are. Beginner's failures are
   stalls, not deaths, so they contribute no death-heatmap signal.

14. **SampleScene physics is NOT bit-exact across Unity processes** (found in T13, root-caused and bounded in
   T14). CONFIRMED and quantified independently, with a minimal repro that does not need the solved plan:
   `Tests/PlayMode/CrossProcessDeterminismTests` replays a hardcoded 400-action stream (hold right, tap jump every
   20 ticks) on a cold-loaded SampleScene and writes a raw-bit FNV hash of the whole position trace;
   `tools/cross-process-determinism.sh` runs it in two processes and diffs.

   **Measured** (11 runs, same machine, same build, cold scene each time):
   - 5 distinct traces across 11 runs. Runs are *self*-consistent: repeated arenas inside one process always agree.
   - First divergence always at tick 280 or tick 371 — never gradual drift; ticks 0..279 are bit-identical.
   - Worst-case |delta| between any two processes over 400 ticks: **0.0125 units** (~1/80 of a tile).
     T13's 754-tick number (x = 105.96 vs 106.13) is the same effect grown to ~0.17 over nearly twice the length.

   **Mechanism** (evidence, not speculation). Both divergence sites are contact-resolution events, not free
   flight: tick 280 is the player sliding across the seam between two adjacent 1x1 ground `BoxCollider2D` tiles at
   x = 20 (the level's ground is per-tile boxes, `CelesteBenchmarkSceneBuilder.FillSpriteTileRect`), tick 371 is
   the player pressed against the pillar face at x = 30. The delta at onset is 0.0032 units — the scale of Box2D's
   linear slop / max-linear-correction, i.e. Unity resolves the *same* contact to a slightly different rest
   separation. Ruled out with experiments, not by reading settings:
   - `useMultithreading: 0` already; **`useConsistencySorting: 1` does not help** (3 distinct traces with it on,
     and those traces fall into the same equivalence classes as the runs with it off). It is a no-op when
     multithreading is off, so it is not the lever it looks like. **Not applied** — no cost paid, none measured.
   - `-job-worker-count 0` does not help (3 runs, 2 distinct traces).
   - Not our code: no RNG, no `GetInstanceID`, no hash-ordered iteration on the simulated path; the player only
     issues `OverlapBox` sensor queries whose results are boolean-tested, and all timers are tick-driven.
   - Not allocation order *within* a process: `SampleScene_TwoArenasSameProcess_SameStream` loads two independent
     copies of the scene in one process and gets bit-identical traces. The varying quantity is therefore
     process-scope state established at startup (FPU/library-init/allocator-level), not per-object. Which exact
     quantity is not determined — it is inside Unity's native Physics2D and not reachable from C#.

   **Not fixable at our layer.** It is not a Physics2D setting and not our code. The only remaining lever would be
   removing the ambiguous contacts themselves (composite colliders instead of per-tile boxes), which changes
   keyboard gameplay and is out of scope.

   **What this does and does not invalidate:**
   - SURVIVES: "every finding links to a replay that reproduces it," because an episode is always simulated
     start-to-finish inside one process. Same-process replay is bit-exact with 0 tolerance
     (`SampleScene_DeterministicAcrossResets`, `ClosedLoop_IsDeterministic`, and the two-arena test above).
   - SURVIVES: multi-process batching (ADR-002). Workers own whole episodes; nothing is compared bit-for-bit
     between workers. Results are statistically comparable — a 0.0125-unit position difference is far below any
     reported quantity (section boundaries, death positions, heatmap bins).
   - BREAKS: byte-exact golden traces on disk, and cross-process/cross-machine desync detection at the 1e-4
     quantization grid. Replaying a recording made by another process against `ReplayVerifier`'s default exact
     comparison WILL report a spurious desync.
   - MITIGATION SHIPPED: `new ReplayVerifier(keyframes, ReplayVerifier.CrossProcessTolerance)` (0.25 units, 20x
     the measured worst case, well under a tile) for verifying a recording made by a different process. Flags and
     dash count are still compared exactly, so a genuinely different outcome still desyncs. Default construction
     is unchanged and stays exact.
   - Closed-loop agents still amplify it: a sub-slop difference can cross a deviation threshold and flip a
     branch, which is why T13's per-profile completion rates are ranges. That is inherent to closed-loop control
     on top of a non-bit-exact engine, and is why profile results must be reported as distributions over seeds.

   **Regression guard**: the standard suite cannot cover this (one process is deterministic by construction).
   `tools/cross-process-determinism.sh [runs]` is the guard; run it manually or in CI. It runs N processes
   (default 3), diffs the traces, and exits non-zero on divergence with the first diverging tick. It is a sampling
   detector, not a proof — with ~5 outcome classes two runs agree by chance often enough to mislead, which is why
   the default is 3. **As of T14 this script FAILS by design**: it documents the current divergence and will start
   passing only if a future Unity version or engine change makes physics cross-process exact. Do not wire it into
   a red/green CI gate as-is; run it to detect *change*.

15. ~~**The committed `Assets/Scenes/SampleScene.unity` is not the scene the tests run against**~~ **CLOSED by
   limitation #15 fix.** The `[InitializeOnLoad]` `CelesteBenchmarkAutoBuild` hook now no-ops when
   `Assets/Scenes/SampleScene.unity` already exists on disk, so opening the editor no longer regenerates the
   committed scene or discards hand edits. The explicit `Celeste Benchmark/Build Tile Benchmark Scene` menu item
   still rebuilds unconditionally; use it only when intentionally regenerating SampleScene from the builder.

## Risks

- Perturbation on the slice ledge showed a fully-forgiving ±8-tick window (17/17 success) — the tolerance machinery is validated but the test obstacle is too easy to demonstrate a narrow window; SampleScene precise jumps are needed for a real spike-vs-forgiving contrast (slice requirement #13 is only partially demonstrated: sections/difficulty flags exist, but the forgiving-vs-precise comparison on the real level awaits the SampleScene scenario).
- Solver is quadratic-in-depth (re-simulation). Checkpoint segmentation (T11) made SampleScene tractable at ~2
  minutes; state snapshots are still the real fix if levels get longer or if per-segment budgets have to grow.
- ~~Cross-process physics divergence (item 14) is the biggest unknown~~ **BOUNDED by T14**: it is real, it is
  inside Unity's Physics2D contact resolution, and it is capped at 0.0125 units over 400 ticks / ~0.17 over 754.
  It does not affect episode-level results or multi-process batching; it does rule out byte-exact golden traces.
  `ReplayVerifier.CrossProcessTolerance` is the supported way to verify a replay recorded by another process.
- `Physics2D.queriesHitTriggers` snapshot at Awake could go stale if a project toggles it at runtime (documented in code).

## Next recommended milestone

T11 delivered the SampleScene scenario, the isolated scene-arena loader, the crumble/refill tick conversion and a
full segmented solve. Next: checkpoint/spring/refill events (item 2) so section reports carry more than deaths,
then process-level parallelism (item 9) — the 116-second solve is single-threaded and embarrassingly parallel
across seeds.
