"""Tests for cli.py — build-detection helpers (pure, no subprocess), the
mlagents command-string builder (pure), and the full fake-trainer fallback
path invoked exactly the way rl/scripts/*.sh will invoke it (argv -> main())."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from playtester_rl.checkpoint_manifest import get_entry
from playtester_rl.cli import (
    CliUsageError,
    build_mlagents_command,
    find_mlagents_learn,
    find_unity_build,
    main,
    mlagents_learn_available,
    portable_manifest_path,
    validate_level_id,
)
from playtester_rl.real_training import (
    CheckpointReference,
    RealTrainingArtifacts,
)
from playtester_rl.remote_execution import RemoteExecutionConfig


@pytest.fixture(autouse=True)
def explicit_fake_mode_for_cli_unit_tests(monkeypatch):
    """Unit tests never become real training jobs just because a build exists."""
    monkeypatch.setenv("PLAYTESTER_RL_EXECUTION_MODE", "fake")


# ---------------------------------------------------------------------------
# Build detection — pure, no subprocess involved
# ---------------------------------------------------------------------------


def test_find_unity_build_returns_none_when_absent(monkeypatch, tmp_path):
    import playtester_rl.cli as cli_module

    # Keep the absence case independent from any real Unity build generated
    # by an integration run in the working tree.
    monkeypatch.setattr(cli_module, "UNITY_BUILDS_DIR", tmp_path / "Builds")
    assert find_unity_build("level_a") is None


def test_find_unity_build_finds_exe(monkeypatch, tmp_path):
    import playtester_rl.cli as cli_module

    fake_builds_dir = tmp_path / "Builds"
    (fake_builds_dir / "level_a").mkdir(parents=True)
    (fake_builds_dir / "level_a" / "level_a.exe").write_text("fake binary", encoding="utf-8")
    monkeypatch.setattr(cli_module, "UNITY_BUILDS_DIR", fake_builds_dir)

    found = find_unity_build("level_a")
    assert found is not None
    assert found.name == "level_a.exe"


def test_find_unity_build_returns_none_for_unrelated_level(monkeypatch, tmp_path):
    import playtester_rl.cli as cli_module

    fake_builds_dir = tmp_path / "Builds"
    (fake_builds_dir / "level_a").mkdir(parents=True)
    (fake_builds_dir / "level_a" / "level_a.exe").write_text("x", encoding="utf-8")
    monkeypatch.setattr(cli_module, "UNITY_BUILDS_DIR", fake_builds_dir)

    assert find_unity_build("level_b") is None


def test_mlagents_learn_available_reflects_path(monkeypatch):
    import playtester_rl.cli as cli_module

    monkeypatch.setattr(cli_module, "REPO_ROOT", Path("/missing"))
    monkeypatch.delenv("PLAYTESTER_MLAGENTS_LEARN", raising=False)
    monkeypatch.setattr(cli_module.shutil, "which", lambda name: None)
    assert mlagents_learn_available() is False

    monkeypatch.setattr(cli_module.shutil, "which", lambda name: "/usr/bin/mlagents-learn")
    assert mlagents_learn_available() is True


def test_find_mlagents_learn_honors_explicit_configuration(monkeypatch):
    monkeypatch.setenv("PLAYTESTER_MLAGENTS_LEARN", "/opt/mlagents-learn")
    assert find_mlagents_learn() == "/opt/mlagents-learn"


def test_portable_manifest_path_is_relative_inside_repository(monkeypatch, tmp_path):
    import playtester_rl.cli as cli_module

    monkeypatch.setattr(cli_module, "REPO_ROOT", tmp_path)
    artifact = tmp_path / "rl/checkpoints/stage1/generalizer.ckpt"
    assert portable_manifest_path(artifact) == "rl/checkpoints/stage1/generalizer.ckpt"


# ---------------------------------------------------------------------------
# mlagents command construction — pure function, fully testable without
# mlagents actually installed
# ---------------------------------------------------------------------------


def test_build_mlagents_command_without_checkpoint_in():
    cmd = build_mlagents_command(
        env_path=Path("Builds/level_a/level_a.exe"),
        run_id="level_a_stage1",
        num_envs=4,
        checkpoint_in=None,
        training_config_path=Path("rl/configs/training_config.yaml"),
    )
    assert cmd[0] == "mlagents-learn"
    assert "--num-envs=4" in cmd
    assert "--run-id=level_a_stage1" in cmd
    assert not any(arg.startswith("--initialize-from") for arg in cmd)


def test_build_mlagents_command_with_checkpoint_in():
    cmd = build_mlagents_command(
        env_path=Path("Builds/level_a/level_a.exe"),
        run_id="level_a_stage2",
        num_envs=2,
        checkpoint_in="checkpoints/level_a/stage1",
        training_config_path=Path("rl/configs/training_config.yaml"),
    )
    assert "--initialize-from=checkpoints/level_a/stage1" in cmd


def test_build_mlagents_command_includes_real_smoke_controls():
    cmd = build_mlagents_command(
        env_path=Path("Builds/level_a/level_a.app"),
        run_id="level_a_stage1_smoke",
        num_envs=1,
        checkpoint_in=None,
        training_config_path=Path("tiny.yaml"),
        results_dir=Path("results"),
        trainer_executable="/venv/bin/mlagents-learn",
        torch_device="cpu",
        env_max_steps=16,
    )
    assert cmd[0] == "/venv/bin/mlagents-learn"
    assert "--results-dir=results" in cmd
    assert "--torch-device=cpu" in cmd
    assert cmd[-3:] == ["--env-args", "--mlagents-max-steps", "16"]


def test_remote_mode_wires_gb10_trainer_to_local_unity(monkeypatch, tmp_path):
    import playtester_rl.cli as cli_module

    repo = tmp_path / "repo"
    training = repo / "rl/configs/training_config.remote_smoke.yaml"
    training.parent.mkdir(parents=True)
    training.write_text(
        "behaviors: {PlaytestAgent: {}}\n"
        "env_settings: {num_envs: 1, base_port: 5004}\n",
        encoding="utf-8",
    )
    build = repo / "unity/Builds/level_a/level_a.app"
    build.mkdir(parents=True)
    onnx = tmp_path / "PlaytestAgent.onnx"
    onnx.write_bytes(b"onnx")
    trainer_output = tmp_path / "remote-results/run"
    trainer_output.mkdir(parents=True)
    remote = RemoteExecutionConfig(
        tailscale_hostname="gb10.tail.example",
        ssh_username="nvidia",
        repository_path="GB10-project",
        results_dir="rl/checkpoints/remote-results",
        training_config_path="rl/configs/training_config.remote_smoke.yaml",
        trainer_executable="rl/.venv-mlagents/bin/mlagents-learn",
        base_port=5004,
        connect_timeout_seconds=2,
        command_timeout_seconds=30,
        require_direct_tailscale=True,
    )
    captured = {}

    monkeypatch.setattr(cli_module, "REPO_ROOT", repo)
    monkeypatch.setattr(cli_module, "find_unity_build", lambda level_id: build)
    monkeypatch.setattr(cli_module, "load_remote_config", lambda path: remote)
    monkeypatch.setattr(cli_module, "verify_remote_preflight", lambda *a, **k: None)
    monkeypatch.setattr(cli_module, "allocate_remote_port", lambda *a, **k: 5123)
    monkeypatch.setattr(cli_module, "verify_remote_port_available", lambda *a, **k: None)
    monkeypatch.setattr(cli_module, "local_player_executable", lambda path: Path("/unity"))
    monkeypatch.setattr(cli_module, "fetch_remote_run", lambda *a, **k: trainer_output)
    monkeypatch.setattr(
        cli_module,
        "collect_real_training_artifacts",
        lambda **kwargs: RealTrainingArtifacts(
            checkpoint=CheckpointReference(
                run_id="remote-run",
                results_dir=tmp_path / "remote-results",
                trainer_output_dir=trainer_output,
                onnx_export_path=onnx,
            ),
            final_mean_reward=1.0,
            training_steps=64,
            steps_to_converge=64,
            reward_curve_path=tmp_path / "curve.json",
        ),
    )

    def run_session(config, *, remote_args, unity_args, port):
        captured.update(remote_args=remote_args, unity_args=unity_args, port=port)
        return 0

    monkeypatch.setattr(cli_module, "run_remote_policy_session", run_session)
    monkeypatch.setattr(
        cli_module.subprocess,
        "run",
        lambda *a, **k: type("Result", (), {"stdout": "abc123\n"})(),
    )
    checkpoint = repo / "rl/checkpoints/stage1/generalizer.ckpt"
    manifest = tmp_path / "manifest.json"

    exit_code = main(
        [
            "stage1",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(checkpoint),
            "--output-manifest",
            str(manifest),
            "--execution-mode",
            "remote",
            "--run-id",
            "remote-run",
        ]
    )

    assert exit_code == 0
    assert captured["port"] == 5123
    assert "--base-port=5123" in captured["remote_args"]
    assert not any(part.startswith("--env=") for part in captured["remote_args"])
    assert captured["unity_args"][:5] == [
        "/unity",
        "-batchmode",
        "-nographics",
        "--mlagents-port",
        "5123",
    ]


# ---------------------------------------------------------------------------
# Full fallback path via main() — this is exactly how rl/scripts/*.sh invoke it
# ---------------------------------------------------------------------------


def test_main_stage1_fallback_writes_checkpoint_and_manifest(tmp_path):
    checkpoint_out = tmp_path / "checkpoints" / "level_a" / "stage1"
    manifest_path = tmp_path / "manifest.json"

    exit_code = main(
        [
            "stage1",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(checkpoint_out),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "20",
            "--seed",
            "1",
        ]
    )

    assert exit_code == 0
    assert checkpoint_out.exists()
    reward_curve_path = checkpoint_out.with_suffix(checkpoint_out.suffix + ".reward_curve.json")
    assert reward_curve_path.exists()
    curve = json.loads(reward_curve_path.read_text(encoding="utf-8"))
    assert len(curve) == 20

    entry = get_entry(manifest_path, "level_a")
    assert entry is not None
    assert entry["stage1_checkpoint"] == str(checkpoint_out)
    assert entry["stage1_metrics"]["training_steps"] > 0
    assert entry["stage2_metrics"] is None
    assert entry["coldstart_baseline_metrics"] is None


def test_main_stage2_requires_checkpoint_in():
    with pytest.raises(SystemExit):
        main(["stage2", "--level-id", "level_a", "--checkpoint-out", "x", "--output-manifest", "y"])


def test_main_stage2_fallback_writes_manifest_without_clobbering_stage1(tmp_path):
    manifest_path = tmp_path / "manifest.json"

    main(
        [
            "stage1",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(tmp_path / "ckpt_stage1"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "15",
            "--seed",
            "2",
        ]
    )
    main(
        [
            "stage2",
            "--level-id",
            "level_a",
            "--checkpoint-in",
            str(tmp_path / "ckpt_stage1"),
            "--checkpoint-out",
            str(tmp_path / "ckpt_stage2"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "15",
            "--seed",
            "2",
        ]
    )

    entry = get_entry(manifest_path, "level_a")
    assert entry["stage1_checkpoint"] == str(tmp_path / "ckpt_stage1"), "stage2 run must not clobber stage1's fields"
    assert entry["stage2_checkpoint"] == str(tmp_path / "ckpt_stage2")
    assert entry["stage2_metrics"] is not None


def test_main_coldstart_only_writes_coldstart_metrics_field(tmp_path):
    manifest_path = tmp_path / "manifest.json"
    main(
        [
            "coldstart",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(tmp_path / "ckpt_coldstart"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "15",
            "--seed",
            "5",
        ]
    )
    entry = get_entry(manifest_path, "level_a")
    assert entry["coldstart_baseline_metrics"] is not None
    # Per the manifest schema, cold-start has no dedicated checkpoint field —
    # only its metrics get recorded.
    assert entry["stage1_checkpoint"] is None
    assert entry["stage2_checkpoint"] is None


def test_full_gate2_pipeline_via_cli_stage2_beats_coldstart(tmp_path):
    """End-to-end: run stage1, then stage2 (warm_start) and coldstart on the
    same level/seed via the CLI exactly as scripts/*.sh would, then confirm
    the resulting manifest entry is what gate_eval.gate2_check expects and
    that it actually passes."""
    from playtester_rl.gate_eval import gate2_check

    manifest_path = tmp_path / "manifest.json"

    main(
        [
            "stage1",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(tmp_path / "ckpt_stage1"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "60",
            "--seed",
            "9",
        ]
    )
    main(
        [
            "stage2",
            "--level-id",
            "level_a",
            "--checkpoint-in",
            str(tmp_path / "ckpt_stage1"),
            "--checkpoint-out",
            str(tmp_path / "ckpt_stage2"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "60",
            "--seed",
            "9",
        ]
    )
    main(
        [
            "coldstart",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(tmp_path / "ckpt_coldstart"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "60",
            "--seed",
            "9",
        ]
    )

    entry = get_entry(manifest_path, "level_a")
    result = gate2_check(entry)
    assert result.passed, result.message


# ---------------------------------------------------------------------------
# level_id safety — level_id flows unvalidated into filesystem paths and
# telemetry/manifest documents downstream tools will trust, so it must be
# rejected early if it isn't a plain identifier
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    "bad_level_id",
    ["../evil", "..\\evil", "a/b", "a\\b", "level a", "level.a", "", "lvl;rm -rf"],
)
def test_validate_level_id_rejects_unsafe_values(bad_level_id):
    with pytest.raises(CliUsageError):
        validate_level_id(bad_level_id)


@pytest.mark.parametrize("good_level_id", ["level_a", "level-b", "Level1", "a", "LEVEL_99"])
def test_validate_level_id_accepts_plain_identifiers(good_level_id):
    validate_level_id(good_level_id)  # must not raise


def test_main_rejects_path_traversal_level_id_with_clean_error(tmp_path):
    exit_code = main(
        [
            "stage1",
            "--level-id",
            "../../evil",
            "--checkpoint-out",
            str(tmp_path / "ckpt1"),
            "--output-manifest",
            str(tmp_path / "manifest.json"),
            "--episodes",
            "5",
            "--seed",
            "1",
        ]
    )
    assert exit_code == 2
    # Nothing should have been written for an input that was rejected before
    # any training or file I/O began.
    assert not (tmp_path / "ckpt1").exists()
    assert not (tmp_path / "manifest.json").exists()


# ---------------------------------------------------------------------------
# checkpoint-in existence — without this check, stage2 silently "succeeds"
# with a fabricated warm-start speedup even when no real Stage 1 checkpoint
# was ever produced (verified: the fake trainer's warm_start behavior keys
# only off the boolean flag, never off the checkpoint file's actual content)
# ---------------------------------------------------------------------------


def test_main_stage2_rejects_nonexistent_checkpoint_in(tmp_path):
    exit_code = main(
        [
            "stage2",
            "--level-id",
            "level_a",
            "--checkpoint-in",
            str(tmp_path / "never_created_by_a_real_stage1_run"),
            "--checkpoint-out",
            str(tmp_path / "ckpt2"),
            "--output-manifest",
            str(tmp_path / "manifest.json"),
            "--episodes",
            "10",
            "--seed",
            "1",
        ]
    )
    assert exit_code == 2
    assert not (tmp_path / "ckpt2").exists()
    entry = get_entry(tmp_path / "manifest.json", "level_a")
    assert entry is None, "no manifest entry should be written for a rejected stage2 run"


def test_main_stage2_accepts_checkpoint_in_that_really_exists(tmp_path):
    manifest_path = tmp_path / "manifest.json"
    main(
        [
            "stage1",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(tmp_path / "ckpt1"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "10",
            "--seed",
            "1",
        ]
    )
    exit_code = main(
        [
            "stage2",
            "--level-id",
            "level_a",
            "--checkpoint-in",
            str(tmp_path / "ckpt1"),
            "--checkpoint-out",
            str(tmp_path / "ckpt2"),
            "--output-manifest",
            str(manifest_path),
            "--episodes",
            "10",
            "--seed",
            "1",
        ]
    )
    assert exit_code == 0


# ---------------------------------------------------------------------------
# --episodes 0 — without validation this silently produces a degenerate
# empty run (0 training steps, final_mean_reward=0.0) that looks like a
# legitimate-if-terrible result rather than an obvious operator mistake
# (verified: it used to return exit code 0 and write a hollow manifest entry)
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("bad_episodes", ["0", "-5"])
def test_main_rejects_non_positive_episodes(tmp_path, bad_episodes):
    exit_code = main(
        [
            "stage1",
            "--level-id",
            "level_a",
            "--checkpoint-out",
            str(tmp_path / "ckpt1"),
            "--output-manifest",
            str(tmp_path / "manifest.json"),
            "--episodes",
            bad_episodes,
            "--seed",
            "1",
        ]
    )
    assert exit_code == 2
    assert not (tmp_path / "ckpt1").exists()
    entry = get_entry(tmp_path / "manifest.json", "level_a")
    assert entry is None
