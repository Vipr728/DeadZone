"""Configuration validation used by the GB10 setup shell wrapper."""

from __future__ import annotations

import argparse
import sys
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path
from urllib.parse import urlparse

import yaml

from .config import AppConfig, ConfigError, load_config
from .openshell_policy import ApplicationEgressPolicy, EgressPolicyError


class SetupValidationError(ValueError):
    """Raised when setup would escape or contradict the local-only contract."""


@dataclass(frozen=True)
class SetupSummary:
    active_model: str
    llm_host: str


def _configured_paths(config: AppConfig) -> tuple[tuple[str, Path], ...]:
    return (
        ("paths.watched_levels_dir", config.paths.watched_levels_dir),
        ("paths.builds_dir", config.paths.builds_dir),
        ("paths.telemetry_dir", config.paths.telemetry_dir),
        ("paths.reports_dir", config.paths.reports_dir),
        ("paths.checkpoints_dir", config.paths.checkpoints_dir),
        ("paths.checkpoint_manifest", config.paths.checkpoint_manifest),
        ("paths.contracts_dir", config.paths.contracts_dir),
        (
            "orchestration.finetune_script",
            config.orchestration.finetune_script,
        ),
        (
            "orchestration.playtest_script",
            config.orchestration.playtest_script,
        ),
        *(
            (f"sandbox.allowed_read_paths[{index}]", path)
            for index, path in enumerate(config.sandbox.allowed_read_paths)
        ),
        *(
            (f"sandbox.allowed_write_paths[{index}]", path)
            for index, path in enumerate(config.sandbox.allowed_write_paths)
        ),
    )


def _require_repository_containment(config: AppConfig) -> None:
    repository_root = config.repository_root.resolve(strict=False)
    for label, path in _configured_paths(config):
        canonical_path = path.resolve(strict=False)
        if (
            canonical_path != repository_root
            and not canonical_path.is_relative_to(repository_root)
        ):
            raise SetupValidationError(
                f"{label} escapes repository root: {canonical_path}"
            )


def _require_policy_coverage(
    config: AppConfig,
    policy: ApplicationEgressPolicy,
) -> None:
    required_reads = (
        config.paths.watched_levels_dir,
        config.paths.builds_dir,
        config.paths.telemetry_dir,
        config.paths.contracts_dir,
        config.paths.checkpoint_manifest,
        config.paths.checkpoints_dir,
        config.orchestration.finetune_script,
        config.orchestration.playtest_script,
    )
    for path in required_reads:
        if not policy.is_read_allowed(path):
            raise SetupValidationError(
                f"sandbox policy denies required read path: {path}"
            )

    required_writes = (
        config.paths.reports_dir,
        config.paths.telemetry_dir,
        config.paths.checkpoints_dir,
        config.paths.checkpoint_manifest,
    )
    for path in required_writes:
        if not policy.is_write_allowed(path):
            raise SetupValidationError(
                f"sandbox policy denies required write path: {path}"
            )


def _create_configured_directories(config: AppConfig) -> None:
    directories = (
        config.paths.watched_levels_dir,
        config.paths.builds_dir,
        config.paths.telemetry_dir,
        config.paths.reports_dir,
        config.paths.checkpoints_dir,
        config.paths.contracts_dir,
        config.paths.checkpoint_manifest.parent,
    )
    for directory in dict.fromkeys(directories):
        try:
            directory.mkdir(parents=True, exist_ok=True)
        except OSError as error:
            raise SetupValidationError(
                f"cannot create configured directory {directory}: {error}"
            ) from error
        if not directory.is_dir():
            raise SetupValidationError(
                f"configured path is not a directory: {directory}"
            )


def validate_setup(
    config_path: str | Path,
    *,
    create_directories: bool = False,
) -> SetupSummary:
    """Validate the shared config before any optional filesystem changes."""

    config = load_config(config_path)
    if config.llm.backend != "ollama":
        raise SetupValidationError("llm.backend must be ollama for this setup")
    if config.sandbox.egress_policy != "block_all":
        raise SetupValidationError("sandbox.egress_policy must be block_all")

    policy = ApplicationEgressPolicy(
        allowed_read_paths=config.sandbox.allowed_read_paths,
        allowed_write_paths=config.sandbox.allowed_write_paths,
        llm_allowlist=config.sandbox.llm_allowlist,
    )
    parsed_host = urlparse(config.llm.host or "")
    endpoint = (
        f"{parsed_host.hostname}:{parsed_host.port}".lower()
        if parsed_host.hostname and parsed_host.port
        else ""
    )
    if endpoint not in config.sandbox.llm_allowlist:
        raise SetupValidationError(
            "llm.host must appear in sandbox.llm_allowlist"
        )

    _require_repository_containment(config)
    _require_policy_coverage(config, policy)
    if create_directories:
        _create_configured_directories(config)
    return SetupSummary(
        active_model=config.llm.active_model,
        llm_host=config.llm.host,
    )


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    parser.add_argument("--create-directories", action="store_true")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    try:
        summary = validate_setup(
            args.config,
            create_directories=bool(args.create_directories),
        )
    except (
        ConfigError,
        EgressPolicyError,
        SetupValidationError,
        OSError,
        yaml.YAMLError,
    ) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1

    print(summary.active_model)
    print(summary.llm_host)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
