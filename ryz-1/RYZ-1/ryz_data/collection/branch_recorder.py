from __future__ import annotations
import math
from typing import Any
from ..dataset.schema import TransitionRecord
from ..solver.beam_solver import SolverBudget, SolverResult

def _softmax(scores: list[float], temperature: float) -> list[float] | None:
    if not scores: return None
    scaled = [x / max(temperature, 1e-6) for x in scores]; top = max(scaled); exps = [math.exp(x-top) for x in scaled]; total = sum(exps)
    return [x / total for x in exps]

def _ancestor_ids(result: SolverResult) -> set[int]:
    if result.solution_node_id is None: return set()
    node, out = result.node(result.solution_node_id), set()
    while node.parent_id is not None: out.add(node.node_id); node = result.node(node.parent_id)
    return out

def records_from_search(result: SolverResult, *, task_id: str, task_seed: int, trial_id: str, trial_index: int,
                        trajectory_id: str, ground_truth_manifest: dict[str, Any], inferred_manifest: dict[str, Any],
                        memory: dict[str, Any], budget: SolverBudget, global_features: dict[str, Any] | None = None) -> list[TransitionRecord]:
    solved = _ancestor_ids(result); output: list[TransitionRecord] = []
    for node in result.nodes:
        if node.parent_id is None: continue
        parent = result.node(node.parent_id); action_index = next((i for i, a in enumerate(parent.candidate_actions) if a == node.action_from_parent), 0)
        eventual = node.node_id in solved
        if eventual and node.branch_status not in ("solution",): node.branch_status = "successful_prefix"
        teacher_policy = _softmax(parent.candidate_scores, budget.temperature)
        steps = (result.node(result.solution_node_id).depth - node.depth) if eventual and result.solution_node_id is not None else None
        output.append(TransitionRecord(task_id=task_id, trial_id=trial_id, trajectory_id=trajectory_id,
            transition_id=f"{trajectory_id}:n{node.node_id}", source_type=node.source_type, branch_status=node.branch_status,
            task_seed=task_seed, trial_index=trial_index, step_index=node.depth, ground_truth_manifest=ground_truth_manifest,
            inferred_manifest=inferred_manifest, manifest_visibility_mask=inferred_manifest.get("visibility_mask", {}),
            player_state=parent.observation.player.__dict__.copy(), local_geometry=parent.observation.local_geometry,
            nearby_entities=list(parent.observation.nearby_entities), global_task_features=global_features or {}, memory_context=memory,
            candidate_action=node.action_from_parent.to_dict() if node.action_from_parent else {}, candidate_action_index=action_index,
            candidate_validity_mask=parent.candidate_validity, resulting_player_state=node.observation.player.__dict__.copy(),
            resulting_local_geometry=node.observation.local_geometry, resulting_entities=list(node.observation.nearby_entities),
            immediate_reward=node.immediate_reward, progress_delta=node.progress_delta, events=node.events, died=node.died,
            completed=node.completed, terminal=node.terminal, search_score=node.search_score,
            cumulative_search_score=node.cumulative_reward, prune_reason=node.prune_reason, branch_eventually_solved=eventual if result.solution_node_id else None,
            steps_to_verified_completion=steps, best_verified_descendant_score=result.node(result.solution_node_id).search_score if eventual and result.solution_node_id else None,
            teacher_policy=teacher_policy, teacher_value={"binary_solved_descendant": eventual if result.solution_node_id else None,
                "discounted_probability_of_completion": (0.99 ** steps) if steps is not None else None,
                "normalized_best_descendant_score": node.search_score, "steps_to_completion": steps,
                "solver_confidence": 1.0 if result.solution_node_id else 0.0},
            dynamics_target={"position_delta": [node.observation.player.x-parent.observation.player.x, node.observation.player.y-parent.observation.player.y],
                "velocity_delta": [node.observation.player.vx-parent.observation.player.vx, node.observation.player.vy-parent.observation.player.vy],
                "events": node.events, "elapsed_frames": node.action_from_parent.duration_frames if node.action_from_parent else 0,
                "death": node.died, "completion": node.completed}, solver_budget={"beam_width": budget.beam_width, "max_depth": budget.max_depth,
                "candidate_count": budget.candidate_count or 0}, calibration_confidence=inferred_manifest.get("confidence", {}),
            parent_transition_id=f"{trajectory_id}:n{parent.node_id}" if parent.parent_id is not None else None))
    return output
