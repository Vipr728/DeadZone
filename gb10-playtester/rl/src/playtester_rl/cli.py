"""CLI entry point for rl/scripts/*.sh — implements the locked CLI contract
from prd-ml.md §5:

    --level-id <str>
    --checkpoint-in <path>    (optional, omitted for cold-start/Stage 1)
    --checkpoint-out <path>   (required)
    --num-envs <int>          (optional, defaults to training_config.yaml)
    --output-manifest <path>  (required)

Execution mode is explicit when correctness matters: ``--execution-mode real``
fails unless both the Unity build and trainer exist, while ``fake`` never
touches either. ``auto`` remains the backwards-compatible laptop default and
prints which route it selected.

The real adapter writes a stable project checkpoint marker containing the
ML-Agents run ID/results directory, parses ML-Agents' training-status JSON,
exports a reward curve, updates the manifest, and records the ONNX model.
This is necessary because ML-Agents' ``--initialize-from`` accepts a run ID,
whereas the project's public contract intentionally accepts a checkpoint path.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import replace
from pathlib import Path

from playtester_rl.checkpoint_manifest import ManifestValidationError, upsert_entry_field
from playtester_rl.config_loader import (
    ConfigValidationError,
    load_observation_config,
    load_piece_config,
    load_reward_config,
    load_training_config,
)
from playtester_rl.fake_trainer import run_fake_training
from playtester_rl.gate_eval import save_reward_curve
from playtester_rl.real_training import (
    RealTrainingArtifacts,
    RealTrainingError,
    collect_real_training_artifacts,
    read_checkpoint_reference,
)
from playtester_rl.remote_execution import (
    RemoteExecutionError,
    allocate_remote_port,
    build_local_unity_command,
    build_remote_trainer_command,
    fetch_remote_run,
    load_remote_config,
    local_player_executable,
    run_remote_policy_session,
    verify_remote_preflight,
    verify_remote_port_available,
)
from playtester_rl.telemetry_writer import TelemetryValidationError

REPO_ROOT = Path(__file__).resolve().parents[3]
UNITY_BUILDS_DIR = REPO_ROOT / "unity" / "PlaytesterProject" / "Builds"
CONFIGS_DIR = REPO_ROOT / "rl" / "configs"

_BUILD_EXTENSIONS = (".exe", ".x86_64", ".app")

# level_id flows unvalidated into filesystem paths (Builds/<level_id>/...),
# telemetry documents, and the checkpoint manifest — all of which downstream
# consumers (the Unity Editor tool, infra's report pipeline) will eventually
# treat as a trusted identifier for constructing their own paths. Rejecting
# anything but a plain identifier here (not path separators, not "..") is
# cheap insurance against a path-traversal-shaped bug surfacing later in code
# that assumes level_id is already safe because "the CLI validated it."
_LEVEL_ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]+$")


class CliUsageError(ValueError):
    """Raised for invalid CLI input that isn't a config/schema problem —
    e.g. an unsafe level_id or a --checkpoint-in path that doesn't exist."""


def validate_level_id(level_id: str) -> None:
    if not _LEVEL_ID_PATTERN.match(level_id):
        raise CliUsageError(
            f"Invalid --level-id {level_id!r}: must match {_LEVEL_ID_PATTERN.pattern} "
            "(letters, digits, underscore, hyphen only — no path separators or '..', "
            "since this value is used to build filesystem paths and is written into "
            "telemetry/manifest documents other tools will trust)."
        )


def portable_manifest_path(path: str | Path) -> str:
    """Prefer repository-relative artifact paths so manifests move to GB10."""
    resolved = Path(path).expanduser().resolve(strict=False)
    try:
        return resolved.relative_to(REPO_ROOT.resolve(strict=False)).as_posix()
    except ValueError:
        return str(resolved)


def find_unity_build(level_id: str) -> Path | None:
    """Locked build-layout contract (prd-ml.md §5): one standalone headless
    executable per level at Builds/<level_id>/<level_id>.<ext>."""
    level_dir = UNITY_BUILDS_DIR / level_id
    for ext in _BUILD_EXTENSIONS:
        candidate = level_dir / f"{level_id}{ext}"
        if candidate.exists():
            return candidate
    return None


def find_mlagents_learn() -> str | None:
    """Resolve the trainer consistently for direct RL and infra-launched runs."""
    configured = os.environ.get("PLAYTESTER_MLAGENTS_LEARN")
    if configured:
        return configured
    local = REPO_ROOT / "rl" / ".venv-mlagents" / "bin" / "mlagents-learn"
    if local.is_file() and os.access(local, os.X_OK):
        return str(local)
    return shutil.which("mlagents-learn")


def mlagents_learn_available() -> bool:
    return find_mlagents_learn() is not None


def build_mlagents_command(
    env_path: Path,
    run_id: str,
    num_envs: int,
    checkpoint_in: str | None,
    training_config_path: Path,
    *,
    results_dir: Path | None = None,
    trainer_executable: str = "mlagents-learn",
    torch_device: str | None = None,
    env_max_steps: int | None = None,
) -> list[str]:
    """Pure function (no subprocess call) so the exact command line is
    independently testable without needing mlagents-learn installed."""
    cmd = [
        trainer_executable,
        str(training_config_path),
        f"--env={env_path}",
        f"--run-id={run_id}",
        f"--num-envs={num_envs}",
        "--no-graphics",
    ]
    if results_dir is not None:
        cmd.append(f"--results-dir={results_dir}")
    # The public helper keeps its historical ``checkpoint_in`` parameter name,
    # but callers must pass the run ID resolved from our checkpoint marker.
    if checkpoint_in:
        cmd.append(f"--initialize-from={checkpoint_in}")
    if torch_device:
        cmd.append(f"--torch-device={torch_device}")
    if env_max_steps is not None:
        cmd.extend(["--env-args", "--mlagents-max-steps", str(env_max_steps)])
    return cmd


def run_real_training(
    *,
    env_path: Path,
    run_id: str,
    num_envs: int,
    initialize_from_run_id: str | None,
    training_config_path: Path,
    results_dir: Path,
    trainer_executable: str,
    torch_device: str | None,
    env_max_steps: int | None,
) -> int:
    """Launch real ML-Agents and propagate its raw process exit code."""
    cmd = build_mlagents_command(
        env_path,
        run_id,
        num_envs,
        initialize_from_run_id,
        training_config_path,
        results_dir=results_dir,
        trainer_executable=trainer_executable,
        torch_device=torch_device,
        env_max_steps=env_max_steps,
    )
    print(f"[cli] Real Unity build + mlagents-learn found — launching: {' '.join(cmd)}")
    # check=False is intentional here, not an oversight: we want the raw exit
    # code (propagated below) rather than an exception on non-zero exit, so
    # main()'s own return-code plumbing controls the CLI's exit status.
    result = subprocess.run(cmd, cwd=REPO_ROOT, check=False)
    return result.returncode


def _record_real_artifacts(
    *,
    stage: str,
    level_id: str,
    checkpoint_out_path: Path,
    output_manifest: str,
    artifacts: RealTrainingArtifacts,
    execution_label: str,
) -> int:
    manifest_path = Path(output_manifest)
    metrics = {
        "final_mean_reward": artifacts.final_mean_reward,
        "training_steps": artifacts.training_steps,
    }
    if stage == "stage1":
        upsert_entry_field(
            manifest_path,
            level_id,
            "stage1_checkpoint",
            portable_manifest_path(checkpoint_out_path),
        )
        upsert_entry_field(manifest_path, level_id, "stage1_metrics", metrics)
    elif stage == "stage2":
        metrics["steps_to_converge"] = artifacts.steps_to_converge
        upsert_entry_field(
            manifest_path,
            level_id,
            "stage2_checkpoint",
            portable_manifest_path(checkpoint_out_path),
        )
        upsert_entry_field(
            manifest_path,
            level_id,
            "onnx_export_path",
            portable_manifest_path(artifacts.checkpoint.onnx_export_path),
        )
        upsert_entry_field(manifest_path, level_id, "stage2_metrics", metrics)
    elif stage == "coldstart":
        metrics["steps_to_converge"] = artifacts.steps_to_converge
        upsert_entry_field(manifest_path, level_id, "coldstart_baseline_metrics", metrics)
    print(f"[cli] {stage} run for level={level_id} complete ({execution_label}).")
    print(f"    checkpoint marker: {checkpoint_out_path}")
    print(f"    ONNX export: {artifacts.checkpoint.onnx_export_path}")
    print(f"    reward curve: {artifacts.reward_curve_path}")
    print(f"    manifest: {manifest_path}")
    return 0


def _run_stage(
    level_id: str,
    stage: str,
    warm_start: bool,
    checkpoint_in: str | None,
    checkpoint_out: str,
    num_envs: int | None,
    output_manifest: str,
    episodes: int,
    seed: int,
    execution_mode: str,
    training_config_path: str | None,
    results_dir: str | None,
    run_id_override: str | None,
    trainer_executable: str | None,
    torch_device: str | None,
    env_max_steps: int | None,
    remote_config_path: str | None,
    remote_port: int | None,
) -> int:
    validate_level_id(level_id)

    if episodes < 1:
        # Without this check, --episodes 0 silently "succeeds": an empty
        # reward curve, final_mean_reward=0.0, a manifest entry that looks
        # like a legitimate (if terrible) result instead of an obvious
        # operator mistake (a typo'd flag value, a script bug passing the
        # wrong variable) — confirmed by actually running it.
        raise CliUsageError(f"--episodes must be >= 1, got {episodes}")

    if checkpoint_in is not None and not Path(checkpoint_in).exists():
        # Without this check, the fake-trainer fallback below would happily
        # run a "warm start" whose speed advantage is purely a function of
        # the warm_start=True flag, NOT of any real checkpoint content —
        # silently fabricating a fast-convergence result for a Stage 2 run
        # that never actually had a Stage 1 checkpoint to fine-tune from.
        # That is exactly the kind of confusing, hard-to-trace bug this
        # check exists to prevent: fail loudly here, not three steps later
        # when Gate 2 reports a suspicious pass.
        raise CliUsageError(
            f"--checkpoint-in {checkpoint_in!r} does not exist. Run stage1 first (or double-check the path) "
            "before fine-tuning/comparing against it — proceeding without this would silently fabricate a "
            "result, since the fake-trainer fallback's warm-start speedup does not depend on the checkpoint "
            "file's actual contents."
        )

    build_path = find_unity_build(level_id)
    resolved_trainer = trainer_executable or find_mlagents_learn()
    if execution_mode == "remote":
        if build_path is None:
            raise CliUsageError(
                f"--execution-mode remote requires the Mac Unity build at "
                f"{UNITY_BUILDS_DIR / level_id}"
            )
        remote = load_remote_config(
            remote_config_path or CONFIGS_DIR / "remote_execution.yaml"
        )
        if training_config_path:
            selected_local_config = Path(training_config_path).expanduser().resolve(strict=True)
            try:
                remote_config_relative = selected_local_config.relative_to(REPO_ROOT).as_posix()
            except ValueError as error:
                raise CliUsageError(
                    "Remote training config must be tracked inside the repository"
                ) from error
            remote = replace(remote, training_config_path=remote_config_relative)
        local_remote_training_config = REPO_ROOT / remote.training_config_path
        training_config = load_training_config(local_remote_training_config)
        effective_num_envs = num_envs or training_config["env_settings"]["num_envs"]
        if effective_num_envs != 1:
            raise CliUsageError(
                "One remote CLI session owns one external Unity connection; "
                "launch concurrent sessions with unique allocated ports instead"
            )

        run_id = run_id_override or f"{level_id}_{stage}_{time.time_ns()}"
        if not _LEVEL_ID_PATTERN.match(run_id):
            raise CliUsageError(
                f"Invalid --run-id {run_id!r}: use letters, digits, underscore, or hyphen."
            )
        initialize_from_run_id = None
        if checkpoint_in is not None:
            initialize_from_run_id = read_checkpoint_reference(
                Path(checkpoint_in).expanduser().resolve(strict=False)
            ).run_id

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
            run_id=run_id,
            requested_port=remote_port,
        )
        verify_remote_port_available(remote, port)
        remote_command = build_remote_trainer_command(
            remote,
            run_id=run_id,
            port=port,
            num_envs=effective_num_envs,
            initialize_from_run_id=initialize_from_run_id,
            inference=False,
            torch_device=torch_device or "cuda",
        )
        player = local_player_executable(build_path)
        unity_command = build_local_unity_command(
            player,
            port=port,
            env_max_steps=env_max_steps,
        )
        print(
            f"[cli] Remote GB10 policy mode: Unity={player}, "
            f"trainer={remote.ssh_target}, port={port}"
        )
        return_code = run_remote_policy_session(
            remote,
            remote_args=remote_command,
            unity_args=unity_command,
            port=port,
        )
        if return_code != 0:
            return return_code

        checkpoint_out_path = Path(checkpoint_out).expanduser().resolve(strict=False)
        local_results_dir = checkpoint_out_path.parent / ".remote-results"
        fetch_remote_run(
            remote,
            run_id=run_id,
            local_results_dir=local_results_dir,
        )
        artifacts = collect_real_training_artifacts(
            results_dir=local_results_dir,
            run_id=run_id,
            checkpoint_out=checkpoint_out_path,
        )
        return _record_real_artifacts(
            stage=stage,
            level_id=level_id,
            checkpoint_out_path=checkpoint_out_path,
            output_manifest=output_manifest,
            artifacts=artifacts,
            execution_label="remote GB10 ML-Agents",
        )

    use_real = execution_mode == "real" or (
        execution_mode == "auto" and build_path is not None and resolved_trainer is not None
    )
    if execution_mode == "real":
        if build_path is None:
            raise CliUsageError(
                f"--execution-mode real requires a Unity build at {UNITY_BUILDS_DIR / level_id}"
            )
        if resolved_trainer is None:
            raise CliUsageError(
                "--execution-mode real requires mlagents-learn on PATH or "
                "--trainer-executable pointing to it."
            )

    if use_real:
        assert build_path is not None
        assert resolved_trainer is not None
        checkpoint_out_path = Path(checkpoint_out).expanduser().resolve(strict=False)
        initialize_from_run_id = None
        prior_results_dir = None
        if checkpoint_in is not None:
            prior = read_checkpoint_reference(Path(checkpoint_in).expanduser().resolve(strict=False))
            initialize_from_run_id = prior.run_id
            prior_results_dir = prior.results_dir

        if results_dir is not None:
            resolved_results_dir = Path(results_dir).expanduser().resolve(strict=False)
            if prior_results_dir is not None and resolved_results_dir != prior_results_dir:
                raise CliUsageError(
                    f"--results-dir {resolved_results_dir} does not contain the warm-start "
                    f"run; checkpoint {checkpoint_in} requires {prior_results_dir}."
                )
        elif prior_results_dir is not None:
            resolved_results_dir = prior_results_dir
        else:
            resolved_results_dir = checkpoint_out_path.parent / ".mlagents-results"

        run_id = run_id_override or f"{level_id}_{stage}_{time.time_ns()}"
        if not _LEVEL_ID_PATTERN.match(run_id):
            raise CliUsageError(
                f"Invalid --run-id {run_id!r}: use letters, digits, underscore, or hyphen."
            )
        selected_training_config = (
            Path(training_config_path).expanduser().resolve(strict=True)
            if training_config_path
            else CONFIGS_DIR / "training_config.yaml"
        )
        training_config = load_training_config(selected_training_config)
        effective_num_envs = num_envs or training_config["env_settings"]["num_envs"]
        return_code = run_real_training(
            env_path=build_path,
            run_id=run_id,
            num_envs=effective_num_envs,
            initialize_from_run_id=initialize_from_run_id,
            training_config_path=selected_training_config,
            results_dir=resolved_results_dir,
            trainer_executable=resolved_trainer,
            torch_device=torch_device,
            env_max_steps=env_max_steps,
        )
        if return_code != 0:
            return return_code

        artifacts: RealTrainingArtifacts = collect_real_training_artifacts(
            results_dir=resolved_results_dir,
            run_id=run_id,
            checkpoint_out=checkpoint_out_path,
        )
        return _record_real_artifacts(
            stage=stage,
            level_id=level_id,
            checkpoint_out_path=checkpoint_out_path,
            output_manifest=output_manifest,
            artifacts=artifacts,
            execution_label="local ML-Agents",
        )

    if execution_mode == "fake":
        print("[cli] Explicit fake execution mode selected.")
    elif build_path is None:
        print(f"[cli] No Unity build found at {UNITY_BUILDS_DIR / level_id} — using fake trainer fallback.")
    elif resolved_trainer is None:
        print("[cli] mlagents-learn not found on PATH — using fake trainer fallback (see rl/README.md).")

    piece_config = load_piece_config()
    reward_config = load_reward_config()
    observation_config = load_observation_config()

    # telemetry.schema.json's `stage` enum only knows stage1/stage2 (spec §2.1's
    # two-stage method) — a cold-start baseline run is methodologically a
    # Stage-2-shaped run against a real level (just without --initialize-from),
    # so its telemetry is tagged "stage2". The manifest write below still keys
    # off the original `stage` value ("coldstart") to land in the correct
    # dedicated manifest field.
    telemetry_stage = "stage1" if stage == "stage1" else "stage2"

    result = run_fake_training(
        level_id=level_id,
        stage=telemetry_stage,
        checkpoint_path=checkpoint_out,
        piece_config=piece_config,
        reward_config=reward_config,
        observation_config=observation_config,
        num_episodes=episodes,
        warm_start=warm_start,
        seed=seed,
    )

    checkpoint_out_path = Path(checkpoint_out)
    checkpoint_out_path.parent.mkdir(parents=True, exist_ok=True)
    checkpoint_out_path.write_text(
        f"fake checkpoint marker for level={level_id} stage={stage} "
        f"final_mean_reward={result.final_mean_reward}\n",
        encoding="utf-8",
    )

    reward_curve_path = checkpoint_out_path.with_suffix(checkpoint_out_path.suffix + ".reward_curve.json")
    save_reward_curve(result.reward_curve, reward_curve_path)

    manifest_path = Path(output_manifest)
    metrics = {"final_mean_reward": result.final_mean_reward, "training_steps": result.training_steps}
    if stage == "stage1":
        upsert_entry_field(
            manifest_path,
            level_id,
            "stage1_checkpoint",
            portable_manifest_path(checkpoint_out_path),
        )
        upsert_entry_field(manifest_path, level_id, "stage1_metrics", metrics)
    elif stage == "stage2":
        metrics["steps_to_converge"] = result.steps_to_converge
        upsert_entry_field(
            manifest_path,
            level_id,
            "stage2_checkpoint",
            portable_manifest_path(checkpoint_out_path),
        )
        upsert_entry_field(manifest_path, level_id, "stage2_metrics", metrics)
    elif stage == "coldstart":
        metrics["steps_to_converge"] = result.steps_to_converge
        upsert_entry_field(manifest_path, level_id, "coldstart_baseline_metrics", metrics)
    else:
        raise ValueError(f"Unknown stage: {stage!r}")

    print(f"[cli] {stage} run for level={level_id} complete (fake trainer).")
    print(f"    final_mean_reward: {result.final_mean_reward}")
    print(f"    training_steps: {result.training_steps}")
    print(f"    steps_to_converge: {result.steps_to_converge}")
    print(f"    reward_curve written to: {reward_curve_path}")
    print(f"    manifest updated at: {manifest_path}")
    return 0


def _add_common_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--level-id", required=True)
    parser.add_argument("--checkpoint-out", required=True)
    parser.add_argument("--num-envs", type=int, default=None)
    parser.add_argument("--output-manifest", required=True)
    parser.add_argument("--episodes", type=int, default=150, help="fake-trainer-only, ignored on the real mlagents path")
    parser.add_argument("--seed", type=int, default=0, help="fake-trainer-only, ignored on the real mlagents path")
    parser.add_argument(
        "--execution-mode",
        choices=("auto", "real", "remote", "fake"),
        default=os.environ.get("PLAYTESTER_RL_EXECUTION_MODE", "auto"),
    )
    parser.add_argument(
        "--training-config",
        default=os.environ.get("PLAYTESTER_RL_TRAINING_CONFIG"),
        help="optional ML-Agents YAML override (useful for bounded smoke runs)",
    )
    parser.add_argument(
        "--results-dir",
        default=os.environ.get("PLAYTESTER_RL_RESULTS_DIR"),
        help="optional ML-Agents results directory",
    )
    parser.add_argument("--run-id", help="optional ML-Agents run ID override")
    parser.add_argument(
        "--trainer-executable",
        default=os.environ.get("PLAYTESTER_MLAGENTS_LEARN"),
        help="path to mlagents-learn when it is not on PATH",
    )
    parser.add_argument(
        "--torch-device",
        default=os.environ.get("PLAYTESTER_TORCH_DEVICE"),
        help="ML-Agents torch device, e.g. cpu or cuda",
    )
    parser.add_argument(
        "--env-max-steps",
        type=int,
        default=(
            int(os.environ["PLAYTESTER_ENV_MAX_STEPS"])
            if "PLAYTESTER_ENV_MAX_STEPS" in os.environ
            else None
        ),
        help="optional per-episode Unity MaxStep override",
    )
    parser.add_argument(
        "--remote-config",
        default=os.environ.get("PLAYTESTER_REMOTE_CONFIG"),
        help="SSH/Tailscale remote execution YAML (required only in remote mode)",
    )
    parser.add_argument(
        "--remote-port",
        type=int,
        default=(
            int(os.environ["PLAYTESTER_REMOTE_PORT"])
            if "PLAYTESTER_REMOTE_PORT" in os.environ
            else None
        ),
        help="optional explicit tunnel/trainer port; otherwise allocated per run",
    )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="playtester_rl.cli")
    subparsers = parser.add_subparsers(dest="command", required=True)

    stage1 = subparsers.add_parser("stage1", help="Stage 1 generalizer training (train_stage1.sh)")
    _add_common_args(stage1)

    stage2 = subparsers.add_parser("stage2", help="Stage 2 fine-tune from a Stage 1 checkpoint (finetune_stage2.sh)")
    _add_common_args(stage2)
    stage2.add_argument("--checkpoint-in", required=True)

    coldstart = subparsers.add_parser("coldstart", help="Cold-start baseline for Gate 2 comparison (baseline_coldstart.sh)")
    _add_common_args(coldstart)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        if args.command == "stage1":
            return _run_stage(
                level_id=args.level_id,
                stage="stage1",
                warm_start=False,
                checkpoint_in=None,
                checkpoint_out=args.checkpoint_out,
                num_envs=args.num_envs,
                output_manifest=args.output_manifest,
                episodes=args.episodes,
                seed=args.seed,
                execution_mode=args.execution_mode,
                training_config_path=args.training_config,
                results_dir=args.results_dir,
                run_id_override=args.run_id,
                trainer_executable=args.trainer_executable,
                torch_device=args.torch_device,
                env_max_steps=args.env_max_steps,
                remote_config_path=args.remote_config,
                remote_port=args.remote_port,
            )
        elif args.command == "stage2":
            return _run_stage(
                level_id=args.level_id,
                stage="stage2",
                warm_start=True,
                checkpoint_in=args.checkpoint_in,
                checkpoint_out=args.checkpoint_out,
                num_envs=args.num_envs,
                output_manifest=args.output_manifest,
                episodes=args.episodes,
                seed=args.seed,
                execution_mode=args.execution_mode,
                training_config_path=args.training_config,
                results_dir=args.results_dir,
                run_id_override=args.run_id,
                trainer_executable=args.trainer_executable,
                torch_device=args.torch_device,
                env_max_steps=args.env_max_steps,
                remote_config_path=args.remote_config,
                remote_port=args.remote_port,
            )
        elif args.command == "coldstart":
            return _run_stage(
                level_id=args.level_id,
                stage="coldstart",
                warm_start=False,
                checkpoint_in=None,
                checkpoint_out=args.checkpoint_out,
                num_envs=args.num_envs,
                output_manifest=args.output_manifest,
                episodes=args.episodes,
                seed=args.seed,
                execution_mode=args.execution_mode,
                training_config_path=args.training_config,
                results_dir=args.results_dir,
                run_id_override=args.run_id,
                trainer_executable=args.trainer_executable,
                torch_device=args.torch_device,
                env_max_steps=args.env_max_steps,
                remote_config_path=args.remote_config,
                remote_port=args.remote_port,
            )
        else:
            parser.error(f"Unknown command: {args.command}")
            return 2
    except (
        CliUsageError,
        ConfigValidationError,
        TelemetryValidationError,
        ManifestValidationError,
        RealTrainingError,
        RemoteExecutionError,
    ) as e:
        # These are known, "expected" failure modes with a clear message
        # already attached — print cleanly and exit 2, rather than a raw
        # Python traceback a teammate has to parse at 2am during the
        # unattended sleep-window run. Anything NOT one of these types is a
        # real bug and should surface with its full traceback, not be
        # silently swallowed here.
        print(f"[cli] ERROR ({type(e).__name__}): {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
