# ADR-023: GB10 Native SimCore Runtime

Status: Accepted.

Context: Dell Pro Max / NVIDIA GB10 runs DGX OS on ARM64 Linux. Unity Editor and standard Linux Player builds
are x64-only, so they cannot be the primary native hackathon runtime.

Decision: Split RYZ-1 into Unity authoring, pure .NET `Ryz1.SimCore`, and Python training/evaluation. SimCore is
the authoritative hackathon simulation and replay verifier on GB10. Unity exports task bundles and displays
reports but is not required on GB10.

Consequences:

- `Ryz1.SimCore` must avoid UnityEngine and Editor APIs.
- Task bundles become the Unity/native contract.
- Unity-to-SimCore parity tests are required for supported hackathon mechanics.
- WebGL can be a visual fallback only, not the dataset-generation path.
