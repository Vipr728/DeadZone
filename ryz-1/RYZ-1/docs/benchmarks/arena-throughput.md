# Arena throughput benchmark (T4)

Multi-arena isolation + throughput measurement for the physics-scene-per-arena design (ADR-002).

## Machine / build context

| | |
|---|---|
| Date | 2026-07-23 |
| CPU | Apple M5 (10 logical cores, `SystemInfo.processorCount = 10`) |
| OS | macOS 26.0.0 (darwin) |
| Unity | 6000.3.6f1 |
| Fixed delta | 0.02 s (50 ticks/s real-time equivalent) |
| Run | batchmode `-nographics`, PlayMode test `ThroughputBenchmarkTests.Throughput_Sweep` |

Numbers below are parsed from the `BENCH|ROW|…` lines in the batch-mode PlayMode log.

## Methodology

For each arena count in {1, 2, 4, 8, 16, 32}:

- Create N additive scenes, each with `LocalPhysicsMode.Physics2D` (one `Arena`), cloning the in-code
  test level (`TestLevelBuilder.Build`).
- Bind one `CelesteBenchmarkAdapter` per arena, then run one scripted episode per arena
  (`ScriptedAgent`, `stepBudget = 500`, same seed) **sequentially**, aggregating simulated ticks over
  wall-clock time. Sequential-per-arena is the honest metric for in-process batching: `EpisodeRunner.Run`
  is synchronous per episode, so wall-time and tick-count both scale with N and the *rate* is what matters.
- `reset latency` = wall time of one `adapter.ResetEpisode` (averaged across the N arenas).
- Memory sampled after the sweep row via `GC.GetTotalMemory(false)` and
  `Profiler.GetTotalAllocatedMemoryLong()`.

Each scripted episode reaches the goal in 77 ticks, so `simTicks = 77 × arenas`.

## Results

| arenas | sim ticks | wall (s) | ticks/sec | episodes/min | peak arenas | reset (ms) | GC heap (MB) | profiler (MB) |
|-------:|----------:|---------:|----------:|-------------:|------------:|-----------:|-------------:|--------------:|
| 1  | 77   | 0.002 | 35 954 | 28 016 | 1  | 0.003 | 722.4 | 211.8 |
| 2  | 154  | 0.004 | 36 093 | 28 124 | 2  | 0.001 | 722.5 | 212.1 |
| 4  | 308  | 0.008 | 36 382 | 28 350 | 4  | 0.000 | 722.5 | 212.7 |
| 8  | 616  | 0.017 | 36 280 | 28 270 | 8  | 0.000 | 722.5 | 213.9 |
| 16 | 1232 | 0.034 | 36 409 | 28 371 | 16 | 0.000 | 722.6 | 216.1 |
| 32 | 2464 | 0.075 | 32 853 | 25 600 | 32 | 0.001 | 722.7 | 220.7 |

## Analysis

- **Throughput is ~flat at ~36k simulated ticks/sec** (≈ 720× real-time) from 1 to 16 arenas, because the
  work is sequential and single-threaded — adding arenas adds proportional work *and* wall time. At 32
  arenas it dips ~10% (32.8k) from scene-management / GC overhead, not per-tick cost.
- **Memory scales gently**: ~0.28 MB additional Profiler-allocated per arena (211.8 → 220.7 MB across 1 →
  32). The managed GC heap is flat (editor-dominated). In-process batching of dozens of arenas is cheap.
- **Reset latency is negligible** (< 0.003 ms) — `ResetEpisode` re-inits player + dynamics in place, no
  scene reload.
- **Episodes/min** here reflect the trivial 77-tick test level (~28k/min); real scenarios with longer
  budgets scale down proportionally but keep the same per-tick rate.

The in-process, physics-scene-per-arena approach adds no measurable per-tick penalty and trivial memory up
to 32 arenas. The benefit of higher counts is amortizing management/reset overhead and enabling future
worker-process parallelism (ADR-002), not raw sequential speed — that requires threads/processes.

## Recommended default arena count: **8**

Rationale from the data: throughput is identical from 1–16 arenas and memory cost is negligible, so the
choice is about management granularity, not performance. 8 keeps memory overhead ~2 MB, sits safely below
the 32-arena throughput dip, and matches the 10-core host for the eventual one-arena-per-worker parallel
mode. Raise it only when parallel workers land and can turn the flat sequential curve into a real speedup.

## Isolation results

All four `ArenaIsolationTests` (PlayMode) pass:

- `TwoArenas_IndependentTraces` — an idle arena B never drifts in x while a scripted arena A traverses
  beside it (interleaved ticking).
- `IdenticalSeedArenas_MatchWhileNeighbourDiffers` — two same-seed arenas produce byte-identical position
  traces while a third arena runs a different (hold-left) action stream interleaved between them.
- `DropThrough_DoesNotLeakAcrossArenas` — arena A triggers a one-way drop-through (down + jump) and falls
  off its platform; arena B, idle on its own one-way platform, does not fall through during A's window.
  Confirms T1's per-collider `Physics2D.IgnoreCollision` is arena-local.
- `ManyArenas_NoCrossContamination` — 8 same-seed arenas, same agent, produce 8 byte-identical traces.

No strict-isolation fallback was required: in-process multi-arena isolation holds.

## Notes / findings while building this

- **`Physics2D.IgnoreCollision` works inside local (arena) physics scenes.** Verified with a throwaway
  pure-physics probe: a dynamic body settled on a floor in a `LocalPhysicsMode.Physics2D` scene, then
  `IgnoreCollision(box, floor, true)` + manual `Simulate` let it fall through (final y ≈ −27). This is the
  core ADR-002 assumption and it holds. The ignore is per-collider-pair, so it cannot leak between arenas.
- **Seam bug fixed (root cause):** the one-way drop-through `ContactFilter2D` was baked once in
  `CelesteBenchmarkPlayer.Awake` from `oneWayPlatformMask`, but callers (test level builders, and any code
  assigning masks after `AddComponent`) set that mask *after* Awake — leaving the filter empty so
  drop-through detected no platform. Fixed by re-applying the mask each check in `TryDropThrough`, exactly
  as the ground/wall filters already do (a code comment in the same file documents this caller pattern).
- **One-way test-platform tuning:** grounded descent is clamped to ~1 u/s, so drop-through needs a drop
  window proportional to platform thickness. The test level uses a thin (0.2) one-way platform and a 1 s
  `dropThroughDuration` so the drop unambiguously carries the player off the bottom. The drop agent presses
  jump exactly once (re-pressing every tick makes the still-"grounded" player jump back up mid-drop).
