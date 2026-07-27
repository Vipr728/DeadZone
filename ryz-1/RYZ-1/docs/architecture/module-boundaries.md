# Module Boundaries

## Assemblies

- `PlatformerPlaytest.Runtime`
  - `Adapter/` — game-owned adapters and scenario providers. **Only this folder may reference
    CelesteBenchmark types.**
  - `Core/` — generic `IGameAdapter<TAction,TObservation>`, `IAgent<TAction,TObservation>`,
    `EpisodeRunner<TAction,TObservation>`, `IScenarioProvider`, episode lifecycle, and arenas. The non-generic
    interfaces are the bundled Celeste action/observation profile, not a restriction on other games.
  - `Agents/` — IAgent, ScriptedAgent, BeamSearchSolver, ProfiledAgent, ReplayAgent
  - `Telemetry/` — recorder, schema types, writer
  - `Analysis/` — metrics, tolerance perturbation, findings
- `PlatformerPlaytest.Editor` (asmdef, editor-only; references Runtime)
  - Run window, reports, replay controls, scene overlay, counterfactual UI
- `PlatformerPlaytest.Tests.EditMode` / `.PlayMode`

MVP lives under `Assets/PlatformerPlaytest/` with asmdefs (functionally a package; extraction to `Packages/com.project.platformer-playtest` is a later mechanical move — deferred because the adapter must reference Assembly-CSharp, which a registry package can't cleanly do until the simulator itself gets an asmdef).

## Simulator (owned by game, not tool)

`Assets/Scripts/CelesteBenchmark/` — tool may add a *minimal seam* only: virtual-input injection hook and per-collider drop-through (replacing global IgnoreLayerCollision). Behavior with keyboard play must remain identical.

## Ownership / dependency rules

- Bundled agents depend only on `Observation`/`PlayerAction`. A game with different mechanics supplies matching
  action/observation types and agents through the generic core contracts.
- Core never contains level coordinates, scene-object names, goal-selection rules, or procedural-generation
  policy. `IScenarioProvider` discovers those from the loaded arena inside the game adapter layer.
- Analysis depends only on telemetry schema — never on live simulation.
- Editor assembly never contains simulation logic.
- Nothing outside Adapter/ touches CelesteBenchmark. Nothing writes under Assets/ at run time.

## Commercial Boundaries

- `Ryzi.Runtime`: public contracts and serializable data required by simulation/replay consumers.
- `Ryzi.Editor`: AssetDatabase/scene inspection, source-candidate analysis, UI, paths, diagnostics,
  entitlements, provider orchestration, and overlays.
- `Assets/Ryzi.Integrations`: optional customer/project provider. It may reference game and prototype
  assemblies; base package assemblies may not.
- Integration providers are discovered once outside tick loops. Their simulation implementation remains strongly
  typed, so reflection does not enter measured per-tick paths.
- Optional Input System support is asset inspection in the base Editor assembly. A future strongly typed Input
  System integration gets its own version-defined assembly.
