# RYZ-1 Migration Plan

## Phase 1: Audit

Completed. The active repository is a Unity-native search MVP with scanner, manifest, calibration, solver,
counterfactuals, synthetic profiles, reports, and no compiled neural training pipeline.

## Phase 2: Stabilize Contracts

Implemented first native DTOs in `src/Ryz1.Contracts`:

- Mechanics Manifest DTO
- RYZ Task Bundle
- Task spec
- Action and observation schemas
- Dataset and replay records

## Phase 3: Data Generation

Implemented SimCore search dataset export in `Ryz1.Runner`. Unity now exports the deterministic hackathon subset
through `NativeSimCoreBridge`; broad parity for moving/crumbling platforms remains follow-up.

Implemented deterministic aggregate curriculum generation across flat-run, jump-gap, hazard-hop, elevation,
dash-gap, and mixed-course archetypes. Curriculum repetitions vary movement physics and carry feature flags
and mechanics vectors into every transition.

## Phase 4: Neural Model

Implemented Python smoke model, losses, training, evaluation, and ONNX export entry point.

Implemented one teacher target per expanded search state and mechanics-conditioned training input. Held-out
curriculum seeds can be generated separately to measure teacher-action agreement outside the training seeds.

Implemented parent-linked recurrent windows. Training, evaluation, ONNX inference, and SimCore guidance now use
the same previous-action/reward history instead of zero-filled one-step inputs. Square-root class balancing
reduces the curriculum's move-right label dominance.

## Phase 5: GB10 Scripts

Implemented setup, verification, publish, generation, training, evaluation, export, and demo scripts.

## Phase 6: Search Integration

Implemented `INeuralGuide` scoring and ONNX Runtime inference in SimCore. Neural ranking is advisory; SimCore and
Unity replays remain authoritative.

## Phase 7: Unity Neural Playtest UI

Implemented `Tools > RYZ-1 Neural Playtest` for a saved current scene on Rahul's Mac. The window:

- checks the static native-parity subset before upload;
- exports the isolated authored scene rather than a hardcoded demo;
- invokes the GB10 runner through passwordless Tailscale SSH;
- downloads the task bundle, replay, result, and report;
- rejects candidates that fail authoritative Unity replay; and
- opens the verified action stream in Game View with playback diagnostics.

The window stores run evidence under `Library/RYZ1/gui-runs/`.

## Risks

- Full Unity-to-SimCore parity outside the static hackathon subset is incomplete.
- Neural guidance must be evaluated by solve rate, not teacher-action accuracy alone.
- Existing Unity worktree is dirty; avoid broad refactors.
