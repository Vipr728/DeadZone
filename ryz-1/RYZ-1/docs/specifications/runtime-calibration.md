# Runtime Calibration

Calibration runs only through an isolated `IPlaytestIntegrationProvider`. The package never probes the active
scene in place. Each probe captures state, resets to matched initial conditions, applies a bounded action stream,
records before/after observations and events, and restores in `finally`.

Initial probes are no input, horizontal negative/positive, short/long button edges, directional variants, and
deterministic repeatability. Providers skip unsupported airborne, wall, or resource-empty preconditions with an
explicit warning rather than fabricate a result.

Cancellation is checked between probes and during provider loops. A provider must report whether restoration
succeeded. Calibration is unavailable in Edit Mode for providers that require local PhysicsScene2D simulation.
The active scene dirty flag is checked before and after the operation.
