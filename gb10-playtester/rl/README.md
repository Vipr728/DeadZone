# rl/ — ML/RL workstream

Implements `prd-ml.md`. Managed with `uv`.

## Install

```bash
cd rl
uv sync
```

This installs everything needed for config loading, reward strategies, telemetry, gate
evaluation, and the fake-environment pipeline tests — **no Unity or ML-Agents required**.
Every module in this package that doesn't literally call `mlagents-learn` can be developed,
tested, and CI-checked on any machine.

## Two install profiles (important — read before installing mlagents)

`mlagents` (the real Unity ML-Agents Python trainer) pins `numpy>=1.23.5,<1.24.0`, which
conflicts with the modern numpy this package and its tests use. This is a real,
well-known constraint of the `mlagents` package, not a bug here — Unity ML-Agents users
routinely keep it in its own separate environment for exactly this reason.

**Do not try to `uv sync` mlagents into this same venv.** Instead, on the machine that
will actually run training against real Unity builds (GB10 or a dev laptop with the
Unity build available), create a second, separate environment:

```bash
cd rl
uv venv .venv-mlagents --python 3.10.12   # exact patch version — see note below, do NOT use "--python 3.10"
source .venv-mlagents/bin/activate         # or .venv-mlagents\Scripts\activate on Windows
uv pip install mlagents==1.1.0
```

**Why the exact `3.10.12` pin matters:** `mlagents==1.1.0`'s actual PyPI metadata requires
`Python >=3.10.1,<=3.10.12` — a narrow window, not "any 3.10.x." `uv venv --python 3.10`
resolves to whatever the newest installed/downloadable 3.10 build is (verified on this dev
machine: it resolved to 3.10.20, which is outside that range and would make
`uv pip install mlagents==1.1.0` fail to resolve). Pin the exact patch version for the
`.venv-mlagents` environment specifically — this has nothing to do with the main `rl/`
venv (`requires-python = ">=3.10"`, no upper bound), which is unaffected.

The scripts in `scripts/` run the project CLI from the main `uv` environment.
That CLI resolves the real trainer from `PLAYTESTER_MLAGENTS_LEARN`, the
repository-local `.venv-mlagents`, or `PATH`, in that order. This keeps the
incompatible dependency sets isolated while making infra-launched jobs discover
the same trainer as direct terminal runs.

`mlagents==1.1.0` requires `torch>=2.1.1` with no upper bound, so `uv pip install` should
resolve to whatever recent torch build is available at install time — on GB10 specifically,
confirm at hour 0 that the resolved torch build actually has prebuilt wheels/CUDA support
for Blackwell (a very new GPU architecture) and Grace's ARM64 CPU; if not, an NVIDIA-provided
PyTorch build (NGC container or NVIDIA's own wheel index) may be needed instead of the
default PyPI torch wheel.

## Does this package itself need any GB10-specific changes? No — verified, not assumed.

Every module in `rl/src/playtester_rl/` (config loading, reward strategies, the fake env,
the fake trainer, the real gradient-based learnability check, telemetry, gate evaluation,
checkpoint manifest, the CLI) is pure Python + numpy/pyyaml/jsonschema/gymnasium/filelock —
no CUDA, no torch, no OS-specific paths (everything goes through `pathlib.Path`), no
Windows-only calls. All five dependencies have Linux ARM64 (aarch64) wheels on PyPI. This
was checked, not assumed: a fresh `git clone` into an unrelated directory + `uv sync --extra
dev` + the full test suite + an actual shell-script run all passed with zero modifications
(same commands anyone would run on GB10). `scripts/*.sh` are plain POSIX bash
(`set -euo pipefail`, no Windows-specific syntax) — they were only exercised via Git Bash on
this Windows dev machine, but nothing in them is Windows-specific; native Linux execution
on GB10 should behave identically or more reliably.

**What is NOT verified, because it can't be from this machine — a real, non-obvious risk
worth flagging to the whole team, not just the RL side:** GB10's Grace CPU is **ARM64**.
Unity's default "Linux Standalone" export target produces an **x86_64** binary, which will
not run natively on GB10's CPU. Unity does support ARM64 Linux, but only via the
"**Embedded Linux**" build target with **Arm64** architecture + the **IL2CPP** scripting
backend + a specific toolchain/sysroot package (`com.unity.sdk.linux-arm64`) — not the
default Linux export a build step might reflexively use. If Rahul's build pipeline
(`prd-ml.md` §5's locked build-layout: `unity/PlaytesterProject/Builds/<level_id>/<level_id>.<ext>`)
produces a standard x86_64 Linux Standalone build instead, that binary needs to be verified
against GB10 before assuming `cli.py`'s real-mlagents path (`find_unity_build` /
`run_real_training`) will actually work there — confirm this at hour 0, not hour 18.
`cli.py`'s `_BUILD_EXTENSIONS` tuple (`.exe`, `.x86_64`, `.app`) may also need one line added
once the real Embedded-Linux-ARM64 build's actual output filename convention is known (not
yet confirmed since no build exists this session).

## Fake environment for pipeline testing

Real Stage 1/2 training requires a Unity build at `unity/PlaytesterProject/Builds/
<level_id>/<level_id>.<platform-extension>`. The fake path remains available
for fast structural tests through `--execution-mode fake`.
`src/playtester_rl/fake_env.py` is a pure-Python Gymnasium-style environment that
reproduces the piece-composition task's structure (3 sampled pieces, dense per-piece
reward, episode lifecycle, tile-grid-shaped observation) without needing Unity at all.
This is a **test harness**, not a training environment — it validates that the reward
strategy, telemetry writer, config loading, and gate evaluation are wired together
correctly (no leaky/missing fields, no reward collapse, schema-valid output at every
stage) so that swapping in the real Unity env is a drop-in replacement, not a rewrite.

## Is this actually learnable? (mvp_policy_gradient.py)

`fake_trainer.py`'s epsilon-schedule "improves" by construction — it proves
the CLI/telemetry/manifest plumbing is wired, but says nothing about whether
a real gradient-based agent can learn anything from these exact
observation/action/reward contracts. `src/playtester_rl/mvp_policy_gradient.py`
is a from-scratch linear-softmax policy trained with real backprop (vanilla
REINFORCE, random weight init, no scripted behavior) against the same
`fake_env.py` contracts the real Unity PPO agent will use.

Verified live: starting from random initialization, mean batch reward goes
from ~1.8 (default config) / ~-0.65 (a harder gap-jump-only config) to
~8.1–8.2 within 5–10 batches and stays converged, with the policy gradient's
norm shrinking as it approaches the optimum (the correct convergence signal
for vanilla policy gradient — there's no single monotonic "loss" the way
there is in supervised learning). Also checked with the elevation piece type
enabled (off by default) so that flag isn't exercising untested code if
flipped on later. `tests/test_mvp_policy_gradient.py` encodes this as a
permanent regression check. **This answers "if real PPO training fails to
learn, is it a data/wiring bug or a real method-quality question" — that
class of structural failure is positively ruled out here**, independent of
whether the compositional-piece method itself turns out to work well.

## Fake trainer — the current stand-in for mlagents-learn

`src/playtester_rl/fake_trainer.py` runs many fake-env episodes with an
epsilon-greedy blend of random and scripted-competent actions, epsilon
decaying per episode. `warm_start=True` (simulating a Stage 1 checkpoint)
starts at a much lower epsilon than `warm_start=False` (cold start), so it
structurally converges faster — the same shape Gate 2 checks for. This is
**not a real RL algorithm** and makes no claim about real training dynamics;
it exists so the full CLI → env → telemetry → manifest → gate pipeline is
provably correct today. `src/playtester_rl/cli.py` supports explicit
`--execution-mode real|fake` plus a backwards-compatible `auto` mode. Real
mode fails closed unless both the locked Unity build and `mlagents-learn`
exist. It translates project checkpoint paths to ML-Agents run IDs, parses
trainer status into reward-curve/manifest metrics, and records the ONNX
export for Unity playback. `scripts/run_playtest.sh` resumes the exact real
run in inference mode; it never silently treats a real checkpoint as a
heuristic-policy smoke.

## Running tests

```bash
cd rl
uv run pytest -v
```

131 tests as of this writing, covering: config validation (good + malformed
fixtures), reward-strategy unit tests + the Gate-1-fallback config-swap
mechanism, telemetry schema validation (10 distinct malformed-fixture
shapes) + the `seen_in_stage1_range` boundary heuristic, checkpoint-manifest
round-tripping + **concurrent-write safety** (a real race was found and fixed
— see `test_manifest_concurrency.py`), the fake environment (observation
shape, one-hot grid integrity, reward-event wiring, non-collapse across
randomized seeds), the fake trainer's warm/cold convergence asymmetry, the
full CLI dispatch path, and an end-to-end smoke test across both demo levels.

## Production-shaped training and playback

Stage 1 is one shared randomized composition-gym generalizer. Stage 2 and the
cold-start baseline run against the same real level:

```bash
rl/scripts/train_stage1.sh --level-id gym \
  --checkpoint-out rl/checkpoints/stage1/generalizer.ckpt \
  --output-manifest rl/checkpoint_manifest.json --execution-mode real

rl/scripts/finetune_stage2.sh --level-id level_a \
  --checkpoint-in rl/checkpoints/stage1/generalizer.ckpt \
  --checkpoint-out rl/checkpoints/stage2/level_a/level_a_stage2.ckpt \
  --output-manifest rl/checkpoint_manifest.json --execution-mode real

rl/scripts/baseline_coldstart.sh --level-id level_a \
  --checkpoint-out rl/checkpoints/coldstart/level_a/level_a_coldstart.ckpt \
  --output-manifest rl/checkpoint_manifest.json --execution-mode real

rl/scripts/run_playtest.sh --level-id level_a \
  --checkpoint-in rl/checkpoints/stage2/level_a/level_a_stage2.ckpt \
  --episodes 20 --telemetry-out /tmp/level_a.telemetry.json
```

Repeat Stage 2, cold-start, and playback with `level_b`. For a structural-only
run that must never launch Unity, pass `--execution-mode fake` explicitly.
Real checkpoint markers store paths relative to the marker so the complete
`rl/checkpoints` tree can move to GB10 without embedding a laptop path.

## GB10 policy mode

The primary architecture keeps Unity simulation on the Mac while training and
policy inference run on the GB10:

```text
Mac Unity -- observations --> SSH tunnel --> GB10 ML-Agents/PyTorch
Mac Unity <-- actions ------ SSH tunnel <-- GB10 ML-Agents/PyTorch
```

Set the remote SSH username without committing it:

```bash
export PLAYTESTER_GB10_USER=<gb10-ssh-user>
tailscale ping promaxgb10-8525.taila4d506.ts.net
```

The ping must be direct, not DERP-relayed. `rl/configs/remote_execution.yaml`
contains the Tailscale hostname, remote repository path, trainer path, result
path, and base port—never an IP address. The username may instead be written
there if it is stable for the team.

Verify the manual port-5004 boundary before the automated run:

```bash
# GB10: trainer listens; it does not launch a Unity executable.
cd GB10-project
rl/.venv-mlagents/bin/mlagents-learn \
  rl/configs/training_config.remote_smoke.yaml \
  --run-id=manual_tunnel_smoke \
  --results-dir=rl/checkpoints/remote-results \
  --base-port=5004 --torch-device=cuda

# Mac, second terminal:
ssh -N -L 127.0.0.1:5004:127.0.0.1:5004 \
  "$PLAYTESTER_GB10_USER@promaxgb10-8525.taila4d506.ts.net"

# Mac, third terminal (use the executable inside the .app on macOS):
unity/PlaytesterProject/Builds/gym/gym.app/Contents/MacOS/PlaytesterProject \
  -batchmode -nographics --mlagents-port 5004 --mlagents-max-steps 16
```

Once that works, the CLI owns the trainer, tunnel, local Unity process, unique
port, cleanup, and artifact return:

```bash
rl/scripts/train_stage1.sh --level-id gym \
  --checkpoint-out rl/checkpoints/stage1/generalizer.ckpt \
  --output-manifest rl/checkpoint_manifest.json \
  --execution-mode remote --run-id gym_remote_smoke \
  --training-config rl/configs/training_config.remote_smoke.yaml \
  --env-max-steps 16

rl/scripts/run_playtest.sh --level-id level_a \
  --checkpoint-in rl/checkpoints/stage2/level_a/level_a_stage2.ckpt \
  --episodes 3 --telemetry-out /tmp/level_a.remote.json \
  --execution-mode remote
```

One remote CLI session owns one external Unity connection. Concurrent levels
are separate sessions; each receives a stable non-colliding port derived from
its level and run ID. Sentis remains an ONNX compatibility check and emergency
local fallback, not the primary inference path. Omitting `--training-config`
selects the production `training_config.yaml`; the tracked remote-smoke profile
must always be requested explicitly.

## Module map

| File | Purpose |
|---|---|
| `src/playtester_rl/config_loader.py` | Typed YAML config loading + validation for all `configs/*.yaml` |
| `src/playtester_rl/reward_strategies.py` | `IRewardStrategy` + `CompositionalRewardStrategy` + `SingleGymFallbackStrategy` |
| `src/playtester_rl/telemetry_writer.py` | Schema-conformant telemetry writing/validation + `seen_in_stage1_range` heuristic |
| `src/playtester_rl/gate_eval.py` | Gate 1 (convergence check) / Gate 2 (speedup check) pass/fail logic |
| `src/playtester_rl/fake_env.py` | Pure-Python piece-composition environment used for pipeline smoke tests |
| `src/playtester_rl/fake_trainer.py` | Structural stand-in for `mlagents-learn` — epsilon-greedy rollout loop over `fake_env.py` |
| `src/playtester_rl/checkpoint_manifest.py` | Read/write helper for `contracts/checkpoint_manifest.schema.json` (concurrency-safe) |
| `src/playtester_rl/cli.py` | `python -m playtester_rl.cli {stage1,stage2,coldstart}` — implements the locked CLI contract, real-build detection + fake-trainer fallback |
| `scripts/*.sh` | Thin bash wrappers over the CLI, locked flag contract per `prd-ml.md` §5 |
