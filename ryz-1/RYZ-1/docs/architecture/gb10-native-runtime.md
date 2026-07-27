# GB10 Native Runtime

The hackathon runtime target is Dell Pro Max with NVIDIA GB10 running DGX OS on ARM64 Linux. Standard Unity
Linux Editor and Player builds are x64-only, so Unity is not the native runtime on GB10.

## Layers

1. Unity package and authoring integration

Runs in a normal developer Unity environment. It scans scenes and source, generates or validates Mechanics
Manifests, exports RYZ Task Bundles, imports GB10 reports, and visualizes replays.

2. `Ryz1.SimCore`

Pure C# `net8.0`, no `UnityEngine`, no Editor APIs. It provides explicit deterministic tick APIs, movement,
collisions, reset, actions, observations, mechanics randomization hooks, beam search, and replay verification
for the hackathon mechanics subset. It is publishable with `dotnet publish -r linux-arm64`.

3. RYZ-1 GB10 runtime

`Ryz1.Runner` plus Python `ryz1` run natively on ARM64 Linux. The runner validates task bundles, orchestrates
SimCore, generates solver trajectory datasets, verifies replays, and writes local reports. Python trains and
evaluates the mechanics-conditioned recurrent policy-value model.

## Current Implementation

Implemented:

- `src/Ryz1.Contracts`
- `src/Ryz1.SimCore`
- `src/Ryz1.Runner`
- `src/Ryz1.ScanCli`
- `tests/Ryz1.SimCore.Tests`
- `scripts/publish_gb10.sh`
- `scripts/verify_gb10_runtime.sh`
- guarded Unity PlayMode test `UnityToSimCoreParityTests`

Limitations:

- SimCore currently covers the hackathon subset: axis movement, jump, dash, static platforms, hazards, goal,
  reset, macro replay, observations, dataset export, and deterministic beam search.
- Full CelesteBenchmark parity is not complete. Unity-to-SimCore parity must be expanded before claiming broad
  simulator equivalence.
- This environment does not have `dotnet`, so native publish verification was not executed here.

## WebGL Fallback

WebGL may be used only as a visual fallback if a required Unity behavior cannot yet be represented in SimCore.
It is not the dataset-generation or authoritative replay-verification path.
