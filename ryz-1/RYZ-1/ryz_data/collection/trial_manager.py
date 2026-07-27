from __future__ import annotations
import random
from dataclasses import asdict, dataclass, field
from typing import Any
from .branch_recorder import records_from_search
from .perturbations import perturb_macro
from .random_explorer import random_trajectory
from ..calibration.probe_runner import ProbeRunner
from ..dataset.schema import TransitionRecord, task_split
from ..generation.task_generator import GeneratedTask
from ..sim.interface import SimCoreInterface
from ..solver.beam_solver import BeamSolver, SolverBudget

@dataclass
class TrialMemory:
    discovered_action_mapping: dict[str, int] = field(default_factory=dict)
    estimated_mechanics: dict[str, float | bool | int] = field(default_factory=dict)
    failed_regions: list[dict[str, Any]] = field(default_factory=list)
    successful_subgoals: list[dict[str, Any]] = field(default_factory=list)
    previous_trial_outcomes: list[dict[str, Any]] = field(default_factory=list)
    best_progress: float = 0.; best_trajectory_summary: list[dict[str, Any]] = field(default_factory=list)
    def to_dict(self) -> dict[str, Any]: return asdict(self)

@dataclass
class CollectionOutput:
    task: dict[str, Any]; trials: list[dict[str, Any]]; trajectories: list[dict[str, Any]]
    transitions: list[TransitionRecord]; calibrations: list[dict[str, Any]]

class TrialManager:
    def __init__(self, sim: SimCoreInterface, high_budget: SolverBudget, low_budget: SolverBudget, rng: random.Random) -> None:
        self.sim, self.high_budget, self.low_budget, self.rng = sim, high_budget, low_budget, rng

    def _direct_records(self, generated: GeneratedTask, inferred: dict[str, Any], trial_id: str, trial_index: int,
                        trajectory_id: str, memory: TrialMemory, source: str, steps: list[tuple], perturbation: dict[str, Any] | None = None) -> list[TransitionRecord]:
        records=[]; prev_action=None; prev_reward=0.; prev_events=[]
        for index, (before, macro, result) in enumerate(steps):
            status = "perturbed" if perturbation else "random"
            records.append(TransitionRecord(task_id=generated.config.task_id, trial_id=trial_id, trajectory_id=trajectory_id,
              transition_id=f"{trajectory_id}:{index}", source_type=source, branch_status=status, task_seed=generated.config.seed,
              trial_index=trial_index, step_index=index, ground_truth_manifest=generated.ground_truth_manifest, inferred_manifest=inferred,
              manifest_visibility_mask=inferred.get("visibility_mask", {}), player_state=before.player.__dict__.copy(), local_geometry=before.local_geometry,
              global_task_features={"scenario":generated.config.level.get("route_metadata", {}), "difficulty":generated.config.level.get("difficulty")}, memory_context=memory.to_dict(), previous_action=prev_action, previous_reward=prev_reward, previous_events=prev_events,
              candidate_action=macro.to_dict(), candidate_action_index=0, candidate_validity_mask=[True],
              resulting_player_state=result.observation.player.__dict__.copy(), resulting_local_geometry=result.observation.local_geometry,
              immediate_reward=result.reward, progress_delta=result.reward, events=list(result.events), died=result.died, completed=result.completed,
              terminal=result.terminal, search_score=result.reward, cumulative_search_score=result.reward,
              branch_eventually_solved=result.completed, teacher_value={"binary_solved_descendant": result.completed,
              "discounted_probability_of_completion": float(result.completed), "normalized_best_descendant_score": result.reward,
              "steps_to_completion": 0 if result.completed else None, "solver_confidence": 0.}, dynamics_target={"events": list(result.events),
              "elapsed_frames": result.elapsed_frames, **({"perturbation": perturbation} if perturbation else {})},
              solver_budget={}, calibration_confidence=inferred.get("confidence", {}),
              parent_transition_id=f"{trajectory_id}:{index-1}" if index else None))
            prev_action, prev_reward, prev_events = macro.to_dict(), result.reward, list(result.events)
        return records

    def collect(self, generated: GeneratedTask, trials_count: int, include_perturbations: bool = True,
                include_random: bool = True, include_low_budget: bool = True) -> CollectionOutput:
        task = self.sim.create_task(generated.config); self.sim.reset_task(task)
        calibration = ProbeRunner().run(self.sim, task, generated.config.controls)
        memory = TrialMemory(discovered_action_mapping=generated.config.controls.copy(),
          estimated_mechanics={k: v for k, v in calibration.inferred_manifest.get("continuous_mechanics", {}).items() if v is not None})
        task_row = {"schema_version": "1.0.0", "task_id": generated.config.task_id, "task_seed": generated.config.seed,
          "split": task_split(generated.config.seed), "ground_truth_manifest": generated.ground_truth_manifest,
          "level": generated.config.level, "difficulty": generated.config.level["difficulty"]}
        transitions: list[TransitionRecord] = []; trials=[]; trajectories=[]
        # Calibration data is both an independent table and learning transitions.
        calibration_rows=[{"schema_version":"1.0.0", "task_id":generated.config.task_id, **asdict(p)} for p in calibration.probes]
        for pindex, probe in enumerate(calibration.probes):
            state0, state1 = probe.state_sequence[0], probe.state_sequence[-1]
            transitions.append(TransitionRecord(task_id=generated.config.task_id, trial_id=f"{generated.config.task_id}:calibration",
              trajectory_id=f"{generated.config.task_id}:calibration", transition_id=f"{generated.config.task_id}:cal:{pindex}", source_type="calibration",
              branch_status="unknown", task_seed=generated.config.seed, player_state=state0["player"], local_geometry=state0["local_geometry"],
              resulting_player_state=state1["player"], resulting_local_geometry=state1["local_geometry"], candidate_action=probe.action_sequence[0],
              candidate_validity_mask=[True], events=probe.events, ground_truth_manifest=generated.ground_truth_manifest, inferred_manifest=calibration.inferred_manifest,
              manifest_visibility_mask=calibration.inferred_manifest["visibility_mask"], calibration_confidence=calibration.inferred_manifest["confidence"],
              dynamics_target={"probe_id":probe.probe_id, "estimated_mechanic":probe.estimated_mechanic, "elapsed_frames":1}))
        verified_path = []
        for trial_index in range(trials_count):
            trial_id=f"{generated.config.task_id}:trial:{trial_index}"; self.sim.reset_task(task)
            budget = self.high_budget if trial_index == 0 or not include_low_budget else self.low_budget
            source = "high_budget_solver" if budget is self.high_budget else "low_budget"
            result=BeamSolver(budget).solve(self.sim, generated.config.controls, source)
            traj_id=f"{trial_id}:{source}"; records=records_from_search(result, task_id=generated.config.task_id, task_seed=generated.config.seed,
                trial_id=trial_id, trial_index=trial_index, trajectory_id=traj_id, ground_truth_manifest=generated.ground_truth_manifest,
                inferred_manifest=calibration.inferred_manifest, memory=memory.to_dict(), budget=budget,
                global_features={"scenario": generated.config.level.get("route_metadata", {}), "difficulty": generated.config.level.get("difficulty")})
            transitions.extend(records); trajectories.append({"schema_version":"1.0.0", "trajectory_id":traj_id, "task_id":generated.config.task_id,
                "trial_id":trial_id, "source_type":source, "completed":result.solution_node_id is not None, "node_count":len(result.nodes)})
            if result.solution_node_id is not None and not verified_path: verified_path=result.path(result.solution_node_id)
            progress=max((r.progress_delta for r in records), default=0.); memory.best_progress=max(memory.best_progress, progress)
            memory.previous_trial_outcomes.append({"trial_index":trial_index,"completed":result.solution_node_id is not None,"expanded":result.expansions})
            trials.append({"schema_version":"1.0.0", "trial_id":trial_id,"task_id":generated.config.task_id,"trial_index":trial_index,"memory":memory.to_dict()})
        if include_random:
            self.sim.reset_task(task); rid=f"{generated.config.task_id}:random"
            random_steps=random_trajectory(self.sim, generated.config.controls, self.rng)
            random_trial=f"{generated.config.task_id}:trial:random"
            transitions.extend(self._direct_records(generated, calibration.inferred_manifest, random_trial, trials_count, rid, memory, "random_exploration", random_steps))
            trials.append({"schema_version":"1.0.0", "trial_id":random_trial,"task_id":generated.config.task_id,"trial_index":trials_count,"memory":memory.to_dict()})
            trajectories.append({"schema_version":"1.0.0","trajectory_id":rid,"task_id":generated.config.task_id,"trial_id":random_trial,"source_type":"random_exploration"})
        if include_perturbations and verified_path:
            self.sim.reset_task(task); steps=[]; perturb=None
            for node in verified_path:
                macro, perturb = perturb_macro(node.action_from_parent, self.rng); before=self.sim.get_observation(); outcome=self.sim.step(macro.action, macro.duration_frames); steps.append((before, macro, outcome))
                if outcome.terminal: break
            pid=f"{generated.config.task_id}:perturbed"; perturbed_trial=f"{generated.config.task_id}:trial:perturbed"; transitions.extend(self._direct_records(generated, calibration.inferred_manifest,
               perturbed_trial, trials_count+1, pid, memory, "perturbation", steps, asdict(perturb) if perturb else {}))
            trials.append({"schema_version":"1.0.0", "trial_id":perturbed_trial,"task_id":generated.config.task_id,"trial_index":trials_count+1,"memory":memory.to_dict()})
            trajectories.append({"schema_version":"1.0.0","trajectory_id":pid,"task_id":generated.config.task_id,"trial_id":perturbed_trial,"source_type":"perturbation","parent_trajectory_id":f"{generated.config.task_id}:trial:0:high_budget_solver"})
        return CollectionOutput(task_row, trials, trajectories, transitions, calibration_rows)
