"""Run a bounded Unity playtest with the checkpoint mode made explicit.

Real ML-Agents checkpoint markers are replayed through ``mlagents-learn
--resume --inference`` so the freshly trained policy actually selects actions.
Legacy/fake markers retain a structural standalone-player smoke path, clearly
labelled as such.  This prevents an ignored ``--checkpoint`` argument from
being mistaken for model-backed playback.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from playtester_rl.cli import REPO_ROOT, find_mlagents_learn, find_unity_build, validate_level_id
from playtester_rl.real_training import (
    CHECKPOINT_FORMAT,
    RealTrainingError,
    read_checkpoint_reference,
)
from playtester_rl.remote_execution import (
    RemoteExecutionError,
    allocate_remote_port,
    build_local_unity_command,
    build_remote_trainer_command,
    load_remote_config,
    run_remote_policy_session,
    verify_remote_preflight,
    verify_remote_port_available,
)
from playtester_rl.telemetry_writer import validate_telemetry


class PlaybackError(RuntimeError):
    """Raised when a requested playtest cannot produce trustworthy telemetry."""


def _player_executable(build_path: Path) -> Path:
    if build_path.suffix == ".app":
        candidates = [
            path
            for path in (build_path / "Contents" / "MacOS").iterdir()
            if path.is_file() and os.access(path, os.X_OK)
        ]
        if len(candidates) != 1:
            raise PlaybackError(
                f"Expected one executable in macOS app {build_path}, found {len(candidates)}"
            )
        return candidates[0]
    if not os.access(build_path, os.X_OK):
        raise PlaybackError(f"Unity player is not executable: {build_path}")
    return build_path


def _real_checkpoint(path: Path) -> bool:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        return False
    return isinstance(document, dict) and document.get("format") == CHECKPOINT_FORMAT


def _find_trainer() -> str | None:
    return find_mlagents_learn()


def default_base_port(level_id: str) -> int:
    """Give known levels separate ML-Agents port ranges for concurrent runs."""
    return {"level_a": 5205, "level_b": 5305, "gym": 5405}.get(level_id, 5505)


def build_inference_command(
    *,
    trainer: str,
    configuration_path: Path,
    player: Path,
    run_id: str,
    results_dir: Path,
    level_id: str,
    checkpoint_path: Path,
    telemetry_dir: Path,
    episodes: int,
    base_port: int,
) -> list[str]:
    return [
        trainer,
        str(configuration_path),
        f"--env={player}",
        f"--run-id={run_id}",
        f"--results-dir={results_dir}",
        "--resume",
        "--inference",
        "--num-envs=1",
        "--no-graphics",
        f"--base-port={base_port}",
        "--max-lifetime-restarts=0",
        "--env-args",
        "--smoke-exit",
        "--smoke-episodes",
        str(episodes),
        "--level-id",
        level_id,
        "--mlagents-max-steps",
        os.environ.get("PLAYTESTER_PLAYBACK_MAX_STEPS", "16"),
        "--checkpoint",
        str(checkpoint_path),
        "--telemetry-dir",
        str(telemetry_dir),
    ]


def run_playtest(
    *,
    level_id: str,
    checkpoint_in: Path,
    episodes: int,
    telemetry_out: Path,
    execution_mode: str = "local",
    remote_config_path: Path | None = None,
    remote_port: int | None = None,
) -> Path:
    validate_level_id(level_id)
    if episodes < 1:
        raise PlaybackError(f"episodes must be positive, got {episodes}")
    checkpoint = checkpoint_in.expanduser().resolve(strict=True)
    output = telemetry_out.expanduser().resolve(strict=False)
    if output.exists():
        raise PlaybackError(f"Refusing to overwrite telemetry: {output}")
    build = find_unity_build(level_id)
    if build is None:
        raise PlaybackError(f"No Unity build found for {level_id}")
    player = _player_executable(build)

    with tempfile.TemporaryDirectory(prefix=f"playtester-{level_id}-") as temporary:
        telemetry_dir = Path(temporary)
        if _real_checkpoint(checkpoint):
            reference = read_checkpoint_reference(checkpoint)
            if execution_mode == "remote":
                remote = load_remote_config(
                    remote_config_path or REPO_ROOT / "rl/configs/remote_execution.yaml"
                )
                expected_commit = subprocess.run(
                    ["git", "rev-parse", "HEAD"],
                    cwd=REPO_ROOT,
                    text=True,
                    capture_output=True,
                    check=True,
                ).stdout.strip()
                verify_remote_preflight(remote, expected_commit=expected_commit)
                port = allocate_remote_port(
                    remote,
                    level_id=level_id,
                    run_id=f"{reference.run_id}-inference",
                    requested_port=remote_port,
                )
                verify_remote_port_available(remote, port)
                remote_command = build_remote_trainer_command(
                    remote,
                    run_id=reference.run_id,
                    port=port,
                    num_envs=1,
                    initialize_from_run_id=None,
                    inference=True,
                    torch_device=os.environ.get("PLAYTESTER_TORCH_DEVICE", "cuda"),
                )
                unity_command = build_local_unity_command(
                    player,
                    port=port,
                    env_max_steps=int(
                        os.environ.get("PLAYTESTER_PLAYBACK_MAX_STEPS", "16")
                    ),
                    extra_args=(
                        "--smoke-exit",
                        "--smoke-episodes",
                        str(episodes),
                        "--level-id",
                        level_id,
                        "--checkpoint",
                        str(checkpoint),
                        "--telemetry-dir",
                        str(telemetry_dir),
                    ),
                )
                print(
                    f"[playback] GB10 policy inference for run_id={reference.run_id}, "
                    f"port={port}; Unity remains local."
                )
                completed_returncode = run_remote_policy_session(
                    remote,
                    remote_args=remote_command,
                    unity_args=unity_command,
                    port=port,
                )
            else:
                trainer = _find_trainer()
                if trainer is None:
                    raise PlaybackError(
                        "Real checkpoint playback requires mlagents-learn; refusing "
                        "to silently use Unity's heuristic policy."
                    )
                configuration = reference.trainer_output_dir / "configuration.yaml"
                if not configuration.is_file():
                    raise PlaybackError(
                        f"ML-Agents run has no saved configuration: {configuration}"
                    )
                try:
                    base_port = int(
                        os.environ.get(
                            "PLAYTESTER_BASE_PORT", str(default_base_port(level_id))
                        )
                    )
                except ValueError as error:
                    raise PlaybackError("PLAYTESTER_BASE_PORT must be an integer") from error
                command = build_inference_command(
                    trainer=trainer,
                    configuration_path=configuration,
                    # ML-Agents understands macOS .app bundles and performs its
                    # own platform-specific executable resolution.
                    player=build,
                    run_id=reference.run_id,
                    results_dir=reference.results_dir,
                    level_id=level_id,
                    checkpoint_path=checkpoint,
                    telemetry_dir=telemetry_dir,
                    episodes=episodes,
                    base_port=base_port,
                )
                print(
                    f"[playback] Real checkpoint detected; running local ML-Agents "
                    f"inference for run_id={reference.run_id}."
                )
                completed_returncode = subprocess.run(
                    command, cwd=REPO_ROOT, check=False
                ).returncode
        else:
            if execution_mode == "remote":
                raise PlaybackError(
                    "Remote inference requires a real ML-Agents checkpoint marker"
                )
            command = [
                str(player),
                "-batchmode",
                "-nographics",
                "--smoke-exit",
                "--smoke-episodes",
                str(episodes),
                "--level-id",
                level_id,
                "--mlagents-max-steps",
                os.environ.get("PLAYTESTER_PLAYBACK_MAX_STEPS", "16"),
                "--checkpoint",
                str(checkpoint),
                "--telemetry-dir",
                str(telemetry_dir),
            ]
            print(
                "[playback] Non-ML-Agents checkpoint detected; running structural "
                "standalone smoke without claiming model inference."
            )
            completed_returncode = subprocess.run(
                command, cwd=REPO_ROOT, check=False
            ).returncode

        telemetry_files = sorted(
            telemetry_dir.glob(f"{level_id}_*.json"),
            key=lambda path: path.stat().st_mtime_ns,
        )
        if completed_returncode != 0:
            raise PlaybackError(f"Playtest process exited {completed_returncode}")
        if not telemetry_files:
            raise PlaybackError(f"Unity playtest produced no telemetry for {level_id}")
        newest = telemetry_files[-1]
        document = json.loads(newest.read_text(encoding="utf-8"))
        validate_telemetry(document)
        output.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(newest), output)
    print(f"[playback] Valid telemetry written to {output}")
    return output


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="playtester_rl.playback")
    parser.add_argument("--level-id", required=True)
    parser.add_argument("--checkpoint-in", required=True)
    parser.add_argument("--episodes", required=True, type=int)
    parser.add_argument("--telemetry-out", required=True)
    parser.add_argument(
        "--execution-mode",
        choices=("local", "remote"),
        default=os.environ.get("PLAYTESTER_RL_PLAYBACK_MODE", "local"),
    )
    parser.add_argument(
        "--remote-config",
        default=os.environ.get("PLAYTESTER_REMOTE_CONFIG"),
    )
    parser.add_argument("--remote-port", type=int)
    args = parser.parse_args(argv)
    try:
        run_playtest(
            level_id=args.level_id,
            checkpoint_in=Path(args.checkpoint_in),
            episodes=args.episodes,
            telemetry_out=Path(args.telemetry_out),
            execution_mode=args.execution_mode,
            remote_config_path=Path(args.remote_config) if args.remote_config else None,
            remote_port=args.remote_port,
        )
        return 0
    except (
        OSError,
        ValueError,
        PlaybackError,
        RealTrainingError,
        RemoteExecutionError,
    ) as error:
        print(f"[playback] ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
