# System Overview — Platformer Playtest Tool

Unity-native tool that runs simulated players through the CelesteBenchmark simulator to detect difficulty spikes,
unfair obstacles, softlocks, and game-feel regressions, with evidence-linked replays and counterfactual testing.
This remains the preserved Unity authoring/reference subsystem.

RYZ-1 adds a GB10-native runtime split described in `docs/architecture/gb10-native-runtime.md`: Unity exports
task bundles, `Ryz1.SimCore` runs authoritative ARM64 simulation and replay verification, and Python trains the
mechanics-conditioned policy-value model.

## Unity Layers

```
Unity Editor package (com.project.platformer-playtest)
├─ Editor assembly: setup wizard, run dashboard, reports, heatmaps, replay UI, counterfactuals
└─ Runtime assembly
   ├─ Generic simulation contracts (IGameAdapter<TAction,TObservation>, IAgent<TAction,TObservation>)
   ├─ Game plugin (adapter + scenario provider + game-specific solver/telemetry codecs)
   ├─ Runtime scenario snapshot (discovered spawn, objectives, sections, tunables, procedural seed)
   ├─ Arena (isolated physics scene: level copy + player + adapter instance)
   ├─ Episode runner (tick loop: observe → agent.Act → apply → step physics → record)
   ├─ Agents (IAgent: Scripted, BeamSearchSolver, ProfiledAgent[beginner/intermediate/expert])
   ├─ Telemetry (episode header + per-tick records + events, JSON-lines → Library/PlatformerPlaytest/)
   ├─ Replay (action stream + keyframes, desync detection)
   ├─ Analysis (completion/failure metrics, tolerance perturbation, section stats)
   └─ Worker protocol (later: headless process orchestration)
```

## Unity Core Loop

1. ArenaManager loads level content into an additive scene with `LocalPhysicsMode.Physics2D`.
2. The selected game's `IScenarioProvider` prepares deterministic procedural content, then discovers spawn,
   objectives, and ordered checkpoints from the loaded arena. Missing or ambiguous metadata is a hard error.
3. EpisodeRunner each tick: adapter builds Observation → agent returns PlayerAction (press edges + held) → adapter injects into player controller (virtual input, bypassing Keyboard) → `PhysicsScene2D.Simulate(fixedDeltaTime)` → adapter drains events (death, checkpoint, goal) → telemetry records.
4. Analysis consumes telemetry files; Editor window renders reports and replays; replay re-runs the action stream and compares keyframes.

## Unity Execution Modes

- **Quick Editor mode** (MVP): 1–8 arenas in-editor, scripted stepping (many `Simulate()` calls per editor frame → faster than real time), immediate cancellation, full cleanup (unload additive scenes).
- **Batch worker mode** (legacy/post-MVP): editor orchestrator ↔ persistent x64 headless Unity players. This is
  not the GB10-native hackathon runtime.
- **GB10 native mode**: exported RYZ Task Bundle → `Ryz1.Runner` / `Ryz1.SimCore` on ARM64 Linux → local report
  imported back into Unity.

## Key design decisions (see decisions/)

Preserved Unity decisions: explicit adapter, physics-scene-per-arena isolation, search-based solver, JSON-lines
telemetry, data under `Library/PlatformerPlaytest/`.

Current RYZ-1 decisions: GB10-native SimCore runtime, versioned task bundles, Python training, data under
`Library/RYZ1/`, no Unity installation required on GB10.

`PlayerAction`, `Observation`, and the bundled beam-search solver are the CelesteBenchmark plugin's default
profile. They are not universal core requirements. A platformer with grappling, vehicles, rhythm inputs, or
another state model uses the generic contracts and supplies its own solver/agent and codecs.

## Commercial Package Layer

`Packages/com.ryzi.unity` adds a distributable layer above the prototype:

```
Ryzi.Runtime
  universal actions/observations, manifest, episode/event/replay/telemetry/profile/tunable contracts
Ryzi.Editor
  scene scanner, evidence ranking, calibration orchestration, window, local paths, services, diagnostics
Ryzi.Integrations.ExistingSimulator
  repository-owned provider; strongly typed to PlatformerPlaytest and discovered by the Editor layer
```

The package Runtime has no dependency on CelesteBenchmark, the prototype, Input System, or UnityEditor.
Discovery is static candidate generation; calibration is the runtime verification gate. The existing solver
remains the execution engine for this first provider.
