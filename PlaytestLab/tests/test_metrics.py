from playtest_lab.metrics import aggregate_episodes, wilson_interval


def test_wilson_interval_is_bounded():
    low, high = wilson_interval(8, 10)
    assert 0 <= low < 0.8 < high <= 1


def test_solved_status_requires_success():
    metrics = aggregate_episodes(
        [{"outcome": "success", "steps": 50, "attempts": 1, "progress": 1}],
        evidence_tier="synthetic",
    )
    assert metrics["solvability_status"] == "solved"
    assert metrics["difficulty_score"] >= 0


def test_impossible_requires_exhaustive_proof():
    episodes = [{"outcome": "timeout", "steps": 200, "attempts": 4, "progress": 0.5}]
    assert aggregate_episodes(episodes, evidence_tier="headless")["solvability_status"] == "not_solved_within_budget"
    assert (
        aggregate_episodes(
            episodes, evidence_tier="headless", proof_status="exhaustive_no_goal"
        )["solvability_status"]
        == "proven_impossible"
    )

