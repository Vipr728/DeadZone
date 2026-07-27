import pytest

from ryz1.data.schema import (
    select_teacher_transitions,
    teacher_sequences_to_arrays,
    transitions_to_arrays,
    validate_dataset,
)


def test_validate_dataset_requires_transitions():
    payload = {
        "schemaVersion": "ryz-search-dataset/1.0",
        "datasetId": "x",
        "taskIds": ["task"],
        "transitions": [],
    }
    try:
        validate_dataset(payload)
    except ValueError as exc:
        assert "no transitions" in str(exc)
    else:
        raise AssertionError("expected empty dataset to fail")


def test_mechanics_vector_is_preserved_for_conditioned_training():
    mechanics = [0.0] * 32
    mechanics[4] = 1.0
    payload = {
        "schemaVersion": "ryz-search-dataset/1.0",
        "datasetId": "conditioned",
        "taskIds": ["dash-task"],
        "transitions": [
            {
                "taskId": "dash-task",
                "trialId": 0,
                "macroId": 6,
                "mechanicsVector": mechanics,
                "observation": {"playerVector": [0.0] * 16},
                "nextObservation": {"playerVector": [1.0] * 16},
            }
        ],
    }

    shape = validate_dataset(payload)
    arrays = transitions_to_arrays(payload, shape)

    assert shape.mechanics_vector_size == 32
    assert arrays["mechanics"].shape == (1, 32)
    assert arrays["mechanics"][0, 4] == 1.0


def test_teacher_selection_chooses_one_winning_action_per_search_state():
    transitions = [
        {
            "taskId": "task",
            "trialId": 0,
            "parentId": 4,
            "nodeId": 5,
            "macroId": 1,
            "candidateScore": 0.9,
            "teacherSelected": True,
        },
        {
            "taskId": "task",
            "trialId": 0,
            "parentId": 4,
            "nodeId": 6,
            "macroId": 3,
            "candidateScore": 0.8,
            "eventuallyCompleted": True,
        },
    ]

    selected = select_teacher_transitions(transitions)

    assert len(selected) == 1
    assert selected[0]["macroId"] == 3


def test_teacher_sequences_follow_parent_chain_and_encode_history():
    mechanics = [0.25] * 32
    base = {
        "taskId": "task",
        "trialId": 2,
        "mechanicsVector": mechanics,
        "observation": {"playerVector": [0.0] * 16},
        "nextObservation": {"playerVector": [1.0] * 16},
        "teacherSelected": True,
    }
    transitions = [
        {**base, "nodeId": 1, "parentId": 0, "macroId": 3, "reward": 0.1},
        {
            **base,
            "nodeId": 2,
            "parentId": 1,
            "macroId": 6,
            "reward": 0.2,
            "observation": {"playerVector": [1.0] * 16},
        },
    ]
    payload = {
        "schemaVersion": "ryz-search-dataset/1.0",
        "datasetId": "sequences",
        "taskIds": ["task"],
        "transitions": transitions,
    }

    shape = validate_dataset(payload)
    arrays = teacher_sequences_to_arrays(payload, shape, sequence_length=4)

    assert arrays["player"].shape == (2, 4, 16)
    assert arrays["lengths"].tolist() == [1, 2]
    assert arrays["history"][1, -1, 0] == pytest.approx(3 / 6)
    assert arrays["history"][1, -1, 1] == pytest.approx(0.1)
    assert arrays["history"][1, -1, 3] == 1.0
    assert arrays["trial_ids"].tolist() == [2, 2]
