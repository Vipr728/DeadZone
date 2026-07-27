"""Gate 1 / Gate 2 pass/fail evaluation — prd-ml.md §4, spec §7.

Both gates print a plain pass/fail plus the numbers to stdout (no dashboard
needed for a 29-hour build) and return a `GateResult` for programmatic use
(e.g. a CI-style check, or the Editor tool reading gate status).

Reward-curve input format: a JSON file containing a list of
`{"step": int, "mean_reward": float}` records, sorted by step. This is a
plain, dependency-light format any training loop (the fake env's own runner,
or a thin wrapper around mlagents-learn's own summary writer) can emit —
gate_eval does not require TensorBoard or mlagents to be installed to run.
If a real TensorBoard event file is preferred later, add a converter that
reads it and writes this same JSON shape; gate1_check itself only ever reads
the JSON list, keeping this module's dependency footprint fixed.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any

# TUNABLE — Gate 1: split the reward curve into a last-10% block and the
# 10%-block immediately before it; require the last block's mean reward to
# exceed the previous block's by at least this relative fraction.
GATE1_TREND_WINDOW_FRACTION = 0.10
GATE1_TREND_THRESHOLD = 0.10

# TUNABLE — "converged" means the (smoothed) reward curve first crosses this
# fraction of the run's final mean reward.
CONVERGENCE_THRESHOLD_FRACTION = 0.90
CONVERGENCE_SMOOTHING_WINDOW = 5


@dataclass
class GateResult:
    passed: bool
    message: str
    metrics: dict[str, Any]

    def print_report(self) -> None:
        status = "PASS" if self.passed else "FAIL"
        print(f"[{status}] {self.message}")
        for key, value in self.metrics.items():
            print(f"    {key}: {value}")


def load_reward_curve(path: Path) -> list[tuple[int, float]]:
    with open(path, encoding="utf-8") as f:
        records = json.load(f)
    curve = [(int(r["step"]), float(r["mean_reward"])) for r in records]
    curve.sort(key=lambda pair: pair[0])
    return curve


def save_reward_curve(curve: list[tuple[int, float]], path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump([{"step": step, "mean_reward": reward} for step, reward in curve], f, indent=2)


def _mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def gate1_check(
    reward_curve: list[tuple[int, float]],
    trend_threshold: float = GATE1_TREND_THRESHOLD,
    window_fraction: float = GATE1_TREND_WINDOW_FRACTION,
) -> GateResult:
    """Does Stage 1 training actually converge — reward curve trending up,
    not collapsing/plateauing? (spec §7 Gate 1). Compares the mean reward of
    the last `window_fraction` of steps against the block immediately before
    it; passes if the relative increase clears `trend_threshold`.
    """
    n = len(reward_curve)
    if n < 4:
        return GateResult(
            passed=False,
            message="Not enough reward-curve data points to evaluate Gate 1 (need >= 4).",
            metrics={"num_points": n},
        )

    window_size = max(1, int(n * window_fraction))
    # Guard against window_size being large enough to swallow the whole curve
    # (tiny curves with a large window_fraction) — always leave at least one
    # point in the "previous" block.
    window_size = min(window_size, n // 2)
    window_size = max(1, window_size)

    last_block = [r for _, r in reward_curve[-window_size:]]
    previous_block = [r for _, r in reward_curve[-2 * window_size : -window_size]]

    last_mean = _mean(last_block)
    previous_mean = _mean(previous_block)

    if abs(previous_mean) < 1e-9:
        relative_change = last_mean - previous_mean
    else:
        relative_change = (last_mean - previous_mean) / abs(previous_mean)

    passed = relative_change >= trend_threshold

    return GateResult(
        passed=passed,
        message=(
            "Gate 1 PASSED — Stage 1 reward is trending up."
            if passed
            else "Gate 1 FAILED — Stage 1 reward is not trending up (plateaued/collapsing). "
            "Fall back to SingleGymFallbackStrategy per spec §7 / reward_config.yaml."
        ),
        metrics={
            "num_points": n,
            "window_size": window_size,
            "previous_block_mean_reward": previous_mean,
            "last_block_mean_reward": last_mean,
            "relative_change": relative_change,
            "threshold": trend_threshold,
        },
    )


def compute_steps_to_converge(
    reward_curve: list[tuple[int, float]],
    final_mean_reward: float,
    threshold_fraction: float = CONVERGENCE_THRESHOLD_FRACTION,
    smoothing_window: int = CONVERGENCE_SMOOTHING_WINDOW,
) -> int | None:
    """First training step at which a smoothed reward curve crosses
    `threshold_fraction` of `final_mean_reward`. Returns None if the curve
    never crosses the threshold (did not converge within the recorded run).

    Smoothing (a trailing moving average) exists so a single noisy high-reward
    step doesn't register as "converged" — a documented simplification
    appropriate for a 29-hour build, not a claim of statistical rigor.
    """
    if not reward_curve or final_mean_reward == 0:
        return None

    target = threshold_fraction * final_mean_reward
    rewards = [r for _, r in reward_curve]
    steps = [s for s, _ in reward_curve]

    for i in range(len(rewards)):
        window = rewards[max(0, i - smoothing_window + 1) : i + 1]
        smoothed = _mean(window)
        if final_mean_reward > 0 and smoothed >= target:
            return steps[i]
        if final_mean_reward < 0 and smoothed <= target:
            return steps[i]
    return None


def gate2_check(manifest_entry: dict[str, Any]) -> GateResult:
    """Does Stage 2 fine-tune converge measurably faster than a cold-start
    baseline on the same level? (spec §7 Gate 2). Reads
    contracts/checkpoint_manifest.schema.json-shaped fields directly from an
    already-loaded manifest entry (see checkpoint_manifest.get_entry)."""
    stage2 = manifest_entry.get("stage2_metrics")
    coldstart = manifest_entry.get("coldstart_baseline_metrics")

    if not stage2 or not coldstart:
        return GateResult(
            passed=False,
            message="Gate 2 INCONCLUSIVE — stage2_metrics or coldstart_baseline_metrics missing from manifest.",
            metrics={"stage2_metrics": stage2, "coldstart_baseline_metrics": coldstart},
        )

    stage2_steps = stage2.get("steps_to_converge")
    coldstart_steps = coldstart.get("steps_to_converge")

    if stage2_steps is None or coldstart_steps is None:
        return GateResult(
            passed=False,
            message="Gate 2 INCONCLUSIVE — one or both runs never converged (steps_to_converge is null).",
            metrics={"stage2_steps_to_converge": stage2_steps, "coldstart_steps_to_converge": coldstart_steps},
        )

    passed = stage2_steps < coldstart_steps
    speedup = (coldstart_steps / stage2_steps) if stage2_steps > 0 else None

    return GateResult(
        passed=passed,
        message=(
            f"Gate 2 PASSED — Stage 2 fine-tune converged in {stage2_steps} steps vs "
            f"{coldstart_steps} cold-start steps ({speedup:.2f}x speedup)."
            if passed
            else f"Gate 2 FAILED — Stage 2 fine-tune ({stage2_steps} steps) did not beat "
            f"cold-start baseline ({coldstart_steps} steps). Commit to whichever path actually worked (spec §7)."
        ),
        metrics={
            "stage2_steps_to_converge": stage2_steps,
            "coldstart_steps_to_converge": coldstart_steps,
            "speedup_factor": speedup,
        },
    )
