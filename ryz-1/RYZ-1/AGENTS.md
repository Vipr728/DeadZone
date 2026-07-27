# RYZ-1 Repository Guide

## Project Identity

Company: Ryzi Labs. Product: RYZ-1.

RYZ-1 is a hackathon prototype for mechanics-conditioned automated Unity platformer testing. The native
hackathon runtime must run on Dell Pro Max / NVIDIA GB10 without Unity installed on the GB10.

## Important Locations

- Unity authoring package: `Packages/com.ryzi.unity/`
- Existing Unity prototype and solver: `Assets/PlatformerPlaytest/`
- Existing Celeste-style simulator: `Assets/Scripts/CelesteBenchmark/`
- Unity integration provider: `Assets/Ryzi.Integrations/`
- Native shared DTOs: `src/Ryz1.Contracts/`
- Native deterministic simulator: `src/Ryz1.SimCore/`
- Native GB10 runner: `src/Ryz1.Runner/`
- Native task-bundle scan CLI: `src/Ryz1.ScanCli/`
- Native tests: `tests/Ryz1.SimCore.Tests/`
- Python training package: `python/ryz1/`
- Generated data: `Library/RYZ1/` or ignored `artifacts/`

## Boundaries

Unity owns project scanning, authoring review, manifest export, task-bundle export, imported report display,
and replay visualization. Unity Editor and standard Unity Linux Player builds are x64-only and must not be
claimed as native GB10 runtime.

`Ryz1.SimCore` owns authoritative hackathon simulation on GB10. It has no `UnityEngine` or Unity Editor
dependency and must publish with `dotnet publish -r linux-arm64`.

Python owns dataset validation/loading, PyTorch model definitions, training, evaluation, checkpointing,
TensorBoard logging, and ONNX export. Do not move authoritative game simulation into Python.

## Versions

- Unity: `6000.3.6f1` (`ProjectSettings/ProjectVersion.txt`)
- Native .NET target: `net8.0`
- Python: `>=3.10`

## Commands

- Native tests: `dotnet test tests/Ryz1.SimCore.Tests/Ryz1.SimCore.Tests.csproj`
- Native dataset generation: `scripts/generate_dataset.sh`
- GB10 runtime verify: `scripts/verify_gb10_runtime.sh`
- GB10 publish: `scripts/publish_gb10.sh`
- Python setup: `scripts/setup_gb10.sh`
- Python smoke training: `scripts/train_ryz1.sh`
- Evaluation: `scripts/evaluate_ryz1.sh`
- Demo workflow: `scripts/run_hackathon_demo.sh`
- Unity tests: run EditMode and PlayMode tests from Unity Test Runner for `PlatformerPlaytest`, `Ryzi`, and
  `Ryzi.Integrations`.

## Prohibited Modifications

- Do not break or replace `Assets/PlatformerPlaytest/Runtime/Agents/Solver/BeamSearchSolver.cs` or
  `SegmentedSolver.cs`; they remain the Unity search backend and teacher.
- Preserve `Assets/Scripts/CelesteBenchmark/` gameplay behavior unless a parity-backed migration requires a
  narrowly scoped change.
- Do not write generated run data under `Assets/`.
- Do not add cloud SDKs, billing code, or upload project data.
- Do not use x86 emulation as the primary GB10 architecture.

## Definition of Done

Existing solver behavior is preserved, native SimCore can validate and run task bundles, datasets are versioned,
Python training runs locally with CPU/CUDA fallback, neural outputs never approve trajectories without SimCore
or Unity replay verification, tests or exact blockers are documented, and docs reflect actual implementation.

Detailed docs:

- `docs/architecture/ryz1-overview.md`
- `docs/architecture/gb10-native-runtime.md`
- `docs/architecture/old-self-learning-migration.md`
- `docs/specifications/ryz-task-bundle.md`
- `docs/specifications/hackathon-demo.md`
