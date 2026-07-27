"""Fake piece-composition environment — pure Python, no Unity/ML-Agents
required. See rl/README.md "Fake environment for pipeline testing".

This is a **test harness**, not a training environment: its job is to let the
rest of this package (config loading, reward strategies, telemetry writing,
gate evaluation) be exercised end-to-end and prove the wiring is correct
(rewards fire on the right events, telemetry has no missing/leaky fields,
episodes actually vary instead of collapsing to one constant outcome) without
needing a real Unity build. When the real Unity env exists, it plugs into the
same reward-strategy/telemetry contracts and this module stops being load-bearing
for anything except tests.

Simplifications, all deliberate and documented (not oversights):
  - 1D world. "Height" (for elevation pieces) is treated the same as gap width
    — a hazard span the agent must be airborne to cross. This collapses two
    piece types into one mechanic for the fake env only; it does not change
    the real config/reward contracts either piece type uses.
  - A single discrete jump action grants a fixed airborne window (see
    JUMP_AIRBORNE_STEPS) during which the agent also moves forward at
    MOVE_SPEED — i.e. "jump" is jump-and-move-forward, matching the real
    game's expected control feel (you jump *while* moving into a gap, you
    don't jump in place). This number is picked so it can clear the widest
    gap in piece_config.yaml's default range (see the constant's docstring).
  - "attempts" per piece (telemetry field) is defined here as the number of
    jump actions issued while inside that piece's hazard span before either
    clearing it or dying — a defensible interpretation for a test harness,
    not a mandate for how the real Unity TelemetryRecorder must define it.
"""

from __future__ import annotations

import random
from dataclasses import dataclass, field
from typing import Any

import numpy as np

from playtester_rl.config_loader import ObservationConfig, PieceConfig
from playtester_rl.reward_strategies import IRewardStrategy

MOVE_SPEED = 1.0  # world units per step
# Chosen so a jump can cross the widest gap_jump default range endpoint (5.0,
# see piece_config.yaml) with margin: 6 steps * 1.0 unit/step = 6 units airborne
# travel, matching that config file's own "jump clears ~6 tiles" rationale.
JUMP_AIRBORNE_STEPS = 6
PIECE_MARGIN = 1.0  # flat ground before/after a piece's hazard span
MAX_EPISODE_STEPS = 200  # fake env's own step budget (independent of reward_config's)

ACTION_NOOP = 0
ACTION_LEFT = 1
ACTION_RIGHT = 2
ACTION_JUMP = 3


@dataclass
class _Piece:
    piece_id: str
    piece_type: str
    param: float  # the width (gap_jump) / distance (move_to_goal) / height (elevation)
    world_start: float
    length: float
    has_hazard: bool

    @property
    def world_end(self) -> float:
        return self.world_start + self.length

    @property
    def hazard_start(self) -> float:
        return self.world_start + PIECE_MARGIN

    @property
    def hazard_end(self) -> float:
        return self.hazard_start + self.param


def _sample_pieces(piece_config: PieceConfig, rng: random.Random) -> list[_Piece]:
    enabled_types = piece_config.enabled_piece_types()
    pieces: list[_Piece] = []
    world_x = 0.0
    for i in range(piece_config.pieces_per_episode):
        piece_type = rng.choice(enabled_types)
        type_config = {
            "gap_jump": piece_config.gap_jump,
            "move_to_goal": piece_config.move_to_goal,
            "elevation": piece_config.elevation,
        }[piece_type]
        lo, hi = type_config.param_range
        param = rng.uniform(lo, hi)

        has_hazard = piece_type in ("gap_jump", "elevation")
        length = (2 * PIECE_MARGIN + param) if has_hazard else param

        pieces.append(
            _Piece(
                piece_id=f"piece_{i}",
                piece_type=piece_type,
                param=param,
                world_start=world_x,
                length=length,
                has_hazard=has_hazard,
            )
        )
        world_x += length
    return pieces


@dataclass
class _EpisodeTelemetry:
    """Accumulates the piece_results this episode produces, in the exact
    shape telemetry_writer.TelemetryBuilder.add_episode expects."""

    piece_results: list[dict[str, Any]] = field(default_factory=list)
    path_trace: list[dict[str, float]] = field(default_factory=list)


class FakeCompositionEnv:
    """Gymnasium-style env (reset/step) but not a gymnasium.Env subclass —
    avoids pulling in the full Gymnasium space-registration machinery for
    what is fundamentally a hand-rolled test double. Mirrors the Unity-side
    PlaytestAgent's lifecycle: OnEpisodeBegin -> reset(), OnActionReceived -> step().
    """

    def __init__(
        self,
        piece_config: PieceConfig,
        observation_config: ObservationConfig,
        reward_strategy: IRewardStrategy,
        seed: int | None = None,
    ) -> None:
        self.piece_config = piece_config
        self.observation_config = observation_config
        self.reward_strategy = reward_strategy
        self._rng = random.Random(seed)

        self._pieces: list[_Piece] = []
        self._x = 0.0
        self._airborne_steps_remaining = 0
        self._current_piece_idx = 0
        self._prev_distance_to_goal = 0.0
        self._step_count = 0
        self._telemetry = _EpisodeTelemetry()
        self._current_piece_attempts = 0
        self._current_piece_death_position: dict[str, float] | None = None

    # -- lifecycle -----------------------------------------------------

    def reset(self) -> tuple[np.ndarray, dict[str, Any]]:
        self._pieces = _sample_pieces(self.piece_config, self._rng)
        self._x = 0.0
        self._airborne_steps_remaining = 0
        self._current_piece_idx = 0
        self._step_count = 0
        self._current_piece_attempts = 0
        self._current_piece_death_position = None
        self._telemetry = _EpisodeTelemetry()
        self._prev_distance_to_goal = self._distance_to_current_goal()
        return self._observe(), {}

    def step(self, action: int) -> tuple[np.ndarray, float, bool, bool, dict[str, Any]]:
        self._step_count += 1
        reward = 0.0

        piece = self._pieces[self._current_piece_idx]
        was_airborne = self._airborne_steps_remaining > 0

        if action == ACTION_LEFT:
            self._x = max(0.0, self._x - MOVE_SPEED)
        elif action == ACTION_RIGHT:
            self._x += MOVE_SPEED
        elif action == ACTION_JUMP:
            self._airborne_steps_remaining = JUMP_AIRBORNE_STEPS
            if piece.has_hazard and piece.world_start <= self._x <= piece.hazard_end:
                self._current_piece_attempts += 1
            self._x += MOVE_SPEED
        # ACTION_NOOP: no movement

        if was_airborne:
            self._airborne_steps_remaining = max(0, self._airborne_steps_remaining - 1)

        self._telemetry.path_trace.append({"t": float(self._step_count), "x": self._x, "y": 1.0 if self._airborne_steps_remaining > 0 else 0.0})

        # Death check: inside this piece's hazard span, not airborne.
        if piece.has_hazard and piece.hazard_start <= self._x < piece.hazard_end and self._airborne_steps_remaining == 0:
            reward += self.reward_strategy.death_penalty()
            self._current_piece_death_position = {"x": self._x, "y": 0.0}
            self._record_piece_result(piece, outcome_time=None)
            obs = self._observe()
            return obs, reward, True, False, {"outcome": "death"}

        # Progress reward toward this piece's local goal.
        new_distance = self._distance_to_current_goal()
        delta_progress = self._prev_distance_to_goal - new_distance
        reward += self.reward_strategy.piece_progress_reward(delta_progress)
        reward += self.reward_strategy.step_time_penalty()
        self._prev_distance_to_goal = new_distance

        # Piece completion check.
        if self._x >= piece.world_end:
            reward += self.reward_strategy.piece_completion_bonus()
            self._record_piece_result(piece, outcome_time=float(self._step_count))
            self._current_piece_idx += 1
            self._current_piece_attempts = 0

            if self._current_piece_idx >= len(self._pieces):
                reward += self.reward_strategy.final_sequence_bonus()
                obs = self._observe()
                return obs, reward, True, False, {"outcome": "success"}

            self._prev_distance_to_goal = self._distance_to_current_goal()

        # Timeout check.
        if self._step_count >= min(MAX_EPISODE_STEPS, self.reward_strategy.max_episode_steps()):
            obs = self._observe()
            return obs, reward, False, True, {"outcome": "timeout"}

        return self._observe(), reward, False, False, {}

    # -- telemetry export ------------------------------------------------

    def episode_telemetry(self) -> tuple[list[dict[str, Any]], list[dict[str, float]]]:
        """Returns (piece_results, path_trace) in the exact shape
        TelemetryBuilder.add_episode expects — call after an episode ends."""
        return self._telemetry.piece_results, self._telemetry.path_trace

    def _record_piece_result(self, piece: _Piece, outcome_time: float | None) -> None:
        width = piece.param if piece.piece_type in ("gap_jump", "move_to_goal") else None
        height = piece.param if piece.piece_type == "elevation" else None
        self._telemetry.piece_results.append(
            {
                "piece_id": piece.piece_id,
                "piece_type": piece.piece_type,
                "params": {"width": width, "height": height},
                "attempts": max(1, self._current_piece_attempts),
                "time_to_clear_seconds": outcome_time,
                "death_position": self._current_piece_death_position,
                # Filled in by the caller via telemetry_writer.compute_seen_in_stage1_range
                # once it has the real piece_config — the fake env doesn't compute this
                # itself since that heuristic explicitly belongs to telemetry_writer.py.
                "seen_in_stage1_range": True,
            }
        )
        self._current_piece_death_position = None

    # -- observation ------------------------------------------------------

    def _distance_to_current_goal(self) -> float:
        if self._current_piece_idx >= len(self._pieces):
            return 0.0
        return max(0.0, self._pieces[self._current_piece_idx].world_end - self._x)

    def _observe(self) -> np.ndarray:
        # Real observation space is a true N×N tilemap window (spec §2.4), not
        # a 1D strip — matched here even though the fake world only has one
        # meaningful row (ground level). Every row except the ground row is
        # "empty" (open air above a 1D platformer's ground plane); the ground
        # row carries the real solid/hazard/goal classification. This keeps
        # observation_size() (PRD-defined, N*N*channels) the single source of
        # truth for shape, rather than the fake env inventing its own formula.
        cfg = self.observation_config
        half = cfg.grid_size // 2
        channel_index = {name: i for i, name in enumerate(cfg.tile_channels)}
        grid = np.zeros((cfg.grid_size, cfg.grid_size, len(cfg.tile_channels)), dtype=np.float32)
        # Unity emits y offsets from -radius to +radius, so the bottom row is
        # serialized first (row zero), then x from left to right.
        ground_row = 0

        player_col = int(self._x)
        for row in range(cfg.grid_size):
            for i, offset in enumerate(range(-half, half + 1)):
                if row == ground_row:
                    category = self._tile_category(float(player_col + offset))
                else:
                    category = "empty"
                grid[row, i, channel_index[category]] = 1.0

        grid_flat = grid.flatten()

        goal_dist = self._distance_to_current_goal()
        normalized_goal_dist = goal_dist / max(1, cfg.grid_size)
        rel_pos = np.array([normalized_goal_dist, 0.0], dtype=np.float32)
        parts = [grid_flat, rel_pos]

        if cfg.include_velocity:
            vx = 0.0 if self._step_count == 0 else MOVE_SPEED
            vy = 1.0 if self._airborne_steps_remaining > 0 else 0.0
            parts.append(np.array([vx, vy], dtype=np.float32))

        if cfg.include_grounded_flag:
            parts.append(np.array([0.0 if self._airborne_steps_remaining > 0 else 1.0], dtype=np.float32))

        direction = 1.0 if goal_dist > 0 else 0.0
        parts.append(np.array([normalized_goal_dist, direction], dtype=np.float32))

        return np.concatenate(parts)

    def _tile_category(self, world_x: float) -> str:
        if world_x < 0 or self._current_piece_idx >= len(self._pieces):
            return "empty"
        piece = self._pieces[self._current_piece_idx]
        if piece.has_hazard and piece.hazard_start <= world_x < piece.hazard_end:
            return "hazard"
        if world_x >= piece.world_end - 1:
            return "goal"
        return "solid"
