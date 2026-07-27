# Spec: Game Adapter

Namespace `PlatformerPlaytest`. Only `Adapter/` references `CelesteBenchmark`.

## Generic contract

The reusable lifecycle is generic over action and observation types:

```csharp
IGameAdapter<TAction, TObservation>
IAgent<TAction, TObservation>
EpisodeRunner<TAction, TObservation>
```

The bundled CelesteBenchmark integration specializes those contracts to the following schema. Other games do
not need to translate custom mechanics into these fields.

## CelesteBenchmark PlayerAction (struct)

```csharp
public struct PlayerAction {
    public float MoveX, MoveY;        // -1..1
    public bool JumpPressed;          // press edge this tick
    public bool JumpHeld;
    public bool DashPressed;          // press edge this tick
    public bool ClimbHeld;
}
```
Press edges are per-tick, distinct from held. No custom bits in MVP.

## CelesteBenchmark Observation (allocated-once and mutated per tick)

Player: Position, Velocity, IsGrounded, OnLeftWall, OnRightWall, IsDashing, IsClimbing, DashesRemaining, Stamina, Progress (0..1 by x toward goal), SectionIndex.
World: occupancy grid `CellKind[W,H]` (Empty/Solid/OneWay/Hazard) centered on player (default 16×12 cells, 1-unit cells, via Physics2D.OverlapBox against arena physics scene); `List<DynamicEntity>` nearby (kind, position, velocity, size) for moving platforms, springs, refills, goal.

## GameEvent (enum + tick)

Death, CheckpointReached, DashRefillCollected, SpringBounced, GoalReached.

## Bundled IGameAdapter specialization

```csharp
public interface IGameAdapter {
    void Bind(Scene arenaScene, ScenarioConfig scenario);   // find/create player, goal, apply tunable overrides
    void ResetEpisode(int seed);                             // spawn pos, zero velocity, refill, reset dynamics, clear events
    void ApplyAction(in PlayerAction action);                // before physics step
    void ReadObservation(Observation target);                // after physics step
    bool IsDead { get; }        // death event fired this episode-step window
    bool IsComplete { get; }    // player inside goal region
    float Progress { get; }
    void DrainEvents(List<TimedGameEvent> into);
    void RestoreOverrides();                                  // undo tunable overrides (teardown); idempotent
}
```

### Tunable-override whitelist (ADR-008, T8)

`Bind` applies `ScenarioConfig.overrides` against an explicit whitelist, capturing each field's original value;
`RestoreOverrides` writes the captured originals back and is idempotent (a second call is a no-op). Any unknown
target or field is a hard error at `Bind` — the whitelist keeps the surface honest and small.

- `targetId == "player"` → fields `{coyoteTime, jumpBufferTime, dashSpeed, jumpVelocity, movementSpeed}` (public floats on `CelesteBenchmarkPlayer`).
- `targetId == "platform:<GameObjectName>"` → field `{speed}` on the matching `BenchmarkMovingPlatform`.

Overrides are never written back to any asset; they live only in the ephemeral arena for the run. The interface
itself is unchanged from earlier tasks — only the previously no-op `RestoreOverrides` now has real behavior.

## CelesteBenchmarkAdapter specifics

- **Scenario discovery:** `CelesteBenchmarkScenarioProvider` prepares procedural generators with the run's layout
  seed, discovers the player, highest-priority `BenchmarkGoal`, and checkpoints from the loaded arena, and emits a
  runtime `ScenarioConfig`. No SampleScene coordinates participate.

- **Virtual input seam** (the only simulator edit besides drop-through fix): add to `CelesteBenchmarkPlayer` a nullable `IVirtualInput` source; when set, `ReadInput`/`WasJumpPressed`/`WasJumpReleased`/`WasDashPressed` read from it instead of devices, and edge buffering moves to the tick (adapter sets edges immediately before `Simulate`). Keyboard path unchanged when source is null.
- **Death**: spike calls `player.Respawn()`; adapter detects via a hook (`event Action Died` added to seam, invoked in `Respawn()`), counts death, and performs its own respawn handling (checkpoint teleport preserved). Respawn freeze coroutine replaced under simulation by immediate deterministic handling (freeze N ticks counter).
- **Completion**: the adapter evaluates the active highest-priority `BenchmarkGoal` collider discovered from the
  arena. A missing or ambiguous goal fails setup; there is no coordinate fallback in SampleScene wiring.
- **Drop-through**: replace `Physics2D.IgnoreLayerCollision` with per-collider `Physics2D.IgnoreCollision` against overlapped one-way colliders; timer becomes tick-counted under simulation. Keyboard behavior equivalent.
- **Reset**: teleport, `linearVelocity = 0`, `RefillDashAndStamina()`, reset checkpoint to spawn, reset moving platforms (re-set position/direction), re-enable crumble/refill colliders+renderers, cancel coroutines (`StopAllCoroutines` + state re-init).

## Acceptance

- Manual keyboard play identical to before seam (regression: play scene, all mechanics work).
- Same seed + same action stream twice → identical position trace (Phase 1 gate test).
