"""Filesystem-triggered orchestration for a local playtest pipeline.

The module contains no OpenClaw SDK integration.  ``process_level_artifact`` is
the reusable trigger boundary; ``LevelWatcher`` is the polling runtime used
until a documented agent runtime is wired to that same boundary.

TODO(OpenClaw): replace only the polling adapter after the sponsor runtime API
is verified; keep ``process_level_artifact`` as the tested orchestration seam.
"""

from __future__ import annotations

import json
import logging
import os
import re
import subprocess
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from threading import Event
from typing import Any, Protocol, cast
from uuid import uuid4

from jsonschema import Draft202012Validator

from playtester_infra.config import AppConfig, DEFAULT_CONFIG_PATH, load_config
from playtester_infra.llm_client import ILLMClient
from playtester_infra.orchestration import (
    ICommandRunner,
    PipelineResult,
    export_fingerprint,
    process_level_export,
)


LOGGER = logging.getLogger(__name__)
LEVEL_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]*$")
LEVEL_EXPORT_FIELDS = frozenset(
    {"level_id", "build_path", "scene_path", "exported_at"}
)


class OrchestrationError(RuntimeError):
    """Raised when an artifact cannot complete the playtest pipeline."""


class CommandRunner(Protocol):
    """Boundary for running the ML/RL scripts."""

    def __call__(
        self,
        command: Sequence[str],
        *,
        check: bool,
        timeout: float,
    ) -> object: ...


class ReportGenerator(Protocol):
    """Boundary implemented by the structured report pipeline."""

    def __call__(self, telemetry_path: Path) -> object: ...


@dataclass(frozen=True)
class OrchestrationResult:
    """Paths and report produced for one level artifact."""

    level_id: str
    artifact_path: Path
    build_path: Path
    checkpoint_path: Path
    telemetry_path: Path
    report: object


@dataclass(frozen=True)
class _LevelExport:
    level_id: str
    build_path: str
    scene_path: str
    exported_at: str


def _default_runner(
    command: Sequence[str],
    *,
    check: bool,
    timeout: float,
) -> object:
    return subprocess.run(command, check=check, timeout=timeout)


def _read_json(document_path: Path, *, label: str) -> Any:
    try:
        return json.loads(document_path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise OrchestrationError(
            f"{label} does not exist: {document_path}"
        ) from error
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise OrchestrationError(
            f"could not read {label} {document_path}: {error}"
        ) from error


def _read_validated_document(
    document_path: Path,
    schema_path: Path,
    *,
    label: str,
) -> dict[str, Any]:
    raw_document = _read_json(document_path, label=label)

    try:
        raw_schema = _read_json(schema_path, label=f"{label} schema")
        schema = cast(dict[str, Any], raw_schema)
        validator = Draft202012Validator(schema)
    except OrchestrationError as error:
        raise OrchestrationError(
            f"could not read {label} schema {schema_path}: {error}"
        ) from error

    errors = sorted(
        validator.iter_errors(raw_document),
        key=lambda item: tuple(str(part) for part in item.path),
    )
    if errors:
        first = errors[0]
        location = ".".join(str(part) for part in first.absolute_path) or "<root>"
        raise OrchestrationError(
            f"{label} failed schema validation at {location}: {first.message}"
        )
    return cast(dict[str, Any], raw_document)


def _read_checkpoint_manifest_entry(
    manifest_path: Path,
    schema_path: Path,
    level_id: str,
) -> dict[str, Any]:
    """Read the RL-owned list manifest, while accepting the legacy single-entry form."""
    raw_manifest = _read_json(manifest_path, label="checkpoint manifest")
    if isinstance(raw_manifest, list):
        entry = next(
            (
                candidate
                for candidate in raw_manifest
                if isinstance(candidate, dict) and candidate.get("level_id") == level_id
            ),
            None,
        )
        # Stage 1 is one shared composition-gym generalizer. A level entry
        # created by Stage 2 may therefore have no Stage 1 field of its own;
        # resolve that field from the canonical gym entry.
        if entry is None or not isinstance(entry.get("stage1_checkpoint"), str):
            entry = next(
                (
                    candidate
                    for candidate in raw_manifest
                    if isinstance(candidate, dict)
                    and candidate.get("level_id") == "gym"
                    and isinstance(candidate.get("stage1_checkpoint"), str)
                ),
                None,
            )
        if entry is None:
            raise OrchestrationError(
                f"checkpoint manifest has no Stage 1 checkpoint for level "
                f"{level_id!r} or the shared gym generalizer"
            )
        raw_document = entry
    elif isinstance(raw_manifest, dict):
        raw_document = raw_manifest
    else:
        raise OrchestrationError("checkpoint manifest must be an entry or a list of entries")

    try:
        schema = cast(dict[str, Any], _read_json(schema_path, label="checkpoint manifest schema"))
        errors = sorted(
            Draft202012Validator(schema).iter_errors(raw_document),
            key=lambda item: tuple(str(part) for part in item.path),
        )
    except OrchestrationError:
        raise
    if errors:
        first = errors[0]
        location = ".".join(str(part) for part in first.absolute_path) or "<root>"
        raise OrchestrationError(
            f"checkpoint manifest failed schema validation at {location}: {first.message}"
        )
    return cast(dict[str, Any], raw_document)


def _read_level_export(artifact_path: Path) -> _LevelExport:
    raw_document = _read_json(artifact_path, label="level export")
    if not isinstance(raw_document, dict):
        raise OrchestrationError("level export must be a JSON object")
    if set(raw_document) != LEVEL_EXPORT_FIELDS:
        fields = ", ".join(sorted(LEVEL_EXPORT_FIELDS))
        raise OrchestrationError(
            f"level export must contain exactly these fields: {fields}"
        )

    values: dict[str, str] = {}
    for field in LEVEL_EXPORT_FIELDS:
        value = raw_document[field]
        if not isinstance(value, str) or not value.strip():
            raise OrchestrationError(
                f"level export {field} must be a non-empty string"
            )
        values[field] = value

    level_id = values["level_id"]
    if LEVEL_ID_PATTERN.fullmatch(level_id) is None:
        raise OrchestrationError(
            "level export level_id must be a safe lowercase slug"
        )

    exported_at = values["exported_at"]
    try:
        parsed_exported_at = datetime.fromisoformat(
            exported_at.replace("Z", "+00:00")
        )
    except ValueError as error:
        raise OrchestrationError(
            "level export exported_at must be timezone-aware ISO8601"
        ) from error
    if (
        parsed_exported_at.tzinfo is None
        or parsed_exported_at.utcoffset() is None
    ):
        raise OrchestrationError(
            "level export exported_at must be timezone-aware ISO8601"
        )

    return _LevelExport(
        level_id=level_id,
        build_path=values["build_path"],
        scene_path=values["scene_path"],
        exported_at=exported_at,
    )


def _resolve_repository_path(repository_root: Path, raw_path: str) -> Path:
    path = Path(raw_path).expanduser()
    if not path.is_absolute():
        path = repository_root / path
    return path.resolve(strict=False)


def _run_step(
    name: str,
    command: list[str],
    runner: CommandRunner,
    timeout: float,
) -> None:
    try:
        runner(command, check=True, timeout=timeout)
    except subprocess.TimeoutExpired as error:
        raise OrchestrationError(
            f"{name} timed out after {timeout:g} seconds"
        ) from error
    except subprocess.CalledProcessError as error:
        raise OrchestrationError(
            f"{name} failed with exit code {error.returncode}"
        ) from error
    except OSError as error:
        raise OrchestrationError(f"could not start {name}: {error}") from error


def _require_executable_script(
    path: Path,
    *,
    label: str,
    repository_root: Path,
) -> None:
    resolved_path = path.resolve(strict=False)
    resolved_root = repository_root.resolve(strict=False)
    if not resolved_path.is_relative_to(resolved_root):
        raise OrchestrationError(
            f"configured {label} script escapes repository: {resolved_path}"
        )
    if not resolved_path.is_file():
        raise OrchestrationError(
            f"configured {label} script does not exist: {resolved_path}"
        )
    if not os.access(resolved_path, os.X_OK):
        raise OrchestrationError(
            f"configured {label} script is not executable: {resolved_path}"
        )


def process_level_artifact(
    artifact_path: str | Path,
    config: AppConfig,
    report_generator: ReportGenerator,
    *,
    runner: CommandRunner | None = None,
    invocation_id: str | None = None,
) -> OrchestrationResult:
    """Fine-tune, playtest, and report on one training-ready level export.

    Relative paths inside artifact and manifest documents are resolved from the
    repository root, never from the watcher's current working directory.
    """

    artifact = Path(artifact_path).expanduser().resolve(strict=False)
    level_export = _read_level_export(artifact)
    level_id = level_export.level_id
    watched_root = config.paths.watched_levels_dir.resolve(strict=False)
    expected_marker = watched_root / level_id / "level_export.json"
    if artifact != expected_marker:
        raise OrchestrationError(
            "level export marker must be located at "
            f"{expected_marker}, got {artifact}"
        )

    build_path = _resolve_repository_path(
        config.repository_root, level_export.build_path
    )
    builds_root = config.paths.builds_dir.resolve(strict=False)
    level_build_root = (builds_root / level_id).resolve(strict=False)
    if (
        not level_build_root.is_relative_to(builds_root)
        or not build_path.is_relative_to(level_build_root)
    ):
        raise OrchestrationError(
            "level build is outside configured build directory "
            f"for {level_id}: {build_path}"
        )
    if not build_path.exists():
        raise OrchestrationError(f"level build does not exist: {build_path}")

    manifest_document = _read_checkpoint_manifest_entry(
        config.paths.checkpoint_manifest,
        config.paths.contracts_dir / "checkpoint_manifest.schema.json",
        level_id,
    )
    stage1_checkpoint = _resolve_repository_path(
        config.repository_root,
        cast(str, manifest_document["stage1_checkpoint"]),
    )
    checkpoints_root = config.paths.checkpoints_dir.resolve(strict=False)
    if not stage1_checkpoint.is_relative_to(checkpoints_root):
        raise OrchestrationError(
            "Stage 1 checkpoint is outside configured checkpoint directory: "
            f"{stage1_checkpoint}"
        )
    if not stage1_checkpoint.exists():
        raise OrchestrationError(
            f"Stage 1 checkpoint does not exist: {stage1_checkpoint}"
        )

    _require_executable_script(
        config.orchestration.finetune_script,
        label="Stage 2 fine-tune",
        repository_root=config.repository_root,
    )
    _require_executable_script(
        config.orchestration.playtest_script,
        label="playtest",
        repository_root=config.repository_root,
    )

    active_invocation_id = invocation_id or uuid4().hex
    if LEVEL_ID_PATTERN.fullmatch(active_invocation_id) is None:
        raise OrchestrationError(
            "invocation_id must be a safe lowercase slug"
        )
    checkpoint_path = (
        config.paths.checkpoints_dir
        / level_id
        / "stage2"
        / active_invocation_id
    ).resolve(strict=False)
    if not checkpoint_path.is_relative_to(checkpoints_root):
        raise OrchestrationError(
            "Stage 2 checkpoint output is outside configured checkpoint "
            f"directory: {checkpoint_path}"
        )
    telemetry_root = config.paths.telemetry_dir.resolve(strict=False)
    telemetry_path = (
        telemetry_root
        / level_id
        / f"{active_invocation_id}.telemetry.json"
    ).resolve(strict=False)
    if not telemetry_path.is_relative_to(telemetry_root):
        raise OrchestrationError(
            "telemetry output is outside configured telemetry directory: "
            f"{telemetry_path}"
        )
    try:
        checkpoint_path.parent.mkdir(parents=True, exist_ok=True)
        telemetry_path.parent.mkdir(parents=True, exist_ok=True)
    except OSError as error:
        raise OrchestrationError(
            f"could not prepare invocation output directories: {error}"
        ) from error

    command_runner = runner or _default_runner
    timeout = config.orchestration.command_timeout_seconds
    finetune_command = [
        str(config.orchestration.finetune_script),
        "--level-id",
        level_id,
        "--checkpoint-in",
        str(stage1_checkpoint),
        "--checkpoint-out",
        str(checkpoint_path),
        "--output-manifest",
        str(config.paths.checkpoint_manifest),
        "--execution-mode",
        config.orchestration.execution_mode,
    ]
    _run_step("Stage 2 fine-tune", finetune_command, command_runner, timeout)
    if not checkpoint_path.exists():
        raise OrchestrationError(
            "Stage 2 fine-tune did not produce checkpoint: "
            f"{checkpoint_path}"
        )

    playtest_command = [
        str(config.orchestration.playtest_script),
        "--level-id",
        level_id,
        "--checkpoint-in",
        str(checkpoint_path),
        "--episodes",
        str(config.orchestration.playtest_episodes),
        "--telemetry-out",
        str(telemetry_path),
        "--execution-mode",
        config.orchestration.execution_mode,
    ]
    _run_step("playtest", playtest_command, command_runner, timeout)

    if not telemetry_path.is_file():
        raise OrchestrationError(
            f"playtest did not produce telemetry: {telemetry_path}"
        )
    try:
        report = report_generator(telemetry_path)
    except OrchestrationError:
        raise
    except Exception as error:
        raise OrchestrationError(
            f"report generation failed for {level_id}: {error}"
        ) from error
    return OrchestrationResult(
        level_id=level_id,
        artifact_path=artifact,
        build_path=build_path,
        checkpoint_path=checkpoint_path,
        telemetry_path=telemetry_path,
        report=report,
    )


class LevelWatcher:
    """Polling adapter for nested ``<level_id>/level_export.json`` markers."""

    def __init__(
        self,
        config: AppConfig | str | Path = DEFAULT_CONFIG_PATH,
        report_generator: ReportGenerator | None = None,
        *,
        runner: CommandRunner | None = None,
        sleep: Callable[[float], None] = time.sleep,
        llm_client: ILLMClient | None = None,
        command_runner: ICommandRunner | None = None,
    ) -> None:
        self._legacy_mode = isinstance(config, (str, Path))
        if self._legacy_mode:
            self._legacy_config = load_config(config)
            self._legacy_config_path = str(self._legacy_config.source_path)
            self._legacy_llm_client = llm_client
            self._legacy_command_runner = command_runner
            self._legacy_seen: dict[Path, str] = {}
            return
        if report_generator is None:
            raise TypeError("report_generator is required with an AppConfig")
        self._config = config
        self._report_generator = report_generator
        self._runner = runner
        self._sleep = sleep
        self._processed_mtimes: dict[Path, int] = {}

    def process_event(self, level_path: str | Path) -> PipelineResult | None:
        """Existing Unity watcher entry point, retained alongside poll_once."""
        if not self._legacy_mode:
            raise OrchestrationError("process_event requires a config path watcher")
        path = Path(level_path).expanduser().resolve(strict=False)
        fingerprint = export_fingerprint(path)
        if self._legacy_seen.get(path) == fingerprint:
            return None
        self._legacy_seen[path] = fingerprint
        return process_level_export(
            str(path),
            self._legacy_config_path,
            llm_client=self._legacy_llm_client,
            command_runner=self._legacy_command_runner,
        )

    def _legacy_scan_once(self) -> list[PipelineResult]:
        watched = self._legacy_config.paths.watched_levels_dir
        watched.mkdir(parents=True, exist_ok=True)
        results: list[PipelineResult] = []
        for artifact in sorted(watched.rglob("level_export.json")):
            result = self.process_event(artifact)
            if result is not None:
                results.append(result)
        return results

    def scan_once(self) -> list[PipelineResult] | list[OrchestrationResult]:
        """Scan all nested exports for the Unity path or poll OpenClaw markers."""
        if self._legacy_mode:
            return self._legacy_scan_once()
        return self.poll_once()

    def poll_once(self) -> list[OrchestrationResult]:
        """Process each marker whose modification time has changed."""

        if self._legacy_mode:
            return cast(list[OrchestrationResult], self._legacy_scan_once())

        watch_dir = self._config.paths.watched_levels_dir
        if not watch_dir.is_dir():
            raise OrchestrationError(
                f"watched levels directory does not exist: {watch_dir}"
            )
        results: list[OrchestrationResult] = []
        for artifact in sorted(watch_dir.glob(self._config.watcher.pattern)):
            resolved_artifact = artifact.resolve(strict=False)
            try:
                observed_mtime = artifact.stat().st_mtime_ns
            except FileNotFoundError:
                continue
            if self._processed_mtimes.get(resolved_artifact) == observed_mtime:
                continue

            try:
                result = process_level_artifact(
                    resolved_artifact,
                    self._config,
                    self._report_generator,
                    runner=self._runner,
                )
            except OrchestrationError:
                LOGGER.exception(
                    "level artifact processing failed: %s",
                    resolved_artifact,
                )
                continue
            self._processed_mtimes[resolved_artifact] = observed_mtime
            results.append(result)
        return results

    def run(self, stop_event: Event | None = None) -> None:
        """Block while polling, optionally stopping when ``stop_event`` is set."""

        if self._legacy_mode:
            while stop_event is None or not stop_event.is_set():
                self._legacy_scan_once()
                if stop_event is not None:
                    if stop_event.wait(self._legacy_config.orchestration.watch_poll_seconds):
                        return
                else:
                    time.sleep(self._legacy_config.orchestration.watch_poll_seconds)
            return

        while stop_event is None or not stop_event.is_set():
            try:
                self.poll_once()
            except OrchestrationError:
                LOGGER.exception("level artifact processing failed")

            interval = self._config.watcher.poll_interval_seconds
            if stop_event is not None:
                if stop_event.wait(interval):
                    return
            else:
                self._sleep(interval)
