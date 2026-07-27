# Compositional RL Playtesting Agent for Unity 2D Platformers

**Technical spec — Dell × NVIDIA Local AI Hackathon (GB10), 29-hour build window**
**Team: 3 — ML/RL lead, Unity/C# lead (Rahul), Infra/Backend lead (Abhi)**

---

## 1. Purpose

An automated playtesting system for Unity 2D platformers, delivered as a Unity Editor tool. It trains an RL agent to play a level, then generates a structured playtest report (difficulty, teachability, problem points). The system is designed around a two-stage training method — a **generalizer** stage that teaches transferable motor control across randomized mechanic combinations, and a **specifier** stage that fine-tunes quickly on a specific real level — so that onboarding a new game should require materially less training time than training from scratch.

**Product framing for the pitch:** "The agent already knows how to move — point it at your game's levels and it adapts to your specific layouts in a fraction of the time cold-start training would take." Not a zero-shot, cross-game claim. Not a claim that the frozen policy weights transfer between different games. The claim is about *pipeline speed on a new game*, not *zero-shot generality of one model*.

---

## 2. Core Method

### 2.1 Two-stage training

**Stage 1 — Generalizer.**
Train a policy on procedurally composed sequences of isolated mechanic "pieces" (e.g., a gap-jump, a move-to-goal segment), randomized every episode — different piece order, different piece parameters (gap width, height) within a fixed range — so the policy cannot memorize a fixed layout and is forced to learn the actual state→action relationship for each mechanic (e.g., "given this distance and velocity, jump now").

**Stage 2 — Specifier.**
Take the Stage 1 checkpoint and fine-tune it on a real, specific level (real tilemap, real goal, real hazard placement). Because the level's individual mechanics fall within the parameter ranges the generalizer already learned, this should converge substantially faster than training from a random initialization. **This is an assumption to be empirically validated, not assumed true — see §7.**

### 2.2 Piece system (Stage 1)

**Primitives (actions):** move left, move right, jump. (Dash cut from scope — see §8.)

**Pieces:** short, isolated mechanic segments, each testing one skill in isolation:
- Gap-jump piece: a gap of randomized width (within range `[W_min, W_max]`), agent must clear it
- Move-to-goal piece: flat ground, goal at a randomized distance, agent must simply traverse it
- Elevation piece (if time allows): a platform at randomized height, agent must jump onto it

**Composition:** each training episode, randomly sample and concatenate 3 pieces (fixed count — see §8) in a random order, with randomized parameters within each piece's range. The player traverses the composed sequence left-to-right, piece after piece.

**Piece-boundary handling:** velocity is reset to zero at each piece transition (the "safe" option — see prior discussion). This is a deliberate simplification, not an oversight; document it as a known limitation and future-work item (a more general version would train across a distribution of entry velocities at each boundary).

### 2.3 Reward design (critical — dense, not sparse)

Reward must be given at **each piece completion**, not only at the end of the full composed sequence, to avoid the sparse/moving-target reward problem that risks non-convergence:

| Event | Reward |
|---|---|
| Per-step progress toward current piece's local goal | small positive, proportional to progress |
| Per-step time penalty | small negative constant (discourage stalling) |
| Piece completed | positive bonus, fires immediately on completion of *that* piece |
| Full sequence completed (final piece) | larger positive bonus |
| Death / fall / hazard contact | negative penalty, `EndEpisode()` |

Implementation: `Agent.OnActionReceived()` computes progress reward each step by comparing current vs. previous position; piece-completion and death/goal triggers are Unity collider/trigger events that call `AddReward()` (and `EndEpisode()` where applicable).

### 2.4 State (observation space)

Fixed across both stages and all levels — this consistency is what makes Stage 1 → Stage 2 transfer valid at all:

- Player position (relative to current local goal/piece, normalized)
- Player velocity (x, y)
- Grounded/airborne flag
- Local tile occupancy grid: an N×N window centered on the player, encoding tile type (empty / solid / hazard / goal) — read via Unity's Tilemap API (`GetTile`, `cellBounds`)
- Distance/direction to next objective (next piece's goal in Stage 1; level goal, possibly with waypointing, in Stage 2)

### 2.5 Action (action space)

Discrete or MultiDiscrete: `{left, right, jump}` (no-op implicit / mutually exclusive as appropriate), mapped each step to Unity's Input System.

### 2.6 Control interface — explicit constraint

The agent does **not** read or interpret the target player's C# scripts. It controls the player exclusively through a fixed input interface (Unity's Input System), and learns the state→action relationship empirically. **Compatibility claim is scoped to player controllers wired through Unity's standard Input System with a conventional move/jump interface** — not arbitrary player code. This constraint is load-bearing for feasibility and must not be silently reintroduced as "reads the player's capabilities" during the build.

### 2.7 Algorithm

PPO via Unity ML-Agents (`mlagents-learn`), headless for training speed, multiple parallel environment instances (`--num-envs`), real achieved parallel count reported honestly in the pitch (no inflation).

---

## 3. Unity Architecture

### 3.1 Existing asset assumption

Team has an existing (or near-existing) Unity 2D platformer project with a player controller and tilemap-based level. Build extends this rather than starting from a blank project. **Open item for Rahul to confirm at hour 0: does the existing player controller already route input through Unity's Input System in a way compatible with §2.6, or does it need adaptation first?** This should be the very first thing checked, since it gates everything else.

### 3.2 Components to build

- **Piece-composition gym scene:** a scene (or runtime-generated layout) supporting randomized piece sequencing per episode, per §2.2.
- **`Agent` subclass (ML-Agents):** implements `CollectObservations`, `OnActionReceived`, `OnEpisodeBegin` (handles both Stage 1 piece-resample-and-reset and Stage 2 level-reset).
- **Tilemap reader:** utility reading a `Tilemap` component into the grid observation format described in §2.4. Reusable across Stage 1 (procedurally generated pieces) and Stage 2 (real authored levels).
- **Unity Editor tool (EditorWindow):** the developer-facing package — select Player GameObject + Tilemap in the current scene, trigger training/fine-tuning, view results. This is the primary "polished product" surface for the demo.
- **Sentis inference integration:** load a trained/exported (ONNX) policy and run it for in-Editor or in-build playback, so the agent is visibly seen playing a level during the demo.
- **Start/goal identification:** developer places marker GameObjects (or a required prefab component) in the scene identifying start position and goal position. **Decision: marker-GameObject convention, not naming-convention inference** — more robust, low added cost. (Optimal-default call — flag for confirmation in PRD if disagreed.)
- **Hazard tile identification:** **Decision: a custom Tile subclass / tile asset property the developer sets once per project**, rather than naming-convention string matching — more robust against arbitrary asset naming. (Optimal-default call — flag for confirmation in PRD.)

---

## 4. Report Generation

### 4.1 Telemetry captured during a playtest run

- Death locations (position, and which piece/section of the level)
- Attempts per section / retries before success
- Time-to-clear (per section and total)
- Path taken (position trace)
- Which specific jumps/sections had highest retry counts (difficulty proxy)
- Whether later sections reuse mechanics introduced earlier vs. introduce a new required skill without a prior "teaching" instance (heuristic: has this piece-type/parameter-range appeared earlier in the level or in Stage 1 training range)

### 4.2 Pipeline

Telemetry (structured JSON) → local LLM (70B-class, e.g. Nemotron or Llama 3.3, run via NemoClaw's local inference routing) → structured report output covering: overall difficulty assessment, specific flagged problem points with location references, teachability assessment, any planted exploit/issue found. Displayed in the Unity Editor tool window.

---

## 5. Stack Integration — OpenClaw + NemoClaw + OpenShell

| Layer | Role | Owner |
|---|---|---|
| **OpenClaw** | Always-on agent runtime. Watches a designated levels/export directory; on new/changed level, automatically triggers Stage 2 fine-tune + playtest + report generation without manual invocation. | Abhi |
| **NemoClaw** | One-command deployment on the GB10: OpenClaw + sandboxed runtime + local model routing (`curl -fsSL https://www.nvidia.com/nemoclaw.sh \| bash`, onboard, point at local model). | Abhi |
| **OpenShell** | Sandbox policy under NemoClaw. Filesystem access scoped to levels/telemetry/report/checkpoint directories only. No outbound network egress — unreleased level/game content never leaves the machine. This is the honest must-be-local argument for the product. | Abhi |

---

## 6. GB10 Utilization Strategy (honest framing — do not overclaim)

**Primary claim: local large-model report generation.** Real memory usage, real inference throughput, real "why local" argument (unreleased game content stays on-box). Lead the pitch with this.

**Secondary claim: accelerated generalist training via large-batch policy updates.** Precise version, to avoid the mechanism-overstatement flagged in review: running many parallel randomized environment instances generates a large rollout buffer of experience quickly; the GB10's throughput is what lets the **PPO policy update step** process that large batch quickly. The environment-stepping/physics side of this remains CPU-bound per instance (standard ML-Agents behavior) — the GB10 is not why you can run many environments, it's why the resulting large-batch gradient updates are fast. State this distinction accurately if asked; do not claim the GPU is why environment parallelism itself is high.

**Product framing enabled by this:** "onboarding a new game, a company could get a generalist-mechanics-warmed-up model plus a specific fine-tuned level agent trained in around an hour" (real number to be measured, not asserted — see §7) — using the GB10's throughput on the policy-update side of that pipeline.

**Explicitly not claimed:** that RL policy training saturates 128GB of memory or requires heavy FP4 throughput in the way a large-model inference workload does. Do not imply this to judges.

---

## 7. Validation Gates (must happen, not optional, timing is load-bearing)

1. **Gate 1 (~hour 4):** Does Stage 1 (piece-composition generalizer) training actually converge — reward curve trending up, not collapsing/plateauing? If unclear or negative, fall back to a simpler single-randomized-gym design (single mechanic type randomized per episode, no composition) rather than continuing to iterate on the compositional design.
2. **Gate 2 (~hour 14, post-overnight run):** Does Stage 2 (real-level fine-tune from Stage 1 checkpoint) converge measurably faster than a cold-start baseline on the same level? Run both and compare directly — do not assume the speedup, measure it. This number, if positive, is also a legitimate thing to put on the pitch slide.

Both gates are hard decision points: if a gate fails, commit to the fallback immediately rather than continuing to debug the ambitious version against the clock.

---

## 8. Explicit Scope Decisions (locked — do not revisit mid-build without a real reason)

1. Action space: `{left, right, jump}` only. Dash cut.
2. Piece composition length: fixed at 3 pieces per episode (not variable, not 3–4).
3. Piece-boundary velocity: reset to zero (not trained across an entry-velocity distribution).
4. Piece types for hackathon scope: gap-jump, move-to-goal, and elevation-if-time (§2.2) — do not add further piece types under time pressure.
5. Cross-game / cross-project generalization: **not a live claim.** The frozen policy does not zero-shot transfer between different games. The claim is pipeline speed (fast fine-tune) on games/levels within one compatible project. An outside/third-party validation project is a stretch goal only if both validation gates pass early and there is genuine slack — not a baseline commitment given 29 hours.
6. Per-game auto-generated gym scenes (procedurally building a warm-up scene inside an arbitrary target project): **out of scope for this build.** Generalizer gym is built once, for the team's own demo project.
7. Real demo levels must be authored with gap widths / heights / piece-order patterns inside the same parameter ranges used in Stage 1 randomization (`[W_min, W_max]` etc.) — required for Gate 2's speedup claim to hold, not optional level-design polish.
8. Compatibility scope: player controllers wired through Unity's standard Input System with a conventional move/jump interface. Not literal arbitrary player scripts.

---

## 9. Demo Requirements

- 2–3 real demo levels, authored within the Stage 1 parameter ranges (per §8.7).
- At least one level with a **deliberately planted issue** (unintended shortcut, near-impossible jump, or softlock spot), verified pre-event to be reliably found and correctly flagged in the generated report.
- Pre-trained checkpoints for all demo levels as the guaranteed fallback — no live from-scratch training gambled on stage. Live demo may show a fast fine-tune/incremental improvement from a known-good checkpoint, never a from-scratch run.
- Concurrent-parallelism demo for the GB10 claim: train/process multiple levels simultaneously (same-project multi-level, per team decision), capture and report the real wall-clock number.
- OpenShell no-egress proof: attempt an outbound network call during the demo, show it blocked.

---

## 10. Team Workload Split

| Owner | Responsibilities |
|---|---|
| **ML/RL lead (you)** | Piece-composition gym design and implementation, reward function, `Agent` subclass logic (in collaboration with Rahul on the Unity-side hooks), Stage 1 training runs and Gate 1 validation, Stage 2 fine-tuning pipeline and Gate 2 validation, concurrent-parallelism training run for the GB10 demo number. |
| **Unity/C# lead (Rahul)** | Confirm/adapt existing player controller for Input System compatibility (hour 0 priority), Unity Editor tool (EditorWindow, GameObject/Tilemap selection UI), Tilemap reader utility, start/goal marker system, hazard tile tagging system, Sentis inference integration for in-Editor playback, demo level authoring (including the planted-issue level). |
| **Infra/Backend lead (Abhi)** | Report-generation pipeline (telemetry schema → local LLM prompt/pipeline → structured output), OpenClaw agent skill (watch levels directory, trigger pipeline), NemoClaw deployment/onboarding, OpenShell policy configuration (filesystem scope, network egress block) and the live no-egress proof, telemetry logging schema (shared contract with ML/RL lead's training code). |

**Cross-cutting dependency to flag explicitly:** the telemetry schema (what fields get logged during a playtest run) is a shared contract between the ML/RL lead's playtest-run code and Abhi's report-generation pipeline. Define and lock this schema early (target: by hour 4, alongside Gate 1) so both sides can build against it independently without blocking each other.

---

## 11. Timeline (29 hours)

**Hours 0–4 — Human-attended, foundational + Gate 1**
- Rahul: confirm/adapt player controller Input System compatibility; begin Editor tool shell + Tilemap reader.
- ML/RL: build piece-composition gym (3 primitives, 3-piece sequences, per-piece dense reward); kick off Stage 1 training run (continues into sleep window).
- Abhi: start Claude Code session scaffolding report-generation pipeline skeleton, OpenClaw agent skill structure, NemoClaw/OpenShell setup scripts — all independent of RL training outcome.
- **Lock the telemetry schema by end of this block.**

**Hours 4–8 — Human-attended, building + data prep**
- Review/fix Claude Code output from hours 0–4.
- Rahul: build 2–3 real demo levels + the planted-issue level, parameters within Stage 1 ranges; continue Editor tool.
- ML/RL: monitor Stage 1 training; check Gate 1 — if failing, decide the fallback now, not later.
- Abhi: continue report pipeline + OpenClaw/NemoClaw/OpenShell scaffolding.

**Hours 8–14 — Sleep window, unattended work**
- Stage 1 training continues/completes.
- Once a usable Stage 1 checkpoint exists, kick off Stage 2 fine-tune validation run (Gate 2) plus a cold-start baseline run on the same level, unattended, for comparison by morning.
- Overnight Claude Code sessions on well-specified, low-ambiguity tasks: report-generation LLM prompt/pipeline, OpenClaw event-watcher skill, NemoClaw onboarding script, OpenShell policy config. One team member loosely on-call to catch silent failures.

**Hours 14–18 — Human-attended, verification**
- Check Gate 2 result: is the fine-tune speedup real and measurable? Commit to whichever RL design path actually worked — no further iteration on the RL design past this point.
- Review/fix overnight Claude Code output.
- Rahul: wire Sentis inference for in-Editor playback; verify by actually running it, watching the agent play.

**Hours 18–23 — Human-attended, integration**
- Run the concurrent-multi-level parallelism demo, capture the real GB10 throughput number.
- Full pipeline integration: Editor tool → trained agent plays level → telemetry → report → OpenClaw auto-trigger on new level.
- Budget real time here — integration breakage is expected, not a sign of a problem.

**Hours 23–27 — Human-attended, hardening**
- Fix integration issues found in the previous block.
- Run the planted-issue level demo repeatedly, confirm reliability, not just a single success.
- Confirm OpenShell no-egress proof works live.

**Hours 27–29 — Rehearsal only**
- Run the full demo sequence multiple times. Lock the script. No new code.

---

## 12. Open Questions for PRD / Confirmation

1. Confirmed at hour 0 by Rahul: does the existing player controller already support Input-System-driven control per §2.6, or does it need adaptation first (and how much time will that take)?
2. Exact reward magnitudes (progress scale, time penalty, piece-completion bonus, death penalty, final-goal bonus) — to be tuned empirically during Gate 1, not fixed in advance.
3. Exact parameter ranges for piece randomization (`W_min`/`W_max` for gap width, height ranges) — to be set once the base player's jump distance/height is measured from the actual player controller.
4. Final decision on whether the elevation piece type makes it into scope, contingent on how hours 0–4 go.
5. Exact format/fields of the telemetry JSON schema (owned jointly by ML/RL lead and Abhi, per §10 cross-cutting dependency) — needs to be nailed down concretely, not just "structured JSON," before both sides build against it.
