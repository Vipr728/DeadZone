from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
import yaml

FIXTURES_DIR = Path(__file__).parent / "fixtures"


def make_report(level_id: str, *, planted: bool = False) -> dict[str, Any]:
    problem_points = []
    description = None
    if planted:
        problem_points = [
            {
                "location": {
                    "piece_id": "b_planted_gap",
                    "x": 14.0,
                    "y": -0.5,
                },
                "issue": "Repeated deaths cluster at the same gap.",
                "severity": "high",
                "evidence": "Three deaths near x=14 and 4-6 attempts per episode.",
            }
        ]
        description = "Repeated deaths and high retries identify the planted gap issue."
    return {
        "level_id": level_id,
        "overall_difficulty": "too_hard" if planted else "appropriate",
        "difficulty_rationale": (
            "Repeated deaths make the level too hard."
            if planted
            else "Both runs cleared each piece on the first attempt."
        ),
        "problem_points": problem_points,
        "teachability_assessment": (
            "The problematic gap is in range but is not reliably clearable."
            if planted
            else "Mechanics are introduced and cleared consistently."
        ),
        "planted_issue_detected": {
            "detected": planted,
            "description": description,
        },
    }


class StaticLLM:
    def __init__(self, report: dict[str, Any]) -> None:
        self.report = report
        self.prompts: list[str] = []

    def generate_structured(self, prompt: str, schema: dict[str, Any]) -> dict[str, Any]:
        self.prompts.append(prompt)
        return self.report


@pytest.fixture
def config_factory(tmp_path):
    def create(*, backend: str = "ollama") -> tuple[Path, dict[str, Path]]:
        directories = {
            "exports": tmp_path / "exports",
            "builds": tmp_path / "builds",
            "telemetry": tmp_path / "telemetry",
            "reports": tmp_path / "reports",
            "checkpoints": tmp_path / "checkpoints",
        }
        for directory in directories.values():
            directory.mkdir(parents=True, exist_ok=True)
        manifest = tmp_path / "checkpoint_manifest.json"
        script = tmp_path / "finetune_stage2.sh"
        script.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
        script.chmod(0o755)
        config = {
            "paths": {
                "watched_levels_dir": str(directories["exports"]),
                "builds_dir": str(directories["builds"]),
                "telemetry_dir": str(directories["telemetry"]),
                "reports_dir": str(directories["reports"]),
                "checkpoint_manifest": str(manifest),
            },
            "llm": {
                "backend": backend,
                "model": "test-model",
                "gb10_model": "test-gb10-model",
                "use_gb10_model": False,
                "ollama_base_url": "http://127.0.0.1:11434",
                "nim_base_url": "http://127.0.0.1:8000",
                "nemoclaw_base_url": "http://127.0.0.1:8080",
                "timeout_seconds": 2,
            },
            "reporting": {
                "death_cluster_min_episodes": 2,
                "high_attempt_threshold": 4,
                "failure_rate_too_hard": 0.5,
            },
            "orchestration": {
                "fine_tune_script": str(script),
                "stage1_checkpoint": str(directories["checkpoints"] / "stage1" / "{level_id}.ckpt"),
                "checkpoint_out_dir": str(directories["checkpoints"] / "stage2"),
                "num_envs": 2,
                "playtest_command": [
                    "{build_path}",
                    "--level",
                    "{level_id}",
                    "--checkpoint",
                    "{checkpoint_out}",
                    "--telemetry-dir",
                    "{telemetry_dir}",
                ],
                "watch_poll_seconds": 0.01,
            },
            "sandbox": {
                "backend": "application",
                "allowed_read_paths": [
                    str(directories["exports"]),
                    str(directories["builds"]),
                    str(directories["telemetry"]),
                    str(directories["checkpoints"]),
                    str(script),
                ],
                "allowed_write_paths": [
                    str(directories["reports"]),
                    str(directories["telemetry"]),
                    str(directories["checkpoints"]),
                    str(manifest),
                ],
                "egress_policy": "block_all",
                "llm_allowlist": ["127.0.0.1:11434"],
            },
        }
        config_path = tmp_path / "config.yaml"
        config_path.write_text(yaml.safe_dump(config), encoding="utf-8")
        directories["manifest"] = manifest
        directories["script"] = script
        directories["config"] = config_path
        return config_path, directories

    return create


def load_fixture(name: str) -> dict[str, Any]:
    return json.loads((FIXTURES_DIR / name).read_text(encoding="utf-8"))
