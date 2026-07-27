"""Idempotent level-export orchestration independent of watcher mechanics."""

from __future__ import annotations

import hashlib
import json
import re
import subprocess
from dataclasses import asdict, dataclass, replace
from pathlib import Path
from typing import Protocol, Sequence

from playtester_infra.config import AppConfig, load_config
from playtester_infra.io_utils import (
    FileAlreadyExistsError,
    atomic_create_json,
    atomic_replace_json,
)
from playtester_infra.llm_client import ILLMClient, create_llm_client
from playtester_infra.openshell_policy import PathEgressPolicy
from playtester_infra.report_pipeline import generate_report, report_output_path
from playtester_infra.remote_reporting import generate_report_on_gb10
from playtester_infra.schemas import DocumentValidationError, load_schema, validate_document

_SAFE_LEVEL_ID = re.compile(r"^[A-Za-z0-9_-]+$")
_BUILD_SUFFIXES = {".exe", ".x86_64", ".app"}
_EXPORT_MARKER_NAME = "level_export.json"


@dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: str = ""
    stderr: str = ""


class ICommandRunner(Protocol):
    def run(self, command: Sequence[str], *, cwd: Path) -> CommandResult: ...


class SubprocessCommandRunner:
    def run(self, command: Sequence[str], *, cwd: Path) -> CommandResult:
        completed = subprocess.run(
            list(command),
            cwd=cwd,
            check=False,
            text=True,
            capture_output=True,
        )
        return CommandResult(completed.returncode, completed.stdout, completed.stderr)


@dataclass(frozen=True)
class PipelineResult:
    level_id: str
    success: bool
    exit_code: int
    export_path: str
    export_fingerprint: str
    build_path: str | None = None
    checkpoint_path: str | None = None
    telemetry_path: str | None = None
    report_path: str | None = None
    error: str | None = None
    cached: bool = False

    @classmethod
    def from_dict(cls, value: dict[str, object]) -> "PipelineResult":
        allowed = cls.__dataclass_fields__.keys()
        return cls(**{key: value[key] for key in allowed if key in value})  # type: ignore[arg-type]


def export_fingerprint(path: Path) -> str:
    """Hash an export's relative names and content for stable event deduplication."""
    digest = hashlib.sha256()
    if path.is_file():
        files = [(Path(path.name), path)]
    elif path.is_dir():
        files = [
            (item.relative_to(path), item)
            for item in sorted(path.rglob("*"))
            if item.is_file()
        ]
        if not files:
            digest.update(b"<empty-directory>")
    else:
        raise FileNotFoundError(f"Level export does not exist: {path}")
    for relative, item in files:
        digest.update(relative.as_posix().encode("utf-8"))
        digest.update(b"\0")
        with item.open("rb") as handle:
            while chunk := handle.read(1024 * 1024):
                digest.update(chunk)
        digest.update(b"\0")
    return digest.hexdigest()


def _level_id(value: str) -> str:
    if not _SAFE_LEVEL_ID.fullmatch(value):
        raise ValueError(
            f"Level export id must use letters, digits, underscore, or hyphen: {value!r}"
        )
    return value


@dataclass(frozen=True)
class LevelExport:
    """A Unity-written marker whose existence guarantees its build is complete."""

    marker_path: Path
    level_id: str
    build_path: Path
    scene_path: str
    exported_at: str


def _marker_build_path(value: str, *, config: AppConfig) -> Path:
    """Resolve a marker path relative to the repository, never the watcher cwd."""
    supplied = Path(value).expanduser()
    if supplied.is_absolute():
        return supplied.resolve(strict=False)
    # Unity's marker contract uses repo-relative paths (unity/...); custom test
    # configs may instead use config-relative paths, so accept either form.
    repository_relative = (config.source_path.parent.parent / supplied).resolve(strict=False)
    config_relative = (config.source_path.parent / supplied).resolve(strict=False)
    if repository_relative.is_relative_to(config.paths.builds_dir):
        return repository_relative
    return config_relative


def load_level_export(marker_path: str | Path, config: AppConfig) -> LevelExport:
    """Parse and validate the fixed Unity -> infra export-marker contract."""
    marker = Path(marker_path).expanduser().resolve(strict=False)
    if marker.name != _EXPORT_MARKER_NAME:
        raise ValueError(f"Expected {_EXPORT_MARKER_NAME}, got {marker.name!r}")
    try:
        document = json.loads(marker.read_text(encoding="utf-8"))
    except OSError as exc:
        raise FileNotFoundError(f"Could not read level export marker {marker}: {exc}") from exc
    except json.JSONDecodeError as exc:
        raise ValueError(f"Level export marker is not valid JSON: {marker}: {exc}") from exc
    if not isinstance(document, dict):
        raise ValueError(f"Level export marker must be a JSON object: {marker}")
    required = ("level_id", "build_path", "scene_path", "exported_at")
    if any(not isinstance(document.get(key), str) or not document[key].strip() for key in required):
        raise ValueError(f"Level export marker is missing required string fields: {marker}")
    level_id = _level_id(document["level_id"])
    build_path = _marker_build_path(document["build_path"], config=config)
    expected_parent = (config.paths.builds_dir / level_id).resolve(strict=False)
    if (
        build_path.parent != expected_parent
        or build_path.stem != level_id
        or build_path.suffix.lower() not in _BUILD_SUFFIXES
    ):
        raise ValueError(
            "Level export build_path must be "
            f"{expected_parent / (level_id + '<platform-extension>')}, got {build_path}"
        )
    if not build_path.exists() or (
        build_path.suffix.lower() == ".app" and not build_path.is_dir()
    ):
        raise FileNotFoundError(
            f"Level export marker exists without its completed build: {build_path}"
        )
    if Path(document["scene_path"]).suffix.lower() != ".unity":
        raise ValueError("Level export scene_path must name a .unity scene")
    return LevelExport(
        marker_path=marker,
        level_id=level_id,
        build_path=build_path,
        scene_path=document["scene_path"],
        exported_at=document["exported_at"],
    )


def _render_path(template: str, *, config: AppConfig, level_id: str) -> Path:
    try:
        rendered = template.format(level_id=level_id)
    except (KeyError, ValueError) as exc:
        raise ValueError(f"Invalid path template {template!r}: {exc}") from exc
    path = Path(rendered).expanduser()
    if not path.is_absolute():
        path = config.source_path.parent / path
    return path.resolve(strict=False)


def _telemetry_snapshot(directory: Path) -> dict[Path, tuple[int, int]]:
    if not directory.exists():
        return {}
    return {
        item.resolve(): (item.stat().st_mtime_ns, item.stat().st_size)
        for item in directory.rglob("*.json")
        if item.is_file()
    }


def _find_new_telemetry(
    directory: Path,
    before: dict[Path, tuple[int, int]],
    level_id: str,
) -> Path:
    schema = load_schema("telemetry.schema.json")
    invalid: list[str] = []
    candidates: list[Path] = []
    for item in directory.rglob("*.json") if directory.exists() else []:
        resolved = item.resolve()
        current = (item.stat().st_mtime_ns, item.stat().st_size)
        if before.get(resolved) == current:
            continue
        try:
            document = json.loads(item.read_text(encoding="utf-8"))
            validate_document(document, schema, f"telemetry candidate {item}")
        except (OSError, json.JSONDecodeError, DocumentValidationError) as exc:
            invalid.append(f"{item}: {exc}")
            continue
        if document["level_id"] == level_id and document["stage"] == "stage2":
            candidates.append(item)
    if not candidates:
        detail = f" Invalid candidates: {'; '.join(invalid)}" if invalid else ""
        raise FileNotFoundError(
            f"Playtest produced no new schema-valid Stage 2 telemetry for {level_id} "
            f"in {directory}.{detail}"
        )
    return max(candidates, key=lambda item: item.stat().st_mtime_ns).resolve()


def _state_path(config: AppConfig, export_path: Path, fingerprint: str) -> Path:
    identity = hashlib.sha256(
        f"{export_path.resolve()}\0{fingerprint}".encode("utf-8")
    ).hexdigest()
    return config.paths.reports_dir / ".pipeline_runs" / f"{identity}.json"


def _load_completed(path: Path) -> PipelineResult | None:
    if not path.is_file():
        return None
    try:
        result = PipelineResult.from_dict(json.loads(path.read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError, TypeError, KeyError):
        return None
    if result.success and result.report_path and Path(result.report_path).is_file():
        return replace(result, cached=True)
    if not result.success:
        return replace(result, cached=True)
    return None


def _failure(
    *,
    level_id: str,
    export_path: Path,
    fingerprint: str,
    exit_code: int,
    error: str,
    checkpoint_path: Path | None = None,
    telemetry_path: Path | None = None,
    build_path: Path | None = None,
) -> PipelineResult:
    return PipelineResult(
        level_id=level_id,
        success=False,
        exit_code=exit_code,
        export_path=str(export_path),
        export_fingerprint=fingerprint,
        build_path=str(build_path) if build_path else None,
        checkpoint_path=str(checkpoint_path) if checkpoint_path else None,
        telemetry_path=str(telemetry_path) if telemetry_path else None,
        error=error,
    )


def _persist(path: Path, result: PipelineResult) -> PipelineResult:
    atomic_replace_json(path, asdict(result))
    return result


def process_level_export(
    level_path: str,
    config_path: str,
    *,
    llm_client: ILLMClient | None = None,
    command_runner: ICommandRunner | None = None,
) -> PipelineResult:
    """Fine-tune, playtest, and report one Unity export marker at most once."""
    config = load_config(config_path)
    export_path = Path(level_path).expanduser().resolve(strict=False)
    path_policy = PathEgressPolicy(
        config.sandbox.allowed_read_paths,
        config.sandbox.allowed_write_paths,
    )
    if not path_policy.is_read_allowed(export_path):
        return _failure(
            level_id="unknown",
            export_path=export_path,
            fingerprint="unavailable",
            exit_code=2,
            error=f"Read denied by sandbox policy: {export_path}",
        )
    try:
        export = load_level_export(export_path, config)
        level_id = export.level_id
    except (OSError, ValueError) as exc:
        return _failure(
            level_id="unknown",
            export_path=export_path,
            fingerprint="unavailable",
            exit_code=2,
            error=str(exc),
        )
    if not path_policy.is_read_allowed(export.build_path):
        return _failure(
            level_id=level_id,
            export_path=export_path,
            fingerprint="unavailable",
            exit_code=2,
            error=f"Read denied by sandbox policy: {export.build_path}",
            build_path=export.build_path,
        )
    try:
        fingerprint = export_fingerprint(export_path)
    except OSError as exc:
        return _failure(
            level_id=level_id,
            export_path=export_path,
            fingerprint="unavailable",
            exit_code=2,
            error=str(exc),
        )

    state_path = _state_path(config, export_path, fingerprint)
    cached = _load_completed(state_path)
    if cached is not None:
        return cached
    if not path_policy.is_write_allowed(config.paths.reports_dir):
        return _failure(
            level_id=level_id,
            export_path=export_path,
            fingerprint=fingerprint,
            exit_code=2,
            error=f"Write denied by sandbox policy: {config.paths.reports_dir}",
        )
    try:
        atomic_create_json(
            state_path,
            {
                "status": "running",
                "level_id": level_id,
                "export_path": str(export_path),
                "export_fingerprint": fingerprint,
            },
        )
    except FileAlreadyExistsError:
        return _failure(
            level_id=level_id,
            export_path=export_path,
            fingerprint=fingerprint,
            exit_code=75,
            error=f"An identical export is already being processed: {export_path}",
        )

    checkpoint_in = _render_path(
        config.orchestration.stage1_checkpoint, config=config, level_id=level_id
    )
    checkpoint_out = (
        config.orchestration.checkpoint_out_dir / level_id / f"{level_id}_stage2.ckpt"
    ).resolve(strict=False)
    if not checkpoint_in.is_file():
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Stage 1 checkpoint does not exist: {checkpoint_in}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )
    if not path_policy.is_read_allowed(checkpoint_in):
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Read denied by sandbox policy: {checkpoint_in}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )
    for output in (
        checkpoint_out,
        config.paths.checkpoint_manifest,
        config.paths.telemetry_dir,
        config.paths.reports_dir,
    ):
        if not path_policy.is_write_allowed(output):
            return _persist(
                state_path,
                _failure(
                    level_id=level_id,
                    export_path=export_path,
                    fingerprint=fingerprint,
                    exit_code=2,
                    error=f"Write denied by sandbox policy: {output}",
                    checkpoint_path=checkpoint_out,
                    build_path=export.build_path,
                ),
            )

    script = config.orchestration.fine_tune_script
    if not script.is_file():
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Fine-tune script does not exist: {script}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )
    if not path_policy.is_read_allowed(script):
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Read denied by sandbox policy: {script}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )

    checkpoint_out.parent.mkdir(parents=True, exist_ok=True)
    config.paths.telemetry_dir.mkdir(parents=True, exist_ok=True)
    config.paths.reports_dir.mkdir(parents=True, exist_ok=True)
    runner = command_runner or SubprocessCommandRunner()
    training_command = [
        str(script),
        "--level-id",
        level_id,
        "--checkpoint-in",
        str(checkpoint_in),
        "--checkpoint-out",
        str(checkpoint_out),
        "--num-envs",
        str(config.orchestration.num_envs),
        "--output-manifest",
        str(config.paths.checkpoint_manifest),
        "--execution-mode",
        config.orchestration.execution_mode,
    ]
    try:
        training = runner.run(training_command, cwd=config.source_path.parent)
    except OSError as exc:
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Could not launch Stage 2 fine-tuning: {exc}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )
    if training.returncode != 0:
        message = training.stderr.strip() or training.stdout.strip() or "no subprocess output"
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=training.returncode,
                error=f"Stage 2 fine-tuning failed: {message}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )

    before = _telemetry_snapshot(config.paths.telemetry_dir)
    values = {
        "level_id": level_id,
        "build_path": str(export.build_path),
        "checkpoint_out": str(checkpoint_out),
        "telemetry_dir": str(config.paths.telemetry_dir),
        "checkpoint_manifest": str(config.paths.checkpoint_manifest),
        "playtest_episodes": str(config.orchestration.playtest_episodes),
        "export_fingerprint": fingerprint,
        "execution_mode": config.orchestration.execution_mode,
    }
    try:
        playtest_command = [part.format_map(values) for part in config.orchestration.playtest_command]
    except (KeyError, ValueError) as exc:
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Invalid playtest command template: {exc}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )
    try:
        playtest = runner.run(playtest_command, cwd=config.source_path.parent)
    except OSError as exc:
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=2,
                error=f"Could not launch playtest: {exc}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )
    if playtest.returncode != 0:
        message = playtest.stderr.strip() or playtest.stdout.strip() or "no subprocess output"
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=playtest.returncode,
                error=f"Playtest failed: {message}",
                checkpoint_path=checkpoint_out,
                build_path=export.build_path,
            ),
        )

    try:
        telemetry_path = _find_new_telemetry(
            config.paths.telemetry_dir, before, level_id
        )
        if not path_policy.is_read_allowed(telemetry_path):
            raise PermissionError(f"Read denied by sandbox policy: {telemetry_path}")
        if config.orchestration.execution_mode == "remote" and llm_client is None:
            report, output_path = generate_report_on_gb10(telemetry_path, config)
        else:
            client = llm_client or create_llm_client(config)
            report = generate_report(
                str(telemetry_path),
                client,
                config_path=config.source_path,
            )
            output_path = report_output_path(
                {"level_id": report["level_id"], "run_id": json.loads(
                    telemetry_path.read_text(encoding="utf-8")
                )["run_id"]},
                config.source_path,
            )
    except Exception as exc:
        return _persist(
            state_path,
            _failure(
                level_id=level_id,
                export_path=export_path,
                fingerprint=fingerprint,
                exit_code=1,
                error=f"Report stage failed: {exc}",
                checkpoint_path=checkpoint_out,
                telemetry_path=locals().get("telemetry_path"),
                build_path=export.build_path,
            ),
        )

    return _persist(
        state_path,
        PipelineResult(
            level_id=level_id,
            success=True,
            exit_code=0,
            export_path=str(export_path),
            export_fingerprint=fingerprint,
            build_path=str(export.build_path),
            checkpoint_path=str(checkpoint_out),
            telemetry_path=str(telemetry_path),
            report_path=str(output_path),
        ),
    )
