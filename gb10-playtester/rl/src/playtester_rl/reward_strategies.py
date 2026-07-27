"""Reward strategies — prd-ml.md §2.

IRewardStrategy is the interface the training loop (and, on the Unity side, the
mirrored C# IRewardStrategy in RewardStrategies.cs) calls to get every reward
value. Swapping strategies (e.g. the Gate 1 fallback from compositional to
single-gym) is a `reward_config.yaml` edit — `active_strategy: single_gym_fallback`
— consumed by `create_reward_strategy()`, never a code change at any call site.
"""

from __future__ import annotations

from typing import Protocol

from playtester_rl.config_loader import RewardConfig


class IRewardStrategy(Protocol):
    """Every reward-relevant event in one episode, per spec §2.3's dense-reward
    table. Implementations must not read config directly outside their own
    __init__ — call sites only ever see this interface."""

    def piece_progress_reward(self, delta_progress: float) -> float:
        """Per-step progress toward current piece's local goal. delta_progress
        is the signed change in distance-to-goal since the previous step
        (positive = moved toward goal). Reward for backing up is always 0,
        never negative — this is a shaping reward, not a punishment channel."""
        ...

    def step_time_penalty(self) -> float:
        """Per-step constant penalty, discourages stalling."""
        ...

    def piece_completion_bonus(self) -> float:
        """Fires once, immediately on completion of the current piece."""
        ...

    def final_sequence_bonus(self) -> float:
        """Fires once, on completion of the full composed sequence (the final piece)."""
        ...

    def death_penalty(self) -> float:
        """Fires on death/fall/hazard contact. Caller is responsible for also
        calling EndEpisode() — this strategy only returns the reward magnitude."""
        ...

    def max_episode_steps(self) -> int:
        """Episode step cap before forced timeout."""
        ...


class CompositionalRewardStrategy(IRewardStrategy):
    """Stage 1/2 default — dense per-piece reward across a composed sequence
    of pieces, per spec §2.3. Loads reward_config.yaml's 'compositional' block."""

    def __init__(self, config: RewardConfig) -> None:
        if config.active_strategy != "compositional":
            # Defensive: this strategy is only meaningful when selected, but we
            # don't hard-require it so tests can construct it directly against
            # any RewardConfig's `.compositional` params.
            pass
        self._params = config.compositional
        self._max_steps = config.max_steps

    def piece_progress_reward(self, delta_progress: float) -> float:
        return max(0.0, delta_progress) * self._params.progress_reward_scale

    def step_time_penalty(self) -> float:
        return self._params.time_penalty

    def piece_completion_bonus(self) -> float:
        return self._params.piece_completion_bonus

    def final_sequence_bonus(self) -> float:
        return self._params.final_sequence_bonus

    def death_penalty(self) -> float:
        return self._params.death_penalty

    def max_episode_steps(self) -> int:
        return self._max_steps


class SingleGymFallbackStrategy(IRewardStrategy):
    """Gate 1 fallback (spec §7) — a single mechanic type randomized per
    episode, no piece composition. Because there is exactly one piece per
    episode in this design, completing it *is* completing the sequence:
    piece_completion_bonus() and final_sequence_bonus() intentionally return
    the same value (there is no separate 'final piece' event to distinguish).
    Loads reward_config.yaml's 'single_gym_fallback' block."""

    def __init__(self, config: RewardConfig) -> None:
        self._params = config.single_gym_fallback
        self._max_steps = config.max_steps

    def piece_progress_reward(self, delta_progress: float) -> float:
        return max(0.0, delta_progress) * self._params.progress_reward_scale

    def step_time_penalty(self) -> float:
        return self._params.time_penalty

    def piece_completion_bonus(self) -> float:
        return self._params.completion_bonus

    def final_sequence_bonus(self) -> float:
        return self._params.completion_bonus

    def death_penalty(self) -> float:
        return self._params.death_penalty

    def max_episode_steps(self) -> int:
        return self._max_steps


_STRATEGY_REGISTRY = {
    "compositional": CompositionalRewardStrategy,
    "single_gym_fallback": SingleGymFallbackStrategy,
}


def create_reward_strategy(config: RewardConfig) -> IRewardStrategy:
    """The single call site that turns reward_config.yaml's `active_strategy`
    field into a concrete strategy instance. This function is the entire
    Gate 1 fallback mechanism (PRD.md §1, §5): edit the YAML, everything
    downstream picks it up automatically."""
    strategy_cls = _STRATEGY_REGISTRY[config.active_strategy]
    return strategy_cls(config)
