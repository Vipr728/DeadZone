from __future__ import annotations

import json
import os
from dataclasses import replace
from pathlib import Path
from urllib.error import URLError
from urllib.request import ProxyHandler, build_opener

import pytest

from playtester_infra.config import load_config
from playtester_infra.llm_client import create_llm_client
from playtester_infra.report_pipeline import generate_report


@pytest.mark.integration
def test_real_ollama_detects_planted_issue_when_explicitly_enabled(
    tmp_path: Path,
) -> None:
    """Exercise the real local-model boundary without ever starting/pulling it."""

    if os.environ.get("PLAYTESTER_RUN_OLLAMA_INTEGRATION") != "1":
        pytest.skip(
            "set PLAYTESTER_RUN_OLLAMA_INTEGRATION=1 to use an already-running "
            "local Ollama model"
        )

    config = load_config(Path(__file__).parents[1] / "config.yaml")
    tags_url = f"{config.llm.host.rstrip('/')}/api/tags"
    try:
        with build_opener(ProxyHandler({})).open(
            tags_url,
            timeout=2,
        ) as response:
            inventory = json.load(response)
    except (OSError, URLError, ValueError) as error:
        pytest.skip(f"local Ollama is unavailable: {error}")

    models = inventory.get("models") if isinstance(inventory, dict) else None
    names = {
        model.get("name", model.get("model"))
        for model in models
        if isinstance(model, dict)
    } if isinstance(models, list) else set()
    if config.llm.active_model not in names:
        pytest.skip(
            f"configured model is not installed: {config.llm.active_model}"
        )

    isolated_config = replace(
        config,
        paths=replace(config.paths, reports_dir=tmp_path / "reports"),
    )
    fixture = (
        Path(__file__).parent
        / "fixtures"
        / "planted_issue.telemetry.json"
    )
    report = generate_report(
        fixture,
        create_llm_client(isolated_config),
        config=isolated_config,
    )

    assert report["planted_issue_detected"]["detected"] is True
