from __future__ import annotations

import pytest

from conftest import load_fixture
from playtester_infra.schemas import (
    DocumentValidationError,
    load_schema,
    validate_document,
)


@pytest.mark.parametrize(
    "fixture_name",
    [
        "level_a_normal.json",
        "level_b_death_cluster.json",
        "out_of_training_range.json",
    ],
)
def test_deterministic_telemetry_fixtures_validate(fixture_name):
    validate_document(
        load_fixture(fixture_name),
        load_schema("telemetry.schema.json"),
        fixture_name,
    )


def test_out_of_range_fixture_is_stamped_not_in_training_range():
    fixture = load_fixture("out_of_training_range.json")
    piece = fixture["episode_summaries"][0]["piece_results"][0]
    assert piece["seen_in_stage1_range"] is False
    assert piece["params"]["width"] == 9.0


def test_malformed_fixture_fails_contract():
    with pytest.raises(DocumentValidationError):
        validate_document(
            load_fixture("malformed_telemetry.json"),
            load_schema("telemetry.schema.json"),
            "malformed fixture",
        )
