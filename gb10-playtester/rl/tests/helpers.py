"""Shared test fixtures/helpers used across multiple test modules."""

from __future__ import annotations

from playtester_rl.config_loader import (
    CompositionalRewardParams,
    RewardConfig,
    SingleGymFallbackParams,
)


def make_reward_config(active_strategy: str = "compositional") -> RewardConfig:
    """A hand-built RewardConfig with known values, independent of whatever
    reward_config.yaml currently contains — used wherever a test needs a
    reward config with values it can assert on precisely."""
    return RewardConfig(
        active_strategy=active_strategy,
        compositional=CompositionalRewardParams(
            progress_reward_scale=0.01,
            time_penalty=-0.001,
            piece_completion_bonus=1.0,
            final_sequence_bonus=5.0,
            death_penalty=-1.0,
        ),
        single_gym_fallback=SingleGymFallbackParams(
            progress_reward_scale=0.02,
            time_penalty=-0.002,
            completion_bonus=2.0,
            death_penalty=-3.0,
        ),
        max_steps=1000,
    )
