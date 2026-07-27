# Repository Audit

Date: 2026-07-23. Audited by Fable (coordinator). Every claim cites code.

## Basics

- **Unity version**: 6000.3.6f1 (`ProjectSettings/ProjectVersion.txt`)
- **Render pipeline**: URP 17.3.0. **Input**: com.unity.inputsystem 1.18.0. **Test framework**: com.unity.test-framework 1.6.0 installed but **zero tests exist**.
- **No asmdefs** — everything compiles into Assembly-CSharp / Assembly-CSharp-Editor.
- **Repo structure**: entire simulator is 9 scripts, 1142 lines, under `Assets/Scripts/CelesteBenchmark/` (namespace `CelesteBenchmark`), plus generated assets under `Assets/CelesteBenchmark/` (sprite, tiles) and one scene `Assets/Scenes/SampleScene.unity`.

## Scripts

| File | Role |
|---|---|
| `CelesteBenchmarkPlayer.cs` (472) | Full player controller: run, jump (coyote + buffer + variable height), wall jump, wall slide, climb + stamina, dash, one-way drop-through, checkpoint respawn |
| `BenchmarkSpike.cs` | Hazard trigger → `player.Respawn()` |
| `BenchmarkCheckpoint.cs` | Trigger → `player.SetCheckpoint(pos + offset)` |
| `BenchmarkSpring.cs` | Trigger → `player.Bounce(...)` with velocity carry |
| `BenchmarkDashRefill.cs` | Trigger → `RefillDashAndStamina()`, respawn 2.5 s — coroutine in keyboard mode, tick countdown under simulation (T11) |
| `BenchmarkMovingPlatform.cs` | Kinematic RB, ping-pong `MovePosition` in FixedUpdate (or manual `Tick` under simulation) |
| `BenchmarkCrumblingPlatform.cs` | Collision → disable collider after 0.28 s, respawn 2.2 s — coroutine in keyboard mode, tick countdown under simulation (T11) |
| `BenchmarkCameraFollow.cs` | LateUpdate exponential lerp, visual-only |
| `Editor/CelesteBenchmarkSceneBuilder.cs` (416) | Builds SampleScene procedurally; `[InitializeOnLoad]` auto-build once per editor session (SessionState key) |

## Player controller architecture

- `Rigidbody2D` dynamic, `gravityScale = 0` — **gravity is manual** in `ApplyGravity` (`CelesteBenchmarkPlayer.cs:303`). Continuous collision, interpolation on.
- Collision sensing via `Physics2D.OverlapBox` ground/wall probes each FixedUpdate (`:235`).
- Movement written by setting `rb.linearVelocity` directly each FixedUpdate. Physics engine only does collision resolution.
- Tunables are public fields (coyoteTime 0.11, jumpBufferTime 0.12, dashSpeed 18, etc.) — ideal counterfactual surface.

## Input path

`Update()` → `ReadInput()` polls `Keyboard.current` / `Gamepad.current` directly (`:173-233`). Jump/dash **press edges** buffered in Update via `wasPressedThisFrame`; held state stored in fields (`moveInput`, `jumpHeld`, `climbHeld`); FixedUpdate consumes buffers. No InputAction assets; no abstraction seam — but all reads go through 4 private methods (`ReadInput`, `WasJumpPressed`, `WasJumpReleased`, `WasDashPressed`), giving one clean injection point.

## Execution path trace

Keyboard/Gamepad → `Update` (edge buffering, framerate-dependent) → `FixedUpdate`: collision probes → timers → dash start/update → drop-through → climb → jump → wall slide → gravity → horizontal accel → `rb.linearVelocity` → Physics2D step (collision, triggers fire hazard/checkpoint/spring/refill) → `LateUpdate` camera → render. Death: spike trigger → `Respawn()` coroutine (freeze 0.05 s wall-adjacent scaled time, teleport to checkpoint, refill). Completion was absent in the audited baseline; current playtest integration discovers explicit `BenchmarkGoal` markers from each loaded arena.

## Level representation

Despite names, **no actual Tilemap component is used at runtime** — `CreateTileLevel` spawns one GameObject + BoxCollider2D **per cell** (`FillSpriteTileRect`, `:312`). Layers: Player 6, Ground 7, MovingPlatform 8, OneWay 9 (PlatformEffector2D one-way), Hazard 10, Trigger 11, Crumble 12. Tile assets exist under `Assets/CelesteBenchmark/Tiles/` but are unused by the builder path that runs. There are no "rooms" — one long linear level (~x −14 to 112), 5 checkpoints define implicit sections.

## Death / respawn / checkpoint / completion

- Death: only via spike triggers. **No kill floor** — a floor at y=−8 spans the whole level (`:156`), so falling never kills; spikes sit in the one pit.
- Respawn: coroutine, `WaitForSeconds(0.05)`, teleport, refill, unfreeze. Player object persists (no destroy/instantiate).
- Checkpoint: last touched trigger sets respawn point. Checkpoints never deactivate (color change only).
- Completion: game-owned `BenchmarkGoal` markers; the adapter discovers the active objective after procedural
  generation.

## Scene lifecycle / static & singleton state

- Single scene, no scene reloads at runtime, no `DontDestroyOnLoad`, no singletons, no static mutable gameplay state in simulator scripts. `Time.timeScale` untouched.
- **One process-global mutation**: `Physics2D.IgnoreLayerCollision(playerLayer, oneWayLayer, ...)` during drop-through (`CelesteBenchmarkPlayer.cs:387`) — affects *all* players in the physics world for 0.22 s. This is the single cross-arena contamination hazard found.

## Determinism limitations

1. **Input in Update**: edge buffering depends on render framerate; two runs at different fps diverge. Fix: adapter injects input at FixedUpdate cadence (virtual input source).
2. **`WaitForSeconds` coroutines** (respawn freeze, drop-through window, crumble, refill respawn): resume on the Update clock, not FixedUpdate — timing quantized by render frames. Under `Physics.simulationMode` scripted stepping or uniform timeScale they are *approximately* stable but not frame-exact. Mitigation for MVP: drive simulation via normal FixedUpdate with high `Time.timeScale` (coroutines scale with it); accept ±1-frame variance risk and verify with repeatability tests; replace with fixed-tick timers only if tests fail.
3. Rigidbody2D interpolation is visual-only (doesn't affect physics state).
4. No RNG anywhere in the simulator — good; all nondeterminism is timing-based.
5. Box2D (Unity 2D physics) is deterministic on same machine/build for identical stepped inputs — cross-machine determinism not guaranteed; scope determinism claims to same-machine.

## Multi-arena feasibility

- Player, hazards, platforms are all instance-scoped — multiple player+level copies can coexist **if**:
  - Arenas are spatially separated or in separate physics scenes (`LoadSceneParameters(LocalPhysicsMode.Physics2D)` + `Scene.GetPhysicsScene2D().Simulate()` — also solves scripted stepping).
  - The global `IgnoreLayerCollision` drop-through is replaced with per-collider `Physics2D.IgnoreCollision` (small, contained change) — otherwise one arena's drop-through opens one-way platforms for all arenas.
- Camera is per-scene visual-only; disable in headless. `EditorBuildSettings.scenes` rewrite in the scene builder is editor-only.
- Verdict: high-throughput multi-arena is feasible; start strict-per-physics-scene, benchmark counts.

## Faster than real time

Nothing blocks it: raise `Time.timeScale` (coroutines follow), or step `PhysicsScene2D.Simulate()` manually in a loop. FixedUpdate-driven logic is well-behaved. Camera/rendering are the only visual-only systems; both trivially disabled headless (`-batchmode -nographics` for workers).

## Testing / replay infrastructure

None exists. Test framework package installed; no test assemblies, no recording of any kind.

## Risks

- Auto-build hook (`CelesteBenchmarkAutoBuild`) rebuilds SampleScene once per editor session — overwrites scene edits; harmless to our work but must not fire in workers (editor-only, it won't).
- Uncommitted user changes exist across ProjectSettings/scene — do not revert.

## 2026-07-25 Commercial-Package Audit Addendum

This addendum supersedes stale baseline statements above. The current working tree has three production
assemblies (`CelesteBenchmark`, `PlatformerPlaytest.Runtime`, `PlatformerPlaytest.Editor`) and two test
assemblies. There are 12 EditMode and 13 PlayMode test source files. The scene builder now avoids overwriting an
existing SampleScene on automatic initialization; its explicit menu command still rebuilds intentionally.

Current input has two paths. Keyboard play polls `Keyboard.current` and `Gamepad.current`. Simulation calls
`CelesteBenchmarkPlayer.SetTickDriven(true)`, injects `IVirtualInput`, calls `Tick(dt)`, then manually advances
the arena PhysicsScene2D. Reset is `CelesteBenchmarkAdapter.ResetEpisode` to
`CelesteBenchmarkPlayer.ResetForEpisode`, followed by reset of refills, crumble platforms, and moving platforms.
Death is `BenchmarkSpike.OnTriggerEnter2D` to `Respawn`, which raises `Died`; the adapter records the edge.
Checkpoints call `SetCheckpoint`. Completion is overlap against the highest-priority active `BenchmarkGoal`,
falling back to the discovered goal rect.

`BeamSearchSolver` expands deterministic movement macros to `PlayerAction` streams and re-simulates from reset.
`SegmentedSolver` targets discovered checkpoints, carries full prefixes, and performs a fresh-reset final replay.
The commercial package must preserve that path. Existing test commands are Unity Test Framework EditMode and
PlayMode batch invocations documented in `CLAUDE.md`; the editor is currently open, so a second CLI Unity process
cannot safely acquire the project until the active editor is closed.

Primary risks are the large pre-existing dirty working tree, Play-Mode-only local physics-scene creation, a
roughly two-minute uncached SampleScene solve, and bounded cross-process Physics2D divergence documented in
`limitations.md`. The package slice is therefore additive and does not rewrite existing solver files.
