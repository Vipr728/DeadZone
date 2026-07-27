# PRD — Compositional RL Playtesting Agent for Unity 2D Platformers

**Status:** Approved design, ready for implementation
**Source spec:** `compositional-playtester-spec.md` (read that first for product rationale; this PRD is the buildable decomposition of it)
**Team:** ML/RL lead, Unity/C# lead (Rahul), Infra/Backend lead (Abhi)
**Window:** 29 hours, GB10 (NVIDIA DGX Spark, Grace Blackwell, 128GB unified memory) as primary target, laptop-CPU as dev/iteration fallback

This master doc holds everything shared across workstreams: architecture, repo layout, data contracts, the modularity requirement, hardware path, timeline, gates, and locked scope. Each lead's file (`prd-ml.md`, `prd-unity.md`, `prd-infra.md`) is self-contained and file-by-file actionable — start there for "what do I build right now." Come back here when a task references a shared contract or a cross-cutting decision.

### Async-independence guarantee

The three leads will run at least one stretch (the hours 8–14 sleep window, per §8 below) with no real-time coordination available. Every cross-workstream integration point in this PRD is therefore resolved as a **fixed, written-down contract** — a file path, a schema, a CLI flag name, a directory convention — never as "confirm with [teammate] at hour X." If you ever find yourself about to message a teammate to ask "what should this path/flag/field be called," that's a sign the contract should already be in this PRD; check `PRD.md` §2–3 and the relevant per-lead doc first. The concrete locked contracts, so you don't have to hunt for them:

- **Level build layout:** `unity/PlaytesterProject/Builds/<level_id>/<level_id>.exe` — Unity produces builds here, RL scripts consume from here (`prd-ml.md` §5).
- **Training/fine-tune CLI contract:** `--level-id --checkpoint-in --checkpoint-out --num-envs --output-manifest` — fixed flag names, used identically by RL scripts and the Editor tool's subprocess calls (`prd-ml.md` §5, `prd-unity.md` §4).
- **Config sync mechanism:** `SyncConfigFromYaml.cs` (Unity editor script) turns `rl/configs/*.yaml` into `RewardConfigAsset`/`PieceLibraryConfig` ScriptableObjects — YAML is the single source of truth, the asset is a generated cache (`prd-ml.md` §2, `prd-unity.md` §2).
- **Directory contract:** `infra/config.yaml`'s `paths.*` fields are the one place every directory lives — `watched_levels_dir` (export markers, watched by OpenClaw), `telemetry_dir` (playtest run output), `reports_dir` (LLM report output) — all three workstreams read this file rather than hardcoding paths locally, and **the config is authoritative**: no field here is subject to confirmation with a teammate, on either side (`PRD.md` §3, `prd-infra.md` §1, `prd-unity.md` §6).
- **Export-to-build ordering contract:** `watched_levels_dir` (`Exports/`) and the locked build layout (`Builds/<level_id>/<level_id>.<ext>`) are two different directories serving two different jobs — Exports/ is a trigger signal, Builds/ is the actual executable. The ordering is fixed: Unity's Editor-tool export step (`prd-unity.md` §4) builds to `Builds/<level_id>/` FIRST, and only writes the `Exports/<level_id>/level_export.json` marker file AFTER that build succeeds. OpenClaw's `LevelWatcher` (`prd-infra.md` §4) therefore never needs to check whether a build exists — by construction, if a marker is present, its build is too. See `PRD.md` §3.4 for the marker file's shape.
- **Player kinematics handoff:** ML/RL needs Rahul's actual move-speed/jump-arc numbers to set gap-width/height ranges correctly. The handoff mechanism is a config-file commit (Rahul writes the measured numbers directly into `rl/configs/piece_config.yaml` and commits), not a conversation (`prd-unity.md` §8).
- **Agent↔player control surface:** `PlayerInputAdapter.SetMove(float)` / `SetJump(bool)` — the only two method names ML/RL's agent code needs to know; everything else about how the player controller works is Rahul's to decide unilaterally (`prd-unity.md` §1).

If a genuine new ambiguity surfaces mid-build that isn't covered above, resolve it by adding to whichever config/schema file already governs that seam (per §1's modularity rules) and committing — the commit itself is the async "message" to the other two leads once connectivity returns.

---

## 1. Cross-Cutting Design Principle: Modularity & Configurability

This is a hackathon build under time pressure, which is exactly when unconfigurable code costs the most — every gate failure (§5) or scope pivot (§6.8, elevation piece cut, reward retuning) must be a **config change or a swapped implementation, not a rewrite.** This applies to all three workstreams and is a review criterion for every PR-sized chunk of work, not a nice-to-have.

Concrete rules, binding for all three leads:

1. **No magic numbers in code.** Every tunable value (reward magnitudes, gap width/height ranges, observation grid size, episode step limits, LLM model name, sandbox paths, egress rules) lives in a config file (YAML for Python, ScriptableObject or JSON for Unity), never hardcoded in a class body. Config files are checked into git so a change is a diffable commit, not tribal knowledge.
2. **Interfaces over concrete coupling** at every point where the design explicitly says "this might change" or "this is an assumption to validate":
   - `IRewardStrategy` (Python, `/rl`) — the dense per-piece reward formula in §2.3 of the spec is one implementation (`CompositionalRewardStrategy`); if Gate 1 shows non-convergence and the team falls back to the single-randomized-gym design (spec §7 Gate 1 fallback), that's a new class implementing the same interface, not a rewrite of the training loop.
   - `IObservationEncoder` (Python + mirrored C# `IObservationEncoder`) — the local tile-grid + kinematics encoding is one implementation; grid size `N` or added/removed fields are a new encoder, not edits scattered across the codebase.
   - `IPieceType` (C#, piece prefab contract) — gap-jump, move-to-goal, elevation each implement this; adding/cutting a piece type (spec §8.4) is adding/removing one class + one config entry, never touching `PieceComposer`.
   - `ILLMClient` (Python, `/infra`) — report generation calls this interface; `OllamaClient` is the default concrete implementation, swappable for a NIM/TensorRT-LLM/real-NemoClaw-routed client without touching prompt construction or report-schema code.
   - `IEgressPolicy` / sandbox config (`/infra`) — OpenShell's real interface is TBD (per your answer, sponsor tool details unclear); the PRD specs this behind a policy-config abstraction (allowed paths, blocked outbound) so swapping in the real OpenShell later is a config/adapter swap, not a redesign.
3. **Every numeric default in this PRD is marked `# TUNABLE`** with the reasoning behind the starting value. Changing it during Gate 1/Gate 2 tuning must never require touching more than the one config file.
4. **Config format convention:** Python side uses YAML + a thin dataclass loader (no heavyweight framework — a hackathon does not need Hydra). Unity side uses `ScriptableObject` assets for anything an Editor-tool user might want to tweak via Inspector, plain JSON for anything only read by scripts/CLI.
5. **Feature flags, not commented-out code.** The elevation piece (§8.4, "if time allows") is implemented behind a config flag (`enable_elevation_piece: true/false` mirrored in a Unity `PieceLibrary` asset), not physically added/removed from the codebase under time pressure.

Each per-lead PRD calls out exactly which files are the config/interface boundary for that rule.

---

## 2. Repo Layout

```
GB10-project/
├── PRD.md                              (this file)
├── prd-ml.md                           (ML/RL lead)
├── prd-unity.md                        (Unity/C# lead)
├── prd-infra.md                        (Infra/Backend lead)
├── compositional-playtester-spec.md    (existing source spec, unchanged)
├── contracts/
│   ├── telemetry.schema.json           (§3.1 below — shared contract, LOCK BY HOUR 4)
│   ├── checkpoint_manifest.schema.json (§3.2 below)
│   └── report.schema.json              (LLM structured-output contract, §3.3 below)
├── unity/
│   └── PlaytesterProject/              (Unity 6.3.6f1 project root)
│       ├── Assets/
│       │   ├── Scripts/
│       │   │   ├── Player/             (controller, Input System wiring)
│       │   │   ├── Gym/                (piece prefabs, PieceComposer, markers, hazard tile)
│       │   │   ├── Agent/              (PlaytestAgent : Agent, observation/reward glue)
│       │   │   ├── EditorTool/         (EditorWindow, one file per panel, incl. the export step — §3.4)
│       │   │   ├── Inference/          (Sentis model loading + playback)
│       │   │   └── Telemetry/          (C# telemetry writer, matches contracts/telemetry.schema.json)
│       │   ├── Configs/                (ScriptableObject assets: PieceLibraryConfig, RewardConfigAsset — mirrors /rl/configs)
│       │   ├── Tilemaps/               (demo levels: LevelA, LevelB)
│       │   └── Scenes/                 (GymScene, LevelA, LevelB)
│       ├── Builds/                     (per-level headless executables — §3.4, written BEFORE the matching Exports/ marker)
│       │   └── <level_id>/<level_id>.<ext>
│       └── Exports/                    (OpenClaw's trigger signal — §3.4, one level_export.json per level, written AFTER its build)
│           └── <level_id>/level_export.json
├── rl/
│   ├── pyproject.toml                  (uv-managed)
│   ├── configs/
│   │   ├── piece_config.yaml
│   │   ├── reward_config.yaml
│   │   ├── observation_config.yaml
│   │   └── training_config.yaml        (mlagents-learn hyperparameters)
│   ├── src/playtester_rl/
│   │   ├── reward_strategies.py        (IRewardStrategy + CompositionalRewardStrategy, SingleGymFallbackStrategy)
│   │   ├── telemetry_writer.py         (writes contracts/telemetry.schema.json-conformant JSON)
│   │   ├── gate_eval.py                (Gate 1 / Gate 2 pass/fail scripts)
│   │   └── config_loader.py
│   ├── scripts/
│   │   ├── train_stage1.sh
│   │   ├── finetune_stage2.sh
│   │   ├── baseline_coldstart.sh
│   │   └── run_concurrent_demo.sh
│   └── tests/
└── infra/
    ├── pyproject.toml                  (uv-managed)
    ├── config.yaml                     (paths, model name, sandbox policy — single source of truth)
    ├── src/playtester_infra/
    │   ├── llm_client.py                (ILLMClient + OllamaClient)
    │   ├── report_pipeline.py           (telemetry JSON -> prompt -> ILLMClient -> report.schema.json)
    │   ├── prompts/                     (versioned prompt templates, not inlined strings)
    │   ├── openclaw_skill.py            (directory watcher -> pipeline trigger)
    │   ├── openshell_policy.py          (fs scope + egress-block abstraction)
    │   └── nemoclaw_setup.sh            (onboarding script skeleton)
    └── tests/
```

---

## 3. Shared Data Contracts

These are the load-bearing files. Both `/rl` and `/infra` import/validate against `contracts/*.schema.json` — nobody hand-rolls a parallel understanding of the shape. **Lock these by hour 4, per spec §10/§11.**

### 3.1 `contracts/telemetry.schema.json`

One playtest run produces one telemetry JSON document. JSON Schema (draft 2020-12), fields:

```json
{
  "run_id": "string (uuid)",
  "level_id": "string",
  "stage": "stage1 | stage2",
  "checkpoint_path": "string",
  "timestamp_start": "string (ISO8601)",
  "episode_summaries": [
    {
      "episode_index": "integer",
      "outcome": "success | death | timeout",
      "total_reward": "number",
      "time_to_clear_seconds": "number | null",
      "path_trace": [{"t": "number", "x": "number", "y": "number"}],
      "piece_results": [
        {
          "piece_id": "string",
          "piece_type": "gap_jump | move_to_goal | elevation",
          "params": {"width": "number | null", "height": "number | null"},
          "attempts": "integer",
          "time_to_clear_seconds": "number | null",
          "death_position": {"x": "number", "y": "number"} | null,
          "seen_in_stage1_range": "boolean"
        }
      ]
    }
  ]
}
```

**`seen_in_stage1_range` is only HALF of spec §4.1's teachability heuristic — do not conflate the two.** Spec §4.1 defines "no prior teaching instance" as: *"has this piece-type/parameter-range appeared earlier in the level **or** in Stage 1 training range."* These are two genuinely different signals, computed by two different owners, from two different sources:

1. **`seen_in_stage1_range`** (this field, RL-owned) — was this piece's parameter value ever inside the range Stage 1 trained on at all? The RL side stamps this by checking against `piece_config.yaml`'s ranges at telemetry-write time. This says nothing about the specific level being played — a piece can be well inside Stage 1's trained range and still be the *first* time that difficulty appears in this particular level's own layout.
2. **Level-local precedent** (infra-owned, NOT a telemetry field) — has an equivalent-difficulty piece of the same `piece_type` appeared **earlier in this same level's own `piece_results` sequence**? `piece_results` within one `episode_summary` is already ordered by traversal (both the fake-env test harness and the real Unity `TelemetryRecorder` append in play order), so this is computable directly from telemetry's existing ordering — it does not need a new schema field. `prd-infra.md` §3 owns computing this (function: `compute_level_local_precedent`) as a pre-processing step before prompt construction, so the LLM receives both signals, clearly labeled and distinguished, rather than being handed `seen_in_stage1_range` alone and asked to infer the level-local half itself.

### 3.2 `contracts/checkpoint_manifest.schema.json`

```json
{
  "level_id": "string",
  "stage1_checkpoint": "string (path)",
  "stage2_checkpoint": "string (path) | null",
  "onnx_export_path": "string (path) | null",
  "stage1_metrics": {"final_mean_reward": "number", "training_steps": "integer"},
  "stage2_metrics": {"final_mean_reward": "number", "training_steps": "integer", "steps_to_converge": "integer | null"},
  "coldstart_baseline_metrics": {"final_mean_reward": "number", "training_steps": "integer", "steps_to_converge": "integer | null"} 
}
```

This is what Gate 2's speedup comparison reads from directly (`stage2_metrics.steps_to_converge` vs `coldstart_baseline_metrics.steps_to_converge`), and what the Unity Editor tool reads to know which ONNX file to load for Sentis playback.

### 3.3 `contracts/report.schema.json`

The LLM's structured output target (enforced via the model's JSON-mode/function-calling, not regex-parsed free text):

```json
{
  "level_id": "string",
  "overall_difficulty": "string (enum: too_easy | appropriate | too_hard)",
  "difficulty_rationale": "string",
  "problem_points": [
    {"location": {"piece_id": "string", "x": "number", "y": "number"}, "issue": "string", "severity": "low | medium | high", "evidence": "string (references attempts/deaths from telemetry)"}
  ],
  "teachability_assessment": "string",
  "planted_issue_detected": {"detected": "boolean", "description": "string | null"}
}
```

### 3.4 `Exports/<level_id>/level_export.json` — the export marker, not a `contracts/` schema

Unlike §3.1–3.3, this file isn't shared between `/rl` and `/infra` — it's written once, by Unity's Editor-tool export step (`prd-unity.md` §4), and read once, by OpenClaw's `LevelWatcher` (`prd-infra.md` §4). It's documented here because it's the resolution to the Exports/-vs-Builds/ transition, not because it needs the same cross-package schema-validation machinery as the three contracts above.

```json
{
  "level_id": "string",
  "build_path": "string (path, e.g. unity/PlaytesterProject/Builds/level_a/level_a.exe)",
  "scene_path": "string (path to the .unity scene this build came from)",
  "exported_at": "string (ISO8601)"
}
```

**The ordering guarantee, stated once, precisely:** Unity's export step writes to `build_path` first (a real `BuildPipeline.BuildPlayer` call) and only writes this marker file after that build succeeds. `LevelWatcher` therefore never needs to check whether the build exists, retry, or poll — a marker's mere presence in `watched_levels_dir` is the guarantee. This is the entire fix for what was previously an underspecified transition between two directories serving two different jobs (Exports/ = trigger signal, Builds/ = the actual executable).

---

## 4. Hardware / Dev Path

Every task in the three per-lead PRDs is tagged `[laptop-ok]` or `[gb10-only]`.

- **`[laptop-ok]`**: runs on any CPU-only dev machine. RL: `--num-envs 1-4`, short training runs to validate code paths (not convergence). Infra: Ollama serving a small model (`llama3.1:8b` default in `infra/config.yaml`, `# TUNABLE`) for pipeline-logic testing against fixture telemetry. Unity: everything except large-batch training runs.
- **`[gb10-only]`**: full parallel env count for real Stage 1/Stage 2 training runs and Gate 1/Gate 2 validation, the real 70B-class model for the actual demo report quality, the concurrent-multi-level parallelism demo number.
- The switch between the two paths is `rl/configs/training_config.yaml`'s `num_envs` field and `infra/config.yaml`'s `llm.model` field — never a code change.

---

## 5. Validation Gates (reproduced from spec §7, binding)

1. **Gate 1 (~hour 4):** Stage 1 reward curve trending up. Fail → switch `reward_config.yaml`'s active strategy to `SingleGymFallbackStrategy` (§1 rule 2) and continue with the simpler single-randomized-gym design. This is a config swap per the modularity rule, not a rewrite.
2. **Gate 2 (~hour 14):** Stage 2 fine-tune must converge measurably faster than cold-start baseline on the same level, both runs launched from `rl/scripts/finetune_stage2.sh` and `rl/scripts/baseline_coldstart.sh`, compared via `checkpoint_manifest.schema.json`'s metrics fields.

Both gates are hard stops: on failure, commit to the fallback immediately (per spec §7).

---

## 6. Locked Scope (reproduced from spec §8, do not revisit mid-build)

1. Action space: `{left, right, jump}` only.
2. Piece composition length: fixed at 3 pieces/episode.
3. Piece-boundary velocity resets to zero.
4. Piece types: gap-jump, move-to-goal, elevation-if-time — elevation gated by `enable_elevation_piece` flag (§1 rule 5), no further piece types.
5. No cross-game/cross-project generalization claim.
6. No per-game auto-generated gym scenes — gym is built once for this project.
7. Real demo levels must use parameters inside Stage 1's ranges (`rl/configs/piece_config.yaml`).
8. Compatibility scope: player controllers on Unity's standard Input System with move/jump interface.

---

## 7. Demo Requirements (locked per your decision: 2 levels, minimum viable)

- **Level A** — clean, well-designed, difficulty gradient inside Stage 1 ranges. Used for the baseline "here's a normal report" demo beat.
- **Level B** — does double duty: contains the one planted issue (design specified in `prd-unity.md` §Demo Levels) and is the fixture for both the difficulty-report demo and the exploit-finding demo.
- Pre-trained checkpoints for both levels are the guaranteed fallback (`checkpoint_manifest.json` entries for both, committed/backed up before hour 23). Live demo shows fast fine-tune from a known-good checkpoint, never from-scratch.
- Concurrent-parallelism demo: train/process Level A and Level B simultaneously, capture real wall-clock number (`rl/scripts/run_concurrent_demo.sh`).
- OpenShell no-egress proof: attempt an outbound call during the demo, show it blocked (`infra/src/playtester_infra/openshell_policy.py` exposes a CLI hook for this, spec'd in `prd-infra.md`).

---

## 8. Timeline

Reproduced from spec §11 verbatim as the shared schedule; each per-lead PRD maps its file-by-file task list onto these blocks explicitly so "work on the prd" resolves to "next unchecked task in the current hour block."

| Block | Focus |
|---|---|
| Hours 0–4 | Foundational + Gate 1. Telemetry schema locked by end of block. |
| Hours 4–8 | Building + data prep. Gate 1 checked. |
| Hours 8–14 | Sleep window — Stage 1 completes, Stage 2 + cold-start baseline kicked off unattended, overnight scaffolding work. |
| Hours 14–18 | Gate 2 checked, commit to RL design path, Sentis wiring verified. |
| Hours 18–23 | Concurrent-parallelism demo run, full pipeline integration. |
| Hours 23–27 | Hardening, repeat planted-issue demo for reliability, OpenShell proof confirmed live. |
| Hours 27–29 | Rehearsal only, no new code. |

---

## 9. Open Items Resolved by This PRD

The spec's §12 open questions are resolved as follows — see per-lead docs for the concrete values:

1. Player controller Input System compatibility — **resolved: building from scratch** (your answer), so `prd-unity.md` specs the controller directly against Input System + `linearVelocity` from the start; no adaptation-audit task needed.
2. Reward magnitudes — locked with starting defaults in `prd-ml.md`, marked `# TUNABLE`.
3. Piece parameter ranges — locked with starting defaults in `prd-ml.md`, derived from an assumed default player jump arc (documented there), to be corrected once the actual controller is built and measured (still hour 0–4 work, now "measure the thing we just built" not "audit an existing thing").
4. Elevation piece — implemented behind `enable_elevation_piece` flag, decision point still at hour 4-8 per timeline, but code exists either way.
5. Telemetry schema — locked in `contracts/telemetry.schema.json` (§3.1 above), this PRD's job, not deferred.
