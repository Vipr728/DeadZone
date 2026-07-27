# Mechanics Manifest

`MechanicsManifest` is the versioned integration IR between discovery, calibration, binding, agents, reports,
and the native RYZ Task Bundle exported for GB10 execution.
It contains channel definitions, resource/state definitions, mechanics, affordances, tunables, issues, and
provenance. Runtime code uses channel IDs; suggested names are presentation metadata only.

Evidence levels are `DeveloperDefined`, `SourceVerified`, `RuntimeVerified`, `SourceCandidate`,
`RuntimeObserved`, `ModelSuggested`, and `Unknown`. Static and runtime confidence are independent. Developer
confirmation is explicit. A mechanic is never promoted to runtime-verified merely because its name resembles
jump, dash, or another familiar ability.

The current Unity manifest version is `1.0`. The native DTO schema is `mechanics-manifest/1.0` in
`src/Ryz1.Contracts`. JSON serialization uses Unity-compatible arrays and public fields. Unity
objects are represented by stable scene paths or type names only, never embedded object graphs.
