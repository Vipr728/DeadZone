from __future__ import annotations
from typing import Any

def estimate_from_probe(mechanic: str, states: list[dict[str, Any]], ground_truth: dict[str, Any]) -> tuple[Any, float]:
    """Conservative estimator: only expose mechanics actually tested with meaningful signal.
    The example adapter permits exact reference values; real adapters replace this function
    with fitted estimators and should lower confidence for unidentifiable parameters.
    """
    signal = len(states) > 1
    value = ground_truth.get(mechanic) if signal else None
    return value, 0.9 if signal and value is not None else 0.0
