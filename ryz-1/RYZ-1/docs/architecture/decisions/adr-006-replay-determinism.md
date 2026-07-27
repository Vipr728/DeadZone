# ADR-006: Replay = action stream re-simulation with keyframe verification

Status: Accepted

Context: Audit determinism findings: input-in-Update (fixed by adapter injecting at tick cadence), WaitForSeconds coroutines (Update-clock), no RNG in simulator, Box2D same-machine deterministic under identical stepped input.

Decision: Replay re-runs the exact per-tick action stream through the same manually-stepped arena; keyframes (position, velocity, state flags every N ticks) recorded during original run are compared during replay; first divergence reported as desync with frame number and deltas. Determinism claims scoped to same machine + same build until tested wider. Coroutine-clock risk: seed repeatability test (same seed twice → identical trajectory) is a Phase 1 gate; if it fails, convert simulator coroutine timers to fixed-tick counters (opus-debugger task).

Alternatives: Full state snapshot/restore replay — rejected: Unity has no cheap deep-state snapshot; keyframe comparison gives the same evidence value.

Consequences: Desync is detected, not silently wrong. Perturbation analysis reuses the same machinery.

Amendment (T14): the determinism scope is **same process**, not merely same machine/build. Unity Physics2D
contact resolution is not bit-exact across processes (measured <= 0.0125 units over 400 ticks on SampleScene;
limitations.md #14 has the repro, the numbers, and what was ruled out — it is not multithreading, not job workers,
not consistency sorting, and not our code). The decision above is unchanged, because episodes are always simulated
start-to-finish inside one process. Two additions: `ReplayVerifier` takes an optional position tolerance and
exposes `CrossProcessTolerance` (0.25 units) for verifying a recording produced by another process, and
`tools/cross-process-determinism.sh` is the out-of-suite guard, since a single-process test cannot detect a
cross-process regression. Byte-exact golden traces on disk are explicitly rejected as an evidence mechanism.
