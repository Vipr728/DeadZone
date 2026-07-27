from __future__ import annotations

import json
import uuid
from pathlib import Path
from typing import Sequence

from conftest import StaticLLM, load_fixture, make_report
from playtester_infra.openclaw_skill import LevelWatcher
from playtester_infra.orchestration import CommandResult, process_level_export


class FakeRunner:
    def __init__(
        self,
        telemetry_dir: Path,
        *,
        training_code: int = 0,
        playtest_code: int = 0,
        write_telemetry: bool = True,
    ) -> None:
        self.telemetry_dir = telemetry_dir
        self.training_code = training_code
        self.playtest_code = playtest_code
        self.write_telemetry = write_telemetry
        self.commands: list[list[str]] = []

    def run(self, command: Sequence[str], *, cwd: Path) -> CommandResult:
        self.commands.append(list(command))
        if len(self.commands) % 2 == 1:
            return CommandResult(self.training_code, stderr="trainer exploded")
        if self.write_telemetry and self.playtest_code == 0:
            level_id = command[command.index("--level") + 1]
            fixture_name = (
                "level_b_death_cluster.json"
                if level_id == "level_b"
                else "level_a_normal.json"
            )
            document = load_fixture(fixture_name)
            document["level_id"] = level_id
            document["run_id"] = str(uuid.UUID(int=len(self.commands)))
            self.telemetry_dir.mkdir(parents=True, exist_ok=True)
            (self.telemetry_dir / f"{level_id}_run.json").write_text(
                json.dumps(document),
                encoding="utf-8",
            )
        return CommandResult(self.playtest_code, stderr="playtest exploded")


def _prepare(config_factory, level_id: str = "level_a"):
    config_path, directories = config_factory()
    build = directories["builds"] / level_id / f"{level_id}.x86_64"
    build.parent.mkdir(parents=True)
    build.write_bytes(b"unity-build")
    export = directories["exports"] / level_id / "level_export.json"
    export.parent.mkdir(parents=True)
    export.write_text(
        json.dumps(
            {
                "level_id": level_id,
                "build_path": str(build),
                "scene_path": f"Assets/Scenes/{'LevelA' if level_id == 'level_a' else 'LevelB'}.unity",
                "exported_at": "2026-07-26T12:00:00Z",
            }
        ),
        encoding="utf-8",
    )
    checkpoint = directories["checkpoints"] / "stage1" / f"{level_id}.ckpt"
    checkpoint.parent.mkdir(parents=True)
    checkpoint.write_bytes(b"stage-1")
    return config_path, directories, export


def test_success_flow_uses_locked_training_flags_and_is_idempotent(config_factory):
    config_path, directories, export = _prepare(config_factory)
    runner = FakeRunner(directories["telemetry"])
    llm = StaticLLM(make_report("level_a"))

    result = process_level_export(
        str(export),
        str(config_path),
        llm_client=llm,
        command_runner=runner,
    )
    assert result.success
    assert result.exit_code == 0
    assert Path(result.report_path).is_file()
    training = runner.commands[0]
    for flag in (
        "--level-id",
        "--checkpoint-in",
        "--checkpoint-out",
        "--num-envs",
        "--output-manifest",
    ):
        assert flag in training
    assert training[training.index("--execution-mode") + 1] == "real"
    assert runner.commands[1][0] == str(directories["builds"] / "level_a" / "level_a.x86_64")

    cached = process_level_export(
        str(export),
        str(config_path),
        llm_client=llm,
        command_runner=runner,
    )
    assert cached.success and cached.cached
    assert len(runner.commands) == 2
    assert len(llm.prompts) == 1


def test_level_b_end_to_end_planted_issue(config_factory):
    config_path, directories, export = _prepare(config_factory, "level_b")
    result = process_level_export(
        str(export),
        str(config_path),
        llm_client=StaticLLM(make_report("level_b", planted=True)),
        command_runner=FakeRunner(directories["telemetry"]),
    )
    assert result.success
    report = json.loads(Path(result.report_path).read_text(encoding="utf-8"))
    assert report["planted_issue_detected"]["detected"] is True


def test_training_failure_records_nonzero_result(config_factory):
    config_path, directories, export = _prepare(config_factory)
    runner = FakeRunner(directories["telemetry"], training_code=17)
    result = process_level_export(
        str(export),
        str(config_path),
        llm_client=StaticLLM(make_report("level_a")),
        command_runner=runner,
    )
    assert not result.success
    assert result.exit_code == 17
    assert "fine-tuning failed" in result.error
    assert len(runner.commands) == 1
    cached = process_level_export(
        str(export),
        str(config_path),
        llm_client=StaticLLM(make_report("level_a")),
        command_runner=runner,
    )
    assert not cached.success and cached.cached
    assert len(runner.commands) == 1


def test_missing_telemetry_is_an_explicit_failure(config_factory):
    config_path, directories, export = _prepare(config_factory)
    result = process_level_export(
        str(export),
        str(config_path),
        llm_client=StaticLLM(make_report("level_a")),
        command_runner=FakeRunner(directories["telemetry"], write_telemetry=False),
    )
    assert not result.success
    assert result.exit_code == 1
    assert "no new schema-valid" in result.error


def test_malformed_report_is_an_explicit_failure(config_factory):
    config_path, directories, export = _prepare(config_factory)
    result = process_level_export(
        str(export),
        str(config_path),
        llm_client=StaticLLM({"level_id": "level_a"}),
        command_runner=FakeRunner(directories["telemetry"]),
    )
    assert not result.success
    assert result.exit_code == 1
    assert "report failed validation" in result.error
    assert not list(directories["reports"].glob("level_a_*.json"))


def test_watcher_suppresses_duplicate_unchanged_export_events(config_factory):
    config_path, directories, export = _prepare(config_factory)
    runner = FakeRunner(directories["telemetry"])
    watcher = LevelWatcher(
        config_path,
        llm_client=StaticLLM(make_report("level_a")),
        command_runner=runner,
    )
    first = watcher.process_event(export)
    duplicate = watcher.process_event(export)
    assert first is not None and first.success
    assert duplicate is None
    assert len(runner.commands) == 2

    marker = json.loads(export.read_text(encoding="utf-8"))
    marker["exported_at"] = "2026-07-26T12:01:00Z"
    export.write_text(json.dumps(marker), encoding="utf-8")
    changed = watcher.process_event(export)
    assert changed is not None
    assert len(runner.commands) == 4


def test_marker_cannot_point_to_a_different_level_build(config_factory):
    config_path, _, export = _prepare(config_factory)
    marker = json.loads(export.read_text(encoding="utf-8"))
    marker["build_path"] = marker["build_path"].replace("level_a", "level_b")
    export.write_text(json.dumps(marker), encoding="utf-8")

    result = process_level_export(str(export), str(config_path))

    assert not result.success
    assert result.exit_code == 2
    assert "build_path must be" in result.error
