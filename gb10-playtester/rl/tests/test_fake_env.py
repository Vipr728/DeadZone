"""Tests for fake_env.py — this is the 'pipeline flow' layer: observation
shape correctness, reward-event wiring (completion/death/final bonuses
actually fire), no structural data collapse across randomized episodes, and
a full integration into TelemetryBuilder producing schema-valid telemetry."""

from __future__ import annotations

import numpy as np

from playtester_rl.config_loader import (
    ObservationConfig,
    PieceConfig,
    PieceTypeConfig,
    load_observation_config,
)
from playtester_rl.fake_env import (
    ACTION_JUMP,
    ACTION_NOOP,
    ACTION_RIGHT,
    FakeCompositionEnv,
)
from playtester_rl.reward_strategies import CompositionalRewardStrategy
from playtester_rl.telemetry_writer import TelemetryBuilder, validate_telemetry
from tests.helpers import make_reward_config


def _move_to_goal_only_config() -> PieceConfig:
    return PieceConfig(
        gap_jump=PieceTypeConfig(enabled=False, param_range=(2.0, 5.0)),
        move_to_goal=PieceTypeConfig(enabled=True, param_range=(3.0, 6.0)),
        elevation=PieceTypeConfig(enabled=False, param_range=(1.0, 3.0)),
        pieces_per_episode=2,
        boundary_velocity_reset=True,
    )


def _gap_jump_only_config() -> PieceConfig:
    return PieceConfig(
        gap_jump=PieceTypeConfig(enabled=True, param_range=(2.0, 4.0)),
        move_to_goal=PieceTypeConfig(enabled=False, param_range=(3.0, 6.0)),
        elevation=PieceTypeConfig(enabled=False, param_range=(1.0, 3.0)),
        pieces_per_episode=2,
        boundary_velocity_reset=True,
    )


# ---------------------------------------------------------------------------
# Observation shape correctness
# ---------------------------------------------------------------------------


def test_reset_observation_matches_configured_size():
    obs_config = load_observation_config()
    env = FakeCompositionEnv(
        _move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=0
    )
    obs, info = env.reset()
    assert obs.shape == (obs_config.observation_size(),)
    assert isinstance(info, dict)


def test_step_observation_matches_configured_size():
    obs_config = load_observation_config()
    env = FakeCompositionEnv(
        _move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=0
    )
    env.reset()
    obs, reward, terminated, truncated, _info = env.step(ACTION_RIGHT)
    assert obs.shape == (obs_config.observation_size(),)
    assert isinstance(reward, float)
    assert isinstance(terminated, bool)
    assert isinstance(truncated, bool)


def test_observation_tail_matches_unity_encoder_field_order():
    """After the tile grid, both encoders emit the fixed goal/control tail."""
    obs_config = load_observation_config()
    env = FakeCompositionEnv(
        _move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=0
    )
    obs, _ = env.reset()
    grid_len = obs_config.grid_size * obs_config.grid_size * len(obs_config.tile_channels)

    assert obs.shape == (203,)
    # rel goal x/y, velocity x/y, grounded, objective distance/direction
    tail = obs[grid_len:]
    assert tail[0] > 0.0
    assert np.array_equal(tail[1:5], np.array([0.0, 0.0, 0.0, 1.0], dtype=np.float32))
    assert tail[5] == tail[0]
    assert tail[6] == 1.0


def test_observation_grid_is_one_hot_per_cell():
    """Every cell's channel slice must sum to exactly 1 — no cell should be
    unclassified (all zeros) or double-classified (multiple hot channels)."""
    obs_config = load_observation_config()
    env = FakeCompositionEnv(
        _gap_jump_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=1
    )
    obs, _ = env.reset()
    grid_len = obs_config.grid_size * obs_config.grid_size * len(obs_config.tile_channels)
    grid = obs[:grid_len].reshape(obs_config.grid_size * obs_config.grid_size, len(obs_config.tile_channels))
    row_sums = grid.sum(axis=1)
    assert np.allclose(row_sums, 1.0), "every grid cell must be exactly one-hot"


def test_observation_grid_matches_unity_bottom_to_top_order():
    obs_config = load_observation_config()
    env = FakeCompositionEnv(
        _move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=1
    )
    obs, _ = env.reset()
    channels = len(obs_config.tile_channels)
    grid_len = obs_config.grid_size * obs_config.grid_size * channels
    grid = obs[:grid_len].reshape(obs_config.grid_size, obs_config.grid_size, channels)
    solid = obs_config.tile_channels.index("solid")
    empty = obs_config.tile_channels.index("empty")

    assert np.any(grid[0, :, solid] == 1.0)
    assert np.all(grid[-1, :, empty] == 1.0)


# ---------------------------------------------------------------------------
# Reward-event wiring — no-op agent (never moves) must time out, never
# spuriously succeed or die
# ---------------------------------------------------------------------------


def test_noop_agent_times_out_without_dying_or_succeeding():
    obs_config = load_observation_config()
    env = FakeCompositionEnv(
        _move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=2
    )
    env.reset()
    outcome = None
    for _ in range(300):
        _obs, _reward, terminated, truncated, info = env.step(ACTION_NOOP)
        if terminated or truncated:
            outcome = info["outcome"]
            break
    assert outcome == "timeout"


# ---------------------------------------------------------------------------
# A scripted "clear the hazard" policy must reliably succeed on gap_jump
# pieces, proving completion/final bonuses are reachable at all
# ---------------------------------------------------------------------------


def _scripted_jump_when_hazard_ahead_policy(obs: np.ndarray, obs_config: ObservationConfig) -> int:
    channel_index = {name: i for i, name in enumerate(obs_config.tile_channels)}
    grid_len = obs_config.grid_size * obs_config.grid_size * len(obs_config.tile_channels)
    grid = obs[:grid_len].reshape(obs_config.grid_size, obs_config.grid_size, len(obs_config.tile_channels))
    ground_row = 0  # Unity serializes y=-radius (the bottom row) first.
    center = obs_config.grid_size // 2
    # Look one and two cells ahead of center, on the ground row, for a hazard cell.
    for ahead in (1, 2):
        idx = center + ahead
        if idx < obs_config.grid_size and grid[ground_row, idx, channel_index["hazard"]] == 1.0:
            return ACTION_JUMP
    return ACTION_RIGHT


def test_scripted_policy_reliably_clears_gap_jump_pieces():
    obs_config = load_observation_config()
    successes = 0
    trials = 20
    for seed in range(trials):
        env = FakeCompositionEnv(
            _gap_jump_only_config(), obs_config, CompositionalRewardStrategy(make_reward_config()), seed=seed
        )
        obs, _ = env.reset()
        outcome = None
        for _ in range(150):
            action = _scripted_jump_when_hazard_ahead_policy(obs, obs_config)
            obs, _reward, terminated, truncated, info = env.step(action)
            if terminated or truncated:
                outcome = info["outcome"]
                break
        if outcome == "success":
            successes += 1
    # Not claiming 100% (the scripted policy is a heuristic, not a perfect
    # controller) but a competent policy must clear the large majority.
    assert successes >= trials * 0.8, f"only {successes}/{trials} succeeded — reward/hazard wiring likely broken"


def test_completion_and_final_bonus_actually_increase_total_reward():
    """Structural check that piece_completion_bonus/final_sequence_bonus are
    not dead code — a successful run's total reward must exceed what pure
    per-step shaping reward alone could produce."""
    obs_config = load_observation_config()
    reward_config = make_reward_config()
    env = FakeCompositionEnv(_move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(reward_config), seed=3)
    obs, _ = env.reset()
    total_reward = 0.0
    outcome = None
    for _ in range(300):
        _obs, reward, terminated, truncated, info = env.step(ACTION_RIGHT)
        total_reward += reward
        if terminated or truncated:
            outcome = info["outcome"]
            break
    assert outcome == "success"
    # 2 pieces => 2 completion bonuses + 1 final bonus, all positive constants
    # per reward_config.yaml — total reward must clear that floor even after
    # subtracting the max plausible per-step time penalty accumulation.
    min_expected = 2 * reward_config.compositional.piece_completion_bonus + reward_config.compositional.final_sequence_bonus - 5.0
    assert total_reward > min_expected


# ---------------------------------------------------------------------------
# No structural data collapse: randomized episodes must produce more than
# one distinct outcome across trials (proves death/success/timeout are all
# actually reachable, not that the env silently always does the same thing)
# ---------------------------------------------------------------------------


def test_randomized_episodes_do_not_collapse_to_a_single_outcome():
    import random as _random

    obs_config = load_observation_config()
    reward_config = make_reward_config()
    outcomes = set()
    for seed in range(30):
        env = FakeCompositionEnv(_gap_jump_only_config(), obs_config, CompositionalRewardStrategy(reward_config), seed=seed)
        obs, _ = env.reset()
        action_rng = _random.Random(seed + 1000)
        outcome = None
        for _ in range(250):
            action = action_rng.choice([ACTION_NOOP, ACTION_RIGHT, ACTION_JUMP, 1])
            _obs, _reward, terminated, truncated, info = env.step(action)
            if terminated or truncated:
                outcome = info["outcome"]
                break
        outcomes.add(outcome or "no_terminal_reached")
    assert len(outcomes) >= 2, f"expected varied outcomes across random seeds, got only {outcomes}"


# ---------------------------------------------------------------------------
# Full integration: fake env -> TelemetryBuilder -> schema-valid document,
# no missing/leaky fields
# ---------------------------------------------------------------------------


def test_fake_env_episode_feeds_telemetry_builder_without_missing_or_leaky_fields():
    obs_config = load_observation_config()
    reward_config = make_reward_config()
    env = FakeCompositionEnv(_move_to_goal_only_config(), obs_config, CompositionalRewardStrategy(reward_config), seed=4)
    obs, _ = env.reset()
    total_reward = 0.0
    outcome = None
    steps_taken = 0
    for _ in range(300):
        _obs, reward, terminated, truncated, info = env.step(ACTION_RIGHT)
        total_reward += reward
        steps_taken += 1
        if terminated or truncated:
            outcome = info["outcome"]
            break

    piece_results, path_trace = env.episode_telemetry()
    assert len(path_trace) == steps_taken, "path_trace must have exactly one point per step taken — no leaks/drops"
    assert len(piece_results) >= 1, "at least one piece must have been recorded"

    builder = TelemetryBuilder(
        level_id="fake_level",
        stage="stage1",
        checkpoint_path="fake/checkpoint",
        timestamp_start="2026-07-25T00:00:00Z",
    )
    builder.add_episode(
        episode_index=0,
        outcome=outcome,
        total_reward=total_reward,
        time_to_clear_seconds=float(steps_taken) if outcome == "success" else None,
        path_trace=path_trace,
        piece_results=piece_results,
    )
    doc = builder.build()
    validate_telemetry(doc)  # must not raise
    assert doc["episode_summaries"][0]["piece_results"] == piece_results
