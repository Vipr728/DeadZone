# ADR-003: Explicit game adapter, no automatic project inference

Status: Accepted

Context: Tool must read/write game state reliably across future Celeste-like projects.

Decision: One `IGameAdapter` interface; per-game implementation (`CelesteBenchmarkAdapter`) owns all knowledge of the game's types. Input injected via an explicit virtual-input seam added to the player controller, not keyboard emulation.

Alternatives: Reflection/heuristic inference of arbitrary controllers — rejected: fragile, unbounded scope. OS-level input emulation — rejected: framerate-dependent, headless-hostile.

Consequences: Each supported game needs a small adapter (acceptable — target is one genre); simulator needs a ~20-line seam (justified, behavior-preserving).
