"""Tests for fake_trainer.py — the structural claim under test is 'warm_start
produces measurably faster convergence than cold start' (the same shape Gate
2 checks for), plus basic sanity on curve length/telemetry validity."""

from __future__ import annotations

from playtester_rl.config_loader import (
    PieceConfig,
    PieceTypeConfig,
    load_observation_config,
)
from playtester_rl.fake_trainer import run_fake_training
from tests.helpers import make_reward_config


def _piece_config() -> PieceConfig:
    return PieceConfig(
        gap_jump=PieceTypeConfig(enabled=True, param_range=(2.0, 4.0)),
        move_to_goal=PieceTypeConfig(enabled=True, param_range=(3.0, 6.0)),
        elevation=PieceTypeConfig(enabled=False, param_range=(1.0, 3.0)),
        pieces_per_episode=2,
        boundary_velocity_reset=True,
    )


def test_reward_curve_has_one_point_per_episode():
    result = run_fake_training(
        level_id="test_level",
        stage="stage1",
        checkpoint_path="fake/ckpt",
        piece_config=_piece_config(),
        reward_config=make_reward_config(),
        observation_config=load_observation_config(),
        num_episodes=20,
        warm_start=False,
        seed=1,
    )
    assert len(result.reward_curve) == 20
    assert len(result.telemetry_doc["episode_summaries"]) == 20


def test_telemetry_doc_is_schema_valid_by_construction():
    # TelemetryBuilder.build() validates internally — if this doesn't raise,
    # the fake trainer's output is guaranteed schema-conformant.
    result = run_fake_training(
        level_id="test_level",
        stage="stage1",
        checkpoint_path="fake/ckpt",
        piece_config=_piece_config(),
        reward_config=make_reward_config(),
        observation_config=load_observation_config(),
        num_episodes=10,
        warm_start=False,
        seed=2,
    )
    assert result.telemetry_doc["level_id"] == "test_level"


def test_warm_start_converges_no_slower_than_cold_start():
    """The load-bearing structural claim: warm_start=True (standing in for a
    Stage 1 checkpoint) must reach convergence in no more steps than
    warm_start=False (cold start) on the same task/seed — this is exactly
    the comparison Gate 2 makes, so the fake trainer must actually produce
    it, not just assert it."""
    common_kwargs = {
        "level_id": "test_level",
        "checkpoint_path": "fake/ckpt",
        "piece_config": _piece_config(),
        "reward_config": make_reward_config(),
        "observation_config": load_observation_config(),
        "num_episodes": 60,
        "seed": 7,
    }
    warm = run_fake_training(stage="stage2", warm_start=True, **common_kwargs)
    cold = run_fake_training(stage="stage1", warm_start=False, **common_kwargs)

    assert warm.steps_to_converge is not None, "warm start should reliably converge within the episode budget"
    assert cold.steps_to_converge is not None, "cold start should also converge (just slower) within this budget"
    assert warm.steps_to_converge < cold.steps_to_converge


def _harder_gap_jump_only_config() -> PieceConfig:
    # A 2-piece easy task saturates near-max reward almost immediately even
    # under a mostly-random policy (verified empirically), which masks any
    # epsilon-driven improvement in a first-block-vs-last-block comparison.
    # 3 gap-jump-only pieces gives random actions much lower odds of a lucky
    # clean run, so the epsilon decay's effect on mean reward is visible.
    return PieceConfig(
        gap_jump=PieceTypeConfig(enabled=True, param_range=(2.0, 4.0)),
        move_to_goal=PieceTypeConfig(enabled=False, param_range=(3.0, 6.0)),
        elevation=PieceTypeConfig(enabled=False, param_range=(1.0, 3.0)),
        pieces_per_episode=3,
        boundary_velocity_reset=True,
    )


def test_mean_reward_improves_from_first_block_to_last_block():
    """Structural check that the simulated 'training' actually trends
    upward over the run (epsilon decaying toward the scripted-good policy)
    rather than being flat noise from start to finish — compares the mean
    of the first 10% of episodes against the last 10%, directly, rather
    than coupling this test to gate1_check's specific windowing default."""
    result = run_fake_training(
        level_id="test_level",
        stage="stage1",
        checkpoint_path="fake/ckpt",
        piece_config=_harder_gap_jump_only_config(),
        reward_config=make_reward_config(),
        observation_config=load_observation_config(),
        num_episodes=150,
        warm_start=False,
        seed=3,
    )
    n = len(result.reward_curve)
    block = max(1, n // 10)
    first_block_mean = sum(r for _, r in result.reward_curve[:block]) / block
    last_block_mean = sum(r for _, r in result.reward_curve[-block:]) / block
    assert last_block_mean > first_block_mean, (
        f"expected reward to improve from first block ({first_block_mean}) "
        f"to last block ({last_block_mean}) as epsilon decays"
    )
