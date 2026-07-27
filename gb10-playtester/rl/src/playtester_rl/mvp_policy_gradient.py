"""A minimal REAL learning algorithm (REINFORCE / vanilla policy gradient,
plain numpy, no mocked behavior) — deliberately separate from
fake_trainer.py, whose epsilon-schedule is a hand-coded heuristic that
"improves" by construction, not by learning.

Purpose: this is the actual stress test of whether the observation encoding,
action space, and reward wiring are *learnable at all* by a real
gradient-based agent trained from a random initialization — i.e. whether a
future real PPO agent (Unity ML-Agents) has any chance of learning something
from these exact contracts, as opposed to the pipeline being schema-valid but
practically useless (constant/degenerate observations, a reward signal with
no gradient, an action space that can't reach the goal, etc).

This is NOT a claim about whether the compositional-piece RL *method*
described in the spec will produce a good playtester — that is a real
empirical question for the actual PPO run on the actual Unity build, and
this tiny linear-softmax policy has nowhere near PPO's capacity. It only
answers: "is there a straightforward, learnable gradient signal here, or is
something structurally broken (data format, missing information, degenerate
reward) that would sink ANY learning algorithm regardless of technique?"

Algorithm: single-layer softmax policy over the flattened observation
vector, trained with per-batch standardized-return REINFORCE (a mean-reward
baseline + return standardization for variance reduction — the standard,
simplest-workable version of vanilla policy gradient).

Two entry points:
  - train_policy_gradient(...): the plain training loop (policy + reward-
    curve progress only). Used by the learnability tests.
  - train_and_produce_pipeline_artifacts(...): the SAME real learning loop,
    additionally wired through telemetry_writer (real per-episode telemetry,
    schema-validated), gate_eval (real reward-curve persistence + Gate 1
    evaluation), and checkpoint_manifest (real manifest write) — i.e. the
    real agent going through every real production module in this package,
    not just fake_env. fake_env.FakeCompositionEnv is the only stand-in
    piece here (temporary glue for the real Unity environment); everything
    else in this call path is the genuine module that ships.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

from playtester_rl.config_loader import ObservationConfig, PieceConfig, RewardConfig
from playtester_rl.fake_env import FakeCompositionEnv
from playtester_rl.gate_eval import GateResult, gate1_check, save_reward_curve
from playtester_rl.reward_strategies import create_reward_strategy
from playtester_rl.telemetry_writer import TelemetryBuilder, compute_seen_in_stage1_range

_MAX_STEPS_PER_EPISODE = 250
_NUM_ACTIONS = 4  # noop, left, right, jump — see fake_env.py's ACTION_* constants


@dataclass
class LinearSoftmaxPolicy:
    """logits = obs @ W + b; action ~ softmax(logits). Gradient of
    log pi(a|s) w.r.t. logits is the standard softmax-cross-entropy gradient:
    (one_hot(a) - probs). This is real backprop through a real (if tiny)
    parametric model — not a lookup table or scripted rule."""

    obs_dim: int
    num_actions: int
    seed: int = 0
    learning_rate: float = 0.05
    W: np.ndarray = field(init=False)
    b: np.ndarray = field(init=False)

    def __post_init__(self) -> None:
        rng = np.random.default_rng(self.seed)
        # Small random init — standard practice, avoids saturating softmax
        # at the very first forward pass.
        self.W = rng.normal(0, 0.01, size=(self.obs_dim, self.num_actions))
        self.b = np.zeros(self.num_actions)

    def _softmax(self, logits: np.ndarray) -> np.ndarray:
        shifted = logits - np.max(logits)
        exp = np.exp(shifted)
        return exp / np.sum(exp)

    def act(self, obs: np.ndarray, rng: np.random.Generator) -> tuple[int, np.ndarray]:
        logits = obs @ self.W + self.b
        probs = self._softmax(logits)
        action = rng.choice(self.num_actions, p=probs)
        return int(action), probs

    def apply_gradient(self, grad_W: np.ndarray, grad_b: np.ndarray) -> None:
        # Gradient ASCENT on expected return (REINFORCE maximizes reward,
        # unlike supervised loss minimization) — hence +=, not -=.
        self.W += self.learning_rate * grad_W
        self.b += self.learning_rate * grad_b

    def save(self, path: Path) -> None:
        """The real checkpoint artifact for this agent — actual trained
        weights, not a text marker (unlike fake_trainer.py's placeholder
        checkpoint file, which has no real parameters behind it)."""
        path.parent.mkdir(parents=True, exist_ok=True)
        np.savez(path, W=self.W, b=self.b)

    @classmethod
    def load(cls, path: Path, learning_rate: float = 0.05) -> "LinearSoftmaxPolicy":
        data = np.load(path)
        policy = cls(obs_dim=data["W"].shape[0], num_actions=data["W"].shape[1], learning_rate=learning_rate)
        policy.W = data["W"]
        policy.b = data["b"]
        return policy


@dataclass
class TrainingProgress:
    batch_index: int
    mean_batch_reward: float
    mean_grad_norm: float


@dataclass
class _EpisodeRollout:
    obs_list: list[np.ndarray]
    action_list: list[int]
    probs_list: list[np.ndarray]
    returns: np.ndarray
    total_reward: float
    piece_results: list[dict[str, Any]]
    path_trace: list[dict[str, float]]
    outcome: str
    steps_taken: int


def _rollout_episode(
    env: FakeCompositionEnv,
    policy: LinearSoftmaxPolicy,
    rng: np.random.Generator,
    gamma: float,
) -> _EpisodeRollout:
    """One real episode: policy.act() drives every step (genuine forward
    pass through the trained-so-far weights), env.step() is the only
    stand-in piece. Shared by both entry points so the plain
    train_policy_gradient and the full-pipeline variant can never silently
    diverge in what "one episode" means."""
    obs, _ = env.reset()

    obs_list: list[np.ndarray] = []
    action_list: list[int] = []
    probs_list: list[np.ndarray] = []
    rewards: list[float] = []
    outcome = "timeout"

    for _ in range(_MAX_STEPS_PER_EPISODE):
        action, probs = policy.act(obs, rng)
        obs_list.append(obs)
        action_list.append(action)
        probs_list.append(probs)

        obs, reward, terminated, truncated, info = env.step(action)
        rewards.append(reward)

        if terminated or truncated:
            outcome = info.get("outcome", "timeout")
            break

    returns = np.zeros(len(rewards))
    running = 0.0
    for t in reversed(range(len(rewards))):
        running = rewards[t] + gamma * running
        returns[t] = running

    piece_results, path_trace = env.episode_telemetry()

    return _EpisodeRollout(
        obs_list=obs_list,
        action_list=action_list,
        probs_list=probs_list,
        returns=returns,
        total_reward=float(sum(rewards)),
        piece_results=piece_results,
        path_trace=path_trace,
        outcome=outcome,
        steps_taken=len(rewards),
    )


def _apply_batch_gradient(policy: LinearSoftmaxPolicy, rollouts: list[_EpisodeRollout]) -> float:
    """Standardized-return REINFORCE update over one batch. Returns the
    combined gradient norm (the policy-gradient analog of 'loss'). Shared
    by both entry points — the actual math is identical either way."""
    all_advantages = [r for rollout in rollouts for r in rollout.returns.tolist()]
    advantages_arr = np.array(all_advantages)
    adv_mean = advantages_arr.mean()
    adv_std = advantages_arr.std() + 1e-8

    batch_grad_W = np.zeros_like(policy.W)
    batch_grad_b = np.zeros_like(policy.b)
    num_steps = 0

    for rollout in rollouts:
        for t in range(len(rollout.obs_list)):
            advantage = (rollout.returns[t] - adv_mean) / adv_std
            one_hot = np.zeros(policy.num_actions)
            one_hot[rollout.action_list[t]] = 1.0
            dlogits = one_hot - rollout.probs_list[t]

            batch_grad_W += np.outer(rollout.obs_list[t], dlogits) * advantage
            batch_grad_b += dlogits * advantage
            num_steps += 1

    num_steps = max(1, num_steps)
    batch_grad_W /= num_steps
    batch_grad_b /= num_steps

    policy.apply_gradient(batch_grad_W, batch_grad_b)
    return float(np.linalg.norm(batch_grad_W)) + float(np.linalg.norm(batch_grad_b))


def train_policy_gradient(
    piece_config: PieceConfig,
    reward_config: RewardConfig,
    observation_config: ObservationConfig,
    num_batches: int,
    episodes_per_batch: int,
    seed: int = 0,
    learning_rate: float = 0.05,
    gamma: float = 0.97,
) -> tuple[LinearSoftmaxPolicy, list[TrainingProgress]]:
    """Real vanilla policy-gradient training loop. Returns the trained policy
    and a per-batch progress log (mean reward, mean gradient norm — the
    latter is printed/checked so a silently-zero or exploding gradient is
    caught, not just a plateaued reward number that could have many causes).
    """
    reward_strategy = create_reward_strategy(reward_config)
    obs_dim = observation_config.observation_size()

    policy = LinearSoftmaxPolicy(obs_dim=obs_dim, num_actions=_NUM_ACTIONS, seed=seed, learning_rate=learning_rate)
    rng = np.random.default_rng(seed + 1)

    progress: list[TrainingProgress] = []

    for batch_index in range(num_batches):
        rollouts = []
        for episode_index in range(episodes_per_batch):
            env = FakeCompositionEnv(
                piece_config, observation_config, reward_strategy, seed=seed * 1_000_000 + batch_index * 1000 + episode_index
            )
            rollouts.append(_rollout_episode(env, policy, rng, gamma))

        grad_norm = _apply_batch_gradient(policy, rollouts)
        progress.append(
            TrainingProgress(
                batch_index=batch_index,
                mean_batch_reward=float(np.mean([r.total_reward for r in rollouts])),
                mean_grad_norm=grad_norm,
            )
        )

    return policy, progress


@dataclass
class RealAgentPipelineResult:
    policy: LinearSoftmaxPolicy
    progress: list[TrainingProgress]
    telemetry_doc: dict[str, Any]
    reward_curve: list[tuple[int, float]]
    gate1_result: GateResult
    final_mean_reward: float
    training_steps: int


def train_and_produce_pipeline_artifacts(
    level_id: str,
    stage: str,
    checkpoint_out: Path,
    reward_curve_out: Path,
    piece_config: PieceConfig,
    reward_config: RewardConfig,
    observation_config: ObservationConfig,
    num_batches: int,
    episodes_per_batch: int,
    seed: int = 0,
    learning_rate: float = 0.05,
    gamma: float = 0.97,
) -> RealAgentPipelineResult:
    """The real agent (LinearSoftmaxPolicy, real backprop), trained through
    every real production module in this package — config_loader's already-
    loaded configs in, reward_strategies.create_reward_strategy for the
    actual reward signal, telemetry_writer for real per-episode telemetry
    (schema-validated, seen_in_stage1_range computed for real), gate_eval
    for a real Gate 1 check against this run's own reward curve, and a real
    saved checkpoint (actual trained weights, via LinearSoftmaxPolicy.save).
    fake_env.FakeCompositionEnv is the only non-production stand-in piece —
    everything else here is exactly what ships.
    """
    reward_strategy = create_reward_strategy(reward_config)
    obs_dim = observation_config.observation_size()

    policy = LinearSoftmaxPolicy(obs_dim=obs_dim, num_actions=_NUM_ACTIONS, seed=seed, learning_rate=learning_rate)
    rng = np.random.default_rng(seed + 1)

    telemetry_builder = TelemetryBuilder(
        level_id=level_id,
        stage=stage,
        checkpoint_path=str(checkpoint_out),
        timestamp_start="1970-01-01T00:00:00Z",
    )

    progress: list[TrainingProgress] = []
    reward_curve: list[tuple[int, float]] = []
    global_step = 0
    global_episode_index = 0

    for batch_index in range(num_batches):
        rollouts = []
        for episode_index in range(episodes_per_batch):
            env = FakeCompositionEnv(
                piece_config, observation_config, reward_strategy, seed=seed * 1_000_000 + batch_index * 1000 + episode_index
            )
            rollout = _rollout_episode(env, policy, rng, gamma)
            rollouts.append(rollout)

            for piece_result in rollout.piece_results:
                piece_result["seen_in_stage1_range"] = compute_seen_in_stage1_range(
                    piece_result["piece_type"], piece_result["params"], piece_config
                )
            telemetry_builder.add_episode(
                episode_index=global_episode_index,
                outcome=rollout.outcome,
                total_reward=rollout.total_reward,
                time_to_clear_seconds=float(rollout.steps_taken) if rollout.outcome == "success" else None,
                path_trace=rollout.path_trace,
                piece_results=rollout.piece_results,
            )
            global_step += rollout.steps_taken
            global_episode_index += 1

        grad_norm = _apply_batch_gradient(policy, rollouts)
        mean_batch_reward = float(np.mean([r.total_reward for r in rollouts]))
        progress.append(TrainingProgress(batch_index=batch_index, mean_batch_reward=mean_batch_reward, mean_grad_norm=grad_norm))
        reward_curve.append((global_step, mean_batch_reward))

    telemetry_doc = telemetry_builder.build()  # real schema validation happens here

    policy.save(checkpoint_out)  # real trained weights, not a text marker

    save_reward_curve(reward_curve, reward_curve_out)  # real gate_eval artifact
    gate1_result = gate1_check(reward_curve)  # real Gate 1 evaluation against THIS run's own curve

    last_window = max(1, len(reward_curve) // 10)
    final_mean_reward = float(np.mean([r for _, r in reward_curve[-last_window:]]))

    return RealAgentPipelineResult(
        policy=policy,
        progress=progress,
        telemetry_doc=telemetry_doc,
        reward_curve=reward_curve,
        gate1_result=gate1_result,
        final_mean_reward=final_mean_reward,
        training_steps=global_step,
    )
