# Ryzi Commercial Plugin Vertical Slice

Status: implemented and validated in an isolated project copy, 2026-07-25.

## Audited Baseline

Unity 6000.3.6f1. The simulator lives in `Assets/Scripts/CelesteBenchmark/`; the prototype tool and solver live in
`Assets/PlatformerPlaytest/`. Existing dirty work is preserved. The current runtime adapter already provides
isolated manual 2D physics, explicit reset, observations, death events, goals, telemetry, replay verification,
segmented search, and tunable restoration.

## Implementation

1. Add dependency-free runtime contracts and Editor-only scanner/UI in `Packages/com.ryzi.unity`.
2. Add a project integration provider in `Assets/Ryzi.Integrations` that strongly types the existing solver
   while the package discovers it through an Editor-only interface/reflection boundary.
3. Scan the current scene and build manifest `1.0` with evidence.
4. Run isolated calibration probes in Play Mode and reconcile runtime evidence.
5. launch the preserved segmented solver, record a commercial run summary/replay outside Assets, and visualize
   path/failure positions.
6. Run three matched-seed jump-value variants and prove restoration in `finally`.
7. Add EditMode contract/discovery/path/serialization/action/restoration tests and PlayMode provider lifecycle,
   calibration, solver/replay, death/completion, and cancellation tests where runtime cost is bounded.

## Gates

- No package Runtime reference to benchmark, Input System, AssetDatabase, or Editor assemblies.
- Window open and scan leave the scene dirty state unchanged.
- No generated result under Assets.
- Existing solver files are not refactored for this slice.
- Exact Unity test output is recorded; unavailable or skipped tests are reported as such.

## Risks

The existing isolated arena path is Play-Mode-only. Full SampleScene segmented solving may take roughly two
minutes on the audited machine and uses an existing cache. Cross-process Physics2D replay is tolerance-based;
same-process replay remains exact in existing tests. Generic source analysis is intentionally bounded and does
not claim Roslyn semantics.

## Validation

- Edit Mode: 86/86 passed in 0.1140719 seconds.
- Ryzi Play Mode: 2/2 passed in 0.4446732 seconds.
- Full Play Mode: 37 passed, 2 failed, 2 skipped in 982.1846365 seconds.
- `SampleScene_SegmentedSolverCompletes`, `SampleScene_ReplayZeroDesync`, the three-variant counterfactual,
  death detection, and completion detection passed.
- Both full-suite failures are existing profile expectation failures in `ProfileRealLevelTests`: Expert completed
  1/5 seeds and recovery ended with `StepBudgetExceeded`.

The open source project Editor was launched with `-noUpm`, so validation used `/tmp/ryzi-unity-nkilKn`.
Restarting the source Editor is required before its Package Manager imports the new embedded package.

## Current Limitations

- Tier 1 automatic behavior is validated only for the included CelesteBenchmark simulator.
- Source inference is bounded member/source inspection, not Roslyn semantic analysis.
- Calibration requires Play Mode because the existing backend uses local physics scenes.
- The Ryzi replay UI previews recorded paths and keyframes; existing solver replay performs state verification.
- Allocation profiling and optional Input System absence were not separately measured.
