"""Tests for gate_eval.py — synthetic reward-curve fixtures (monotonic
increase, plateau, collapse) for Gate 1, and manifest-shape-driven cases for
Gate 2, per prd-ml.md §8's test plan."""

from __future__ import annotations

from playtester_rl.gate_eval import (
    GateResult,
    compute_steps_to_converge,
    gate1_check,
    gate2_check,
    load_reward_curve,
    save_reward_curve,
)


def _monotonic_increasing_curve(n: int = 100) -> list[tuple[int, float]]:
    return [(i * 1000, i * 0.05) for i in range(n)]


def _plateaued_curve(n: int = 100) -> list[tuple[int, float]]:
    # Rises for the first half, then flatlines — the last-10%-vs-previous-10%
    # comparison should see no meaningful further increase.
    curve = []
    for i in range(n):
        reward = min(i, n // 2) * 0.05
        curve.append((i * 1000, reward))
    return curve


def _collapsing_curve(n: int = 100) -> list[tuple[int, float]]:
    # Rises then collapses toward the end (e.g. a policy that destabilizes).
    curve = []
    for i in range(n):
        if i < n // 2:
            reward = i * 0.05
        else:
            reward = (n // 2) * 0.05 - (i - n // 2) * 0.1
        curve.append((i * 1000, reward))
    return curve


# ---------------------------------------------------------------------------
# Gate 1 — synthetic curve fixtures
# ---------------------------------------------------------------------------


def test_gate1_passes_on_monotonic_increasing_curve():
    result = gate1_check(_monotonic_increasing_curve())
    assert isinstance(result, GateResult)
    assert result.passed is True
    assert result.metrics["relative_change"] > 0


def test_gate1_fails_on_plateaued_curve():
    result = gate1_check(_plateaued_curve())
    assert result.passed is False


def test_gate1_fails_on_collapsing_curve():
    result = gate1_check(_collapsing_curve())
    assert result.passed is False
    assert result.metrics["relative_change"] < 0


def test_gate1_inconclusive_on_too_few_points():
    result = gate1_check([(0, 0.0), (1, 0.1)])
    assert result.passed is False
    assert "Not enough" in result.message


def test_gate1_handles_zero_previous_mean_without_crashing():
    # previous block mean is exactly 0 — must not raise a ZeroDivisionError.
    # window_fraction=0.5 makes the split land exactly on the 0.0/5.0 boundary
    # (10 zero points, then 10 points at 5.0).
    curve = [(i, 0.0) for i in range(10)] + [(i + 10, 5.0) for i in range(10)]
    result = gate1_check(curve, window_fraction=0.5)
    assert result.metrics["previous_block_mean_reward"] == 0.0
    assert result.passed is True  # absolute jump from 0 counts as passing


def test_gate1_metrics_report_the_actual_numbers():
    result = gate1_check(_monotonic_increasing_curve())
    assert result.metrics["previous_block_mean_reward"] < result.metrics["last_block_mean_reward"]
    assert result.metrics["num_points"] == 100


# ---------------------------------------------------------------------------
# Reward curve JSON round-trip (used by training scripts to persist logs)
# ---------------------------------------------------------------------------


def test_reward_curve_round_trips_through_json(tmp_path):
    curve = _monotonic_increasing_curve(10)
    path = tmp_path / "reward_curve.json"
    save_reward_curve(curve, path)
    reloaded = load_reward_curve(path)
    assert reloaded == curve


def test_reward_curve_loads_sorted_even_if_written_out_of_order(tmp_path):
    curve = [(3000, 0.3), (1000, 0.1), (2000, 0.2)]
    path = tmp_path / "unsorted.json"
    save_reward_curve(curve, path)
    reloaded = load_reward_curve(path)
    assert reloaded == [(1000, 0.1), (2000, 0.2), (3000, 0.3)]


# ---------------------------------------------------------------------------
# compute_steps_to_converge
# ---------------------------------------------------------------------------


def test_steps_to_converge_finds_first_crossing():
    curve = [(i * 100, i * 0.1) for i in range(11)]  # rewards 0.0 .. 1.0
    # final_mean_reward = 1.0, threshold 0.9 -> first step where smoothed >= 0.9
    step = compute_steps_to_converge(curve, final_mean_reward=1.0, threshold_fraction=0.9, smoothing_window=1)
    assert step == 900  # reward 0.9 first occurs at step index 9 -> step 900


def test_steps_to_converge_returns_none_if_never_reached():
    curve = [(i * 100, 0.01) for i in range(10)]
    step = compute_steps_to_converge(curve, final_mean_reward=1.0, threshold_fraction=0.9)
    assert step is None


def test_steps_to_converge_handles_zero_final_reward():
    curve = [(i, 0.0) for i in range(5)]
    assert compute_steps_to_converge(curve, final_mean_reward=0.0) is None


def test_steps_to_converge_empty_curve_returns_none():
    assert compute_steps_to_converge([], final_mean_reward=1.0) is None


# ---------------------------------------------------------------------------
# Gate 2 — manifest-entry-shape-driven cases
# ---------------------------------------------------------------------------


def test_gate2_passes_when_stage2_converges_faster():
    entry = {
        "stage2_metrics": {"final_mean_reward": 5.0, "training_steps": 20000, "steps_to_converge": 8000},
        "coldstart_baseline_metrics": {"final_mean_reward": 4.9, "training_steps": 60000, "steps_to_converge": 40000},
    }
    result = gate2_check(entry)
    assert result.passed is True
    assert result.metrics["speedup_factor"] == 5.0


def test_gate2_fails_when_stage2_is_not_faster():
    entry = {
        "stage2_metrics": {"final_mean_reward": 5.0, "training_steps": 20000, "steps_to_converge": 50000},
        "coldstart_baseline_metrics": {"final_mean_reward": 4.9, "training_steps": 60000, "steps_to_converge": 40000},
    }
    result = gate2_check(entry)
    assert result.passed is False


def test_gate2_inconclusive_when_metrics_missing():
    entry = {"stage2_metrics": None, "coldstart_baseline_metrics": None}
    result = gate2_check(entry)
    assert result.passed is False
    assert "INCONCLUSIVE" in result.message


def test_gate2_inconclusive_when_never_converged():
    entry = {
        "stage2_metrics": {"final_mean_reward": 5.0, "training_steps": 20000, "steps_to_converge": None},
        "coldstart_baseline_metrics": {"final_mean_reward": 4.9, "training_steps": 60000, "steps_to_converge": 40000},
    }
    result = gate2_check(entry)
    assert result.passed is False
    assert "INCONCLUSIVE" in result.message


def test_gate_result_print_report_does_not_crash(capsys):
    result = gate1_check(_monotonic_increasing_curve())
    result.print_report()
    captured = capsys.readouterr()
    assert "PASS" in captured.out or "FAIL" in captured.out
