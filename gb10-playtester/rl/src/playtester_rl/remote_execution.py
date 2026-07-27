"""SSH/Tailscale transport for Mac Unity simulations and GB10 policies.

The GB10 owns ML-Agents/PyTorch. Unity remains on the Mac and connects to the
trainer through an SSH local-forward. No IP address is accepted or embedded:
the configured Tailscale hostname is always used as the SSH destination.
"""

from __future__ import annotations

import os
import re
import shlex
import shutil
import socket
import subprocess
import time
import zlib
from ipaddress import ip_address
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Callable, Sequence

import yaml


class RemoteExecutionError(RuntimeError):
    """The remote policy/training boundary could not be established."""


_HOST_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9.-]*$")
_USER_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_.-]*$")
_REMOTE_PATH_PATTERN = re.compile(r"^[A-Za-z0-9._/-]+$")


@dataclass(frozen=True)
class RemoteExecutionConfig:
    tailscale_hostname: str
    ssh_username: str
    repository_path: str
    results_dir: str
    training_config_path: str
    trainer_executable: str
    base_port: int
    connect_timeout_seconds: int
    command_timeout_seconds: int
    require_direct_tailscale: bool

    @property
    def ssh_target(self) -> str:
        return f"{self.ssh_username}@{self.tailscale_hostname}"


def _safe_remote_relative_path(value: object, label: str) -> str:
    if (
        not isinstance(value, str)
        or not value.strip()
        or not _REMOTE_PATH_PATTERN.fullmatch(value)
    ):
        raise RemoteExecutionError(f"{label} must be a non-empty relative POSIX path")
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts:
        raise RemoteExecutionError(f"{label} must stay inside the remote repository")
    return str(path)


def load_remote_config(path: str | Path) -> RemoteExecutionConfig:
    source = Path(path).expanduser().resolve(strict=True)
    try:
        raw = yaml.safe_load(source.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, yaml.YAMLError) as error:
        raise RemoteExecutionError(f"Could not load remote config {source}: {error}") from error
    if not isinstance(raw, dict):
        raise RemoteExecutionError("Remote config must be a YAML object")

    host = os.environ.get("PLAYTESTER_GB10_HOST", raw.get("tailscale_hostname", ""))
    user = os.environ.get("PLAYTESTER_GB10_USER", raw.get("ssh_username", ""))
    if not isinstance(host, str) or not _HOST_PATTERN.fullmatch(host):
        raise RemoteExecutionError("tailscale_hostname must be a DNS hostname, never an IP")
    try:
        ip_address(host)
    except ValueError:
        pass
    else:
        raise RemoteExecutionError("tailscale_hostname must be a DNS hostname, never an IP")
    if not isinstance(user, str) or not _USER_PATTERN.fullmatch(user):
        raise RemoteExecutionError(
            "ssh_username is missing or unsafe; set it in remote_execution.yaml "
            "or PLAYTESTER_GB10_USER"
        )

    base_port = raw.get("base_port", 5004)
    connect_timeout = raw.get("connect_timeout_seconds", 10)
    command_timeout = raw.get("command_timeout_seconds", 7200)
    if not isinstance(base_port, int) or not 1024 <= base_port <= 60000:
        raise RemoteExecutionError("base_port must be an integer from 1024 through 60000")
    if not isinstance(connect_timeout, int) or connect_timeout < 1:
        raise RemoteExecutionError("connect_timeout_seconds must be positive")
    if not isinstance(command_timeout, int) or command_timeout < 1:
        raise RemoteExecutionError("command_timeout_seconds must be positive")

    trainer = _safe_remote_relative_path(
        raw.get("trainer_executable", "rl/.venv-mlagents/bin/mlagents-learn"),
        "trainer_executable",
    )
    return RemoteExecutionConfig(
        tailscale_hostname=host,
        ssh_username=user,
        repository_path=_safe_remote_relative_path(
            raw.get("repository_path", "GB10-project"), "repository_path"
        ),
        results_dir=_safe_remote_relative_path(
            raw.get("results_dir", "rl/checkpoints/remote-results"), "results_dir"
        ),
        training_config_path=_safe_remote_relative_path(
            raw.get(
                "training_config_path",
                "rl/configs/training_config.remote_smoke.yaml",
            ),
            "training_config_path",
        ),
        trainer_executable=trainer,
        base_port=base_port,
        connect_timeout_seconds=connect_timeout,
        command_timeout_seconds=command_timeout,
        require_direct_tailscale=bool(raw.get("require_direct_tailscale", True)),
    )


def allocate_remote_port(
    config: RemoteExecutionConfig,
    *,
    level_id: str,
    run_id: str,
    requested_port: int | None = None,
) -> int:
    """Allocate a stable per-run port and reject a local collision."""
    port = requested_port or (
        config.base_port + zlib.crc32(f"{level_id}:{run_id}".encode("utf-8")) % 1000
    )
    if not 1024 <= port <= 65535:
        raise RemoteExecutionError(f"Allocated port is out of range: {port}")
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        try:
            probe.bind(("127.0.0.1", port))
        except OSError as error:
            raise RemoteExecutionError(f"Local tunnel port {port} is already in use") from error
    return port


def ssh_command(config: RemoteExecutionConfig, remote_args: Sequence[str]) -> list[str]:
    remote = (
        f"cd {shlex.quote(config.repository_path)} && "
        f"{shlex.join([str(part) for part in remote_args])}"
    )
    return [
        "ssh",
        "-o",
        "BatchMode=yes",
        "-o",
        f"ConnectTimeout={config.connect_timeout_seconds}",
        config.ssh_target,
        remote,
    ]


def tunnel_command(config: RemoteExecutionConfig, port: int) -> list[str]:
    return [
        "ssh",
        "-N",
        "-o",
        "BatchMode=yes",
        "-o",
        "ExitOnForwardFailure=yes",
        "-o",
        f"ConnectTimeout={config.connect_timeout_seconds}",
        "-L",
        f"127.0.0.1:{port}:127.0.0.1:{port}",
        config.ssh_target,
    ]


def build_remote_trainer_command(
    config: RemoteExecutionConfig,
    *,
    run_id: str,
    port: int,
    num_envs: int,
    initialize_from_run_id: str | None,
    inference: bool,
    torch_device: str | None,
) -> list[str]:
    command = [
        config.trainer_executable,
        config.training_config_path,
        f"--run-id={run_id}",
        f"--results-dir={config.results_dir}",
        f"--num-envs={num_envs}",
        f"--base-port={port}",
        "--no-graphics",
    ]
    if inference:
        command.extend(["--resume", "--inference"])
    elif initialize_from_run_id:
        command.append(f"--initialize-from={initialize_from_run_id}")
    if torch_device:
        command.append(f"--torch-device={torch_device}")
    return command


def build_local_unity_command(
    player: Path,
    *,
    port: int,
    env_max_steps: int | None,
    extra_args: Sequence[str] = (),
) -> list[str]:
    command = [
        str(player),
        "-batchmode",
        "-nographics",
        "--mlagents-port",
        str(port),
    ]
    if env_max_steps is not None:
        command.extend(["--mlagents-max-steps", str(env_max_steps)])
    command.extend(str(part) for part in extra_args)
    return command


def local_player_executable(build_path: Path) -> Path:
    if build_path.suffix != ".app":
        if not build_path.is_file() or not os.access(build_path, os.X_OK):
            raise RemoteExecutionError(f"Unity player is not executable: {build_path}")
        return build_path
    executable_dir = build_path / "Contents" / "MacOS"
    candidates = [
        path
        for path in executable_dir.iterdir()
        if path.is_file() and os.access(path, os.X_OK)
    ]
    if len(candidates) != 1:
        raise RemoteExecutionError(
            f"Expected one executable in macOS app {build_path}, found {len(candidates)}"
        )
    return candidates[0]


def verify_remote_preflight(
    config: RemoteExecutionConfig,
    *,
    expected_commit: str,
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> None:
    tailscale = shutil.which("tailscale")
    if tailscale is None:
        raise RemoteExecutionError("tailscale CLI is required for remote mode")
    ping = run(
        [tailscale, "ping", "-c", "1", config.tailscale_hostname],
        text=True,
        capture_output=True,
        check=False,
    )
    ping_text = f"{ping.stdout}\n{ping.stderr}"
    if ping.returncode != 0 or "pong from" not in ping_text:
        raise RemoteExecutionError(
            f"Tailscale peer {config.tailscale_hostname} is unreachable"
        )
    if config.require_direct_tailscale and "DERP" in ping_text.upper():
        raise RemoteExecutionError("Tailscale connection is relayed through DERP, not direct")

    remote_probe = run(
        ssh_command(
            config,
            [
                "sh",
                "-lc",
                (
                    "test -x "
                    f"{shlex.quote(config.trainer_executable)}"
                    " && test -f "
                    f"{shlex.quote(config.training_config_path)}"
                    " && git rev-parse HEAD"
                ),
            ],
        ),
        text=True,
        capture_output=True,
        check=False,
    )
    if remote_probe.returncode != 0:
        raise RemoteExecutionError(
            f"GB10 preflight failed: {remote_probe.stderr.strip() or remote_probe.stdout.strip()}"
        )
    remote_commit = remote_probe.stdout.strip().splitlines()[-1]
    if remote_commit != expected_commit:
        raise RemoteExecutionError(
            f"GB10 repository is at {remote_commit}, expected {expected_commit}"
        )


def verify_remote_port_available(
    config: RemoteExecutionConfig,
    port: int,
    *,
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> None:
    probe = (
        "import socket; "
        "s=socket.socket(); "
        f"s.bind(('127.0.0.1',{port})); "
        "s.close()"
    )
    completed = run(
        ssh_command(config, ["python3", "-c", probe]),
        text=True,
        capture_output=True,
        check=False,
    )
    if completed.returncode != 0:
        raise RemoteExecutionError(f"Remote trainer port {port} is already in use")


def _terminate(process: subprocess.Popen[object] | None) -> None:
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def run_remote_policy_session(
    config: RemoteExecutionConfig,
    *,
    remote_args: Sequence[str],
    unity_args: Sequence[str],
    port: int,
    popen: Callable[..., subprocess.Popen[object]] = subprocess.Popen,
    startup_seconds: float = 1.0,
    poll_seconds: float = 0.1,
    unity_shutdown_grace_seconds: float = 30.0,
) -> int:
    """Run trainer, tunnel, and Unity as one lifecycle; always reap all three."""
    tunnel: subprocess.Popen[object] | None = None
    trainer: subprocess.Popen[object] | None = None
    unity: subprocess.Popen[object] | None = None
    try:
        tunnel = popen(tunnel_command(config, port))
        time.sleep(startup_seconds)
        if tunnel.poll() is not None:
            raise RemoteExecutionError("SSH tunnel exited before the policy connected")
        trainer = popen(ssh_command(config, remote_args))
        time.sleep(startup_seconds)
        if trainer.poll() is not None:
            return int(trainer.returncode or 1)
        unity = popen(list(unity_args))
        deadline = time.monotonic() + config.command_timeout_seconds
        unity_exited_at: float | None = None
        while True:
            if tunnel.poll() is not None:
                raise RemoteExecutionError("SSH tunnel exited during the policy session")

            trainer_status = trainer.poll()
            if trainer_status is not None:
                return int(trainer_status)

            unity_status = unity.poll()
            if unity_status is not None:
                if unity_status != 0:
                    return int(unity_status)
                if unity_exited_at is None:
                    unity_exited_at = time.monotonic()
                elif (
                    time.monotonic() - unity_exited_at
                    >= unity_shutdown_grace_seconds
                ):
                    raise RemoteExecutionError(
                        "Unity exited successfully but the remote trainer did not "
                        "finish during the shutdown grace period"
                    )

            if time.monotonic() >= deadline:
                raise RemoteExecutionError(
                    f"Remote policy timed out after "
                    f"{config.command_timeout_seconds} seconds"
                )
            time.sleep(poll_seconds)
    finally:
        _terminate(unity)
        _terminate(trainer)
        _terminate(tunnel)


def fetch_remote_run(
    config: RemoteExecutionConfig,
    *,
    run_id: str,
    local_results_dir: Path,
    run: Callable[..., subprocess.CompletedProcess[object]] = subprocess.run,
) -> Path:
    """Copy one completed ML-Agents run back for manifests and ONNX fallback."""
    local_results_dir.mkdir(parents=True, exist_ok=True)
    copied = local_results_dir / run_id
    if copied.exists():
        raise RemoteExecutionError(f"Refusing to overwrite copied remote run: {copied}")
    remote_path = (
        f"{config.ssh_target}:{config.repository_path}/"
        f"{config.results_dir}/{run_id}"
    )
    completed = run(
        [
            "scp",
            "-r",
            "-o",
            "BatchMode=yes",
            "-o",
            f"ConnectTimeout={config.connect_timeout_seconds}",
            remote_path,
            str(local_results_dir),
        ],
        check=False,
    )
    if completed.returncode != 0:
        raise RemoteExecutionError(f"Could not copy remote run {run_id} from the GB10")
    if not copied.is_dir():
        raise RemoteExecutionError(f"Remote run copy is missing expected directory {copied}")
    return copied


def transfer_file_to_remote(
    config: RemoteExecutionConfig,
    *,
    local_path: Path,
    remote_relative_path: str,
    run: Callable[..., subprocess.CompletedProcess[object]] = subprocess.run,
) -> None:
    destination = _safe_remote_relative_path(remote_relative_path, "remote_relative_path")
    parent = str(PurePosixPath(destination).parent)
    mkdir = run(
        ssh_command(config, ["mkdir", "-p", parent]),
        check=False,
    )
    if mkdir.returncode != 0:
        raise RemoteExecutionError(f"Could not prepare remote directory {parent}")
    copied = run(
        [
            "scp",
            "-o",
            "BatchMode=yes",
            str(local_path),
            f"{config.ssh_target}:{config.repository_path}/{destination}",
        ],
        check=False,
    )
    if copied.returncode != 0:
        raise RemoteExecutionError(f"Could not transfer {local_path} to the GB10")
