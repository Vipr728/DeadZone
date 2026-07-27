from __future__ import annotations

import os

import httpx
import pytest

from conftest import FIXTURES_DIR
from playtester_infra.config import load_config
from playtester_infra.llm_client import OllamaClient
from playtester_infra.report_pipeline import generate_report


@pytest.mark.ollama
@pytest.mark.parametrize(
    ("fixture_name", "expect_planted"),
    [
        ("level_a_normal.json", False),
        ("level_b_death_cluster.json", True),
    ],
)
def test_live_ollama_fixture_reports(
    fixture_name, expect_planted, config_factory
):
    if os.environ.get("RUN_OLLAMA_TESTS") != "1":
        pytest.skip("set RUN_OLLAMA_TESTS=1 to exercise the local model")
    source_config = load_config()
    model = os.environ.get("OLLAMA_TEST_MODEL", source_config.llm.selected_model)
    try:
        response = httpx.get(
            f"{source_config.llm.ollama_base_url}/api/tags",
            timeout=2,
            trust_env=False,
        )
        response.raise_for_status()
    except httpx.HTTPError:
        pytest.skip("local Ollama service is unavailable")
    model_names = {item["name"] for item in response.json().get("models", [])}
    if model not in model_names:
        pytest.skip(f"Ollama model {model!r} is not pulled")

    config_path, _ = config_factory()
    report = generate_report(
        str(FIXTURES_DIR / fixture_name),
        OllamaClient(
            model,
            source_config.llm.ollama_base_url,
            source_config.llm.timeout_seconds,
        ),
        config_path=config_path,
    )
    assert report["planted_issue_detected"]["detected"] is expect_planted, report
