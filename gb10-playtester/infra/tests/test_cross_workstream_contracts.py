"""Regression checks for the fixed Unity <-> RL <-> infra integration seams."""

from __future__ import annotations

import json
from pathlib import Path

import yaml

from playtester_infra.config import load_config


REPO_ROOT = Path(__file__).resolve().parents[2]
UNITY_ROOT = REPO_ROOT / "unity" / "PlaytesterProject"


def test_locked_paths_names_and_behavior_match_across_workstreams():
    config = load_config(REPO_ROOT / "infra" / "config.yaml")
    training = yaml.safe_load(
        (REPO_ROOT / "rl" / "configs" / "training_config.yaml").read_text(encoding="utf-8")
    )
    remote = yaml.safe_load(
        (REPO_ROOT / "rl" / "configs" / "remote_execution.yaml").read_text(
            encoding="utf-8"
        )
    )
    build_settings = (UNITY_ROOT / "ProjectSettings" / "EditorBuildSettings.asset").read_text(
        encoding="utf-8"
    )
    manifest = json.loads((UNITY_ROOT / "Packages" / "manifest.json").read_text(encoding="utf-8"))

    assert set(training["behaviors"]) == {"PlaytestAgent"}
    assert manifest["dependencies"]["com.unity.ml-agents"] == "4.0.1"
    assert manifest["dependencies"]["com.unity.sentis"] == "2.2.0"
    assert config.paths.watched_levels_dir == UNITY_ROOT / "Exports"
    assert config.paths.builds_dir == UNITY_ROOT / "Builds"
    assert config.paths.telemetry_dir == UNITY_ROOT / "Telemetry"
    assert config.paths.reports_dir == UNITY_ROOT / "Reports"
    assert config.orchestration.execution_mode == "remote"
    assert remote["base_port"] == 5004
    assert remote["training_config_path"] == "rl/configs/training_config.yaml"
    assert (
        REPO_ROOT / "rl" / "configs" / "training_config.remote_smoke.yaml"
    ).is_file()
    assert not str(remote["tailscale_hostname"]).replace(".", "").isdigit()
    assert "{execution_mode}" in config.orchestration.playtest_command
    for scene_name in ("GymScene", "LevelA", "LevelB"):
        assert f"Assets/Scenes/{scene_name}.unity" in build_settings


def test_unity_controller_keeps_the_locked_agent_control_surface():
    adapter = (UNITY_ROOT / "Assets" / "Scripts" / "Player" / "PlayerInputAdapter.cs").read_text(
        encoding="utf-8"
    )
    controller = (UNITY_ROOT / "Assets" / "Scripts" / "Player" / "PlayerController.cs").read_text(
        encoding="utf-8"
    )
    config_asset = UNITY_ROOT / "Assets" / "Configs" / "PlayerConfig.asset"
    input_actions = UNITY_ROOT / "Assets" / "PlayerControls.inputactions"

    assert "public void SetMove(float direction)" in adapter
    assert "public void SetJump(bool pressed)" in adapter
    assert "body.linearVelocity" in controller
    assert "Input.GetAxis" not in controller
    assert config_asset.is_file()
    actions = json.loads(input_actions.read_text(encoding="utf-8"))
    assert {action["name"] for action in actions["maps"][0]["actions"]} == {"Move", "Jump"}


def test_marker_contract_and_locked_training_flags_are_implemented():
    orchestration = (
        REPO_ROOT / "infra" / "src" / "playtester_infra" / "orchestration.py"
    ).read_text(encoding="utf-8")
    watcher = (
        REPO_ROOT / "infra" / "src" / "playtester_infra" / "openclaw_skill.py"
    ).read_text(encoding="utf-8")
    exporter = (
        UNITY_ROOT / "Assets" / "Scripts" / "EditorTool" / "ExportPanel.cs"
    ).read_text(encoding="utf-8")
    cli = (REPO_ROOT / "rl" / "src" / "playtester_rl" / "cli.py").read_text(
        encoding="utf-8"
    )

    assert "level_export.json" in orchestration
    assert "load_level_export" in orchestration
    assert 'rglob("level_export.json")' in watcher
    assert "BuildPipeline.BuildPlayer" in exporter
    assert '"level_export.json"' in exporter
    assert "BuildResult.Succeeded" in exporter
    for flag in (
        "--level-id",
        "--checkpoint-in",
        "--checkpoint-out",
        "--num-envs",
        "--output-manifest",
        "--execution-mode",
        "--remote-config",
        "--remote-port",
    ):
        assert flag in cli
