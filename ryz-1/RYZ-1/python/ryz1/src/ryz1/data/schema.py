from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np

from ryz1.protocol.schemas import DATASET_SCHEMA, DatasetShape, require_keys


def load_json(path: Path) -> dict[str, Any]:
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def validate_dataset(payload: dict[str, Any]) -> DatasetShape:
    require_keys(payload, ["schemaVersion", "datasetId", "taskIds", "transitions"], "dataset")
    if payload["schemaVersion"] != DATASET_SCHEMA:
        raise ValueError(f"unsupported dataset schema {payload['schemaVersion']!r}")
    transitions = payload["transitions"]
    if not transitions:
        raise ValueError("dataset has no transitions")

    first = transitions[0]
    require_keys(first, ["taskId", "trialId", "macroId", "observation", "nextObservation"], "transition")
    obs = first["observation"]
    require_keys(obs, ["playerVector"], "observation")
    player_size = len(obs["playerVector"])
    if player_size <= 0:
        raise ValueError("player vector is empty")
    mechanics_size = len(first.get("mechanicsVector", [0.0] * 32))
    if mechanics_size <= 0:
        raise ValueError("mechanics vector is empty")
    if not all(len(t.get("mechanicsVector", [0.0] * mechanics_size)) == mechanics_size for t in transitions):
        raise ValueError("transition mechanics vectors have inconsistent sizes")

    max_macro = max(int(t["macroId"]) for t in transitions)
    task_ids = set(payload["taskIds"])
    if not all(t["taskId"] in task_ids for t in transitions):
        raise ValueError("transition contains taskId outside dataset taskIds")
    return DatasetShape(
        player_vector_size=player_size,
        mechanics_vector_size=mechanics_size,
        macro_count=max_macro + 1,
    )


def split_by_task(dataset_paths: list[Path], validation_fraction: float = 0.2) -> tuple[list[Path], list[Path]]:
    ordered = sorted(dataset_paths, key=lambda p: p.name)
    if len(ordered) <= 1:
        return ordered, []
    val_count = max(1, int(round(len(ordered) * validation_fraction)))
    return ordered[:-val_count], ordered[-val_count:]


def select_teacher_transitions(transitions: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Choose one non-contradictory policy target for each expanded search state."""
    groups: dict[tuple[str, int, int], list[dict[str, Any]]] = {}
    for index, transition in enumerate(transitions):
        key = (
            str(transition["taskId"]),
            int(transition.get("trialId", 0)),
            int(transition.get("parentId", index)),
        )
        groups.setdefault(key, []).append(transition)

    def rank(transition: dict[str, Any]) -> tuple[int, int, int, int, int, float, int]:
        return (
            int(bool(transition.get("eventuallyCompleted"))),
            int(bool(transition.get("completion"))),
            int(bool(transition.get("teacherSelected"))),
            int(bool(transition.get("survivedPruning"))),
            int(not bool(transition.get("death"))),
            float(transition.get("candidateScore", 0.0)),
            -int(transition.get("nodeId", 0)),
        )

    return [max(group, key=rank) for group in groups.values()]


def transitions_to_arrays(payload: dict[str, Any], shape: DatasetShape) -> dict[str, np.ndarray]:
    transitions = select_teacher_transitions(payload["transitions"])
    player = np.zeros((len(transitions), shape.player_vector_size), dtype=np.float32)
    next_player = np.zeros_like(player)
    mechanics = np.zeros((len(transitions), shape.mechanics_vector_size), dtype=np.float32)
    actions = np.zeros((len(transitions),), dtype=np.int64)
    rewards = np.zeros((len(transitions),), dtype=np.float32)
    values = np.zeros((len(transitions),), dtype=np.float32)
    dones = np.zeros((len(transitions),), dtype=np.float32)
    trial_ids = np.zeros((len(transitions),), dtype=np.int64)
    for i, transition in enumerate(transitions):
        player[i] = np.asarray(transition["observation"]["playerVector"], dtype=np.float32)
        next_player[i] = np.asarray(transition["nextObservation"]["playerVector"], dtype=np.float32)
        mechanics[i] = np.asarray(
            transition.get("mechanicsVector", [0.0] * shape.mechanics_vector_size),
            dtype=np.float32,
        )
        actions[i] = int(transition["macroId"])
        rewards[i] = float(transition.get("reward", 0.0))
        values[i] = 1.0 if transition.get("eventuallyCompleted") or transition.get("completion") else 0.0
        dones[i] = 1.0 if transition.get("death") or transition.get("completion") else 0.0
        trial_ids[i] = int(transition.get("trialId", 0))
    return {
        "player": player,
        "next_player": next_player,
        "mechanics": mechanics,
        "actions": actions,
        "rewards": rewards,
        "values": values,
        "dones": dones,
        "trial_ids": trial_ids,
    }


def teacher_sequences_to_arrays(
    payload: dict[str, Any],
    shape: DatasetShape,
    sequence_length: int,
) -> dict[str, np.ndarray]:
    """Build causal search-tree windows ending at every teacher-labelled state.

    A transition's observation is the state at ``parentId`` and its ``macroId``
    is the teacher target for that state. Node/parent links recover the actual
    state history without crossing task or trial boundaries. Windows are
    left-padded with zeros; history feature 3 is a validity bit.
    """
    if sequence_length <= 0:
        raise ValueError("sequence_length must be positive")

    selected = select_teacher_transitions(payload["transitions"])
    count = len(selected)
    player = np.zeros((count, sequence_length, shape.player_vector_size), dtype=np.float32)
    mechanics = np.zeros((count, sequence_length, shape.mechanics_vector_size), dtype=np.float32)
    history = np.zeros((count, sequence_length, 4), dtype=np.float32)
    actions = np.zeros((count,), dtype=np.int64)
    values = np.zeros((count,), dtype=np.float32)
    trial_ids = np.zeros((count,), dtype=np.int64)
    lengths = np.zeros((count,), dtype=np.int64)

    scoped_transitions: dict[tuple[str, int], list[dict[str, Any]]] = {}
    for transition in payload["transitions"]:
        scope = (str(transition["taskId"]), int(transition.get("trialId", 0)))
        scoped_transitions.setdefault(scope, []).append(transition)

    scoped_selected: dict[tuple[str, int], list[dict[str, Any]]] = {}
    for transition in selected:
        scope = (str(transition["taskId"]), int(transition.get("trialId", 0)))
        scoped_selected.setdefault(scope, []).append(transition)

    sequence_index = 0
    for scope, targets in scoped_selected.items():
        all_by_node = {
            int(transition.get("nodeId", -1)): transition
            for transition in scoped_transitions[scope]
            if int(transition.get("nodeId", -1)) >= 0
        }
        target_by_parent = {
            int(transition.get("parentId", -1)): transition
            for transition in targets
        }

        for target in targets:
            chain: list[dict[str, Any]] = []
            state_node = int(target.get("parentId", -1))
            visited: set[int] = set()
            while state_node >= 0 and state_node not in visited and len(chain) < sequence_length:
                visited.add(state_node)
                state_target = target_by_parent.get(state_node)
                if state_target is None:
                    break
                chain.append(state_target)
                incoming = all_by_node.get(state_node)
                state_node = int(incoming.get("parentId", -1)) if incoming is not None else -1
            chain.reverse()

            offset = sequence_length - len(chain)
            for step_index, step in enumerate(chain):
                out_index = offset + step_index
                player[sequence_index, out_index] = np.asarray(
                    step["observation"]["playerVector"], dtype=np.float32
                )
                mechanics[sequence_index, out_index] = np.asarray(
                    step.get("mechanicsVector", [0.0] * shape.mechanics_vector_size),
                    dtype=np.float32,
                )

                current_state = int(step.get("parentId", -1))
                incoming = all_by_node.get(current_state)
                if incoming is not None:
                    history[sequence_index, out_index, 0] = (
                        float(incoming["macroId"]) / max(1, shape.macro_count - 1)
                    )
                    history[sequence_index, out_index, 1] = float(incoming.get("reward", 0.0))
                    history[sequence_index, out_index, 2] = float(
                        bool(incoming.get("death")) or bool(incoming.get("completion"))
                    )
                history[sequence_index, out_index, 3] = 1.0

            actions[sequence_index] = int(target["macroId"])
            values[sequence_index] = float(
                bool(target.get("eventuallyCompleted")) or bool(target.get("completion"))
            )
            trial_ids[sequence_index] = scope[1]
            lengths[sequence_index] = len(chain)
            sequence_index += 1

    return {
        "player": player,
        "mechanics": mechanics,
        "history": history,
        "actions": actions,
        "values": values,
        "trial_ids": trial_ids,
        "lengths": lengths,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("dataset", type=Path)
    args = parser.parse_args()
    payload = load_json(args.dataset)
    shape = validate_dataset(payload)
    example_count = len(select_teacher_transitions(payload["transitions"]))
    print(
        f"valid dataset: player={shape.player_vector_size} macros={shape.macro_count} "
        f"transitions={len(payload['transitions'])} teacher_examples={example_count}"
    )


if __name__ == "__main__":
    main()
