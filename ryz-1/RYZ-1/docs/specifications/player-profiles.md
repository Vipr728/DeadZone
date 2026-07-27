# Spec: Synthetic Player Profiles

**Honest labeling: these are synthetic profiles — parameterized degradations of solver plans, NOT validated human models.** Every report surface carries this label until calibration against real demonstrations exists.

## Mechanism (MVP)

ProfiledAgent wraps a plan (solver action stream) and executes it through a limitation model. Execution is
**closed loop**: `Act(obs, tick)` reads the live observation and steers the agent back onto the plan when it
drifts. Open-loop replay of a jittered stream is only viable on toy levels — on the real 115-unit SampleScene a
2-tick jitter desyncs the agent from the moving platforms / crumble chain and the remaining ~700 ticks are noise.

### Plan mutation (once per episode, execution noise)


- reactionDelayTicks: actions shifted late by sampled delay (per plan segment).
- timingVarianceTicks (σ): press edges jittered ±N ticks (seeded Gaussian).
- directionErrorProb: chance a dash/move direction is off by one 45° step.
- planningHorizon: solver depth cap when (re)planning.
- mechanicKnowledge: set of allowed macros (beginner: no wall-jump chains, single-use of climb).
- explorationTendency: probability of preferring unexplored branch at equal score. **(not implemented)**
- riskTolerance: penalty weight on paths near hazards during planning. **(not implemented)**

### Closed loop (per tick, T13)

The caller supplies a **reference trajectory** — the position each base-plan action was issued from, produced by
`ProfiledAgent.RecordTrajectory(adapter, scenario, plan)` (one extra replay of the plan, paid per plan, not per
episode) and handed over via `SetReferenceTrajectory`. It is shifted by `reactionDelayTicks` and tail-padded so it
indexes with the mutated plan. Without a reference the agent falls back to the legacy open-loop replay.

Each tick:

1. **Deviation detection** — distance from `obs.Position` to `reference[planIndex]`. The threshold is
   `deviationToleranceUnits`, profile-scaled (Beginner 2.5 / Intermediate 1.5 / Expert 0.8): sloppier players
   drift further before they notice.
2. **Notice delay** — a deviation must persist for `reactionDelayTicks` before the agent reacts. This is where
   most of the profile separation on the real level comes from.
3. **Re-anchor** — find the nearest point on the reference trajectory and continue the plan from THAT index. This
   single mechanism fixes timing desync: a player who is 6 ticks behind just keeps playing from where they are.
4. **Improvise** — if no reference point is within tolerance (genuinely off route, e.g. just respawned mid-air),
   run a search-free reactive controller toward a lookahead waypoint on the route — walk toward it, jump when it
   is above or when stalled against geometry, wall-jump when pinned, spend a dash when falling short — until a
   reference point comes back into tolerance.

No solver re-entry and no second arena: the live episode owns the adapter, and re-entering it for search would
destroy that episode's state. A deviation-triggered local beam search on a scratch arena was considered and NOT
built — the reactive controller clears SampleScene (see rates below), so the arena juggling is unjustified.

### Persistence

Wired inside the episode, not in a batch loop. A respawn is detected as a single-tick position teleport
(> 3 units — `IAgent` sees only `Observation`, so there is no death callback to hook). On each death
`ShouldRepeatFailedPlan(profile, rng)` decides whether to re-anchor immediately (repeat the same plan) or
improvise for 40 ticks first (try something different). Once `persistenceRetries` deaths are exceeded the agent
sets `Abandoned`; `EpisodeRunner` ends the episode as `Outcome.Abandoned` instead of burning the step budget.

All randomness from one seeded System.Random per episode (`seed ^ profileSalt`), drawn only at episode start and
on death. Same seed, same process → identical trace.

## MVP profiles

| Param | Beginner | Intermediate | Expert |
|---|---|---|---|
| reactionDelayTicks | 12 | 6 | 2 |
| timingVarianceTicks σ | 4 | 2 | 0.5 |
| directionErrorProb | 0.15 | 0.05 | 0.01 |
| planningHorizon | short | medium | full |
| mechanicKnowledge | basic (no wall-jump chain) | all, unreliable | all |
| repeatFailedStrategyProb | 0.5 | 0.2 | 0.05 |
| persistence (retries/obstacle) | 8 | 15 | 40 |
| deviationToleranceUnits | 2.5 | 1.5 | 0.8 |

(50 ticks/s at fixedDeltaTime 0.02 — 12 ticks = 240 ms reaction.)

Speedrunner/Explorer/Adversarial: post-MVP.

## Future learned path (interfaces only now)

Record human demos → behavioral cloning conditioned on profile vector → optional RL fine-tune → ONNX → Sentis inference behind same IAgent. No MVP dependency.

## Tests

Profile parameter application (delay/jitter measurably applied to a known plan); beginner completion rate ≤ expert on the precise obstacle; determinism per seed.

`Tests/PlayMode/ProfileRealLevelTests.cs` is the acceptance bar — the closed loop must work on the real
SampleScene, not the demo level: Expert completes a majority of runs, completion rate is monotone
Beginner ≤ Intermediate ≤ Expert with Beginner < 1.0, the same seed gives an identical position trace, and an
injected off-route perturbation is recovered from and the level still finished.

Measured on SampleScene (754-tick solved plan, 6 seeds, step budget 4000):

| | Beginner | Intermediate | Expert |
|---|---|---|---|
| completion rate | 0–1 / 6 | 3–5 / 6 | 6 / 6 |

The spread between repeated runs is engine-level, not profile-level, and is now root-caused and bounded (T14):
Unity Physics2D contact resolution is not bit-exact across processes, at up to 0.0125 units over 400 ticks
(limitations.md #14). That is far below any gameplay distance, but the closed-loop controller amplifies it — a
sub-slop difference can cross the re-anchor deviation threshold and flip a branch. Within a single process the
same seed gives a bit-identical trace, which is what `ProfileRealLevelTests` asserts. Consequence for reporting:
per-profile completion rates must be published as ranges/distributions over seeds and processes, never as a single
number, and the monotonicity claim (Beginner <= Intermediate <= Expert) is the assertion that actually holds.
