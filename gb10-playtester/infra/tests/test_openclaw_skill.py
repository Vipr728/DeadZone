import json
import os
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from playtester_infra.config import (
    AppConfig,
    LLMConfig,
    OrchestrationConfig,
    PathsConfig,
    SandboxConfig,
    WatcherConfig,
)
from playtester_infra.openclaw_skill import (
    LevelWatcher,
    OrchestrationError,
    process_level_artifact,
)


def _config(tmp_path: Path) -> AppConfig:
    repository_root = tmp_path / "repo"
    infra_dir = repository_root / "infra"
    contracts_dir = Path(__file__).resolve().parents[2] / "contracts"
    return AppConfig(
        source_path=infra_dir / "config.yaml",
        repository_root=repository_root,
        paths=PathsConfig(
            watched_levels_dir=repository_root
            / "unity/PlaytesterProject/Exports",
            builds_dir=repository_root / "unity/PlaytesterProject/Builds",
            telemetry_dir=repository_root
            / "unity/PlaytesterProject/Telemetry",
            reports_dir=repository_root / "unity/PlaytesterProject/Reports",
            checkpoints_dir=repository_root / "rl/checkpoints",
            checkpoint_manifest=repository_root / "rl/checkpoint_manifest.json",
            contracts_dir=contracts_dir,
        ),
        llm=LLMConfig(
            backend="ollama",
            host="http://127.0.0.1:11434",
            model="test",
            gb10_model="test-large",
            use_gb10_model=False,
            timeout_seconds=10,
        ),
        sandbox=SandboxConfig(
            allowed_read_paths=(repository_root,),
            allowed_write_paths=(repository_root,),
            egress_policy="block_all",
            llm_allowlist=("localhost:11434",),
        ),
        watcher=WatcherConfig(
            pattern="*/level_export.json",
            poll_interval_seconds=0.05,
        ),
        orchestration=OrchestrationConfig(
            finetune_script=repository_root / "rl/scripts/finetune_stage2.sh",
            playtest_script=repository_root / "rl/scripts/run_playtest.sh",
            playtest_episodes=7,
            command_timeout_seconds=90,
        ),
    )


def _valid_marker(
    config: AppConfig,
    *,
    build_path: str = (
        "unity/PlaytesterProject/Builds/level_a/level_a"
    ),
) -> dict[str, str]:
    return {
        "level_id": "level_a",
        "build_path": build_path,
        "scene_path": "Assets/Scenes/LevelA.unity",
        "exported_at": "2026-07-25T08:00:00-07:00",
    }


def _write_valid_inputs(
    config: AppConfig,
    *,
    marker: dict[str, Any] | None = None,
    create_scripts: bool = True,
) -> Path:
    marker_document = marker or _valid_marker(config)
    raw_build_path = Path(str(marker_document["build_path"]))
    build = (
        raw_build_path
        if raw_build_path.is_absolute()
        else config.repository_root / raw_build_path
    )
    build.parent.mkdir(parents=True, exist_ok=True)
    build.write_text("unity build", encoding="utf-8")

    stage1 = config.repository_root / "rl/checkpoints/stage1.pt"
    stage1.parent.mkdir(parents=True, exist_ok=True)
    stage1.write_text("checkpoint", encoding="utf-8")
    config.paths.checkpoint_manifest.write_text(
        json.dumps(
            {
                "level_id": "gym",
                "stage1_checkpoint": "rl/checkpoints/stage1.pt",
                "stage2_checkpoint": None,
                "onnx_export_path": None,
                "stage1_metrics": {
                    "final_mean_reward": 3.5,
                    "training_steps": 2000,
                },
                "stage2_metrics": None,
                "coldstart_baseline_metrics": None,
            }
        ),
        encoding="utf-8",
    )

    if create_scripts:
        for script in (
            config.orchestration.finetune_script,
            config.orchestration.playtest_script,
        ):
            script.parent.mkdir(parents=True, exist_ok=True)
            script.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8")
            script.chmod(0o755)

    artifact = (
        config.paths.watched_levels_dir / "level_a" / "level_export.json"
    )
    artifact.parent.mkdir(parents=True, exist_ok=True)
    artifact.write_text(json.dumps(marker_document), encoding="utf-8")
    return artifact


def test_process_level_artifact_uses_shared_gym_stage1_from_list_manifest(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    gym_entry = json.loads(config.paths.checkpoint_manifest.read_text(encoding="utf-8"))
    level_entry = {
        **gym_entry,
        "level_id": "level_a",
        "stage1_checkpoint": None,
        "stage1_metrics": None,
    }
    config.paths.checkpoint_manifest.write_text(
        json.dumps([gym_entry, level_entry]),
        encoding="utf-8",
    )
    calls: list[tuple[list[str], dict[str, Any]]] = []

    result = process_level_artifact(
        artifact,
        config,
        lambda _: {"ok": True},
        runner=_runner_that_writes_telemetry(calls),
        invocation_id="shared-generalizer",
    )

    assert result.level_id == "level_a"
    assert calls[0][0][calls[0][0].index("--checkpoint-in") + 1] == str(
        config.repository_root / "rl/checkpoints/stage1.pt"
    )


def _runner_that_writes_telemetry(
    calls: list[tuple[list[str], dict[str, Any]]],
) -> Callable[..., object]:
    def run(command: list[str], **kwargs: Any) -> object:
        calls.append((command, kwargs))
        if "--checkpoint-out" in command:
            output = Path(command[command.index("--checkpoint-out") + 1])
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text("stage2 checkpoint", encoding="utf-8")
        if "--telemetry-out" in command:
            output = Path(command[command.index("--telemetry-out") + 1])
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text('{"level_id": "level_a"}', encoding="utf-8")
        return object()

    return run


def test_process_level_artifact_runs_the_locked_pipeline_contract(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    calls: list[tuple[list[str], dict[str, Any]]] = []
    report_calls: list[Path] = []

    def generate_report(telemetry_path: Path) -> dict[str, str]:
        report_calls.append(telemetry_path)
        return {"level_id": "level_a"}

    result = process_level_artifact(
        artifact,
        config,
        generate_report,
        runner=_runner_that_writes_telemetry(calls),
        invocation_id="test-run",
    )

    stage1 = config.repository_root / "rl/checkpoints/stage1.pt"
    expected_stage2 = (
        config.paths.checkpoints_dir / "level_a" / "stage2" / "test-run"
    )
    expected_telemetry = (
        config.paths.telemetry_dir
        / "level_a"
        / "test-run.telemetry.json"
    )
    assert [command for command, _ in calls] == [
        [
            str(config.orchestration.finetune_script),
            "--level-id",
            "level_a",
            "--checkpoint-in",
            str(stage1),
            "--checkpoint-out",
            str(expected_stage2),
                "--output-manifest",
                str(config.paths.checkpoint_manifest),
                "--execution-mode",
                "real",
            ],
        [
            str(config.orchestration.playtest_script),
            "--level-id",
            "level_a",
            "--checkpoint-in",
            str(expected_stage2),
            "--episodes",
            "7",
                "--telemetry-out",
                str(expected_telemetry),
                "--execution-mode",
                "real",
            ],
    ]
    assert all(
        kwargs == {"check": True, "timeout": 90} for _, kwargs in calls
    )
    assert report_calls == [expected_telemetry]
    assert result.level_id == "level_a"
    assert result.build_path == (
        config.paths.builds_dir / "level_a" / "level_a"
    )
    assert result.checkpoint_path == expected_stage2
    assert result.telemetry_path == expected_telemetry
    assert result.report == {"level_id": "level_a"}


def test_process_level_artifact_accepts_an_absolute_build_inside_level_dir(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    absolute_build = config.paths.builds_dir / "level_a" / "level_a"
    artifact = _write_valid_inputs(
        config,
        marker=_valid_marker(config, build_path=str(absolute_build)),
    )

    result = process_level_artifact(
        artifact,
        config,
        lambda _: {"ok": True},
        runner=_runner_that_writes_telemetry([]),
    )

    assert result.build_path == absolute_build.resolve()


@pytest.mark.parametrize(
    "marker_location",
    ["wrong_filename", "wrong_parent", "outside_watch_root"],
)
def test_process_level_artifact_rejects_a_marker_at_the_wrong_location(
    tmp_path: Path,
    marker_location: str,
) -> None:
    config = _config(tmp_path)
    valid_artifact = _write_valid_inputs(config)
    if marker_location == "wrong_filename":
        artifact = valid_artifact.with_name("marker.json")
    elif marker_location == "wrong_parent":
        artifact = (
            config.paths.watched_levels_dir
            / "some_other_level"
            / "level_export.json"
        )
    else:
        artifact = (
            config.repository_root
            / "external-markers"
            / "level_a"
            / "level_export.json"
        )
    artifact.parent.mkdir(parents=True, exist_ok=True)
    artifact.write_text(
        valid_artifact.read_text(encoding="utf-8"),
        encoding="utf-8",
    )

    with pytest.raises(
        OrchestrationError, match="marker must be located at"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry([]),
        )


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (
            lambda marker: marker.update({"unexpected": "field"}),
            "exactly",
        ),
        (
            lambda marker: marker.pop("scene_path"),
            "exactly",
        ),
        (
            lambda marker: marker.update({"scene_path": "   "}),
            "scene_path must be a non-empty string",
        ),
        (
            lambda marker: marker.update({"level_id": "NOT VALID"}),
            "safe lowercase slug",
        ),
        (
            lambda marker: marker.update(
                {"exported_at": "2026-07-25T08:00:00"}
            ),
            "timezone-aware ISO8601",
        ),
        (
            lambda marker: marker.update({"exported_at": "last Tuesday"}),
            "timezone-aware ISO8601",
        ),
    ],
)
def test_process_level_artifact_rejects_an_invalid_export_marker(
    tmp_path: Path,
    mutate: Callable[[dict[str, Any]], object],
    message: str,
) -> None:
    config = _config(tmp_path)
    marker: dict[str, Any] = _valid_marker(config)
    mutate(marker)
    artifact = _write_valid_inputs(config, marker=marker)
    calls: list[tuple[list[str], dict[str, Any]]] = []

    with pytest.raises(OrchestrationError, match=message):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry(calls),
        )

    assert calls == []


@pytest.mark.parametrize("build_path_kind", ["absolute", "traversal"])
def test_process_level_artifact_rejects_a_build_outside_its_level_directory(
    tmp_path: Path,
    build_path_kind: str,
) -> None:
    config = _config(tmp_path)
    external_build = config.repository_root / "external" / "level_a"
    if build_path_kind == "absolute":
        build_path = str(external_build)
    else:
        build_path = (
            "unity/PlaytesterProject/Builds/level_a/../../../external/level_a"
        )
    artifact = _write_valid_inputs(
        config,
        marker=_valid_marker(config, build_path=build_path),
    )

    with pytest.raises(OrchestrationError, match="outside configured build"):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry([]),
        )


def test_process_level_artifact_rejects_a_symlink_escape(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    external_build = config.repository_root / "external" / "level_a"
    external_build.parent.mkdir(parents=True)
    external_build.write_text("unity build", encoding="utf-8")
    level_build_dir = config.paths.builds_dir / "level_a"
    level_build_dir.mkdir(parents=True)
    (level_build_dir / "level_a").symlink_to(external_build)
    artifact = (
        config.paths.watched_levels_dir / "level_a" / "level_export.json"
    )
    artifact.parent.mkdir(parents=True)
    artifact.write_text(json.dumps(_valid_marker(config)), encoding="utf-8")

    with pytest.raises(OrchestrationError, match="outside configured build"):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry([]),
        )


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (
            lambda artifact, config: (
                config.paths.builds_dir / "level_a" / "level_a"
            ).unlink(),
            "build does not exist",
        ),
        (
            lambda artifact, config: config.paths.checkpoint_manifest.write_text(
                '{"level_id": "gym"}', encoding="utf-8"
            ),
            "checkpoint manifest",
        ),
        (
            lambda artifact, config: (
                config.repository_root / "rl/checkpoints/stage1.pt"
            ).unlink(),
            "Stage 1 checkpoint does not exist",
        ),
    ],
)
def test_process_level_artifact_rejects_invalid_inputs_before_running(
    tmp_path: Path,
    mutate: Callable[[Path, AppConfig], object],
    message: str,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    mutate(artifact, config)
    calls: list[tuple[list[str], dict[str, Any]]] = []

    with pytest.raises(OrchestrationError, match=message):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry(calls),
        )

    assert calls == []


def test_process_level_artifact_rejects_checkpoint_manifest_path_escape(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    outside_checkpoint = tmp_path / "outside-stage1.pt"
    outside_checkpoint.write_text("checkpoint", encoding="utf-8")
    manifest = json.loads(
        config.paths.checkpoint_manifest.read_text(encoding="utf-8")
    )
    manifest["stage1_checkpoint"] = str(outside_checkpoint)
    config.paths.checkpoint_manifest.write_text(
        json.dumps(manifest),
        encoding="utf-8",
    )
    calls: list[tuple[list[str], dict[str, Any]]] = []

    with pytest.raises(
        OrchestrationError, match="outside configured checkpoint"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry(calls),
        )

    assert calls == []


def test_process_level_artifact_rejects_checkpoint_output_symlink_escape(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    outside = tmp_path / "outside-checkpoints"
    outside.mkdir()
    (config.paths.checkpoints_dir / "level_a").symlink_to(
        outside,
        target_is_directory=True,
    )

    with pytest.raises(
        OrchestrationError, match="checkpoint output is outside"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry([]),
            invocation_id="safe-run",
        )

    assert list(outside.iterdir()) == []


def test_process_level_artifact_wraps_output_directory_error(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    config.paths.telemetry_dir.mkdir(parents=True)
    (config.paths.telemetry_dir / "level_a").write_text(
        "not a directory",
        encoding="utf-8",
    )

    with pytest.raises(
        OrchestrationError, match="prepare invocation output directories"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry([]),
            invocation_id="safe-run",
        )


def test_process_level_artifact_requires_playtest_telemetry(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    calls: list[tuple[list[str], dict[str, Any]]] = []

    def write_only_checkpoint(
        command: list[str], **kwargs: Any
    ) -> object:
        calls.append((command, kwargs))
        if "--checkpoint-out" in command:
            output = Path(command[command.index("--checkpoint-out") + 1])
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text("stage2 checkpoint", encoding="utf-8")
        return object()

    with pytest.raises(
        OrchestrationError, match="playtest did not produce telemetry"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=write_only_checkpoint,
        )


def test_process_level_artifact_requires_finetune_checkpoint(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)

    with pytest.raises(
        OrchestrationError, match="did not produce checkpoint"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=lambda *_args, **_kwargs: object(),
        )


def test_process_level_artifact_does_not_accept_stale_outputs(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    process_level_artifact(
        artifact,
        config,
        lambda _: {"ok": True},
        runner=_runner_that_writes_telemetry([]),
        invocation_id="first-run",
    )

    with pytest.raises(
        OrchestrationError, match="did not produce checkpoint"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=lambda *_args, **_kwargs: object(),
            invocation_id="second-run",
        )


def test_process_level_artifact_fails_before_running_when_script_is_missing(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config, create_scripts=False)
    calls: list[tuple[list[str], dict[str, Any]]] = []

    with pytest.raises(
        OrchestrationError, match="fine-tune script does not exist"
    ):
        process_level_artifact(
            artifact,
            config,
            lambda _: {"ok": True},
            runner=_runner_that_writes_telemetry(calls),
        )

    assert calls == []
    assert not config.paths.telemetry_dir.exists()


def test_process_level_artifact_wraps_report_failure_for_watcher_recovery(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)

    def fail_report(_: Path) -> object:
        raise RuntimeError("model is temporarily unavailable")

    with pytest.raises(
        OrchestrationError, match="report generation failed"
    ):
        process_level_artifact(
            artifact,
            config,
            fail_report,
            runner=_runner_that_writes_telemetry([]),
        )


def test_level_watcher_processes_only_nested_new_or_changed_markers(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    artifact = _write_valid_inputs(config)
    ignored = config.paths.watched_levels_dir / "level_export.json"
    ignored.write_text(artifact.read_text(encoding="utf-8"), encoding="utf-8")
    calls: list[tuple[list[str], dict[str, Any]]] = []
    watcher = LevelWatcher(
        config,
        lambda _: {"ok": True},
        runner=_runner_that_writes_telemetry(calls),
    )

    first = watcher.poll_once()
    unchanged = watcher.poll_once()
    next_mtime = artifact.stat().st_mtime_ns + 1_000_000
    os.utime(artifact, ns=(next_mtime, next_mtime))
    changed = watcher.poll_once()

    assert [result.level_id for result in first] == ["level_a"]
    assert unchanged == []
    assert [result.level_id for result in changed] == ["level_a"]
    assert len(calls) == 4


def test_level_watcher_does_not_let_bad_marker_starve_later_marker(
    tmp_path: Path,
) -> None:
    config = _config(tmp_path)
    _write_valid_inputs(config)
    bad_artifact = (
        config.paths.watched_levels_dir / "aaa" / "level_export.json"
    )
    bad_artifact.parent.mkdir(parents=True)
    bad_artifact.write_text(
        json.dumps(
            {
                **_valid_marker(config),
                "level_id": "NOT VALID",
            }
        ),
        encoding="utf-8",
    )
    watcher = LevelWatcher(
        config,
        lambda _: {"ok": True},
        runner=_runner_that_writes_telemetry([]),
    )

    results = watcher.poll_once()

    assert [result.level_id for result in results] == ["level_a"]
