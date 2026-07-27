# Spec: Replay

A replay = scenario ref + seed + agent/profile ids + tunable overrides + action stream + keyframes + build/settings hashes (all already in telemetry files).

## Mechanism

1. Load arena, `Bind`, `ResetEpisode(seed)` — hash-check build/settings; warn on mismatch before running.
2. `ReplayAgent` feeds recorded action for tick t (missing tick = neutral action).
3. Each keyframe tick: compare live (quantized pos/vel/flags/stateHash) vs recorded. First mismatch → DesyncReport {tick, field deltas, recorded vs actual}; keep running but mark replay desynced.
4. Controls (editor): play, pause, frame-step, jump-to-failure (run headless to N ticks before first death event, then visualize), input-state display, path overlay (recorded trajectory polyline + live position).

## Determinism scope

**Same process** is the exactness boundary. Within one Unity process, same build, same fixedDeltaTime, same
tunables: replay is bit-exact and `ReplayVerifier` compares with zero tolerance (exact on the 1e-4 quantization
grid, including the state hash). This is the normal case — an episode is always simulated start-to-finish in one
process, so "every finding links to a replay that reproduces it" holds exactly.

**Across processes** (and therefore across machines) Unity Physics2D is not bit-exact: it resolves the same
contact to a slightly different rest separation, measured at up to 0.0125 units over 400 ticks and ~0.17 over 754
on SampleScene. See architecture/limitations.md #14 for the evidence and what was ruled out. To verify a recording
made by a *different* process, construct `new ReplayVerifier(keyframes, ReplayVerifier.CrossProcessTolerance)`
(0.25 units); flags and dash count are still compared exactly, so a genuinely different outcome still desyncs. Do
not store byte-exact golden traces — they will not reproduce in another process.

Hash mismatch downgrades replay to "advisory" with visible warning. Desync is reported, never silently ignored.

## Tests

- Record scripted episode → replay → zero desync.
- Tamper one action → desync detected at/after tampered tick.
- Keyframe comparison tolerance: exact on quantized values (quantization 1e-4) by default; the tolerant
  cross-process mode absorbs the measured 0.0125-unit drift but still desyncs on a whole-tile divergence or a
  flags/dash mismatch (`TelemetryEditModeTests.ReplayVerifier_*`).
- Cross-process exactness is NOT covered by the suite (one process is deterministic by construction). Run
  `tools/cross-process-determinism.sh [runs]`, which replays a fixed stream in N Unity processes and diffs the
  traces. It currently fails by design (see limitations.md #14) — it exists to detect change, not as a CI gate.
