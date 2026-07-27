from __future__ import annotations

import json
from pathlib import Path

import pytest

from playtester_rl.real_training import (
    BEHAVIOR_NAME,
    CHECKPOINT_FORMAT,
    CheckpointReference,
    RealTrainingError,
    collect_real_training_artifacts,
    read_checkpoint_reference,
    write_checkpoint_reference,
)


def test_checkpoint_reference_round_trip(tmp_path: Path) -> None:
    trainer_output = tmp_path / "results" / "stage1"
    trainer_output.mkdir(parents=True)
    onnx = trainer_output / f"{BEHAVIOR_NAME}.onnx"
    onnx.write_bytes(b"onnx")
    marker = tmp_path / "stage1.ckpt"
    reference = CheckpointReference(
        run_id="stage1",
        results_dir=tmp_path / "results",
        trainer_output_dir=trainer_output,
        onnx_export_path=onnx,
    )

    write_checkpoint_reference(marker, reference)

    assert read_checkpoint_reference(marker) == reference
    document = json.loads(marker.read_text(encoding="utf-8"))
    assert document["format"] == CHECKPOINT_FORMAT
    assert not Path(document["onnx_export_path"]).is_absolute()


def test_fake_marker_cannot_warm_start_real_training(tmp_path: Path) -> None:
    marker = tmp_path / "fake.ckpt"
    marker.write_text("fake checkpoint marker", encoding="utf-8")

    with pytest.raises(RealTrainingError, match="real ML-Agents run"):
        read_checkpoint_reference(marker)


def test_collect_real_training_artifacts_parses_status_and_writes_contracts(
    tmp_path: Path,
) -> None:
    results = tmp_path / "results"
    run_id = "level_a_stage2"
    output = results / run_id
    onnx = output / f"{BEHAVIOR_NAME}.onnx"
    onnx.parent.mkdir(parents=True)
    onnx.write_bytes(b"onnx")
    status_path = output / "run_logs" / "training_status.json"
    status_path.parent.mkdir()
    status_path.write_text(
        json.dumps(
            {
                BEHAVIOR_NAME: {
                    "checkpoints": [
                        {"steps": 10, "reward": -1.0},
                        {"steps": 20, "reward": 0.5},
                    ],
                    "final_checkpoint": {
                        "steps": 20,
                        "reward": 0.5,
                        "file_path": str(onnx),
                    },
                }
            }
        ),
        encoding="utf-8",
    )
    marker = tmp_path / "checkpoints" / "stage2.ckpt"

    artifacts = collect_real_training_artifacts(
        results_dir=results,
        run_id=run_id,
        checkpoint_out=marker,
    )

    assert artifacts.training_steps == 20
    assert artifacts.final_mean_reward == 0.5
    assert artifacts.checkpoint.onnx_export_path == onnx
    assert marker.is_file()
    curve = json.loads(artifacts.reward_curve_path.read_text(encoding="utf-8"))
    assert curve == [
        {"step": 10, "mean_reward": -1.0},
        {"step": 20, "mean_reward": 0.5},
    ]


def test_collect_remote_copy_uses_local_onnx_fallback(tmp_path: Path) -> None:
    results = tmp_path / "remote-results"
    run_id = "copied-gb10-run"
    output = results / run_id
    onnx = output / f"{BEHAVIOR_NAME}.onnx"
    onnx.parent.mkdir(parents=True)
    onnx.write_bytes(b"copied onnx")
    status_path = output / "run_logs" / "training_status.json"
    status_path.parent.mkdir()
    status_path.write_text(
        json.dumps(
            {
                BEHAVIOR_NAME: {
                    "checkpoints": [{"steps": 64, "reward": 1.0}],
                    "final_checkpoint": {
                        "steps": 64,
                        "reward": 1.0,
                        "file_path": "/home/gb10/remote/PlaytestAgent.onnx",
                    },
                }
            }
        ),
        encoding="utf-8",
    )

    artifacts = collect_real_training_artifacts(
        results_dir=results,
        run_id=run_id,
        checkpoint_out=tmp_path / "copied.ckpt",
    )

    assert artifacts.checkpoint.onnx_export_path == onnx
