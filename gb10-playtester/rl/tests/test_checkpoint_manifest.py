"""Tests for checkpoint_manifest.py — round-tripping, upsert semantics
(must not clobber sibling fields), and schema enforcement."""

from __future__ import annotations

import pytest

from playtester_rl.checkpoint_manifest import (
    ManifestValidationError,
    get_entry,
    load_manifest,
    save_manifest,
    upsert_entry_field,
    validate_manifest_entry,
)


def test_missing_manifest_file_returns_empty_list(tmp_path):
    assert load_manifest(tmp_path / "does_not_exist.json") == []


def test_upsert_creates_new_entry(tmp_path):
    path = tmp_path / "manifest.json"
    entry = upsert_entry_field(path, "level_a", "stage1_checkpoint", "checkpoints/level_a/stage1")
    assert entry["level_id"] == "level_a"
    assert entry["stage1_checkpoint"] == "checkpoints/level_a/stage1"
    assert entry["stage2_checkpoint"] is None


def test_upsert_does_not_clobber_sibling_fields(tmp_path):
    path = tmp_path / "manifest.json"
    upsert_entry_field(path, "level_a", "stage1_checkpoint", "ckpt/stage1")
    upsert_entry_field(
        path,
        "level_a",
        "stage1_metrics",
        {"final_mean_reward": 4.2, "training_steps": 500000},
    )
    entry = get_entry(path, "level_a")
    # The second upsert must not have wiped the first field.
    assert entry["stage1_checkpoint"] == "ckpt/stage1"
    assert entry["stage1_metrics"]["final_mean_reward"] == 4.2


def test_upsert_multiple_levels_stay_independent(tmp_path):
    path = tmp_path / "manifest.json"
    upsert_entry_field(path, "level_a", "stage1_checkpoint", "ckpt/a")
    upsert_entry_field(path, "level_b", "stage1_checkpoint", "ckpt/b")
    entries = load_manifest(path)
    assert len(entries) == 2
    assert get_entry(path, "level_a")["stage1_checkpoint"] == "ckpt/a"
    assert get_entry(path, "level_b")["stage1_checkpoint"] == "ckpt/b"


def test_upsert_unknown_field_rejected(tmp_path):
    path = tmp_path / "manifest.json"
    with pytest.raises(ManifestValidationError, match="Unknown manifest field"):
        upsert_entry_field(path, "level_a", "not_a_real_field", 123)


def test_save_and_load_round_trips(tmp_path):
    path = tmp_path / "manifest.json"
    entries = [
        {
            "level_id": "level_a",
            "stage1_checkpoint": "ckpt/a1",
            "stage2_checkpoint": "ckpt/a2",
            "onnx_export_path": "onnx/a.onnx",
            "stage1_metrics": {"final_mean_reward": 3.0, "training_steps": 100000},
            "stage2_metrics": {"final_mean_reward": 5.0, "training_steps": 20000, "steps_to_converge": 15000},
            "coldstart_baseline_metrics": {"final_mean_reward": 4.9, "training_steps": 60000, "steps_to_converge": 55000},
        }
    ]
    save_manifest(entries, path)
    reloaded = load_manifest(path)
    assert reloaded == entries


def test_validate_rejects_malformed_entry():
    bad_entry = {"level_id": "level_a"}  # missing required fields
    with pytest.raises(ManifestValidationError):
        validate_manifest_entry(bad_entry)


def test_get_entry_returns_none_for_missing_level(tmp_path):
    path = tmp_path / "manifest.json"
    upsert_entry_field(path, "level_a", "stage1_checkpoint", "ckpt/a")
    assert get_entry(path, "level_z") is None
