# Unity project

Open `unity/PlaytesterProject` in Unity Hub. This is the Unity 6.3.6f1 project
root; the repository root is not a Unity project.

The first editor launch generates `Library/`, resolves the packages in
`Packages/manifest.json`, and may update the lockfile. Keep generated builds at
`PlaytesterProject/Builds/<level_id>/` to honor the RL build-layout contract.

## Reproduce the complete local flow

1. In Unity, run **Playtester → Create Or Repair Playable Scenes**. This
   regenerates `GymScene`, `LevelA`, and `LevelB` and synchronizes the generated
   ScriptableObjects from the RL YAML source of truth.
2. Run **Playtester → Run Gym Smoke Test**.
3. Build all three production-shaped players:
   **Build Stage 1 Gym Player**, **Build Level A Smoke Player**, and
   **Build Level B Smoke Player**. They land at:

   - `Builds/gym/gym.<platform-extension>`
   - `Builds/level_a/level_a.<platform-extension>`
   - `Builds/level_b/level_b.<platform-extension>`

4. Follow `rl/README.md` to train the shared `gym` generalizer, fine-tune each
   level, run the cold-start baselines, and replay the exported models.
5. Follow `infra/README.md` to process the export markers and generate reports.

The scenes are 2D, use the Input System controller, expose the 203-value
observation contract, record schema-valid piece telemetry, and contain the
Level B width-5 planted issue. `SentisModelSmoke.BuildInferencePlayback` builds
a disposable player with a trainer-exported ONNX model assigned to
`BehaviorParameters` in `InferenceOnly`/Burst mode.

Laptop builds prove the scene, ML-Agents communicator, ONNX, and Sentis seams.
The Grace Linux ARM64 build target and visual playback must still be verified
on GB10 before claiming hardware completion.
