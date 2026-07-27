"""Tests for config_loader.py — both 'the real shipped configs parse and are
internally consistent' and 'bad configs are rejected, not silently accepted'."""

from __future__ import annotations

import textwrap
from pathlib import Path

import pytest

from playtester_rl.config_loader import (
    ConfigValidationError,
    load_observation_config,
    load_piece_config,
    load_reward_config,
    load_training_config,
)

# ---------------------------------------------------------------------------
# Real shipped configs
# ---------------------------------------------------------------------------


def test_real_piece_config_loads_and_is_consistent():
    cfg = load_piece_config()
    assert cfg.pieces_per_episode == 3
    assert cfg.boundary_velocity_reset is True
    assert "gap_jump" in cfg.enabled_piece_types()
    assert "move_to_goal" in cfg.enabled_piece_types()
    # elevation is locked disabled by default per the feature-flag design
    assert "elevation" not in cfg.enabled_piece_types()
    lo, hi = cfg.gap_jump.param_range
    assert lo < hi


def test_real_reward_config_loads_and_is_consistent():
    cfg = load_reward_config()
    assert cfg.active_strategy == "compositional"
    assert cfg.compositional.piece_completion_bonus > 0
    assert cfg.compositional.death_penalty < 0
    assert cfg.max_steps > 0


def test_real_observation_config_loads_and_is_consistent():
    cfg = load_observation_config()
    assert cfg.grid_size % 2 == 1
    assert len(cfg.tile_channels) == 4
    # Master spec §2.4: 7*7*4 tile values + goal-relative x/y + velocity
    # x/y + grounded + objective distance/direction.
    assert cfg.observation_size() == 203


def test_real_training_config_loads_and_is_consistent():
    cfg = load_training_config()
    assert cfg["env_settings"]["num_envs"] >= 1
    assert "PlaytestAgent" in cfg["behaviors"]


# ---------------------------------------------------------------------------
# Rejection of malformed configs — these are the "structural" tests: a
# hand-crafted bad config must fail loudly, not produce a silently-wrong object.
# ---------------------------------------------------------------------------


def _write(tmp_path: Path, name: str, content: str) -> Path:
    p = tmp_path / name
    p.write_text(textwrap.dedent(content), encoding="utf-8")
    return p


def test_piece_config_rejects_inverted_range(tmp_path):
    path = _write(
        tmp_path,
        "bad_piece.yaml",
        """
        pieces:
          gap_jump:
            enabled: true
            width_range: [5.0, 2.0]
          move_to_goal:
            enabled: true
            distance_range: [4.0, 10.0]
          elevation:
            enabled: false
            height_range: [1.0, 3.0]
        composition:
          pieces_per_episode: 3
          boundary_velocity_reset: true
        """,
    )
    with pytest.raises(ConfigValidationError, match="greater than max"):
        load_piece_config(path)


def test_piece_config_rejects_all_disabled(tmp_path):
    path = _write(
        tmp_path,
        "bad_piece.yaml",
        """
        pieces:
          gap_jump:
            enabled: false
            width_range: [2.0, 5.0]
          move_to_goal:
            enabled: false
            distance_range: [4.0, 10.0]
          elevation:
            enabled: false
            height_range: [1.0, 3.0]
        composition:
          pieces_per_episode: 3
          boundary_velocity_reset: true
        """,
    )
    with pytest.raises(ConfigValidationError, match="At least one piece type"):
        load_piece_config(path)


def test_reward_config_rejects_unknown_strategy(tmp_path):
    path = _write(
        tmp_path,
        "bad_reward.yaml",
        """
        active_strategy: made_up_strategy
        compositional:
          progress_reward_scale: 0.01
          time_penalty: -0.001
          piece_completion_bonus: 1.0
          final_sequence_bonus: 5.0
          death_penalty: -1.0
        single_gym_fallback:
          progress_reward_scale: 0.01
          time_penalty: -0.001
          completion_bonus: 1.0
          death_penalty: -1.0
        episode:
          max_steps: 1000
        """,
    )
    with pytest.raises(ConfigValidationError, match="active_strategy"):
        load_reward_config(path)


def test_observation_config_rejects_even_grid_size(tmp_path):
    path = _write(
        tmp_path,
        "bad_obs.yaml",
        """
        grid_size: 8
        tile_channels: [empty, solid, hazard, goal]
        include_velocity: true
        include_grounded_flag: true
        """,
    )
    with pytest.raises(ConfigValidationError, match="odd"):
        load_observation_config(path)


def test_training_config_rejects_missing_behaviors(tmp_path):
    path = _write(
        tmp_path,
        "bad_training.yaml",
        """
        behaviors: {}
        env_settings:
          num_envs: 1
        """,
    )
    with pytest.raises(ConfigValidationError, match="behaviors"):
        load_training_config(path)


def test_config_file_not_found_raises(tmp_path):
    with pytest.raises(FileNotFoundError):
        load_piece_config(tmp_path / "does_not_exist.yaml")
