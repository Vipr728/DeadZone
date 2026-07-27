# RYZ-1 Overview

RYZ-1 is a mechanics-conditioned platformer testing prototype. The current architecture separates Unity
authoring from native GB10 execution.

```text
Unity authoring scanner
        ↓
Mechanics Manifest
        ↓
RYZ Task Bundle
        ↓
Ryz1.SimCore on GB10
        ↓
Runtime calibration / deterministic search / dataset export
        ↓
PyTorch recurrent policy-value training
        ↓
Neural-guided SimCore search
        ↓
SimCore replay verification
        ↓
Local report imported into Unity
```

The existing Unity solver remains functional and is preserved as a teacher, fallback, and parity reference. The
new native stack is the authoritative hackathon execution path because GB10 cannot run the Unity Editor or
standard Linux Player natively.

P0 implemented in this migration:

- Versioned native task-bundle contracts.
- Deterministic SimCore subset for the demo mechanics.
- Native runner and scan CLI.
- Dataset export from search transitions.
- PyTorch GRU policy-value model with task/trial fields in the dataset.
- GB10 setup, publish, generation, training, evaluation, export, and demo scripts.

P0 still requiring follow-up:

- Broader Unity-to-SimCore numeric parity over the current CelesteBenchmark scene.

Implemented bridge path:

1. Unity exports supported static colliders, hazards, goal, checkpoints, fixed timestep, and player movement
   parameters as `ryz-unity-snapshot/1.0`.
2. The GB10 runner converts the snapshot to a fingerprinted task bundle and performs sequence-aware ONNX-guided
   search.
3. SimCore verifies the macro replay.
4. Unity imports the macro IDs, expands the task vocabulary, and authoritatively replays the actions in an
   isolated real-physics arena.

`Tools > RYZ-1 Neural Playtest` is the author-facing path. It loads the current saved scene into an isolated
Unity physics arena, rejects geometry outside the supported parity subset, transfers the snapshot over
passwordless Tailscale SSH, runs the trained guide on the GB10, downloads the artifacts, verifies them in Unity,
and opens a visible Game View replay.

`scripts/run_unity_bridge.sh` remains the non-interactive DemoLevel regression path.
