"""Typed loading for the single infra YAML configuration."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import yaml

DEFAULT_CONFIG_PATH = Path(__file__).resolve().parents[2] / "config.yaml"
REPO_ROOT = Path(__file__).resolve().parents[3]


class ConfigError(ValueError):
    """Raised when config is missing, malformed, or internally inconsistent."""


def _mapping(value: Any, name: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise ConfigError(f"{name} must be a mapping")
    return value


def _string(data: dict[str, Any], key: str, section: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ConfigError(f"{section}.{key} must be a non-empty string")
    return value


def _resolve(base: Path, value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = base / path
    return path.resolve(strict=False)


@dataclass(frozen=True)
class PathsConfig:
    watched_levels_dir: Path
    builds_dir: Path
    telemetry_dir: Path
    reports_dir: Path
    checkpoint_manifest: Path
    checkpoints_dir: Path | None = None
    contracts_dir: Path | None = None


@dataclass(frozen=True)
class LLMConfig:
    backend: str
    model: str
    gb10_model: str
    use_gb10_model: bool
    timeout_seconds: float
    ollama_base_url: str = "http://127.0.0.1:11434"
    nim_base_url: str = "http://127.0.0.1:8000"
    nemoclaw_base_url: str = "http://127.0.0.1:8080"
    host: str | None = None

    def __post_init__(self) -> None:
        if self.host is None:
            object.__setattr__(self, "host", self.ollama_base_url)

    @property
    def selected_model(self) -> str:
        return self.gb10_model if self.use_gb10_model else self.model

    @property
    def active_model(self) -> str:
        """Compatibility name used by the merged-main setup tooling."""
        return self.selected_model


@dataclass(frozen=True)
class ReportingConfig:
    death_cluster_min_episodes: int
    high_attempt_threshold: int
    failure_rate_too_hard: float


@dataclass(frozen=True)
class OrchestrationConfig:
    fine_tune_script: Path | None = None
    stage1_checkpoint: str = ""
    checkpoint_out_dir: Path = Path(".")
    num_envs: int = 1
    playtest_command: tuple[str, ...] = ()
    watch_poll_seconds: float = 1.0
    finetune_script: Path | None = None
    playtest_script: Path | None = None
    playtest_episodes: int = 20
    command_timeout_seconds: float = 7200.0
    execution_mode: str = "real"

    def __post_init__(self) -> None:
        if self.fine_tune_script is None and self.finetune_script is not None:
            object.__setattr__(self, "fine_tune_script", self.finetune_script)
        if self.finetune_script is None and self.fine_tune_script is not None:
            object.__setattr__(self, "finetune_script", self.fine_tune_script)


@dataclass(frozen=True)
class SandboxConfig:
    allowed_read_paths: tuple[Path, ...]
    allowed_write_paths: tuple[Path, ...]
    egress_policy: str
    llm_allowlist: tuple[str, ...]
    backend: str = "application"


@dataclass(frozen=True)
class WatcherConfig:
    pattern: str = "*/level_export.json"
    poll_interval_seconds: float = 1.0


@dataclass(frozen=True)
class AppConfig:
    source_path: Path
    paths: PathsConfig
    llm: LLMConfig
    sandbox: SandboxConfig
    reporting: ReportingConfig = field(
        default_factory=lambda: ReportingConfig(2, 4, 0.5)
    )
    orchestration: OrchestrationConfig = field(default_factory=OrchestrationConfig)
    repository_root: Path | None = None
    watcher: WatcherConfig = field(default_factory=WatcherConfig)

    def __post_init__(self) -> None:
        if self.repository_root is None:
            object.__setattr__(self, "repository_root", self.source_path.parent.parent)


def _path_list(data: dict[str, Any], key: str, base: Path) -> tuple[Path, ...]:
    values = data.get(key)
    if not isinstance(values, list) or not values:
        raise ConfigError(f"sandbox.{key} must be a non-empty list")
    if not all(isinstance(item, str) and item.strip() for item in values):
        raise ConfigError(f"sandbox.{key} entries must be non-empty strings")
    return tuple(_resolve(base, item) for item in values)


def load_config(config_path: str | Path = DEFAULT_CONFIG_PATH) -> AppConfig:
    source = Path(config_path).expanduser().resolve(strict=False)
    if not source.is_file():
        raise ConfigError(f"Config file does not exist: {source}")
    try:
        raw = yaml.safe_load(source.read_text(encoding="utf-8"))
    except (OSError, yaml.YAMLError) as exc:
        raise ConfigError(f"Could not load config {source}: {exc}") from exc
    root = _mapping(raw, "config")
    base = source.parent

    paths_raw = _mapping(root.get("paths"), "paths")
    paths = PathsConfig(
        watched_levels_dir=_resolve(base, _string(paths_raw, "watched_levels_dir", "paths")),
        builds_dir=_resolve(base, _string(paths_raw, "builds_dir", "paths")),
        telemetry_dir=_resolve(base, _string(paths_raw, "telemetry_dir", "paths")),
        reports_dir=_resolve(base, _string(paths_raw, "reports_dir", "paths")),
        checkpoint_manifest=_resolve(base, _string(paths_raw, "checkpoint_manifest", "paths")),
        checkpoints_dir=_resolve(
            base,
            str(paths_raw.get("checkpoints_dir") or "../rl/checkpoints"),
        ),
        contracts_dir=_resolve(
            base,
            str(paths_raw.get("contracts_dir") or "../contracts"),
        ),
    )

    llm_raw = _mapping(root.get("llm"), "llm")
    backend = _string(llm_raw, "backend", "llm").lower()
    if backend not in {"ollama", "nim", "nemoclaw"}:
        raise ConfigError("llm.backend must be one of: ollama, nim, nemoclaw")
    use_gb10_model = llm_raw.get("use_gb10_model", False)
    environment_override = os.environ.get("PLAYTESTER_USE_GB10_MODEL")
    if environment_override is not None:
        normalized_override = environment_override.strip().lower()
        if normalized_override not in {"0", "1", "false", "true"}:
            raise ConfigError(
                "PLAYTESTER_USE_GB10_MODEL must be one of 0, 1, false, or true"
            )
        use_gb10_model = normalized_override in {"1", "true"}
    if not isinstance(use_gb10_model, bool):
        raise ConfigError("llm.use_gb10_model must be a boolean")
    timeout_seconds = llm_raw.get("timeout_seconds", 120)
    if not isinstance(timeout_seconds, (int, float)) or timeout_seconds <= 0:
        raise ConfigError("llm.timeout_seconds must be greater than zero")
    llm = LLMConfig(
        backend=backend,
        model=_string(llm_raw, "model", "llm"),
        gb10_model=_string(llm_raw, "gb10_model", "llm"),
        use_gb10_model=use_gb10_model,
        timeout_seconds=float(timeout_seconds),
        ollama_base_url=str(
            llm_raw.get("host") or llm_raw.get("ollama_base_url") or "http://127.0.0.1:11434"
        ).rstrip("/"),
        nim_base_url=str(llm_raw.get("nim_base_url") or "http://127.0.0.1:8000").rstrip("/"),
        nemoclaw_base_url=str(llm_raw.get("nemoclaw_base_url") or "http://127.0.0.1:8080").rstrip("/"),
        host=str(llm_raw.get("host") or llm_raw.get("ollama_base_url") or "http://127.0.0.1:11434").rstrip("/"),
    )

    reporting_raw = _mapping(root.get("reporting"), "reporting")
    death_cluster_min = reporting_raw.get("death_cluster_min_episodes")
    high_attempt_threshold = reporting_raw.get("high_attempt_threshold")
    failure_rate_too_hard = reporting_raw.get("failure_rate_too_hard")
    if (
        not isinstance(death_cluster_min, int)
        or isinstance(death_cluster_min, bool)
        or death_cluster_min < 1
    ):
        raise ConfigError("reporting.death_cluster_min_episodes must be an integer >= 1")
    if (
        not isinstance(high_attempt_threshold, int)
        or isinstance(high_attempt_threshold, bool)
        or high_attempt_threshold < 1
    ):
        raise ConfigError("reporting.high_attempt_threshold must be an integer >= 1")
    if (
        not isinstance(failure_rate_too_hard, (int, float))
        or isinstance(failure_rate_too_hard, bool)
        or not 0 < float(failure_rate_too_hard) <= 1
    ):
        raise ConfigError("reporting.failure_rate_too_hard must be in (0, 1]")
    reporting = ReportingConfig(
        death_cluster_min_episodes=death_cluster_min,
        high_attempt_threshold=high_attempt_threshold,
        failure_rate_too_hard=float(failure_rate_too_hard),
    )

    orchestration_raw = _mapping(root.get("orchestration"), "orchestration")
    num_envs = orchestration_raw.get("num_envs")
    if not isinstance(num_envs, int) or isinstance(num_envs, bool) or num_envs < 1:
        raise ConfigError("orchestration.num_envs must be an integer >= 1")
    playtest_command = orchestration_raw.get("playtest_command")
    if (
        not isinstance(playtest_command, list)
        or not playtest_command
        or not all(isinstance(part, str) and part for part in playtest_command)
    ):
        raise ConfigError("orchestration.playtest_command must be a non-empty string list")
    poll_seconds = orchestration_raw.get("watch_poll_seconds", 1.0)
    if not isinstance(poll_seconds, (int, float)) or poll_seconds <= 0:
        raise ConfigError("orchestration.watch_poll_seconds must be greater than zero")
    execution_mode = str(orchestration_raw.get("execution_mode", "real")).lower()
    if execution_mode not in {"real", "remote"}:
        raise ConfigError("orchestration.execution_mode must be real or remote")
    orchestration = OrchestrationConfig(
        fine_tune_script=_resolve(base, _string(orchestration_raw, "fine_tune_script", "orchestration")),
        stage1_checkpoint=_string(orchestration_raw, "stage1_checkpoint", "orchestration"),
        checkpoint_out_dir=_resolve(
            base, _string(orchestration_raw, "checkpoint_out_dir", "orchestration")
        ),
        num_envs=num_envs,
        playtest_command=tuple(playtest_command),
        watch_poll_seconds=float(poll_seconds),
        playtest_script=_resolve(
            base, str(orchestration_raw.get("playtest_script") or "../rl/scripts/run_playtest.sh")
        ),
        playtest_episodes=int(orchestration_raw.get("playtest_episodes", 20)),
        command_timeout_seconds=float(orchestration_raw.get("command_timeout_seconds", 7200)),
        execution_mode=execution_mode,
    )

    sandbox_raw = _mapping(root.get("sandbox"), "sandbox")
    sandbox_backend = _string(sandbox_raw, "backend", "sandbox").lower()
    if sandbox_backend not in {"application", "network_namespace", "openshell"}:
        raise ConfigError(
            "sandbox.backend must be one of: application, network_namespace, openshell"
        )
    egress_policy = _string(sandbox_raw, "egress_policy", "sandbox").lower()
    if egress_policy not in {"block_all", "allow_list"}:
        raise ConfigError("sandbox.egress_policy must be block_all or allow_list")
    llm_allowlist = sandbox_raw.get("llm_allowlist")
    if (
        not isinstance(llm_allowlist, list)
        or not llm_allowlist
        or not all(isinstance(item, str) and item.strip() for item in llm_allowlist)
    ):
        raise ConfigError("sandbox.llm_allowlist must be a non-empty list of host:port strings")
    selected_base_url = {
        "ollama": llm.ollama_base_url,
        "nim": llm.nim_base_url,
        "nemoclaw": llm.nemoclaw_base_url,
    }[llm.backend]
    parsed_llm_url = urlparse(selected_base_url)
    if not parsed_llm_url.hostname or not parsed_llm_url.port:
        raise ConfigError("selected LLM base URL must include a host and port")
    selected_endpoint = f"{parsed_llm_url.hostname}:{parsed_llm_url.port}".lower()
    normalized_allowlist = tuple(item.strip().lower() for item in llm_allowlist)
    if selected_endpoint not in normalized_allowlist:
        raise ConfigError(
            "sandbox.llm_allowlist must include the selected LLM endpoint "
            f"{selected_endpoint!r}"
        )
    sandbox = SandboxConfig(
        backend=sandbox_backend,
        allowed_read_paths=_path_list(sandbox_raw, "allowed_read_paths", base),
        allowed_write_paths=_path_list(sandbox_raw, "allowed_write_paths", base),
        egress_policy=egress_policy,
        llm_allowlist=normalized_allowlist,
    )

    return AppConfig(
        source_path=source,
        paths=paths,
        llm=llm,
        reporting=reporting,
        orchestration=orchestration,
        sandbox=sandbox,
        repository_root=source.parent.parent,
        watcher=WatcherConfig(
            pattern=str(_mapping(root.get("watcher", {}), "watcher").get("pattern", "*/level_export.json")),
            poll_interval_seconds=float(
                _mapping(root.get("watcher", {}), "watcher").get("poll_interval_seconds", poll_seconds)
            ),
        ),
    )
