"""Adapter between the project's locked training CLI and ML-Agents.

ML-Agents identifies warm-start sources by ``run_id`` inside a shared
``results_dir``.  The project contract intentionally exposes filesystem
checkpoint paths instead.  A checkpoint marker written here bridges those
two contracts and also records the exported ONNX path for Unity playback.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from playtester_rl.gate_eval import compute_steps_to_converge, save_reward_curve

CHECKPOINT_FORMAT = "playtester-mlagents-checkpoint-v1"
BEHAVIOR_NAME = "PlaytestAgent"


class RealTrainingError(RuntimeError):
    """Raised when ML-Agents output cannot satisfy the project contracts."""


@dataclass(frozen=True)
class CheckpointReference:
    run_id: str
    results_dir: Path
    trainer_output_dir: Path
    onnx_export_path: Path


@dataclass(frozen=True)
class RealTrainingArtifacts:
    checkpoint: CheckpointReference
    final_mean_reward: float
    training_steps: int
    steps_to_converge: int | None
    reward_curve_path: Path


def write_checkpoint_reference(path: Path, reference: CheckpointReference) -> None:
    """Atomically publish a stable project checkpoint after training succeeds."""
    path.parent.mkdir(parents=True, exist_ok=True)

    def portable(target: Path) -> str:
        # Markers are copied with their result tree to GB10. Relative paths
        # keep that bundle usable after the repository root changes.
        return os.path.relpath(target.resolve(strict=False), path.parent.resolve(strict=False))

    document = {
        "format": CHECKPOINT_FORMAT,
        "behavior_name": BEHAVIOR_NAME,
        "run_id": reference.run_id,
        "results_dir": portable(reference.results_dir),
        "trainer_output_dir": portable(reference.trainer_output_dir),
        "onnx_export_path": portable(reference.onnx_export_path),
    }
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    temporary.replace(path)


def read_checkpoint_reference(path: Path) -> CheckpointReference:
    try:
        document: Any = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RealTrainingError(
            f"Real Stage 2 requires a checkpoint marker produced by a real "
            f"ML-Agents run; could not read {path}: {error}"
        ) from error

    if not isinstance(document, dict) or document.get("format") != CHECKPOINT_FORMAT:
        raise RealTrainingError(
            f"Real Stage 2 requires {CHECKPOINT_FORMAT!r}, but {path} is not "
            "a real ML-Agents checkpoint marker (it may be a fake-mode artifact)."
        )
    if document.get("behavior_name") != BEHAVIOR_NAME:
        raise RealTrainingError(
            f"Checkpoint behavior {document.get('behavior_name')!r} does not "
            f"match Unity behavior {BEHAVIOR_NAME!r}."
        )

    required = ("run_id", "results_dir", "trainer_output_dir", "onnx_export_path")
    if any(not isinstance(document.get(field), str) or not document[field] for field in required):
        raise RealTrainingError(f"Checkpoint marker is missing required fields: {path}")

    def resolve_marker_path(value: str) -> Path:
        candidate = Path(value).expanduser()
        if not candidate.is_absolute():
            candidate = path.parent / candidate
        return candidate.resolve(strict=False)

    reference = CheckpointReference(
        run_id=document["run_id"],
        results_dir=resolve_marker_path(document["results_dir"]),
        trainer_output_dir=resolve_marker_path(document["trainer_output_dir"]),
        onnx_export_path=resolve_marker_path(document["onnx_export_path"]),
    )
    if not reference.trainer_output_dir.is_dir():
        raise RealTrainingError(
            f"Checkpoint trainer output directory does not exist: {reference.trainer_output_dir}"
        )
    if not reference.onnx_export_path.is_file():
        raise RealTrainingError(
            f"Checkpoint ONNX export does not exist: {reference.onnx_export_path}"
        )
    return reference


def collect_real_training_artifacts(
    *,
    results_dir: Path,
    run_id: str,
    checkpoint_out: Path,
) -> RealTrainingArtifacts:
    """Parse ML-Agents' versioned JSON status and publish project artifacts."""
    resolved_results = results_dir.expanduser().resolve(strict=False)
    trainer_output = resolved_results / run_id
    status_path = trainer_output / "run_logs" / "training_status.json"
    try:
        status: Any = json.loads(status_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise RealTrainingError(
            f"ML-Agents exited successfully but training status is unreadable: "
            f"{status_path}: {error}"
        ) from error

    try:
        behavior = status[BEHAVIOR_NAME]
        final = behavior["final_checkpoint"]
        training_steps = int(final["steps"])
        final_mean_reward = float(final["reward"])
        reported_onnx_path = Path(final["file_path"]).expanduser().resolve(strict=False)
        checkpoints = behavior["checkpoints"]
    except (KeyError, TypeError, ValueError) as error:
        raise RealTrainingError(
            f"ML-Agents status does not contain the expected {BEHAVIOR_NAME!r} "
            f"checkpoint shape: {status_path}"
        ) from error

    # A copied GB10 status file contains the GB10's absolute file_path. Prefer
    # it locally when valid, otherwise resolve the canonical copied export.
    onnx_path = reported_onnx_path
    if not onnx_path.is_file():
        onnx_path = trainer_output / f"{BEHAVIOR_NAME}.onnx"
    if not onnx_path.is_file():
        raise RealTrainingError(
            f"ML-Agents reported an ONNX file that does not exist locally: "
            f"{reported_onnx_path}; copied fallback also missing: {onnx_path}"
        )

    reward_by_step: dict[int, float] = {}
    for checkpoint in checkpoints:
        try:
            reward_by_step[int(checkpoint["steps"])] = float(checkpoint["reward"])
        except (KeyError, TypeError, ValueError) as error:
            raise RealTrainingError(
                f"Malformed checkpoint metrics in {status_path}: {checkpoint!r}"
            ) from error
    reward_by_step[training_steps] = final_mean_reward
    reward_curve = sorted(reward_by_step.items())
    reward_curve_path = checkpoint_out.with_suffix(
        checkpoint_out.suffix + ".reward_curve.json"
    )
    save_reward_curve(reward_curve, reward_curve_path)

    reference = CheckpointReference(
        run_id=run_id,
        results_dir=resolved_results,
        trainer_output_dir=trainer_output,
        onnx_export_path=onnx_path,
    )
    write_checkpoint_reference(checkpoint_out, reference)
    return RealTrainingArtifacts(
        checkpoint=reference,
        final_mean_reward=final_mean_reward,
        training_steps=training_steps,
        steps_to_converge=compute_steps_to_converge(reward_curve, final_mean_reward),
        reward_curve_path=reward_curve_path,
    )
