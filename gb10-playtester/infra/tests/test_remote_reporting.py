from __future__ import annotations

import json
import subprocess
from pathlib import Path

import pytest

from conftest import make_report
from playtester_infra.config import load_config
from playtester_infra.remote_reporting import (
    RemoteReportingError,
    generate_report_on_gb10,
)


def test_remote_report_transfers_telemetry_runs_llm_and_returns_report(
    config_factory,
):
    config_path, directories = config_factory()
    config = load_config(config_path)
    remote_config = config.repository_root / "rl/configs/remote_execution.yaml"
    remote_config.parent.mkdir(parents=True, exist_ok=True)
    remote_config.write_text(
        'tailscale_hostname: "gb10.tail.example"\n'
        'ssh_username: "nvidia"\n'
        'repository_path: "GB10-project"\n',
        encoding="utf-8",
    )
    telemetry = directories["telemetry"] / "level_a_run-1.json"
    telemetry.write_text(
        json.dumps({"level_id": "level_a", "run_id": "run-1"}),
        encoding="utf-8",
    )
    calls: list[list[str]] = []

    def run(command, **kwargs):
        calls.append(command)
        if command[0] == "scp" and command[-1].startswith(
            str(directories["reports"])
        ):
            Path(command[-1]).write_text(
                json.dumps(make_report("level_a")),
                encoding="utf-8",
            )
        return subprocess.CompletedProcess(command, 0)

    report, report_path = generate_report_on_gb10(telemetry, config, run=run)

    assert report["level_id"] == "level_a"
    assert report_path == directories["reports"] / "level_a_run-1.json"
    assert [call[0] for call in calls] == ["ssh", "scp", "ssh", "scp"]
    assert all("100." not in " ".join(call) for call in calls)
    assert "nvidia@gb10.tail.example" in calls[0]
    assert "PLAYTESTER_USE_GB10_MODEL=1" in calls[2][-1]
    assert not any(directories["reports"].glob("*.tmp"))


def test_remote_report_rejects_ip_destination(config_factory):
    config_path, directories = config_factory()
    config = load_config(config_path)
    remote_config = config.repository_root / "rl/configs/remote_execution.yaml"
    remote_config.parent.mkdir(parents=True, exist_ok=True)
    remote_config.write_text(
        'tailscale_hostname: "100.64.0.10"\n'
        'ssh_username: "nvidia"\n'
        'repository_path: "GB10-project"\n',
        encoding="utf-8",
    )
    telemetry = directories["telemetry"] / "level_a_run-1.json"
    telemetry.write_text(
        json.dumps({"level_id": "level_a", "run_id": "run-1"}),
        encoding="utf-8",
    )

    with pytest.raises(RemoteReportingError, match="never an IP"):
        generate_report_on_gb10(telemetry, config)
