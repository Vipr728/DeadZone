from __future__ import annotations

import json

import pytest

from conftest import FIXTURES_DIR, StaticLLM, make_report
from playtester_infra.report_pipeline import (
    ReportPipelineError,
    compute_level_local_precedent,
    generate_report,
)
from playtester_infra.schemas import load_schema, validate_document


def test_level_a_report_is_valid_atomically_named_and_prompted(config_factory):
    config_path, directories = config_factory()
    telemetry_path = FIXTURES_DIR / "level_a_normal.json"
    llm = StaticLLM(make_report("level_a"))

    report = generate_report(str(telemetry_path), llm, config_path=config_path)

    output = (
        directories["reports"]
        / "level_a_11111111-1111-4111-8111-111111111111.json"
    )
    assert json.loads(output.read_text(encoding="utf-8")) == report
    assert "seen_in_stage1_range" in llm.prompts[0]
    assert "taught_earlier_in_this_level" in llm.prompts[0]
    assert '"planted_issue_candidate_count": 0' in llm.prompts[0]
    assert "level_a" in llm.prompts[0]
    assert not list(directories["reports"].glob("*.tmp"))
    validate_document(report, load_schema("report.schema.json"), "report")


def test_level_local_precedent_is_ordered_and_distinct_from_training_range():
    pieces = [
        {"piece_type": "move_to_goal"},
        {"piece_type": "gap_jump"},
        {"piece_type": "move_to_goal"},
    ]
    assert compute_level_local_precedent(pieces) == [False, False, True]


def test_level_b_fixture_detects_planted_issue(config_factory):
    config_path, _ = config_factory()
    report = generate_report(
        str(FIXTURES_DIR / "level_b_death_cluster.json"),
        StaticLLM(make_report("level_b", planted=False)),
        config_path=config_path,
    )
    assert report["planted_issue_detected"]["detected"] is True
    assert report["problem_points"][0]["location"]["piece_id"] == "b_planted_gap"
    assert report["overall_difficulty"] == "too_hard"


def test_existing_report_is_never_overwritten(config_factory):
    config_path, directories = config_factory()
    telemetry_path = FIXTURES_DIR / "level_a_normal.json"
    first = make_report("level_a")
    generate_report(str(telemetry_path), StaticLLM(first), config_path=config_path)
    with pytest.raises(ReportPipelineError, match="Refusing to overwrite"):
        generate_report(
            str(telemetry_path),
            StaticLLM({**first, "difficulty_rationale": "different"}),
            config_path=config_path,
        )
    output = next(directories["reports"].glob("level_a_*.json"))
    assert json.loads(output.read_text(encoding="utf-8")) == first


def test_malformed_telemetry_stops_before_llm(config_factory):
    config_path, _ = config_factory()
    llm = StaticLLM(make_report("level_bad"))
    with pytest.raises(ReportPipelineError, match="telemetry failed validation"):
        generate_report(
            str(FIXTURES_DIR / "malformed_telemetry.json"),
            llm,
            config_path=config_path,
        )
    assert llm.prompts == []


@pytest.mark.parametrize(
    "report",
    [
        {"level_id": "level_a"},
        make_report("wrong_level"),
    ],
)
def test_malformed_or_mismatched_report_is_not_written(
    config_factory, report
):
    config_path, directories = config_factory()
    with pytest.raises(ReportPipelineError) as caught:
        generate_report(
            str(FIXTURES_DIR / "level_a_normal.json"),
            StaticLLM(report),
            config_path=config_path,
        )
    assert caught.value is not None
    assert not list(directories["reports"].glob("*.json"))
