"""Tests for reward_strategies.py — known-input/known-output unit tests, plus
the structural guarantee that a config edit (active_strategy) changes behavior
without any code change (the Gate 1 fallback mechanism)."""

from __future__ import annotations

from playtester_rl.config_loader import load_reward_config
from playtester_rl.reward_strategies import (
    CompositionalRewardStrategy,
    SingleGymFallbackStrategy,
    create_reward_strategy,
)
from tests.helpers import make_reward_config as _make_config

# ---------------------------------------------------------------------------
# CompositionalRewardStrategy — known input -> known output
# ---------------------------------------------------------------------------


def test_compositional_progress_reward_scales_linearly():
    strategy = CompositionalRewardStrategy(_make_config())
    assert strategy.piece_progress_reward(1.0) == 0.01
    assert strategy.piece_progress_reward(2.0) == 0.02


def test_compositional_progress_reward_clamps_negative_to_zero():
    strategy = CompositionalRewardStrategy(_make_config())
    # Backing up must never produce a negative reward per the shaping-reward design.
    assert strategy.piece_progress_reward(-5.0) == 0.0


def test_compositional_fixed_events():
    strategy = CompositionalRewardStrategy(_make_config())
    assert strategy.step_time_penalty() == -0.001
    assert strategy.piece_completion_bonus() == 1.0
    assert strategy.final_sequence_bonus() == 5.0
    assert strategy.death_penalty() == -1.0
    assert strategy.max_episode_steps() == 1000


def test_compositional_final_bonus_exceeds_piece_bonus():
    # Structural sanity check on the shipped defaults: finishing the whole
    # sequence must be worth strictly more than finishing one piece, or the
    # agent has no incentive gradient toward full completion.
    strategy = CompositionalRewardStrategy(_make_config())
    assert strategy.final_sequence_bonus() > strategy.piece_completion_bonus()


# ---------------------------------------------------------------------------
# SingleGymFallbackStrategy
# ---------------------------------------------------------------------------


def test_single_gym_fallback_events():
    strategy = SingleGymFallbackStrategy(_make_config())
    assert strategy.piece_progress_reward(1.0) == 0.02
    assert strategy.piece_progress_reward(-1.0) == 0.0
    assert strategy.step_time_penalty() == -0.002
    assert strategy.death_penalty() == -3.0


def test_single_gym_fallback_piece_and_final_bonus_are_the_same_event():
    # By design (no composition in the fallback), completing the one piece
    # IS completing the sequence — both accessors must agree.
    strategy = SingleGymFallbackStrategy(_make_config())
    assert strategy.piece_completion_bonus() == strategy.final_sequence_bonus() == 2.0


# ---------------------------------------------------------------------------
# Factory / config-swap mechanism — this IS the Gate 1 fallback
# ---------------------------------------------------------------------------


def test_factory_selects_compositional_by_default():
    strategy = create_reward_strategy(_make_config("compositional"))
    assert isinstance(strategy, CompositionalRewardStrategy)
    assert strategy.piece_completion_bonus() == 1.0


def test_factory_selects_fallback_on_config_flip():
    strategy = create_reward_strategy(_make_config("single_gym_fallback"))
    assert isinstance(strategy, SingleGymFallbackStrategy)
    assert strategy.piece_completion_bonus() == 2.0


def test_config_swap_changes_behavior_with_zero_code_changes():
    """The load-bearing test: same config object shape, one field flipped,
    different runtime behavior — proving the fallback really is config-only."""
    compositional_run = create_reward_strategy(_make_config("compositional"))
    fallback_run = create_reward_strategy(_make_config("single_gym_fallback"))
    assert compositional_run.death_penalty() != fallback_run.death_penalty()
    assert type(compositional_run) is not type(fallback_run)


# ---------------------------------------------------------------------------
# Real shipped reward_config.yaml wired through the real factory
# ---------------------------------------------------------------------------


def test_real_config_produces_a_working_strategy():
    config = load_reward_config()
    strategy = create_reward_strategy(config)
    # Whatever strategy is active, it must satisfy basic reward-shape invariants.
    assert strategy.step_time_penalty() < 0
    assert strategy.death_penalty() < 0
    assert strategy.piece_completion_bonus() > 0
    assert strategy.piece_progress_reward(-1.0) == 0.0
