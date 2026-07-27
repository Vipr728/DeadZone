from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from playtester_rl.remote_execution import (
    RemoteExecutionError,
    allocate_remote_port,
    build_local_unity_command,
    build_remote_trainer_command,
    load_remote_config,
    run_remote_policy_session,
    ssh_command,
    tunnel_command,
    verify_remote_preflight,
    verify_remote_port_available,
)


def _write_config(tmp_path: Path, *, username: str = "nvidia"):
    path = tmp_path / "remote.yaml"
    path.write_text(
        "\n".join(
            [
                'tailscale_hostname: "gb10.example.ts.net"',
                f'ssh_username: "{username}"',
                'repository_path: "GB10-project"',
                'results_dir: "rl/checkpoints/remote-results"',
                'training_config_path: "rl/configs/training_config.remote_smoke.yaml"',
                'trainer_executable: "rl/.venv-mlagents/bin/mlagents-learn"',
                "base_port: 5004",
                "connect_timeout_seconds: 3",
                "command_timeout_seconds: 30",
                "require_direct_tailscale: true",
            ]
        )
        + "\n",
        encoding="utf-8",
    )
    return path


def test_remote_config_requires_explicit_ssh_username(tmp_path, monkeypatch):
    monkeypatch.delenv("PLAYTESTER_GB10_USER", raising=False)
    with pytest.raises(RemoteExecutionError, match="ssh_username"):
        load_remote_config(_write_config(tmp_path, username=""))


def test_remote_config_identity_can_be_overridden_by_environment(tmp_path, monkeypatch):
    monkeypatch.setenv("PLAYTESTER_GB10_HOST", "actual-gb10.tail.example")
    monkeypatch.setenv("PLAYTESTER_GB10_USER", "remote-user")
    config = load_remote_config(_write_config(tmp_path, username="placeholder"))

    assert config.ssh_target == "remote-user@actual-gb10.tail.example"


def test_remote_paths_cannot_escape_repository(tmp_path):
    path = _write_config(tmp_path)
    text = path.read_text(encoding="utf-8").replace(
        'repository_path: "GB10-project"',
        'repository_path: "../outside"',
    )
    path.write_text(text, encoding="utf-8")
    with pytest.raises(RemoteExecutionError, match="inside the remote repository"):
        load_remote_config(path)


def test_remote_paths_reject_shell_ambiguous_characters(tmp_path):
    path = _write_config(tmp_path)
    text = path.read_text(encoding="utf-8").replace(
        'repository_path: "GB10-project"',
        'repository_path: "GB10 project"',
    )
    path.write_text(text, encoding="utf-8")
    with pytest.raises(RemoteExecutionError, match="relative POSIX path"):
        load_remote_config(path)


def test_remote_config_rejects_hardcoded_ip(tmp_path):
    path = _write_config(tmp_path)
    path.write_text(
        path.read_text(encoding="utf-8").replace(
            "gb10.example.ts.net", "100.122.207.66"
        ),
        encoding="utf-8",
    )
    with pytest.raises(RemoteExecutionError, match="never an IP"):
        load_remote_config(path)


def test_remote_commands_use_hostname_and_no_remote_unity_env(tmp_path):
    config = load_remote_config(_write_config(tmp_path))
    trainer = build_remote_trainer_command(
        config,
        run_id="level_a_stage2",
        port=5123,
        num_envs=1,
        initialize_from_run_id="gym_stage1",
        inference=False,
        torch_device="cuda",
    )

    assert "--env" not in " ".join(trainer)
    assert "--base-port=5123" in trainer
    assert "--initialize-from=gym_stage1" in trainer
    assert "--torch-device=cuda" in trainer
    assert config.ssh_target in ssh_command(config, trainer)
    assert "127.0.0.1:5123:127.0.0.1:5123" in tunnel_command(config, 5123)


def test_remote_inference_resumes_exact_run(tmp_path):
    config = load_remote_config(_write_config(tmp_path))
    command = build_remote_trainer_command(
        config,
        run_id="level_b_stage2",
        port=5124,
        num_envs=1,
        initialize_from_run_id=None,
        inference=True,
        torch_device="cuda",
    )
    assert "--resume" in command
    assert "--inference" in command
    assert "--run-id=level_b_stage2" in command


def test_unique_ports_are_allocated_per_run(tmp_path):
    config = load_remote_config(_write_config(tmp_path))
    first = allocate_remote_port(config, level_id="level_a", run_id="run-a")
    second = allocate_remote_port(config, level_id="level_b", run_id="run-b")
    assert first != second


def test_local_unity_connects_to_forwarded_port():
    command = build_local_unity_command(
        Path("/build/Playtester"),
        port=5004,
        env_max_steps=16,
        extra_args=("--smoke-exit",),
    )
    assert command[:5] == [
        "/build/Playtester",
        "-batchmode",
        "-nographics",
        "--mlagents-port",
        "5004",
    ]
    assert command[-1] == "--smoke-exit"


def test_preflight_requires_direct_matching_commit(tmp_path, monkeypatch):
    config = load_remote_config(_write_config(tmp_path))
    monkeypatch.setattr(
        "playtester_rl.remote_execution.shutil.which",
        lambda name: "/opt/bin/tailscale",
    )
    calls: list[list[str]] = []

    def run(command, **kwargs):
        calls.append(command)
        if command[0] == "/opt/bin/tailscale":
            return subprocess.CompletedProcess(
                command, 0, "pong from gb10 via 192.0.2.1:41641 in 2ms\n", ""
            )
        return subprocess.CompletedProcess(command, 0, "abc123\n", "")

    verify_remote_preflight(config, expected_commit="abc123", run=run)
    assert calls[1][0] == "ssh"


def test_preflight_rejects_derp_relay(tmp_path, monkeypatch):
    config = load_remote_config(_write_config(tmp_path))
    monkeypatch.setattr(
        "playtester_rl.remote_execution.shutil.which",
        lambda name: "/opt/bin/tailscale",
    )

    def run(command, **kwargs):
        return subprocess.CompletedProcess(
            command, 0, "pong from gb10 via DERP(sea) in 20ms\n", ""
        )

    with pytest.raises(RemoteExecutionError, match="relayed"):
        verify_remote_preflight(config, expected_commit="abc123", run=run)


def test_remote_port_probe_runs_over_ssh(tmp_path):
    config = load_remote_config(_write_config(tmp_path))
    calls = []

    def run(command, **kwargs):
        calls.append(command)
        return subprocess.CompletedProcess(command, 0, "", "")

    verify_remote_port_available(config, 5123, run=run)
    assert calls[0][0] == "ssh"
    assert "5123" in calls[0][-1]


def test_remote_session_reaps_unity_trainer_and_tunnel(tmp_path):
    config = load_remote_config(_write_config(tmp_path))
    processes = []

    class FakeProcess:
        def __init__(self, command):
            self.command = command
            self.returncode = None
            self.terminated = False
            self.poll_count = 0

        def poll(self):
            self.poll_count += 1
            if (
                self.command[0] == "ssh"
                and "-N" not in self.command
                and self.poll_count >= 2
            ):
                self.returncode = 0
            return self.returncode

        def wait(self, timeout=None):
            if self.command[0] == "ssh" and "-N" not in self.command:
                self.returncode = 0
            elif self.terminated:
                self.returncode = 0
            return self.returncode

        def terminate(self):
            self.terminated = True

        def kill(self):
            self.returncode = -9

    def popen(command):
        process = FakeProcess(command)
        processes.append(process)
        return process

    result = run_remote_policy_session(
        config,
        remote_args=["trainer"],
        unity_args=["unity"],
        port=5004,
        popen=popen,
        startup_seconds=0,
        poll_seconds=0,
    )

    assert result == 0
    assert len(processes) == 3
    assert all(process.poll() is not None for process in processes)


def test_remote_session_returns_immediately_when_unity_fails(tmp_path):
    config = load_remote_config(_write_config(tmp_path))
    processes = []

    class FakeProcess:
        def __init__(self, command):
            self.command = command
            self.returncode = None

        def poll(self):
            if self.command[0] == "unity":
                self.returncode = 23
            return self.returncode

        def wait(self, timeout=None):
            return self.returncode

        def terminate(self):
            self.returncode = -15

        def kill(self):
            self.returncode = -9

    def popen(command):
        process = FakeProcess(command)
        processes.append(process)
        return process

    result = run_remote_policy_session(
        config,
        remote_args=["trainer"],
        unity_args=["unity"],
        port=5004,
        popen=popen,
        startup_seconds=0,
        poll_seconds=0,
    )

    assert result == 23
    assert all(process.poll() is not None for process in processes)
