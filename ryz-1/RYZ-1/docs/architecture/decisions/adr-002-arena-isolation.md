# ADR-002: Physics-scene-per-arena in-process, worker processes for scale

Status: Accepted

Context: Audit found simulator state is instance-scoped except one global (`Physics2D.IgnoreLayerCollision` in drop-through, CelesteBenchmarkPlayer.cs:387). Unity supports isolated 2D physics worlds via additive scenes with `LocalPhysicsMode.Physics2D` and manual `PhysicsScene2D.Simulate()`.

Decision: Each arena = one additive scene with local 2D physics, stepped manually. Replace the global drop-through with per-collider `Physics2D.IgnoreCollision`. Process-level parallelism via headless workers post-MVP. Arena counts chosen by benchmark, not hardcoded.

Alternatives: (a) spatial separation in one physics world — rejected, drop-through global leak plus query-layer crosstalk; (b) strict one-arena-per-process only — kept as fallback mode, but in-process batching is cheap and the audit shows it's safe after the drop-through fix.

Consequences: Manual stepping also gives faster-than-real-time and deterministic ticking for free. Coroutine timing (WaitForSeconds) still runs on the Update clock — flagged as determinism risk to be tested (ADR-006).
