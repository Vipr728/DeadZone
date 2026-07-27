"""Fake trainer — structural stand-in for `mlagents-learn`, used by
rl/scripts/*.sh when no real Unity build is present at the locked build-path
convention (prd-ml.md §5). This is NOT a real RL algorithm: it does not learn
from experience or update any weights. It exists so the full pipeline
(CLI -> env rollout -> telemetry -> reward curve -> checkpoint manifest ->
gate evaluation) is provably wired correctly today, without needing Unity or
ML-Agents installed, and so `warm_start=True` (simulating a Stage 1
checkpoint) structurally produces faster convergence than `warm_start=False`
(cold start) — which is precisely the shape Gate 2 checks for. Swapping in
real `mlagents-learn` for the Unity build path is a config/branch change in
cli.py, not a rewrite of anything downstream (telemetry/manifest/gate_eval
consume the same shapes either way).
"""

from __future__ import annotations

import random
from dataclasses import dataclass
from typing import Any

from playtester_rl.config_loader import ObservationConfig, PieceConfig, RewardConfig
from playtester_rl.fake_env import (
    ACTION_JUMP,
    ACTION_LEFT,
    ACTION_NOOP,
    ACTION_RIGHT,
    FakeCompositionEnv,
)
from playtester_rl.gate_eval import compute_steps_to_converge
from playtester_rl.reward_strategies import create_reward_strategy
from playtester_rl.telemetry_writer import (
    TelemetryBuilder,
    compute_seen_in_stage1_range,
)

_ALL_ACTIONS = [ACTION_NOOP, ACTION_LEFT, ACTION_RIGHT, ACTION_JUMP]
_MAX_STEPS_PER_EPISODE = 250

# TUNABLE — fake-trainer-only constants controlling how fast the simulated
# "policy" shifts from random toward the scripted-good policy across episodes.
# These have no bearing on real mlagents-learn hyperparameters.
WARM_START_INITIAL_EPSILON = 0.15
COLD_START_INITIAL_EPSILON = 0.9
EPSILON_DECAY_PER_EPISODE = 0.97
MIN_EPSILON = 0.02


def _good_policy_action(obs, obs_config: ObservationConfig) -> int:
    channel_index = {name: i for i, name in enumerate(obs_config.tile_channels)}
    grid_len = obs_config.grid_size * obs_config.grid_size * len(obs_config.tile_channels)
    grid = obs[:grid_len].reshape(obs_config.grid_size, obs_config.grid_size, len(obs_config.tile_channels))
    ground_row = 0  # Unity serializes y=-radius (the bottom row) first.
    center = obs_config.grid_size // 2
    for ahead in (1, 2):
        idx = center + ahead
        if idx < obs_config.grid_size and grid[ground_row, idx, channel_index["hazard"]] == 1.0:
            return ACTION_JUMP
    return ACTION_RIGHT


@dataclass
class FakeTrainingResult:
    reward_curve: list[tuple[int, float]]
    telemetry_doc: dict[str, Any]
    final_mean_reward: float
    training_steps: int
    steps_to_converge: int | None


def run_fake_training(
    level_id: str,
    stage: str,
    checkpoint_path: str,
    piece_config: PieceConfig,
    reward_config: RewardConfig,
    observation_config: ObservationConfig,
    num_episodes: int,
    warm_start: bool,
    seed: int = 0,
) -> FakeTrainingResult:
    """Runs `num_episodes` fake-env episodes with an epsilon-greedy blend of
    a scripted-competent policy and random actions, epsilon decaying each
    episode. `warm_start=True` starts at a much lower epsilon (standing in
    for 'already has a Stage 1 checkpoint') so the reward curve rises faster
    — this is what makes the Stage 2 vs cold-start comparison meaningful for
    Gate 2 without a real trained model existing yet.
    """
    reward_strategy = create_reward_strategy(reward_config)
    rng = random.Random(seed)

    epsilon = WARM_START_INITIAL_EPSILON if warm_start else COLD_START_INITIAL_EPSILON

    reward_curve: list[tuple[int, float]] = []
    telemetry_builder = TelemetryBuilder(
        level_id=level_id,
        stage=stage,
        checkpoint_path=checkpoint_path,
        timestamp_start="1970-01-01T00:00:00Z",
    )

    global_step = 0
    for episode_index in range(num_episodes):
        env = FakeCompositionEnv(piece_config, observation_config, reward_strategy, seed=seed * 100_000 + episode_index)
        obs, _ = env.reset()
        total_reward = 0.0
        outcome = "timeout"
        episode_steps = 0

        for _ in range(_MAX_STEPS_PER_EPISODE):
            if rng.random() < epsilon:
                action = rng.choice(_ALL_ACTIONS)
            else:
                action = _good_policy_action(obs, observation_config)

            obs, reward, terminated, truncated, info = env.step(action)
            total_reward += reward
            episode_steps += 1
            global_step += 1

            if terminated or truncated:
                outcome = info.get("outcome", "timeout")
                break

        piece_results, path_trace = env.episode_telemetry()
        for piece_result in piece_results:
            piece_result["seen_in_stage1_range"] = compute_seen_in_stage1_range(
                piece_result["piece_type"], piece_result["params"], piece_config
            )

        telemetry_builder.add_episode(
            episode_index=episode_index,
            outcome=outcome,
            total_reward=total_reward,
            time_to_clear_seconds=float(episode_steps) if outcome == "success" else None,
            path_trace=path_trace,
            piece_results=piece_results,
        )
        reward_curve.append((global_step, total_reward))

        epsilon = max(MIN_EPSILON, epsilon * EPSILON_DECAY_PER_EPISODE)

    last_window = max(1, num_episodes // 10)
    final_mean_reward = sum(r for _, r in reward_curve[-last_window:]) / last_window
    steps_to_converge = compute_steps_to_converge(reward_curve, final_mean_reward)

    return FakeTrainingResult(
        reward_curve=reward_curve,
        telemetry_doc=telemetry_builder.build(),
        final_mean_reward=final_mean_reward,
        training_steps=global_step,
        steps_to_converge=steps_to_converge,
    )
