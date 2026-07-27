"""Deterministic snapshot based beam search; no randomized tie breaking."""
from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any
from .macros import macro_vocabulary
from .pruning import heuristic_prune
from .scoring import progress, score
from .search_node import SearchNode
from ..sim.interface import SimCoreInterface

@dataclass(frozen=True)
class SolverBudget:
    beam_width: int = 64; max_depth: int = 150; candidate_count: int | None = None
    temperature: float = 1.0
@dataclass
class SolverResult:
    nodes: list[SearchNode]; best_node_id: int; solution_node_id: int | None; expansions: int
    def node(self, node_id: int) -> SearchNode: return self.nodes[node_id]
    def path(self, node_id: int | None = None) -> list[SearchNode]:
        current = self.node(self.best_node_id if node_id is None else node_id); output = []
        while current.parent_id is not None: output.append(current); current = self.node(current.parent_id)
        return list(reversed(output))

class BeamSolver:
    def __init__(self, budget: SolverBudget, score_weights: dict[str, float] | None = None) -> None:
        self.budget, self.score_weights = budget, score_weights

    def solve(self, sim: SimCoreInterface, controls: dict[str, int], source_type: str = "high_budget_solver") -> SolverResult:
        root_obs = sim.get_observation(); start = (root_obs.player.x, root_obs.player.y)
        root = SearchNode(0, None, 0, sim.clone_state(), sim.hash_state(), root_obs, None, 0, 0,
            score(root_obs, start=start, cumulative_reward=0, completed=False, died=False, depth=0),
            0, False, False, False, source_type=source_type)
        root.search_score = root.heuristic_score
        nodes, frontier, seen, expansions, solution = [root], [root], {root.state_hash}, 0, None
        macros = macro_vocabulary(controls)
        if self.budget.candidate_count: macros = macros[:self.budget.candidate_count]
        for _ in range(self.budget.max_depth):
            children: list[SearchNode] = []
            for parent in sorted(frontier, key=lambda n: (-n.search_score, n.node_id)):
                if parent.terminal: continue
                parent.candidate_actions = list(macros); parent.candidate_scores = []; parent.candidate_validity = []
                for macro in macros:
                    sim.restore_state(parent.snapshot); before = progress(parent.observation, start)
                    outcome = sim.step(macro.action, macro.duration_frames)
                    snapshot, state_hash = sim.clone_state(), sim.hash_state(); after = progress(outcome.observation, start)
                    prune = heuristic_prune(outcome.observation, parent.depth + 1, self.budget.max_depth)
                    duplicate = state_hash in seen
                    search_score = score(outcome.observation, start=start, cumulative_reward=parent.cumulative_reward + outcome.reward,
                        completed=outcome.completed, died=outcome.died, depth=parent.depth + 1, prior_hashes=int(duplicate), weights=self.score_weights)
                    parent.candidate_scores.append(search_score); parent.candidate_validity.append(not duplicate and prune is None)
                    status = "solution" if outcome.completed else "death" if outcome.died else "successful_prefix"
                    reason = "duplicate_state" if duplicate else prune
                    if duplicate: status = "pruned_duplicate"
                    elif prune: status = "pruned_heuristic"
                    child = SearchNode(len(nodes), parent.node_id, parent.depth + 1, snapshot, state_hash, outcome.observation,
                        macro, outcome.reward, parent.cumulative_reward + outcome.reward, search_score, search_score,
                        outcome.terminal, outcome.completed, outcome.died, reason, status, events=list(outcome.events),
                        progress_delta=after - before, source_type=source_type)
                    nodes.append(child); expansions += 1
                    if duplicate or prune: continue
                    seen.add(state_hash); children.append(child)
                    if outcome.completed and solution is None: solution = child.node_id
            if solution is not None: break
            if not children: break
            children.sort(key=lambda n: (-n.search_score, n.node_id))
            frontier = children[:self.budget.beam_width]
            for child in children[self.budget.beam_width:]:
                child.prune_reason, child.branch_status = "beam_width", "pruned_beam"
        candidates = [n for n in nodes if not n.died]
        best = solution if solution is not None else max(candidates, key=lambda n: (n.search_score, -n.node_id)).node_id
        # annotate near success after final best progress establishes a task-relative criterion
        for node in nodes:
            if node.died and progress(node.observation, start) >= .7: node.branch_status = "near_success"
        return SolverResult(nodes, best, solution, expansions)
