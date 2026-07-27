"""Typed loaders + validation for rl/configs/*.yaml.

Every tunable numeric/behavioral value in this package lives in one of the YAML
files under rl/configs/ (PRD.md §1 modularity rule: no magic numbers in code).
This module is the single place that parses those files into validated,
strongly-typed objects — reward strategies, the fake env, and gate evaluation
all consume these types rather than re-parsing YAML themselves.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

import yaml

CONFIGS_DIR = Path(__file__).resolve().parent.parent.parent / "configs"


class ConfigValidationError(ValueError):
    """Raised when a config file's values are internally inconsistent
    (e.g. a range where min > max, an unknown enum value)."""


def _load_yaml(path: Path) -> dict[str, Any]:
    if not path.exists():
        raise FileNotFoundError(f"Config file not found: {path}")
    with open(path, encoding="utf-8") as f:
        data = yaml.safe_load(f)
    if not isinstance(data, dict):
        raise ConfigValidationError(f"{path} did not parse to a mapping")
    return data


def _validate_range(name: str, rng: list[float]) -> tuple[float, float]:
    if len(rng) != 2:
        raise ConfigValidationError(f"{name} must be a [min, max] pair, got {rng!r}")
    lo, hi = float(rng[0]), float(rng[1])
    if lo > hi:
        raise ConfigValidationError(f"{name} min ({lo}) is greater than max ({hi})")
    if lo < 0 or hi < 0:
        raise ConfigValidationError(f"{name} must be non-negative, got {rng!r}")
    return lo, hi


# ---------------------------------------------------------------------------
# Piece config
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class PieceTypeConfig:
    enabled: bool
    param_range: tuple[float, float]


@dataclass(frozen=True)
class PieceConfig:
    gap_jump: PieceTypeConfig
    move_to_goal: PieceTypeConfig
    elevation: PieceTypeConfig
    pieces_per_episode: int
    boundary_velocity_reset: bool

    def enabled_piece_types(self) -> list[str]:
        types = []
        if self.gap_jump.enabled:
            types.append("gap_jump")
        if self.move_to_goal.enabled:
            types.append("move_to_goal")
        if self.elevation.enabled:
            types.append("elevation")
        return types


def load_piece_config(path: Path | None = None) -> PieceConfig:
    raw = _load_yaml(path or CONFIGS_DIR / "piece_config.yaml")
    pieces = raw.get("pieces", {})
    composition = raw.get("composition", {})

    gap_jump_raw = pieces.get("gap_jump", {})
    move_to_goal_raw = pieces.get("move_to_goal", {})
    elevation_raw = pieces.get("elevation", {})

    gap_jump = PieceTypeConfig(
        enabled=bool(gap_jump_raw.get("enabled", False)),
        param_range=_validate_range("pieces.gap_jump.width_range", gap_jump_raw.get("width_range", [0, 0])),
    )
    move_to_goal = PieceTypeConfig(
        enabled=bool(move_to_goal_raw.get("enabled", False)),
        param_range=_validate_range(
            "pieces.move_to_goal.distance_range", move_to_goal_raw.get("distance_range", [0, 0])
        ),
    )
    elevation = PieceTypeConfig(
        enabled=bool(elevation_raw.get("enabled", False)),
        param_range=_validate_range("pieces.elevation.height_range", elevation_raw.get("height_range", [0, 0])),
    )

    pieces_per_episode = int(composition.get("pieces_per_episode", 3))
    if pieces_per_episode < 1:
        raise ConfigValidationError("composition.pieces_per_episode must be >= 1")

    config = PieceConfig(
        gap_jump=gap_jump,
        move_to_goal=move_to_goal,
        elevation=elevation,
        pieces_per_episode=pieces_per_episode,
        boundary_velocity_reset=bool(composition.get("boundary_velocity_reset", True)),
    )

    if not config.enabled_piece_types():
        raise ConfigValidationError("At least one piece type must be enabled in piece_config.yaml")

    return config


# ---------------------------------------------------------------------------
# Reward config
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class CompositionalRewardParams:
    progress_reward_scale: float
    time_penalty: float
    piece_completion_bonus: float
    final_sequence_bonus: float
    death_penalty: float


@dataclass(frozen=True)
class SingleGymFallbackParams:
    progress_reward_scale: float
    time_penalty: float
    completion_bonus: float
    death_penalty: float


@dataclass(frozen=True)
class RewardConfig:
    active_strategy: str
    compositional: CompositionalRewardParams
    single_gym_fallback: SingleGymFallbackParams
    max_steps: int


_VALID_STRATEGIES = {"compositional", "single_gym_fallback"}


def load_reward_config(path: Path | None = None) -> RewardConfig:
    raw = _load_yaml(path or CONFIGS_DIR / "reward_config.yaml")

    active_strategy = raw.get("active_strategy")
    if active_strategy not in _VALID_STRATEGIES:
        raise ConfigValidationError(
            f"reward_config.yaml active_strategy must be one of {_VALID_STRATEGIES}, got {active_strategy!r}"
        )

    comp_raw = raw.get("compositional", {})
    compositional = CompositionalRewardParams(
        progress_reward_scale=float(comp_raw["progress_reward_scale"]),
        time_penalty=float(comp_raw["time_penalty"]),
        piece_completion_bonus=float(comp_raw["piece_completion_bonus"]),
        final_sequence_bonus=float(comp_raw["final_sequence_bonus"]),
        death_penalty=float(comp_raw["death_penalty"]),
    )

    fallback_raw = raw.get("single_gym_fallback", {})
    single_gym_fallback = SingleGymFallbackParams(
        progress_reward_scale=float(fallback_raw["progress_reward_scale"]),
        time_penalty=float(fallback_raw["time_penalty"]),
        completion_bonus=float(fallback_raw["completion_bonus"]),
        death_penalty=float(fallback_raw["death_penalty"]),
    )

    max_steps = int(raw.get("episode", {}).get("max_steps", 1000))
    if max_steps < 1:
        raise ConfigValidationError("episode.max_steps must be >= 1")

    return RewardConfig(
        active_strategy=active_strategy,
        compositional=compositional,
        single_gym_fallback=single_gym_fallback,
        max_steps=max_steps,
    )


# ---------------------------------------------------------------------------
# Observation config
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class ObservationConfig:
    grid_size: int
    tile_channels: tuple[str, ...]
    include_velocity: bool
    include_grounded_flag: bool

    def observation_size(self) -> int:
        """Total flattened observation vector length for this config —
        used to validate encoder output shape in tests and by CollectObservations
        callers that need to size a VectorSensor ahead of time."""
        grid_floats = self.grid_size * self.grid_size * len(self.tile_channels)
        # Keep this formula identical to GridObservationEncoder.Encode:
        # tile grid, goal-relative x/y, velocity x/y, grounded, then
        # objective distance/direction. The master spec §2.4 requires both
        # goal-relative and objective signals across both stages.
        extra = 2
        extra += 2 if self.include_velocity else 0
        extra += 1 if self.include_grounded_flag else 0
        extra += 2
        return grid_floats + extra


def load_observation_config(path: Path | None = None) -> ObservationConfig:
    raw = _load_yaml(path or CONFIGS_DIR / "observation_config.yaml")

    grid_size = int(raw.get("grid_size", 0))
    if grid_size < 1 or grid_size % 2 == 0:
        raise ConfigValidationError(f"observation_config.yaml grid_size must be a positive odd integer, got {grid_size}")

    tile_channels = tuple(raw.get("tile_channels", []))
    if not tile_channels:
        raise ConfigValidationError("observation_config.yaml tile_channels must be non-empty")

    return ObservationConfig(
        grid_size=grid_size,
        tile_channels=tile_channels,
        include_velocity=bool(raw.get("include_velocity", True)),
        include_grounded_flag=bool(raw.get("include_grounded_flag", True)),
    )


# ---------------------------------------------------------------------------
# Training config (pass-through, mlagents-native shape — validated shallowly)
# ---------------------------------------------------------------------------


def load_training_config(path: Path | None = None) -> dict[str, Any]:
    """Returns the raw dict — this file's shape is dictated by mlagents-learn's
    own schema, not ours, so we only sanity-check the fields our own scripts
    read (num_envs) rather than re-implementing ML-Agents' schema."""
    raw = _load_yaml(path or CONFIGS_DIR / "training_config.yaml")

    num_envs = raw.get("env_settings", {}).get("num_envs")
    if not isinstance(num_envs, int) or num_envs < 1:
        raise ConfigValidationError(f"training_config.yaml env_settings.num_envs must be a positive int, got {num_envs!r}")

    if "behaviors" not in raw or not raw["behaviors"]:
        raise ConfigValidationError("training_config.yaml must define at least one entry under 'behaviors'")

    return raw
