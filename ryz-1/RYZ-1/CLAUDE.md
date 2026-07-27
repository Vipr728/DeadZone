# Platformer Playtest — repo instructions

## Facts
- Unity 6000.3.6f1, URP, new Input System. macOS.
- Simulator (do not rebuild): `Assets/Scripts/CelesteBenchmark/`, namespace `CelesteBenchmark`. Scene auto-built by `Editor/CelesteBenchmarkSceneBuilder.cs` (menu: Celeste Benchmark/Build Tile Benchmark Scene; auto-runs once per editor session and OVERWRITES SampleScene).
- Playtest tool: `Assets/PlatformerPlaytest/` (Runtime/Editor/Tests asmdefs). Only `Runtime/Adapter/` may reference CelesteBenchmark types.
- Generated run data: `Library/PlatformerPlaytest/runs/` — never write run data under `Assets/`.
- Architecture docs: `docs/architecture/`, ADRs in `docs/architecture/decisions/`, specs in `docs/specifications/`, plan in `docs/plans/mvp-vertical-slice.md`.

## Build/test
- Tests: Unity Test Framework. CLI: `Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results.xml` (also PlayMode). Prefer UnityMCP `run_tests` when editor connected.
- No standalone build pipeline yet; headless workers post-MVP.

## Conventions
- C#: file-scoped classes `sealed` where possible, public tunables as fields on MonoBehaviours (simulator style), no LINQ in per-tick paths, no static mutable state in runtime systems, no new dependencies without an ADR.
- Determinism: seeded System.Random only; no wall-clock in simulated paths; timers in ticks, not WaitForSeconds, inside simulation code.
- Synthetic profiles must always be labeled synthetic in UI/reports.

## Model routing
- fable: architecture, interfaces, plans, final review (agent: fable-architect, read-only).
- sonnet-implementer: routine Unity C#, editor UI, tests.
- gpt-systems-engineer: search/concurrency/replay-determinism/perf (currently runs on opus — no GPT gateway configured; see agent file).
- opus-debugger: nondeterminism, desync, lifecycle bugs, failed tasks.
- independent-reviewer: read-only; model must differ from implementer.

## Definition of done
Implementation + tests in same task; real test output verified (never trust agent claims); independent review passed; simulator keyboard play unchanged; no data under Assets/; docs updated when interfaces change.
