from __future__ import annotations

import math
import statistics
from typing import Any


WEIGHTS = {
    "failure_pressure": 0.30,
    "clear_time": 0.20,
    "retry_burden": 0.15,
    "search_complexity": 0.15,
    "execution_precision": 0.10,
    "novelty_ood": 0.10,
}


def wilson_interval(successes: int, total: int, z: float = 1.96) -> tuple[float, float]:
    if total <= 0:
        return 0.0, 1.0
    probability = successes / total
    denominator = 1 + z * z / total
    center = (probability + z * z / (2 * total)) / denominator
    margin = z * math.sqrt(
        probability * (1 - probability) / total + z * z / (4 * total * total)
    ) / denominator
    return max(0.0, center - margin), min(1.0, center + margin)


def _clamp(value: float) -> float:
    return round(max(0.0, min(100.0, value)), 2)


def aggregate_episodes(
    episodes: list[dict[str, Any]],
    *,
    evidence_tier: str,
    search_nodes: int = 0,
    ood_score: float = 0.0,
    proof_status: str = "not_attempted",
) -> dict[str, Any]:
    total = len(episodes)
    successes = sum(episode["outcome"] == "success" for episode in episodes)
    deaths = sum(episode["outcome"] == "death" for episode in episodes)
    timeouts = sum(episode["outcome"] == "timeout" for episode in episodes)
    success_low, success_high = wilson_interval(successes, total)
    clear_times = [
        float(episode["steps"]) for episode in episodes if episode["outcome"] == "success"
    ]
    attempts = [float(episode.get("attempts", 1)) for episode in episodes]
    action_changes = [float(episode.get("action_change_rate", 0)) for episode in episodes]

    components = {
        "failure_pressure": _clamp(100 * (deaths + timeouts) / max(1, total)),
        "clear_time": _clamp((statistics.mean(clear_times) if clear_times else 200) / 2),
        "retry_burden": _clamp((statistics.mean(attempts) - 1) * 25),
        "search_complexity": _clamp(math.log10(max(1, search_nodes)) * 20),
        "execution_precision": _clamp(
            100 * (1 - statistics.mean(action_changes)) if action_changes else 50
        ),
        "novelty_ood": _clamp(ood_score),
    }
    difficulty = round(sum(components[name] * weight for name, weight in WEIGHTS.items()), 2)

    if successes:
        solvability = "solved"
    elif proof_status == "exhaustive_no_goal":
        solvability = "proven_impossible"
    else:
        solvability = "not_solved_within_budget"

    return {
        "schema_version": 1,
        "evidence_tier": evidence_tier,
        "solvability_status": solvability,
        "proof_status": proof_status,
        "difficulty_score": difficulty,
        "difficulty_components": components,
        "metric_coverage": 1.0,
        "episode_count": total,
        "outcomes": {"success": successes, "death": deaths, "timeout": timeouts},
        "success_rate": round(successes / max(1, total), 4),
        "success_rate_95ci": [round(success_low, 4), round(success_high, 4)],
        "mean_clear_steps": round(statistics.mean(clear_times), 2) if clear_times else None,
        "mean_attempts": round(statistics.mean(attempts), 2) if attempts else None,
        "furthest_progress": round(max((e.get("progress", 0) for e in episodes), default=0), 4),
        "search_nodes": search_nodes,
        "ood_score": _clamp(ood_score),
    }

