# PRD — ML/RL Lead

Read `PRD.md` first for shared contracts, repo layout, modularity rules, and timeline. This file is your file-by-file build list. Every task is tagged `[laptop-ok]` or `[gb10-only]`.

Your collaboration boundary with Rahul: `PlaytestAgent : Agent` and the piece prefabs/scene live in `/unity` (C#), but you own their *design* (observation contents, reward triggers, episode lifecycle) — Rahul implements the Unity-side plumbing to your spec. Write the exact method signatures below and hand them to Rahul directly rather than describing behavior in prose.

---

## 1. Config files (build these first — hour 0)

### `rl/configs/piece_config.yaml` `[laptop-ok]`

```yaml
pieces:
  gap_jump:
    enabled: true
    width_range: [2.0, 5.0]      # TUNABLE — tiles. Derived from an assumed player horizontal jump
                                   # distance of ~6 tiles at default move speed 5 u/s + jump arc ~0.5s
                                   # apex; 5.0 leaves a safety margin below max clearable distance.
                                   # RE-DERIVE once Rahul's actual controller constants exist (hour 0-4).
  move_to_goal:
    enabled: true
    distance_range: [4.0, 10.0]   # TUNABLE — tiles, arbitrary flat traversal length
  elevation:
    enabled: false                # feature flag — flip true if hour 4-8 review has slack (spec §8.4)
    height_range: [1.0, 3.0]      # TUNABLE — tiles, must stay under max jump height

composition:
  pieces_per_episode: 3           # LOCKED — spec §8.2, do not change
  boundary_velocity_reset: true   # LOCKED — spec §8.3, do not change
```

### `rl/configs/reward_config.yaml` `[laptop-ok]`

```yaml
active_strategy: compositional     # switches to "single_gym_fallback" on Gate 1 failure — no code change
compositional:
  progress_reward_scale: 0.01      # TUNABLE — per-step, multiplied by forward progress delta toward
                                     # current piece's local goal (clamped >= 0, no reward for backing up)
  time_penalty: -0.001              # TUNABLE — per-step constant, discourages stalling
  piece_completion_bonus: 1.0       # TUNABLE — fires once per piece via OnTriggerEnter2D on piece-goal collider
  final_sequence_bonus: 5.0         # TUNABLE — fires once on completing the 3rd piece
  death_penalty: -1.0                # TUNABLE — fires on hazard/fall-off collider, calls EndEpisode()
episode:
  max_steps: 1000                   # TUNABLE — ML-Agents episode step cap before forced timeout
single_gym_fallback:
  progress_reward_scale: 0.01
  time_penalty: -0.001
  completion_bonus: 1.0
  death_penalty: -1.0
```

### `rl/configs/observation_config.yaml` `[laptop-ok]`

```yaml
grid_size: 7                       # TUNABLE — NxN tile window centered on player, odd so player is exact center
                                     # 7x7 chosen as a starting size: large enough to see an incoming gap/hazard
                                     # at typical approach speed, small enough to keep observation vector modest
tile_channels: [empty, solid, hazard, goal]   # one-hot per cell — LOCKED shape, not tunable without touching encoder
include_velocity: true
include_grounded_flag: true
```

### `rl/configs/training_config.yaml` `[gb10-only for real runs, laptop-ok for smoke test]`

Standard `mlagents-learn` YAML. Key fields to set explicitly (not left at ML-Agents defaults):

```yaml
behaviors:
  PlaytestAgent:
    trainer_type: ppo
    hyperparameters:
      batch_size: 2048        # TUNABLE — raise on GB10 for large-batch policy update claim (spec §6)
      buffer_size: 20480
      learning_rate: 3.0e-4
    network_settings:
      normalize: true
      hidden_units: 256
      num_layers: 2
    max_steps: 2000000        # TUNABLE — Stage 1; Stage 2 fine-tune uses a much smaller max_steps override
env_settings:
  num_envs: 1                 # [laptop-ok]=1-4, [gb10-only]=raise to real achieved parallel count, report honestly
```

---

## 2. `rl/src/playtester_rl/reward_strategies.py` `[laptop-ok]`

```python
class IRewardStrategy(Protocol):
    def piece_progress_reward(self, delta_progress: float) -> float: ...
    def step_time_penalty(self) -> float: ...
    def piece_completion_bonus(self) -> float: ...
    def final_sequence_bonus(self) -> float: ...
    def death_penalty(self) -> float: ...

class CompositionalRewardStrategy(IRewardStrategy):
    """Loads rl/configs/reward_config.yaml['compositional']."""

class SingleGymFallbackStrategy(IRewardStrategy):
    """Gate 1 fallback — single mechanic type randomized per episode, no piece composition.
    Loads rl/configs/reward_config.yaml['single_gym_fallback']."""
```

This is a **Python-side reference implementation and config schema** — since the actual reward calls (`AddReward()`, `EndEpisode()`) execute in C# inside `PlaytestAgent.OnActionReceived()`, mirror this exact interface as a C# `IRewardStrategy` in `/unity/.../Agent/RewardStrategies.cs`. **Locked sync mechanism (Rahul owns implementing this, no hour-0 discussion needed — it's specified here and in `prd-unity.md` §2 identically):** Rahul writes a one-off Unity Editor script, `SyncConfigFromYaml.cs`, that parses `rl/configs/reward_config.yaml` and `rl/configs/piece_config.yaml` (both plain YAML, use a small C# YAML parser package) and populates `RewardConfigAsset`/`PieceLibraryConfig` ScriptableObjects on demand (a menu item, run manually after editing YAML — no live file-watching needed for a hackathon). Retuning during Gate 1 means: edit the YAML, re-run the sync menu item, both language runtimes see the same numbers. This YAML file is the single source of truth; the ScriptableObject is a generated cache, never hand-edited.

## 3. `rl/src/playtester_rl/telemetry_writer.py` `[laptop-ok]`

Writes `contracts/telemetry.schema.json`-conformant JSON. This is invoked from the **Unity side** at end-of-episode/end-of-run (C# telemetry writer in `/unity/.../Telemetry/`) — the Python module here exists so `/infra`'s pipeline tests and your own Gate 1/2 analysis scripts can validate/generate fixture telemetry without needing a live Unity run. Define:

```python
def validate_telemetry(doc: dict) -> None:
    """Raises on schema violation. Both /rl analysis code and /infra tests call this."""

def compute_seen_in_stage1_range(piece_type: str, params: dict, piece_config: dict) -> bool:
    """Only HALF of spec §4.1's 'no prior teaching instance' heuristic — the
    Stage-1-training-range half (checks params against piece_config.yaml
    ranges), NOT the level-local 'appeared earlier in this level' half. Do
    not describe this function as implementing §4.1 on its own (PRD.md §3.1
    is explicit about this — the other half is `prd-infra.md` §3's
    compute_level_local_precedent, computed from telemetry's own piece_results
    ordering, not from this function). Called once per piece_result before
    writing telemetry."""
```

## 4. `rl/src/playtester_rl/gate_eval.py` `[gb10-only]`

```python
def gate1_check(tensorboard_logdir: str) -> GateResult:
    """Reward curve trending up over last N checkpoints, not collapsed/plateaued.
    N and 'trending up' threshold are TUNABLE constants at top of file, default:
    compare mean reward of last 10% of training steps vs previous 10% block, require +10% relative."""

def gate2_check(manifest_path: str) -> GateResult:
    """Reads contracts/checkpoint_manifest.schema.json; passes if
    stage2_metrics.steps_to_converge < coldstart_baseline_metrics.steps_to_converge,
    'converge' defined as reward crossing a TUNABLE threshold (default: 90% of final mean reward)."""
```

Both print a plain pass/fail plus the numbers to stdout — this is what gets read out loud at the gate checkpoints in the timeline, no dashboard needed for a 29-hour build.

---

## 5. Training scripts `[gb10-only for real runs]`

**Locked build-layout contract (removes any need to sync with Rahul mid-build):** every Unity level build is a standalone executable at `unity/PlaytesterProject/Builds/<level_id>/<level_id>.<platform-extension>`, one build per level (`gym`, `level_a`, `level_b`). Rahul owns producing these via Unity's standard build pipeline.

**Locked runtime topology:** Unity simulation runs on the Mac while ML-Agents/PyTorch training and primary policy inference run on the GB10. The orchestrator selects a unique trainer port (base `5004`), opens an SSH local-forward over the GB10's Tailscale DNS hostname, starts `mlagents-learn` on the GB10 **without** an `--env` argument, then launches the local Unity build with the forwarded port. No GB10 IP may be stored or accepted. Sentis on the Mac is the required ONNX compatibility check and emergency fallback, not the primary inference path.

**Locked CLI argument contract for all scripts below** (this is also what `TrainingControlPanel.cs` in `prd-unity.md` invokes via subprocess — same flags, no separate confirmation needed):

```
--level-id <str>          # e.g. "level_a" — determines --env path per the build-layout contract above
--checkpoint-in <path>    # optional, omitted for cold-start/Stage 1
--checkpoint-out <path>   # required, where the run's checkpoint gets written
--num-envs <int>          # from training_config.yaml, overridable
--output-manifest <path>  # where this run's metrics get appended to checkpoint_manifest.json
--execution-mode remote   # required for Mac-simulation/GB10-policy operation
--remote-config <path>    # defaults to rl/configs/remote_execution.yaml
--remote-port <int>       # optional explicit port; otherwise allocated uniquely
```

The remote execution config supplies the Tailscale hostname and SSH username (or they are overridden by `PLAYTESTER_GB10_HOST` and `PLAYTESTER_GB10_USER`), remote repository path, trainer executable, and port range. The preflight must prove a direct Tailscale route, SSH reachability, the expected remote files, and an exact remote Git commit before starting a run.

- `rl/scripts/train_stage1.sh` — starts the remote trainer against the Stage 1 configuration, forwards its unique trainer port to the Mac, launches the local `Builds/gym/gym.<ext>`, and writes `--checkpoint-out` + updates `--output-manifest`'s `stage1_checkpoint`/`stage1_metrics`.
- `rl/scripts/finetune_stage2.sh` — same but passes `--initialize-from=<checkpoint-in>` to the remote trainer and uses the local `Builds/<level-id>/<level-id>.<ext>` simulation; updates manifest's `stage2_checkpoint`/`stage2_metrics`.
- `rl/scripts/baseline_coldstart.sh` — identical to finetune_stage2 minus `--initialize-from`, for Gate 2 comparison. Must launch same night, same level, so the comparison is apples-to-apples. Updates manifest's `coldstart_baseline_metrics`.
- `rl/scripts/run_concurrent_demo.sh` — launches two independent remote trainer/local Unity pairs with distinct forwarded ports, one for Level A and one for Level B, captures wall-clock via `time` around both, and writes it into a results file for the pitch slide. **Do not inflate this number — report exactly what was measured (spec §6).**

The tracked `training_config.remote_smoke.yaml` is only for one-shot connectivity and lifecycle validation. Gate results must use the production configuration and real GB10 metrics.

---

## 6. Numeric defaults summary (all `# TUNABLE`, all in the YAML files above — this table is a reading aid, not a separate source of truth)

| Parameter | Default | Rationale |
|---|---|---|
| Gap width range | 2.0–5.0 tiles | assumed jump clears ~6 tiles; margin below max |
| Move-to-goal distance | 4.0–10.0 tiles | arbitrary flat traversal, no failure mode to bound against |
| Elevation height range | 1.0–3.0 tiles | must stay under max jump height (measure once controller exists) |
| Observation grid | 7×7 | visibility of incoming hazard/gap at approach speed |
| Progress reward scale | 0.01/step | small relative to completion bonus, avoids reward hacking via micro-oscillation |
| Time penalty | -0.001/step | discourages stalling without dominating progress signal |
| Piece completion bonus | +1.0 | one full order of magnitude above per-step rewards |
| Final sequence bonus | +5.0 | 5x piece bonus, rewards completing the full composition |
| Death penalty | -1.0 | matches piece bonus magnitude, meaningful but not run-ending in expectation |
| Episode max steps | 1000 | generous upper bound for a 3-piece sequence at assumed move speed |

**Action item for hour 0–4:** once Rahul's player controller exists, measure actual move speed and jump arc, then correct the gap-width and elevation-height ranges in `piece_config.yaml` before Stage 1 training starts in earnest (spec §8.7 makes this correctness a hard requirement for Gate 2 to mean anything).

---

## 7. Timeline mapping

- **Hours 0–4:** all config files above, `reward_strategies.py`, `telemetry_writer.py`, hand `IRewardStrategy`/`IObservationEncoder`/piece signatures to Rahul, kick off Stage 1 training (`[gb10-only]`, continues into sleep window). Lock `contracts/telemetry.schema.json` with Abhi.
- **Hours 4–8:** monitor Stage 1, run `gate_eval.gate1_check`. If failing, flip `reward_config.yaml.active_strategy` to `single_gym_fallback` and restart — this is the entire fallback action per the modularity design.
- **Hours 8–14 (sleep window):** `finetune_stage2.sh` + `baseline_coldstart.sh` launched unattended once a usable Stage 1 checkpoint exists.
- **Hours 14–18:** `gate_eval.gate2_check`, commit to whichever path worked.
- **Hours 18–23:** `run_concurrent_demo.sh`, capture real wall-clock number, feed into full pipeline integration test with Abhi's report pipeline.
- **Hours 23–27:** re-run planted-issue level (Level B) repeatedly, confirm `seen_in_stage1_range` heuristic and telemetry reliably reproduce the plantable finding.

## 8. Testing

- `rl/tests/test_reward_strategies.py` — unit tests per strategy: known input deltas produce expected reward values, config swap changes output without code change.
- `rl/tests/test_telemetry_writer.py` — `validate_telemetry` against both a valid and a deliberately malformed fixture; `compute_seen_in_stage1_range` against boundary cases (exactly at `width_range` edges).
- `rl/tests/test_gate_eval.py` — feed synthetic reward-curve fixtures (monotonic increase, plateau, collapse) into `gate1_check`, assert correct pass/fail.

No Unity-side unit tests owned here — Gate 1/2 validation *is* the integration test for the RL side.
