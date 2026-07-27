# Hackathon Demo

Native GB10 path:

1. Export or create a RYZ Task Bundle.
2. Run `scripts/verify_gb10_runtime.sh`.
3. Run `scripts/generate_dataset.sh`.
4. Run `scripts/setup_gb10.sh`.
5. Run `scripts/train_ryz1.sh`.
6. Run `scripts/evaluate_ryz1.sh`.
7. Optional ONNX export: `scripts/export_ryz1.sh`.
8. Orchestrated smoke path: `scripts/run_hackathon_demo.sh`.

## Multi-mechanic curriculum

The GB10-native curriculum path generates deterministic multi-stage task families with jump, dash, hazards,
elevation, checkpoints, and randomized movement parameters toggled in different combinations:

```bash
RYZ1_RUN_DIR=Library/RYZ1/runs/curriculum-v1 \
RYZ1_SEED=100 \
RYZ1_CURRICULUM_REPETITIONS=5 \
RYZ1_BEAM=16 \
RYZ1_DEPTH=40 \
scripts/generate_curriculum_dataset.sh

RYZ1_DATASET=Library/RYZ1/runs/curriculum-v1/dataset.json \
RYZ1_TRAIN_CONFIG=python/ryz1/configs/curriculum.json \
RYZ1_MODEL_DIR=Library/RYZ1/models/curriculum-v1 \
scripts/train_ryz1.sh
```

The aggregate `curriculum_manifest.json` records every task, enabled feature set, solver result, and transition
count. Each search transition carries the task mechanics vector. Python selects one non-contradictory teacher
action per expanded search state, prioritizing the verified completion path.

This is mechanics-conditioned teacher-policy training from SimCore search, not PPO reinforcement learning.
The recurrent trainer reconstructs causal parent chains, and the ONNX guide receives the same previous-action
and reward history during search.

## Verified Unity bridge

Run the full Rahul-Mac → GB10 → Rahul-Mac loop:

```bash
scripts/run_unity_bridge.sh
```

The bridge exports supported Unity geometry and controller physics, runs ONNX-guided native search, verifies the
macro replay in SimCore, then replays it through the real Unity physics arena. Neural ranking cannot remove the
deterministic baseline's best candidate from the beam, and neither neural scores nor SimCore completion substitute
for the final Unity replay.

Verified on 2026-07-26:

- 1,500 CUDA/BF16 optimizer steps on GB10, 771,466 parameters, and 5,427 sequence examples.
- 90.86% teacher-action agreement over 1,127 examples from six unseen physics-seed tasks.
- 6/6 held-out tasks solved and replay-verified with the baseline-preserving neural beam.
- Unity-exported DemoLevel solved by SimCore and replayed to the real Unity goal in 51 ticks without a death.
- A separate unseen static Unity fixture was exported on the Mac, transferred Mac-to-GB10, solved with the
  trained guide, and replayed back in Unity in 57 executed ticks without a death.

## Run an authored scene from the Unity GUI

The trained-model workflow on Rahul's Mac is:

1. Save the scene.
2. Add the scene to Build Settings if the window offers the button.
3. Ensure it has exactly one `CelesteBenchmarkPlayer` and a primary `BenchmarkGoal`.
4. Use static, axis-aligned `BoxCollider2D` platforms on `Ground` or `OneWay`, and rectangular hazards on
   `Hazard`.
5. Open `Tools > RYZ-1 Neural Playtest`.
6. Enter Play Mode from the window.
7. Click `Run Trained Model On Current Scene`.
8. After Unity verification passes, watch the replay in Game View or reveal the evidence folder.

The default connection points at the GB10 Tailscale address and
`Library/RYZ1/models/curriculum-sequence-v3/ryz1-sequence.onnx`. The window runs SSH and file transfer behind
the button; no terminal, Python, CUDA installation, or model copy is required on the Mac.

Moving and crumbling platforms, springs, rotated/non-box platforms, and non-static platform rigidbodies are
rejected during preflight. Dash-refill locations currently produce an explicit warning because SimCore does
not model them; final Unity replay remains authoritative. Run artifacts are written to
`Library/RYZ1/gui-runs/<run-id>/`.

Live training mode is the default. Pretrained mode is selected with:

```bash
RYZ1_DEMO_MODE=pretrained scripts/run_hackathon_demo.sh
```

Do not claim a prepared checkpoint was trained live. Do not claim Unity runs natively on GB10.
