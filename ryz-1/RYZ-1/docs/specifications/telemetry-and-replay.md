# Telemetry And Replay

All generated data resolves below `<project>/Library/Ryzi/`. A centralized path service rejects paths under
`Assets`. Run metadata includes episode/scenario IDs, package/manifest/Unity versions, project revision when
available, agent/profile IDs, seed, fixed timestep, settings hashes, timings, ticks, and solver expansions.

Replay records contain scenario, seed, action stream, state keyframes, versions, and failure tick. Playback
supports play, pause, step, and jump-to-failure through provider capabilities. State hashes report
desynchronization; deterministic replay is claimed only when the provider's repeatability probe passes.

JSON is local and developer-initiated. No source, scenes, prefabs, telemetry, or reports cross a service boundary
in the MVP.
