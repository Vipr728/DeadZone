"""Tests for mvp_policy_gradient.py — the real learnability stress test.

Unlike fake_trainer.py (a hand-scheduled epsilon heuristic that "improves"
by construction), this trains an actual randomly-initialized linear-softmax
policy via real backpropagation (REINFORCE) against the exact
observation/action/reward contracts the real Unity/ML-Agents PPO agent will
use. If these tests pass, a structural data/wiring problem (degenerate
observations, an unreachable reward, a broken action mapping) is NOT what
would sink real training — that was the actual concern being tested here.

Note on "loss going down": vanilla policy gradient has no single scalar loss
that monotonically decreases the way a supervised loss does (the "loss" is
just -log-prob weighted by return, which isn't a convergence signal on its
own). The metric that plays that role here is the policy gradient's norm:
as the policy approaches a local optimum, that gradient (and hence the
weight update from batch to batch) should shrink, which is asserted below
alongside reward improvement.
"""

from __future__ import annotations

import numpy as np

from playtester_rl.config_loader import (
    PieceConfig,
    PieceTypeConfig,
    load_observation_config,
    load_piece_config,
)
from playtester_rl.mvp_policy_gradient import train_policy_gradient
from tests.helpers import make_reward_config


def _mean_of_block(progress, start_frac: float, end_frac: float, field: str) -> float:
    n = len(progress)
    start, end = int(n * start_frac), int(n * end_frac)
    values = [getattr(p, field) for p in progress[start:end]]
    return sum(values) / len(values)


def test_policy_gradient_learns_on_the_real_shipped_default_config():
    """The exact config the hackathon build ships with (rl/configs/*.yaml) —
    gap_jump + move_to_goal enabled, elevation off, 3 pieces/episode."""
    piece_config = load_piece_config()
    reward_config = make_reward_config()
    obs_config = load_observation_config()

    policy, progress = train_policy_gradient(
        piece_config, reward_config, obs_config, num_batches=60, episodes_per_batch=16, seed=0, learning_rate=0.1
    )

    first_block_reward = _mean_of_block(progress, 0.0, 0.1, "mean_batch_reward")
    last_block_reward = _mean_of_block(progress, 0.7, 1.0, "mean_batch_reward")
    assert last_block_reward > first_block_reward + 2.0, (
        f"expected a real gradient-trained policy to substantially improve reward "
        f"(first block {first_block_reward:.2f} -> last block {last_block_reward:.2f}) — "
        f"if this fails, something in the observation/reward/action wiring is not "
        f"learnable, independent of any real training algorithm's quality"
    )

    first_block_grad = _mean_of_block(progress, 0.0, 0.1, "mean_grad_norm")
    last_block_grad = _mean_of_block(progress, 0.7, 1.0, "mean_grad_norm")
    assert last_block_grad < first_block_grad, (
        "expected the policy gradient's norm to shrink as the policy approaches "
        "a local optimum (the policy-gradient analog of 'loss decreasing') — "
        f"got first block {first_block_grad:.4f} -> last block {last_block_grad:.4f}"
    )

    assert all(np.isfinite(p.mean_batch_reward) and np.isfinite(p.mean_grad_norm) for p in progress)
    assert np.all(np.isfinite(policy.W)) and np.all(np.isfinite(policy.b))


def test_policy_gradient_learns_on_harder_gap_jump_only_config():
    """A harder task (no move_to_goal escape hatch, 3 gap-jumps in a row) —
    starts from a WORSE (often negative) random-policy baseline, so this
    specifically checks learning still happens even when the random-init
    starting point is bad, not just when the task is already easy."""
    hard_config = PieceConfig(
        gap_jump=PieceTypeConfig(enabled=True, param_range=(2.0, 4.0)),
        move_to_goal=PieceTypeConfig(enabled=False, param_range=(3.0, 6.0)),
        elevation=PieceTypeConfig(enabled=False, param_range=(1.0, 3.0)),
        pieces_per_episode=3,
        boundary_velocity_reset=True,
    )
    reward_config = make_reward_config()
    obs_config = load_observation_config()

    _policy, progress = train_policy_gradient(
        hard_config, reward_config, obs_config, num_batches=60, episodes_per_batch=16, seed=1, learning_rate=0.1
    )

    # Learning happens fast on these tiny tasks (observed empirically:
    # convergence within ~10 batches) — comparing against the very first
    # batch's reward (not a smoothed first-10% block, which already includes
    # some post-convergence batches) is the fair "did it start bad and end
    # good" comparison.
    first_batch_reward = progress[0].mean_batch_reward
    last_block_reward = _mean_of_block(progress, 0.7, 1.0, "mean_batch_reward")
    assert last_block_reward > first_batch_reward + 3.0
    assert all(np.isfinite(p.mean_batch_reward) for p in progress)


def test_policy_gradient_learns_with_elevation_piece_enabled():
    """Elevation is disabled by default (piece_config.yaml's feature flag) —
    verified here anyway so flipping that flag on later doesn't silently
    introduce an unlearnable/broken observation-encoding path that nobody
    noticed because the default config never exercised it."""
    elevation_config = PieceConfig(
        gap_jump=PieceTypeConfig(enabled=True, param_range=(2.0, 4.0)),
        move_to_goal=PieceTypeConfig(enabled=True, param_range=(3.0, 6.0)),
        elevation=PieceTypeConfig(enabled=True, param_range=(1.0, 3.0)),
        pieces_per_episode=3,
        boundary_velocity_reset=True,
    )
    reward_config = make_reward_config()
    obs_config = load_observation_config()

    _policy, progress = train_policy_gradient(
        elevation_config, reward_config, obs_config, num_batches=60, episodes_per_batch=16, seed=2, learning_rate=0.1
    )

    first_batch_reward = progress[0].mean_batch_reward
    last_block_reward = _mean_of_block(progress, 0.7, 1.0, "mean_batch_reward")
    assert last_block_reward > first_batch_reward + 2.0
    assert all(np.isfinite(p.mean_batch_reward) for p in progress)


def test_observation_vectors_fed_to_the_policy_are_never_degenerate():
    """A structural sanity check independent of whether learning succeeds:
    across a real rollout, observations must actually vary (not be a
    constant vector regardless of state) — a constant observation would make
    ANY learning algorithm structurally incapable of learning, regardless of
    reward shape."""
    from playtester_rl.fake_env import ACTION_JUMP, ACTION_RIGHT, FakeCompositionEnv

    piece_config = load_piece_config()
    obs_config = load_observation_config()
    reward_strategy = __import__("playtester_rl.reward_strategies", fromlist=["create_reward_strategy"]).create_reward_strategy(
        make_reward_config()
    )
    env = FakeCompositionEnv(piece_config, obs_config, reward_strategy, seed=5)
    obs, _ = env.reset()

    seen_observations = [obs.copy()]
    for i in range(30):
        action = ACTION_JUMP if i % 3 == 0 else ACTION_RIGHT
        obs, _reward, terminated, truncated, _info = env.step(action)
        seen_observations.append(obs.copy())
        if terminated or truncated:
            break

    distinct_count = len({tuple(o.round(6)) for o in seen_observations})
    assert distinct_count > 1, "observations never changed across 30 steps — this would block any learning algorithm"
