"""Tests for telemetry_writer.py — schema validation (valid + deliberately
malformed fixtures), the seen_in_stage1_range boundary heuristic, and
write/read round-tripping (no leaky/missing data across a disk round-trip)."""

from __future__ import annotations

import copy

import pytest

from playtester_rl.config_loader import PieceConfig, PieceTypeConfig
from playtester_rl.telemetry_writer import (
    TelemetryBuilder,
    TelemetryValidationError,
    compute_seen_in_stage1_range,
    read_telemetry,
    validate_telemetry,
    write_telemetry,
)


def _valid_doc() -> dict:
    return {
        "run_id": "9d1f6f0a-4b2e-4f3a-8c7d-1a2b3c4d5e6f",
        "level_id": "level_a",
        "stage": "stage2",
        "checkpoint_path": "rl/checkpoints/level_a/stage2.onnx",
        "timestamp_start": "2026-07-25T10:00:00Z",
        "episode_summaries": [
            {
                "episode_index": 0,
                "outcome": "success",
                "total_reward": 6.2,
                "time_to_clear_seconds": 12.5,
                "path_trace": [{"t": 0.0, "x": 0.0, "y": 0.0}, {"t": 1.0, "x": 1.0, "y": 0.0}],
                "piece_results": [
                    {
                        "piece_id": "piece_0",
                        "piece_type": "gap_jump",
                        "params": {"width": 3.0, "height": None},
                        "attempts": 1,
                        "time_to_clear_seconds": 4.0,
                        "death_position": None,
                        "seen_in_stage1_range": True,
                    }
                ],
            }
        ],
    }


# ---------------------------------------------------------------------------
# Valid document round-trips cleanly
# ---------------------------------------------------------------------------


def test_valid_document_passes_validation():
    validate_telemetry(_valid_doc())  # must not raise


def test_write_then_read_round_trips_with_no_data_loss(tmp_path):
    doc = _valid_doc()
    path = tmp_path / "telemetry" / "run.json"
    write_telemetry(doc, path)
    reloaded = read_telemetry(path)
    assert reloaded == doc, "round-trip must be lossless — no leaky/missing fields"


# ---------------------------------------------------------------------------
# Malformed fixtures — each must be rejected, not silently accepted
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "mutate",
    [
        lambda d: d.pop("run_id"),
        lambda d: d.pop("episode_summaries"),
        lambda d: d["episode_summaries"][0].pop("outcome"),
        lambda d: d["episode_summaries"][0].__setitem__("outcome", "not_a_real_outcome"),
        lambda d: d["episode_summaries"][0]["piece_results"][0].pop("seen_in_stage1_range"),
        lambda d: d["episode_summaries"][0]["piece_results"][0].__setitem__("piece_type", "unknown_piece"),
        lambda d: d["episode_summaries"][0]["piece_results"][0].__setitem__("attempts", -1),
        lambda d: d.__setitem__("stage", "stage3"),
        lambda d: d["episode_summaries"][0]["path_trace"][0].pop("x"),
        lambda d: d.__setitem__("extra_unexpected_field", "should not be allowed"),
    ],
    ids=[
        "missing_run_id",
        "missing_episode_summaries",
        "missing_outcome",
        "invalid_outcome_enum",
        "missing_seen_in_stage1_range",
        "invalid_piece_type_enum",
        "negative_attempts",
        "invalid_stage_enum",
        "missing_path_point_field",
        "unexpected_additional_field",
    ],
)
def test_malformed_document_is_rejected(mutate):
    doc = copy.deepcopy(_valid_doc())
    mutate(doc)
    with pytest.raises(TelemetryValidationError):
        validate_telemetry(doc)


def test_write_rejects_malformed_document_before_touching_disk(tmp_path):
    doc = copy.deepcopy(_valid_doc())
    doc.pop("run_id")
    path = tmp_path / "telemetry" / "bad_run.json"
    with pytest.raises(TelemetryValidationError):
        write_telemetry(doc, path)
    assert not path.exists(), "a malformed document must never land on disk"


# ---------------------------------------------------------------------------
# TelemetryBuilder
# ---------------------------------------------------------------------------


def test_builder_produces_schema_valid_document():
    builder = TelemetryBuilder(
        level_id="level_b",
        stage="stage1",
        checkpoint_path="rl/checkpoints/gym/stage1.onnx",
        timestamp_start="2026-07-25T09:00:00Z",
    )
    builder.add_episode(
        episode_index=0,
        outcome="death",
        total_reward=-0.4,
        time_to_clear_seconds=None,
        path_trace=[{"t": 0.0, "x": 0.0, "y": 0.0}],
        piece_results=[
            {
                "piece_id": "piece_0",
                "piece_type": "gap_jump",
                "params": {"width": 4.9, "height": None},
                "attempts": 3,
                "time_to_clear_seconds": None,
                "death_position": {"x": 4.5, "y": -1.0},
                "seen_in_stage1_range": True,
            }
        ],
    )
    doc = builder.build()
    validate_telemetry(doc)  # must not raise
    assert doc["level_id"] == "level_b"
    assert len(doc["episode_summaries"]) == 1


def test_builder_rejects_invalid_outcome():
    builder = TelemetryBuilder(
        level_id="level_b", stage="stage1", checkpoint_path="x", timestamp_start="2026-07-25T09:00:00Z"
    )
    with pytest.raises(ValueError, match="Invalid outcome"):
        builder.add_episode(0, "not_a_real_outcome", 0.0, None, [], [])


def test_builder_generates_a_run_id_if_not_supplied():
    b1 = TelemetryBuilder(level_id="x", stage="stage1", checkpoint_path="x", timestamp_start="t")
    b2 = TelemetryBuilder(level_id="x", stage="stage1", checkpoint_path="x", timestamp_start="t")
    assert b1.run_id != b2.run_id, "two builders must not collide on run_id"


# ---------------------------------------------------------------------------
# seen_in_stage1_range heuristic — boundary cases
# ---------------------------------------------------------------------------


@pytest.fixture
def piece_config() -> PieceConfig:
    return PieceConfig(
        gap_jump=PieceTypeConfig(enabled=True, param_range=(2.0, 5.0)),
        move_to_goal=PieceTypeConfig(enabled=True, param_range=(4.0, 10.0)),
        elevation=PieceTypeConfig(enabled=False, param_range=(1.0, 3.0)),
        pieces_per_episode=3,
        boundary_velocity_reset=True,
    )


def test_seen_in_range_true_inside_range(piece_config):
    assert compute_seen_in_stage1_range("gap_jump", {"width": 3.5}, piece_config) is True


@pytest.mark.parametrize("width", [2.0, 5.0])
def test_seen_in_range_true_at_exact_boundaries(piece_config, width):
    assert compute_seen_in_stage1_range("gap_jump", {"width": width}, piece_config) is True


@pytest.mark.parametrize("width", [1.999, 5.001])
def test_seen_in_range_false_just_outside_boundaries(piece_config, width):
    assert compute_seen_in_stage1_range("gap_jump", {"width": width}, piece_config) is False


def test_seen_in_range_false_when_piece_type_disabled(piece_config):
    # elevation is disabled in this fixture config — even a value that would
    # fall inside the configured range must read False, since Stage 1 never
    # actually trained on it.
    assert compute_seen_in_stage1_range("elevation", {"height": 2.0}, piece_config) is False


def test_seen_in_range_false_when_value_missing(piece_config):
    assert compute_seen_in_stage1_range("gap_jump", {"width": None}, piece_config) is False


def test_seen_in_range_move_to_goal_uses_width_field_convention(piece_config):
    # move_to_goal's traversal distance is stored under 'width', per the
    # documented field-reuse convention (schema has no generic 'distance' field).
    assert compute_seen_in_stage1_range("move_to_goal", {"width": 7.0}, piece_config) is True
    assert compute_seen_in_stage1_range("move_to_goal", {"width": 100.0}, piece_config) is False


def test_seen_in_range_unknown_piece_type_raises(piece_config):
    with pytest.raises(ValueError, match="Unknown piece_type"):
        compute_seen_in_stage1_range("dash", {"width": 1.0}, piece_config)
