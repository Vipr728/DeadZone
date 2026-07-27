"""Run structured report generation on the GB10 and return its artifacts."""

from __future__ import annotations

import json
import os
import re
import shlex
import subprocess
import tempfile
from ipaddress import ip_address
from pathlib import Path, PurePosixPath
from typing import Any, Callable

import yaml

from playtester_infra.config import AppConfig
from playtester_infra.schemas import load_schema, validate_document

_SAFE_ID = re.compile(r"^[A-Za-z0-9_-]+$")
_SAFE_HOST = re.compile(r"^[A-Za-z0-9.-]+$")
_SAFE_USER = re.compile(r"^[A-Za-z0-9._-]+$")
_SAFE_REMOTE_PATH = re.compile(r"^[A-Za-z0-9._/-]+$")


class RemoteReportingError(RuntimeError):
    """Telemetry/report transfer or remote report execution failed."""


def _load_identity(config: AppConfig) -> tuple[str, str, str]:
    if config.repository_root is None:
        raise RemoteReportingError("Repository root is unavailable")
    remote_path = config.repository_root / "rl/configs/remote_execution.yaml"
    try:
        raw = yaml.safe_load(remote_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, yaml.YAMLError) as error:
        raise RemoteReportingError(
            f"Could not load remote execution config {remote_path}: {error}"
        ) from error
    if not isinstance(raw, dict):
        raise RemoteReportingError("Remote execution config must be a YAML object")
    host = os.environ.get("PLAYTESTER_GB10_HOST", raw.get("tailscale_hostname", ""))
    user = os.environ.get("PLAYTESTER_GB10_USER", raw.get("ssh_username", ""))
    repository = raw.get("repository_path", "GB10-project")
    if not all(
        isinstance(value, str) and value.strip()
        for value in (host, user, repository)
    ):
        raise RemoteReportingError(
            "Remote reporting requires Tailscale hostname, SSH username, "
            "and repository path"
        )
    try:
        ip_address(host)
    except ValueError:
        pass
    else:
        raise RemoteReportingError("Remote reporting requires a hostname, never an IP")
    if not _SAFE_HOST.fullmatch(host) or not _SAFE_USER.fullmatch(user):
        raise RemoteReportingError("Remote hostname or SSH username contains unsafe characters")
    if (
        not _SAFE_REMOTE_PATH.fullmatch(repository)
        or PurePosixPath(repository).is_absolute()
        or ".." in PurePosixPath(repository).parts
    ):
        raise RemoteReportingError("Remote repository path must be relative to SSH home")
    return host, user, repository


def generate_report_on_gb10(
    telemetry_path: Path,
    config: AppConfig,
    *,
    run: Callable[..., subprocess.CompletedProcess[object]] = subprocess.run,
) -> tuple[dict[str, Any], Path]:
    telemetry_file = telemetry_path.expanduser().resolve(strict=True)
    telemetry = json.loads(telemetry_file.read_text(encoding="utf-8"))
    level_id = telemetry.get("level_id")
    run_id = telemetry.get("run_id")
    if not isinstance(level_id, str) or not _SAFE_ID.fullmatch(level_id):
        raise RemoteReportingError(f"Unsafe telemetry level_id: {level_id!r}")
    if not isinstance(run_id, str) or not _SAFE_ID.fullmatch(run_id):
        raise RemoteReportingError(f"Unsafe telemetry run_id: {run_id!r}")

    host, user, repository = _load_identity(config)
    target = f"{user}@{host}"
    remote_telemetry = (
        f"unity/PlaytesterProject/Telemetry/remote/{telemetry_file.name}"
    )
    remote_report = f"unity/PlaytesterProject/Reports/{level_id}_{run_id}.json"
    ssh_options = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10"]
    remote_parent = str(PurePosixPath(remote_telemetry).parent)
    prepare = run(
        [
            "ssh",
            *ssh_options,
            target,
            (
                f"cd {shlex.quote(repository)} && "
                f"mkdir -p {shlex.quote(remote_parent)}"
            ),
        ],
        check=False,
    )
    if prepare.returncode != 0:
        raise RemoteReportingError("Could not prepare the GB10 telemetry directory")

    upload = run(
        [
            "scp",
            *ssh_options,
            str(telemetry_file),
            f"{target}:{repository}/{remote_telemetry}",
        ],
        check=False,
    )
    if upload.returncode != 0:
        raise RemoteReportingError("Could not transfer telemetry to the GB10")

    report_command = [
        "env",
        "PLAYTESTER_USE_GB10_MODEL=1",
        "uv",
        "run",
        "--project",
        "infra",
        "playtester-report",
        remote_telemetry,
        "--config",
        "infra/config.yaml",
    ]
    generated = run(
        [
            "ssh",
            *ssh_options,
            target,
            (
                f"cd {shlex.quote(repository)} && "
                f"{shlex.join(report_command)}"
            ),
        ],
        check=False,
    )
    if generated.returncode != 0:
        raise RemoteReportingError("GB10 report generation failed")

    local_report = config.paths.reports_dir / f"{level_id}_{run_id}.json"
    if local_report.exists():
        raise RemoteReportingError(f"Refusing to overwrite report: {local_report}")
    local_report.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{local_report.name}.",
        suffix=".tmp",
        dir=local_report.parent,
    )
    os.close(descriptor)
    temporary_report = Path(temporary_name)
    try:
        download = run(
            [
                "scp",
                *ssh_options,
                f"{target}:{repository}/{remote_report}",
                str(temporary_report),
            ],
            check=False,
        )
        if download.returncode != 0:
            raise RemoteReportingError(
                "Could not copy the GB10 report back to the Mac"
            )

        report = json.loads(temporary_report.read_text(encoding="utf-8"))
        validate_document(report, load_schema("report.schema.json"), "remote report")
        os.replace(temporary_report, local_report)
    finally:
        temporary_report.unlink(missing_ok=True)
    return report, local_report
