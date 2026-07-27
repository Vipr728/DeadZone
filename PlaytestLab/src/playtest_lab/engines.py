from __future__ import annotations

import importlib
import os
import random
import sys
from pathlib import Path
from typing import Any, Callable

import numpy as np

from .metrics import aggregate_episodes
from .registry import load_registry, model_by_id
from .schemas import RunRequest


Progress = Callable[[str, str, dict[str, Any] | None], None]


def generate_mock_level(domain: str, seed: int, stress: float = 0.5) -> dict[str, Any]:
    rng = random.Random(seed)
    if domain == "symbolic_puzzle":
        archetypes = ["key_return", "switch_return", "two_key_chain", "decoy_key"]
        steps = rng.randint(4, 8)
        return {
            "schema_version": 1,
            "id": f"mock-puzzle-{seed}",
            "domain": domain,
            "source": "mock_generator",
            "evidence_tier": "synthetic",
            "seed": seed,
            "archetype": rng.choice(archetypes),
            "required_steps": steps,
            "decoy_count": rng.randint(0, 2),
            "backtracking": rng.random() < 0.55,
            "stress": stress,
        }

    pieces = []
    for index in range(3):
        kind = rng.choice(["move_to_goal", "gap_jump", "elevation"])
        ranges = {
            "move_to_goal": (4.0, 10.0),
            "gap_jump": (2.0, 5.0 + 2.0 * stress),
            "elevation": (1.0, 3.0 + stress),
        }
        low, high = ranges[kind]
        pieces.append(
            {
                "id": f"piece_{index}",
                "type": kind,
                "parameter": round(rng.uniform(low, high), 3),
            }
        )
    return {
        "schema_version": 1,
        "id": f"mock-platformer-{seed}",
        "domain": "platformer",
        "source": "mock_generator",
        "evidence_tier": "synthetic",
        "seed": seed,
        "pieces": pieces,
        "stress": stress,
    }


def _synthetic_episodes(
    level: dict[str, Any], model: dict[str, Any] | None, episodes: int, seed: int
) -> list[dict[str, Any]]:
    rng = random.Random(seed)
    reward = float(model.get("training_reward") or 6.0) if model else 6.0
    skill = max(0.35, min(0.94, 0.48 + reward / 20))
    stress = float(level.get("stress", 0.5))
    if level.get("domain") == "symbolic_puzzle":
        complexity = level.get("required_steps", 5) / 10 + level.get("decoy_count", 0) * 0.08
    else:
        complexity = sum(
            float(piece["parameter"]) / (12 if piece["type"] == "move_to_goal" else 7)
            for piece in level.get("pieces", [])
        ) / max(1, len(level.get("pieces", [])))
    success_probability = max(0.03, min(0.97, skill - 0.42 * complexity - 0.15 * stress + 0.35))
    results = []
    for _ in range(episodes):
        roll = rng.random()
        outcome = "success" if roll < success_probability else ("death" if rng.random() < 0.7 else "timeout")
        steps = int(25 + 140 * complexity + rng.randint(-8, 18))
        results.append(
            {
                "outcome": outcome,
                "steps": max(1, steps),
                "attempts": 1 + (0 if outcome == "success" else rng.randint(1, 4)),
                "progress": 1.0 if outcome == "success" else round(rng.uniform(0.2, 0.92), 3),
                "action_change_rate": round(rng.uniform(0.18, 0.7), 3),
            }
        )
    return results


def _configure_gb10_imports() -> Path:
    root = Path(os.getenv("GB10_PROJECT_ROOT", "/home/dell/gb10-project"))
    source = root / "rl" / "src"
    if not source.is_dir():
        raise RuntimeError(f"GB10 runtime source not found: {source}")
    if str(source) not in sys.path:
        sys.path.insert(0, str(source))
    return root


def run_gb10_proxy(
    model: dict[str, Any], episodes: int, seed: int, progress: Progress
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    root = _configure_gb10_imports()
    onnx_path = Path(model["onnx"]["resolved_path"])
    if not onnx_path.is_file():
        raise RuntimeError(f"ONNX artifact unavailable for {model['id']}")
    ort = importlib.import_module("onnxruntime")
    config_loader = importlib.import_module("playtester_rl.config_loader")
    reward_module = importlib.import_module("playtester_rl.reward_strategies")
    env_module = importlib.import_module("playtester_rl.fake_env")
    piece = config_loader.load_piece_config(root / "rl" / "configs" / "piece_config.yaml")
    observation = config_loader.load_observation_config(root / "rl" / "configs" / "observation_config.yaml")
    reward = reward_module.create_reward_strategy(
        config_loader.load_reward_config(root / "rl" / "configs" / "reward_config.yaml")
    )
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    input_names = {item.name for item in session.get_inputs()}
    output_names = {item.name for item in session.get_outputs()}
    action_output = (
        "deterministic_discrete_actions"
        if "deterministic_discrete_actions" in output_names
        else "discrete_actions"
    )
    results: list[dict[str, Any]] = []
    all_actions: list[int] = []
    for episode_index in range(episodes):
        env = env_module.FakeCompositionEnv(piece, observation, reward, seed + episode_index)
        obs, _ = env.reset()
        actions: list[int] = []
        total_reward = 0.0
        outcome = "timeout"
        steps = 0
        while steps < 200:
            feed = {"obs_0": np.asarray(obs, dtype=np.float32).reshape(1, -1)}
            if "action_masks" in input_names:
                feed["action_masks"] = np.ones((1, 4), dtype=np.float32)
            action = int(session.run([action_output], feed)[0].reshape(-1)[0])
            obs, reward_value, terminated, truncated, info = env.step(action)
            actions.append(action)
            total_reward += float(reward_value)
            steps += 1
            if terminated or truncated:
                outcome = info.get("outcome", "timeout")
                break
        changes = sum(a != b for a, b in zip(actions, actions[1:]))
        piece_results, _ = env.episode_telemetry()
        results.append(
            {
                "outcome": outcome,
                "steps": steps,
                "attempts": sum(int(item.get("attempts", 1)) for item in piece_results) or 1,
                "progress": min(1.0, len(piece_results) / max(1, piece.pieces_per_episode)),
                "action_change_rate": changes / max(1, len(actions) - 1),
                "reward": round(total_reward, 6),
            }
        )
        all_actions.extend(actions)
    progress("engine.proxy_complete", f"Ran {episodes} exact-shape GB10 proxy episodes.", None)
    action_diversity = len(set(all_actions))
    return results, {
        "onnx_path": str(onnx_path),
        "action_diversity": action_diversity,
        "static_policy_warning": action_diversity <= 1,
        "observation_size": observation.observation_size(),
        "action_count": 4,
    }


def run_ryz_onnx_probe(
    model: dict[str, Any], episodes: int, seed: int, progress: Progress
) -> dict[str, Any]:
    onnx_path = Path(model["onnx"]["resolved_path"])
    if not onnx_path.is_file():
        raise RuntimeError(f"ONNX artifact unavailable for {model['id']}")
    ort = importlib.import_module("onnxruntime")
    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    rng = np.random.default_rng(seed)
    action_ids: list[int] = []
    values: list[float] = []
    sequence_length = 16
    for _ in range(episodes):
        feed = {
            "player": rng.normal(0, 0.35, (1, sequence_length, 24)).astype(np.float32),
            "mechanics": rng.integers(0, 2, (1, sequence_length, 32)).astype(np.float32),
            "history": rng.normal(0, 0.2, (1, sequence_length, 4)).astype(np.float32),
        }
        logits, value = session.run(["policy_logits", "value"], feed)
        action_ids.extend(np.argmax(logits, axis=-1).reshape(-1).tolist())
        values.extend(np.asarray(value).reshape(-1).tolist())
    progress(
        "engine.ryz_probe_complete",
        f"Ran {episodes} RYZ-1 ONNX sequence probes.",
        {"model_id": model["id"]},
    )
    return {
        "onnx_path": str(onnx_path),
        "input_contract": {"player": 24, "mechanics": 32, "history": 4, "sequence": 16},
        "policy_actions": 9,
        "action_diversity": len(set(action_ids)),
        "mean_value": round(float(np.mean(values)), 6),
        "value_range": [round(float(np.min(values)), 6), round(float(np.max(values)), 6)],
        "probe_only": True,
        "warning": "Randomized ONNX probes validate inference shape, not closed-loop gameplay.",
    }


def execute(request: RunRequest, progress: Progress) -> dict[str, Any]:
    registry = load_registry(verify=False)
    selected_ids = request.model_ids or [
        model["id"] for model in registry["models"] if model["status"] in {"default", "provisional_default"}
    ]
    level = request.source.get("level") if isinstance(request.source.get("level"), dict) else None
    if request.kind.value == "generate" or level is None:
        level = generate_mock_level(request.domain, request.seed, float(request.source.get("stress", 0.5)))
        progress("level.generated", f"Generated deterministic mock level {level['id']}.", {"seed": request.seed})

    if request.kind.value == "train":
        return {
            "schema_version": 1,
            "kind": request.kind.value,
            "evidence_tier": "synthetic",
            "status": "requires_worker",
            "message": "Training is registered but requires an authenticated Unity/ML-Agents worker.",
            "requirements": [
                "Use Level A for tuning and preserve Level B as sealed evaluation.",
                "Do not train on the physical-puzzle probe until all six Unity parity replays pass.",
            ],
            "level": level,
            "models": selected_ids,
        }

    model_reports = []
    for model_id in selected_ids:
        model = model_by_id(model_id, verify=False)
        progress("model.started", f"Testing {model['display_name']}.", {"model_id": model_id})
        diagnostics: dict[str, Any] = {}
        if request.engine in {"gb10_proxy", "auto"} and model["family"] == "gb10_ppo":
            try:
                episodes, diagnostics = run_gb10_proxy(
                    model, request.episodes, request.seed, progress
                )
            except Exception as error:
                diagnostics = {"proxy_error": str(error), "fallback": "synthetic_model"}
                episodes = _synthetic_episodes(level, model, request.episodes, request.seed)
        elif request.engine in {"ryz_simcore", "auto"} and model["family"] == "ryz1":
            episodes = _synthetic_episodes(level, model, request.episodes, request.seed)
            try:
                diagnostics = run_ryz_onnx_probe(model, request.episodes, request.seed, progress)
            except Exception as error:
                diagnostics = {"probe_error": str(error), "fallback": "synthetic_model"}
        else:
            episodes = _synthetic_episodes(level, model, request.episodes, request.seed)
        ood = min(100.0, float(level.get("stress", 0.5)) * 80)
        metrics = aggregate_episodes(
            episodes,
            evidence_tier="synthetic",
            search_nodes=request.budget if request.domain == "symbolic_puzzle" else 0,
            ood_score=ood,
        )
        model_reports.append(
            {
                "model_id": model_id,
                "display_name": model["display_name"],
                "status": model["status"],
                "compatibility_note": model["compatibility_note"],
                "metrics": metrics,
                "diagnostics": diagnostics,
                "episodes": episodes,
            }
        )

    best = min(model_reports, key=lambda item: item["metrics"]["difficulty_score"]) if model_reports else None
    return {
        "schema_version": 1,
        "kind": request.kind.value,
        "domain": request.domain,
        "engine": request.engine,
        "evidence_tier": "synthetic",
        "synthetic": True,
        "level": level,
        "models": model_reports,
        "recommended_model_id": best["model_id"] if best else None,
        "summary": (
            f"{len(model_reports)} model(s) evaluated against deterministic synthetic evidence. "
            "Run a Unity worker before treating these results as real gameplay validation."
        ),
    }
